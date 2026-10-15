using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RecBox.Models;
using RecBox.Services;
using WinForms = System.Windows.Forms;

/// <summary>
/// End-to-end reliability tests for RecBox's recording engine (real FFmpeg, real devices).
/// Usage: SmokeTest [test names...] [soak]   (no names = all except soak)
/// </summary>
internal static class Program
{
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "RecBoxSmoke");
    private static readonly List<string> Failures = new();
    private static readonly List<string> Passes = new();
    private static FFmpegLocator _locator = null!;
    private static string _ffmpeg = null!;
    private static EncoderSelector _encoders = null!;
    private static MonitorService _monitors = null!;
    private static AudioDeviceService _audio = null!;
    private static MonitorInfo _primary = null!;
    private static bool _systemAudio;

    [STAThread]
    private static int Main(string[] args)
    {
        // Run async tests on a thread-pool context; Main stays STA for COM / WinForms.
        return Task.Run(() => MainAsync(args)).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        Directory.CreateDirectory(OutDir);
        foreach (var old in Directory.GetFiles(OutDir))
            File.Delete(old);
        SessionStore.Directory = Path.Combine(OutDir, "sessions");

        _locator = new FFmpegLocator();
        _encoders = new EncoderSelector(_locator);
        _monitors = new MonitorService();
        _audio = new AudioDeviceService();

        Console.WriteLine("=== RecBox reliability tests ===");
        _ffmpeg = _locator.Find() ?? "";
        Console.WriteLine($"FFmpeg: {(_ffmpeg.Length > 0 ? _ffmpeg : "NOT FOUND")}");
        if (_ffmpeg.Length == 0)
            return 1;

        await _encoders.ProbeAsync();
        Console.WriteLine($"Working encoders: {string.Join(", ", _encoders.Available.Select(e => e.CodecName))}");

        var monitors = _monitors.GetMonitors();
        foreach (var m in monitors)
            Console.WriteLine($"  [{m.Index}] {m.DeviceName} {m.Width}x{m.Height} @ ({m.BoundsX},{m.BoundsY}) {m.RefreshRate}Hz primary={m.IsPrimary}");
        _primary = monitors.First(m => m.IsPrimary);

        var mics = _audio.GetMicrophones();
        foreach (var mic in mics)
            Console.WriteLine($"  mic: {mic.DisplayName}");
        _systemAudio = _audio.IsSystemAudioAvailable();
        Console.WriteLine($"System audio available: {_systemAudio}");

        var selected = args.Select(a => a.ToLowerInvariant()).ToHashSet();
        bool Want(string name, bool defaultOn = true) => selected.Count == 0 ? defaultOn : selected.Contains(name);

        if (Want("settings")) TestSettings();
        if (Want("video")) await TestBasic("video-only", new AppSettings { RecordMicrophone = false, RecordSystemAudio = false }, 6);
        if (Want("system") && _systemAudio) await TestBasic("system-audio", new AppSettings { RecordMicrophone = false, RecordSystemAudio = true }, 6);
        if (Want("mic")) await TestBasic("mic-default", new AppSettings { RecordMicrophone = true, RecordSystemAudio = false }, 6);
        if (Want("micsys") && _systemAudio) await TestBasic("mic+system/max", new AppSettings { RecordMicrophone = true, RecordSystemAudio = true, QualityPreset = QualityPreset.MaximumQuality }, 6);
        if (Want("low")) await TestBasic("low-preset", new AppSettings { RecordMicrophone = true, RecordSystemAudio = _systemAudio, QualityPreset = QualityPreset.LowFileSize }, 5);

        var broadcast = mics.FirstOrDefault(m => m.Name.Contains("Broadcast", StringComparison.OrdinalIgnoreCase));
        if (Want("broadcast") && broadcast is not null)
            await TestBasic("nvidia-broadcast", new AppSettings { RecordMicrophone = true, MicrophoneId = broadcast.Id, RecordSystemAudio = false }, 5);
        foreach (var mic in mics.Where(m => !m.IsDefault && m != broadcast))
        {
            if (Want("allmics"))
                await TestBasic($"mic:{mic.Name}", new AppSettings { RecordMicrophone = true, MicrophoneId = mic.Id, RecordSystemAudio = false }, 4);
        }

        if (Want("monitors"))
        {
            foreach (var m in monitors)
                await TestBasic($"monitor {m.DeviceName}", new AppSettings { RecordMicrophone = false, RecordSystemAudio = false }, 4, m, extractFrame: true);
        }

        if (Want("x264"))
        {
            var saved = _encoders.Available;
            _encoders.OverrideAvailableForTests([EncoderSelector.Software]);
            foreach (var preset in Enum.GetValues<QualityPreset>())
                await TestBasic($"x264/{preset}", new AppSettings { RecordMicrophone = true, RecordSystemAudio = _systemAudio, QualityPreset = preset }, 5);
            _encoders.OverrideAvailableForTests(saved);
        }

        if (Want("fallback")) await TestEncoderFallback();
        if (Want("sync") && _systemAudio) await TestSync("sync-30s", 30, [3, 25]);
        if (Want("kill")) await TestKillMidRecording();
        if (Want("killstorm")) await TestKillStorm();
        if (Want("cycles")) await TestStartStopCycles();
        if (Want("recovery")) await TestRecovery();
        if (Want("soak", defaultOn: false) && _systemAudio) await TestSync("soak-5min", 300, [3, 150, 290], micToo: true);

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        foreach (var p in Passes) Console.WriteLine("PASS  " + p);
        foreach (var f in Failures) Console.WriteLine("FAIL  " + f);
        Console.WriteLine(Failures.Count == 0 ? $"ALL {Passes.Count} PASSED" : $"{Failures.Count} FAILED");
        Console.WriteLine($"Outputs: {OutDir}");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void TestSettings()
    {
        Header("settings round-trip");
        var store = new SettingsStore();
        var original = store.Load();
        try
        {
            var copy = original.Clone();
            copy.QualityPreset = QualityPreset.MaximumQuality;
            copy.MonitorDeviceName = @"\\.\DISPLAY9";
            store.Save(copy);
            var back = store.Load();
            Check("settings", back.QualityPreset == QualityPreset.MaximumQuality && back.MonitorDeviceName == @"\\.\DISPLAY9", "values did not persist");
        }
        finally
        {
            store.Save(original);
        }
    }

    private static async Task<string?> TestBasic(string name, AppSettings settings, int seconds, MonitorInfo? monitor = null, bool extractFrame = false)
    {
        Header(name);
        monitor ??= _primary;
        settings.OutputFolder = OutDir;
        using var rec = new RecordingService(_locator, _encoders, _monitors);
        try
        {
            await rec.StartAsync(settings, monitor);
            Console.WriteLine($"  encoder={rec.EncoderDisplayName}");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await rec.StopAsync();
        }
        catch (Exception ex)
        {
            Fail(name, ex.Message);
            return null;
        }

        var path = rec.OutputPath;
        if (rec.State != RecordingState.Idle || path is null || !File.Exists(path))
        {
            Fail(name, $"state={rec.State} error={rec.LastError} path={path}");
            return null;
        }

        var problems = new List<string>();
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            problems.Add($"not MP4: {Path.GetFileName(path)}");
        if (rec.OutputFiles.Count != 1)
            problems.Add($"{rec.OutputFiles.Count} output files");
        if (Directory.GetFiles(OutDir, "*.mkv").Length > 0)
            problems.Add("leftover MKV segment");
        if (!string.IsNullOrEmpty(rec.LastWarning))
            problems.Add($"warning: {rec.LastWarning}");

        var info = await StreamInfo(path);
        var v = await StreamDuration(path, "v");
        Console.WriteLine($"  {Path.GetFileName(path)} {new FileInfo(path).Length / 1024} KB  {info.Width}x{info.Height}  video={v:F2}s");
        if (info.Width != monitor.Width || info.Height != monitor.Height)
            problems.Add($"resolution {info.Width}x{info.Height} != native {monitor.Width}x{monitor.Height}");
        // Startup/stop handshakes add a little footage; losing footage is the failure.
        if (v < seconds - 0.5 || v > seconds + 3.5)
            problems.Add($"video duration {v:F2}s vs {seconds}s recorded");

        var wantAudio = settings.RecordMicrophone || settings.RecordSystemAudio;
        if (wantAudio != info.HasAudio)
            problems.Add(wantAudio ? "audio stream missing" : "unexpected audio stream");
        if (info.HasAudio)
        {
            var a = await StreamDuration(path, "a");
            Console.WriteLine($"  audio={a:F2}s  (A-V {a - v:+0.00;-0.00}s)");
            // On stop all audio is drained and closed first; video runs on for the drain time
            // plus FFmpeg's stop reaction (~0.5 s with NVENC, ~1 s with x264).
            if (a - v > 0.2 || v - a > 1.5)
                problems.Add($"audio {a:F2}s vs video {v:F2}s");
        }

        if (extractFrame)
        {
            var png = Path.Combine(OutDir, $"frame_{monitor.DeviceName.Replace("\\", "").Replace(".", "")}.png");
            await Run($"-hide_banner -loglevel error -y -ss 1 -i \"{path}\" -frames:v 1 \"{png}\"");
            Console.WriteLine($"  frame: {png}");
        }

        Check(name, problems.Count == 0, string.Join("; ", problems));
        return path;
    }

    private static async Task TestEncoderFallback()
    {
        // AMF/QSV cannot open on a machine without that GPU: RecBox must fall back automatically.
        var bogus = new List<EncoderInfo>();
        if (!_encoders.Available.Any(e => e.Kind == VideoEncoderKind.Amf))
            bogus.Add(new EncoderInfo { Kind = VideoEncoderKind.Amf, CodecName = "h264_amf", DisplayName = "AMD AMF" });
        else if (!_encoders.Available.Any(e => e.Kind == VideoEncoderKind.Qsv))
            bogus.Add(new EncoderInfo { Kind = VideoEncoderKind.Qsv, CodecName = "h264_qsv", DisplayName = "Intel Quick Sync" });
        if (bogus.Count == 0)
        {
            Console.WriteLine("SKIP fallback: no unavailable encoder to simulate");
            return;
        }

        var saved = _encoders.Available;
        _encoders.OverrideAvailableForTests([.. bogus, EncoderSelector.Software]);
        try
        {
            var path = await TestBasic("encoder fallback (broken HW -> x264)", new AppSettings { RecordMicrophone = false, RecordSystemAudio = false }, 4);
            if (path is not null)
            {
                var codec = (await Run($"-hide_banner -i \"{path}\"")).Contains("h264", StringComparison.OrdinalIgnoreCase);
                Check("fallback produced h264", codec, "codec not h264");
            }
        }
        finally
        {
            _encoders.OverrideAvailableForTests(saved);
        }
    }

    private static async Task TestKillMidRecording()
    {
        const string name = "FFmpeg killed mid-recording -> auto-resume, one file";
        Header(name);
        var settings = new AppSettings { RecordMicrophone = true, RecordSystemAudio = _systemAudio, OutputFolder = OutDir };
        using var rec = new RecordingService(_locator, _encoders, _monitors);
        var sw = Stopwatch.StartNew();
        await rec.StartAsync(settings, _primary);
        await Task.Delay(5000);
        var killed = KillOurFfmpeg();
        Console.WriteLine($"  killed {killed} ffmpeg process(es) at {sw.Elapsed.TotalSeconds:F1}s");
        await Task.Delay(7000);
        var stateWhileRecording = rec.State;
        var status = rec.StatusMessage;
        await rec.StopAsync();
        var total = sw.Elapsed.TotalSeconds;

        var problems = new List<string>();
        if (killed == 0) problems.Add("no ffmpeg found to kill");
        if (stateWhileRecording != RecordingState.Recording) problems.Add($"state after kill = {stateWhileRecording}");
        if (rec.State != RecordingState.Idle) problems.Add($"final state {rec.State}: {rec.LastError}");
        if (rec.Interruptions != 1) problems.Add($"interruptions={rec.Interruptions}");
        Console.WriteLine($"  status while resumed: {status}");
        Console.WriteLine($"  warning: {rec.LastWarning}");

        var path = rec.OutputPath;
        if (path is null || !File.Exists(path) || rec.OutputFiles.Count != 1)
        {
            problems.Add($"outputs: {string.Join(", ", rec.OutputFiles)}");
        }
        else
        {
            var v = await StreamDuration(path, "v");
            var a = await StreamDuration(path, "a");
            Console.WriteLine($"  {Path.GetFileName(path)} video={v:F2}s audio={a:F2}s wall={total:F1}s");
            if (v < total - 4.5) problems.Add($"video {v:F1}s too short for {total:F1}s session");
            if (a - v > 0.2 || v - a > 1.5) problems.Add($"audio/video length mismatch {a:F2}/{v:F2}");
        }

        if (Directory.GetFiles(OutDir, "*.mkv").Length > 0) problems.Add("leftover MKV");
        Check(name, problems.Count == 0, string.Join("; ", problems));
    }

    private static async Task TestKillStorm()
    {
        const string name = "capture unavailable for ~6s (restart failures) -> resumes";
        Header(name);
        var settings = new AppSettings { RecordMicrophone = false, RecordSystemAudio = _systemAudio, OutputFolder = OutDir };
        using var rec = new RecordingService(_locator, _encoders, _monitors);
        await rec.StartAsync(settings, _primary);
        await Task.Delay(4000);

        var storm = Stopwatch.StartNew();
        var kills = 0;
        while (storm.Elapsed < TimeSpan.FromSeconds(6))
        {
            kills += KillOurFfmpeg();
            await Task.Delay(150);
        }

        Console.WriteLine($"  killed {kills} processes during storm; status: {rec.StatusMessage}");
        await Task.Delay(8000);
        var resumed = rec.State == RecordingState.Recording && rec.StatusMessage?.Contains("resumed", StringComparison.OrdinalIgnoreCase) == true;
        await rec.StopAsync();

        var problems = new List<string>();
        if (!resumed) problems.Add($"did not resume (state={rec.State})");
        if (rec.State != RecordingState.Idle) problems.Add($"final {rec.State}: {rec.LastError}");
        if (rec.OutputFiles.Count != 1) problems.Add($"{rec.OutputFiles.Count} outputs");
        if (rec.OutputPath is { } p && File.Exists(p))
        {
            var v = await StreamDuration(p, "v");
            // 4 s before the storm + 8 s after, minus FFmpeg start-up and at most one ~2 s retry wait.
            Console.WriteLine($"  {Path.GetFileName(p)} video={v:F1}s (expected 9-12s)");
            if (v < 9) problems.Add($"video only {v:F1}s");
        }

        if (Directory.GetFiles(OutDir, "*.mkv").Length > 0) problems.Add("leftover MKV");
        Check(name, problems.Count == 0, string.Join("; ", problems));
    }

    private static async Task TestStartStopCycles()
    {
        const string name = "10 rapid start/stop cycles";
        Header(name);
        var problems = new List<string>();
        using var rec = new RecordingService(_locator, _encoders, _monitors);
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await rec.StartAsync(new AppSettings { RecordMicrophone = true, RecordSystemAudio = _systemAudio, OutputFolder = OutDir }, _primary);
                await Task.Delay(1000 + i * 100);
                await rec.StopAsync();
                if (rec.State != RecordingState.Idle || rec.OutputPath is null || !File.Exists(rec.OutputPath))
                    problems.Add($"cycle {i}: {rec.State} {rec.LastError}");
            }
            catch (Exception ex)
            {
                problems.Add($"cycle {i}: {ex.Message}");
            }
        }

        var mp4s = Directory.GetFiles(OutDir, "*.mp4").Length;
        Console.WriteLine($"  mp4 files in output: {mp4s}");
        if (Directory.GetFiles(OutDir, "*.mkv").Length > 0) problems.Add("leftover MKV");
        if (Process.GetProcessesByName("ffmpeg").Any(IsOurFfmpeg)) problems.Add("orphan ffmpeg running");
        Check(name, problems.Count == 0, string.Join("; ", problems));
    }

    private static async Task TestRecovery()
    {
        const string name = "crash recovery (orphaned MKV -> MP4 on next start)";
        Header(name);
        var seg = Path.Combine(OutDir, "RecBox_crash_part01.mkv");
        // Simulate a crash: FFmpeg killed without finalizing the file.
        var psi = new ProcessStartInfo(_ffmpeg,
            $"-hide_banner -loglevel error -y -f gdigrab -framerate 30 -offset_x {_primary.BoundsX} -offset_y {_primary.BoundsY} -video_size {_primary.Width}x{_primary.Height} -i desktop " +
            $"-c:v libx264 -preset veryfast -g 30 -live 1 -cluster_time_limit 250 -flush_packets 1 -f matroska \"{seg}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        using (var p = Process.Start(psi)!)
        {
            await Task.Delay(5000);
            p.Kill();
            await p.WaitForExitAsync();
        }

        SessionStore.Save(new SessionManifest
        {
            Id = "crashtest",
            FinalPath = Path.Combine(OutDir, "RecBox_crash.mp4"),
            StartedUtc = DateTime.UtcNow,
            OwnerProcessId = 0,
            Segments = [new SegmentRecord { Path = seg, EncoderCodec = "libx264", Width = _primary.Width, Height = _primary.Height, Seconds = 4 }]
        });

        using var rec = new RecordingService(_locator, _encoders, _monitors);
        var result = await rec.RecoverOrphanedSessionsAsync();
        var problems = new List<string>();
        var mp4 = Path.Combine(OutDir, "RecBox_crash.mp4");
        if (!result.Outputs.Contains(mp4) || !File.Exists(mp4)) problems.Add($"outputs: {string.Join(",", result.Outputs)}");
        if (File.Exists(seg)) problems.Add("MKV not cleaned up after verified MP4");
        if (SessionStore.LoadOrphans().Count > 0) problems.Add("manifest still present");
        if (File.Exists(mp4))
        {
            var v = await StreamDuration(mp4, "v");
            Console.WriteLine($"  recovered {Path.GetFileName(mp4)} video={v:F1}s");
            // 5 s of FFmpeg minus start-up (~1 s) and x264's lookahead (~0.7 s, lost on a hard kill).
            if (v < 3.0) problems.Add($"recovered only {v:F1}s");
        }

        Check(name, problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>Flashes the screen white while playing a tone through the speakers, then measures the A/V offset in the file.</summary>
    private static async Task TestSync(string name, int seconds, int[] eventsAt, bool micToo = false)
    {
        Header($"{name} (A/V sync via screen flash + tone)");
        using var flash = new FlashWindow(_primary);
        using var tone = new TonePlayer();
        flash.Show();
        await Task.Delay(800);

        var settings = new AppSettings { RecordMicrophone = micToo, RecordSystemAudio = true, OutputFolder = OutDir };
        using var rec = new RecordingService(_locator, _encoders, _monitors);
        await rec.StartAsync(settings, _primary);
        var sw = Stopwatch.StartNew();
        foreach (var t in eventsAt)
        {
            var wait = TimeSpan.FromSeconds(t) - sw.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            flash.Flash(() => tone.Beep(300));
            await Task.Delay(500);
            flash.Black();
        }

        var rest = TimeSpan.FromSeconds(seconds) - sw.Elapsed;
        if (rest > TimeSpan.Zero) await Task.Delay(rest);
        // A beep right before Stop must survive: proves the end of the audio is not cut off.
        tone.Beep(250);
        await Task.Delay(150);
        await rec.StopAsync();
        flash.Close();

        var path = rec.OutputPath;
        if (path is null || !File.Exists(path))
        {
            Fail(name, $"no output: {rec.LastError}");
            return;
        }

        var v = await StreamDuration(path, "v");
        var a = await StreamDuration(path, "a");
        var videoOnsets = await VideoOnsets(path);
        var audioOnsets = await AudioOnsets(path);
        Console.WriteLine($"  video={v:F2}s audio={a:F2}s  [{rec.LastAudioStats}]");
        Console.WriteLine($"  flashes at: {string.Join(", ", videoOnsets.Select(x => x.ToString("F3")))}");
        Console.WriteLine($"  tones at:   {string.Join(", ", audioOnsets.Select(x => x.ToString("F3")))}");

        var problems = new List<string>();
        if ((v < seconds - 0.5 || v > seconds + 3.5)) problems.Add($"video {v:F1}s vs {seconds}s");
        if (a - v > 0.2 || v - a > 0.6) problems.Add($"audio {a:F2}s vs video {v:F2}s");
        var offsets = new List<double>();
        foreach (var vo in videoOnsets)
        {
            var match = audioOnsets.Where(ao => Math.Abs(ao - vo) < 1.0).OrderBy(ao => Math.Abs(ao - vo)).FirstOrDefault(double.NaN);
            if (double.IsNaN(match)) { problems.Add($"no tone near flash at {vo:F2}s"); continue; }
            offsets.Add(match - vo);
        }

        if (videoOnsets.Count != eventsAt.Length) problems.Add($"detected {videoOnsets.Count}/{eventsAt.Length} flashes");
        var finalBeep = audioOnsets.Where(t => t > videoOnsets.LastOrDefault() + 1).ToList();
        if (!finalBeep.Any(t => t < a - 0.05)) problems.Add("beep right before Stop is missing (audio tail cut)");
        else Console.WriteLine($"  final beep (150 ms before Stop) present at {finalBeep.First():F3}s, audio ends {a:F3}s");
        if (offsets.Count > 0)
        {
            Console.WriteLine($"  audio-minus-video offsets: {string.Join(", ", offsets.Select(o => $"{o * 1000:+0;-0}ms"))}  drift={(offsets[^1] - offsets[0]) * 1000:+0;-0}ms");
            if (offsets.Any(o => Math.Abs(o) > 0.15)) problems.Add("A/V offset above 150 ms");
            if (Math.Abs(offsets[^1] - offsets[0]) > 0.08) problems.Add("A/V drift above 80 ms");
        }

        Check(name, problems.Count == 0, string.Join("; ", problems));
    }

    // ---------- helpers ----------

    private static int KillOurFfmpeg()
    {
        var n = 0;
        foreach (var p in Process.GetProcessesByName("ffmpeg"))
        {
            if (!IsOurFfmpeg(p)) continue;
            try { p.Kill(); n++; } catch { /* already gone */ }
        }

        return n;
    }

    private static bool IsOurFfmpeg(Process p)
    {
        try { return string.Equals(p.MainModule?.FileName, _ffmpeg, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static async Task<string> Run(string args)
        => (await FFmpegLocator.RunProcessWithExitCodeAsync(_ffmpeg, args, 600_000)).Output;

    private static async Task<double> StreamDuration(string path, string type)
    {
        var output = await Run($"-hide_banner -i \"{path}\" -map 0:{type}:0 -c copy -f null -");
        var matches = Regex.Matches(output, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (matches.Count == 0) return 0;
        var m = matches[^1];
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
               double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<(int Width, int Height, bool HasAudio)> StreamInfo(string path)
    {
        var output = await Run($"-hide_banner -i \"{path}\"");
        var video = Regex.Match(output, @"Video:.*?(\d{3,5})x(\d{3,5})");
        return (video.Success ? int.Parse(video.Groups[1].Value) : 0,
            video.Success ? int.Parse(video.Groups[2].Value) : 0,
            output.Contains("Audio:", StringComparison.Ordinal));
    }

    private static async Task<List<double>> VideoOnsets(string path)
    {
        var output = await Run($"-hide_banner -i \"{path}\" -an -vf \"crop=280:200:0:0,signalstats,metadata=print:key=lavfi.signalstats.YAVG\" -f null -");
        var onsets = new List<double>();
        double? pts = null;
        var bright = false;
        foreach (var line in output.Split('\n'))
        {
            var t = Regex.Match(line, @"pts_time:([\d.]+)");
            if (t.Success) { pts = double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            var y = Regex.Match(line, @"YAVG=([\d.]+)");
            if (!y.Success || pts is null) continue;
            var isBright = double.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture) > 128;
            if (isBright && !bright) onsets.Add(pts.Value);
            bright = isBright;
        }

        return onsets;
    }

    private static async Task<List<double>> AudioOnsets(string path)
    {
        var output = await Run($"-hide_banner -i \"{path}\" -vn -af \"highpass=f=800,lowpass=f=1200,silencedetect=noise=-30dB:d=0.2\" -f null -");
        return Regex.Matches(output, @"silence_end:\s*([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static void Header(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {name} ---");
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { Passes.Add(name); Console.WriteLine("  PASS"); }
        else Fail(name, detail);
    }

    private static void Fail(string name, string detail)
    {
        Failures.Add($"{name}: {detail}");
        Console.WriteLine($"  FAIL: {detail}");
    }

    /// <summary>Full-screen black window that can flash white, on its own UI thread.</summary>
    private sealed class FlashWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private WinForms.Form? _form;

        public FlashWindow(MonitorInfo monitor)
        {
            _thread = new Thread(() =>
            {
                var screen = WinForms.Screen.AllScreens.First(s => s.DeviceName == monitor.DeviceName);
                _form = new WinForms.Form
                {
                    FormBorderStyle = WinForms.FormBorderStyle.None,
                    StartPosition = WinForms.FormStartPosition.Manual,
                    Bounds = new System.Drawing.Rectangle(screen.Bounds.X, screen.Bounds.Y, 320, 240),
                    TopMost = true,
                    ShowInTaskbar = false,
                    BackColor = System.Drawing.Color.Black
                };
                _form.Load += (_, _) => _ready.Set();
                WinForms.Application.Run(_form);
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
        }

        public void Show()
        {
            _thread.Start();
            _ready.Wait(5000);
        }

        public void Flash(Action atSameMoment) => _form?.Invoke(() =>
        {
            _form.TopMost = false;
            _form.TopMost = true;
            _form.BringToFront();
            _form.BackColor = System.Drawing.Color.White;
            _form.Refresh();
            atSameMoment();
        });

        public void Black() => _form?.Invoke(() =>
        {
            _form.BackColor = System.Drawing.Color.Black;
            _form.Refresh();
        });

        public void Close()
        {
            try { _form?.Invoke(() => _form.Close()); } catch { /* closed */ }
        }

        public void Dispose() => Close();
    }

    /// <summary>Low-latency WASAPI output that emits a 1 kHz tone on demand.</summary>
    private sealed class TonePlayer : IDisposable
    {
        private readonly WasapiOut _out;
        private readonly ToneProvider _tone;

        public TonePlayer()
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
            _tone = new ToneProvider(mix.SampleRate, mix.Channels);
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
            _out.Init(_tone);
            _out.Play();
        }

        public void Beep(int ms) => _tone.Trigger(ms);

        public void Dispose()
        {
            _out.Stop();
            _out.Dispose();
        }

        private sealed class ToneProvider(int rate, int channels) : ISampleProvider
        {
            private int _remaining;
            private long _phase;

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

            public void Trigger(int ms) => Interlocked.Exchange(ref _remaining, rate * ms / 1000);

            public int Read(float[] buffer, int offset, int count)
            {
                for (var i = 0; i < count; i += channels)
                {
                    var on = Interlocked.CompareExchange(ref _remaining, 0, 0) > 0;
                    var v = on ? (float)(0.2 * Math.Sin(2 * Math.PI * 1000 * _phase++ / rate)) : 0f;
                    if (on) Interlocked.Decrement(ref _remaining);
                    for (var c = 0; c < channels && i + c < count; c++)
                        buffer[offset + i + c] = v;
                }

                return count;
            }
        }
    }
}
