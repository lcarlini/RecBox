using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RecBox.Models;
using RecBox.Services;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "RecBoxSmoke");
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "*.mp4"))
            File.Delete(old);

        var locator = new FFmpegLocator();
        var encoders = new EncoderSelector(locator);
        var monitors = new MonitorService();
        var audio = new AudioDeviceService();
        var settingsStore = new SettingsStore();

        Console.WriteLine("=== RecBox Smoke Test ===");
        Console.WriteLine($"STA: {Thread.CurrentThread.GetApartmentState()}");
        Console.WriteLine($"FFmpeg: {locator.Find() ?? "NOT FOUND"}");
        if (!locator.IsAvailable())
        {
            Console.WriteLine("FAIL: FFmpeg missing");
            return 1;
        }

        await encoders.ProbeAsync();
        Console.WriteLine($"Encoder: {encoders.Selected.DisplayName} ({encoders.Selected.CodecName})");
        Console.WriteLine($"ddagrab: {encoders.SupportsDdagrab}");

        var monitorList = monitors.GetMonitors();
        Console.WriteLine($"Monitors: {monitorList.Count}");
        foreach (var m in monitorList)
            Console.WriteLine($"  [{m.Index}] {m.DisplayName} @ ({m.BoundsX},{m.BoundsY})");

        var mics = audio.GetMicrophones();
        Console.WriteLine($"Mics: {mics.Count}");
        foreach (var mic in mics)
            Console.WriteLine($"  {mic.DisplayName}");

        var defaults = settingsStore.Load();
        Console.WriteLine($"Settings load OK. Output: {defaults.OutputFolder}");

        // Round-trip settings
        defaults.QualityPreset = QualityPreset.MaximumQuality;
        settingsStore.Save(defaults);
        var reloaded = settingsStore.Load();
        if (reloaded.QualityPreset != QualityPreset.MaximumQuality)
        {
            Console.WriteLine("FAIL: settings persistence");
            return 1;
        }
        Console.WriteLine("Settings persistence OK");

        var monitor = monitorList.FirstOrDefault(m => m.IsPrimary) ?? monitorList[0];
        var failures = new List<string>();

        async Task RunCase(string name, AppSettings settings, int seconds = 3)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {name} ---");
            settings.OutputFolder = outDir;
            using var recording = new RecordingService(locator, encoders);
            await recording.EnsureReadyAsync();

            try
            {
                await recording.StartAsync(settings, monitor);
                Console.WriteLine($"Started: {recording.OutputPath}");
                if (!string.IsNullOrEmpty(recording.LastArguments))
                    Console.WriteLine($"Args: {recording.LastArguments}");

                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await recording.StopAsync();
                Console.WriteLine($"Stopped. State={recording.State}");

                if (recording.State == RecordingState.Error)
                {
                    failures.Add($"{name}: error state — {recording.LastError}");
                    Console.WriteLine($"FAIL: {recording.LastError}");
                    return;
                }

                var path = recording.OutputPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    failures.Add($"{name}: output file missing");
                    Console.WriteLine("FAIL: no output file");
                    return;
                }

                var info = new FileInfo(path);
                Console.WriteLine($"File: {info.Name} ({info.Length:N0} bytes)");
                if (info.Length < 10_000)
                {
                    failures.Add($"{name}: file too small ({info.Length} bytes)");
                    Console.WriteLine("FAIL: file suspiciously small");
                    return;
                }

                var probe = await ProbeAsync(locator.Find()!, path);
                Console.WriteLine(probe.Summary);

                if (!probe.HasVideo)
                    failures.Add($"{name}: no video stream");
                if (settings.RecordMicrophone || settings.RecordSystemAudio)
                {
                    if (!probe.HasAudio)
                        failures.Add($"{name}: expected audio stream but none found");
                }

                if (probe.Width > 0 && probe.Height > 0)
                {
                    if (Math.Abs(probe.Width - monitor.Width) > 16 || Math.Abs(probe.Height - monitor.Height) > 16)
                        Console.WriteLine($"WARN: resolution {probe.Width}x{probe.Height} vs monitor {monitor.Width}x{monitor.Height}");
                    else
                        Console.WriteLine($"OK resolution {probe.Width}x{probe.Height}");
                }

                Console.WriteLine("PASS");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL: {ex.Message}");
            }
        }

        await RunCase("video-only / balanced", new AppSettings
        {
            RecordMicrophone = false,
            RecordSystemAudio = false,
            QualityPreset = QualityPreset.Balanced,
            MicrophoneId = "default"
        });

        var systemAudioOk = audio.IsSystemAudioAvailable();
        Console.WriteLine($"System audio loopback available: {systemAudioOk}");

        if (systemAudioOk)
        {
            await RunCase("system-audio only", new AppSettings
            {
                RecordMicrophone = false,
                RecordSystemAudio = true,
                QualityPreset = QualityPreset.LowFileSize,
                MicrophoneId = "default"
            });
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("--- system-audio only ---");
            Console.WriteLine("SKIP: no active Windows playback device in this session");
        }

        await RunCase("mic only", new AppSettings
        {
            RecordMicrophone = true,
            RecordSystemAudio = false,
            QualityPreset = QualityPreset.Balanced,
            MicrophoneId = "default"
        });

        if (systemAudioOk)
        {
            await RunCase("mic + system / max quality", new AppSettings
            {
                RecordMicrophone = true,
                RecordSystemAudio = true,
                QualityPreset = QualityPreset.MaximumQuality,
                MicrophoneId = "default"
            });
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("--- mic + system / max quality ---");
            Console.WriteLine("SKIP: no active Windows playback device in this session");
        }

        await RunCase("low file size preset", new AppSettings
        {
            RecordMicrophone = false,
            RecordSystemAudio = false,
            QualityPreset = QualityPreset.LowFileSize,
            MicrophoneId = "default"
        });

        // Second monitor if present
        if (monitorList.Count > 1)
        {
            var secondary = monitorList.First(m => !m.IsPrimary);
            Console.WriteLine();
            Console.WriteLine($"--- secondary monitor [{secondary.Index}] ---");
            var settings = new AppSettings
            {
                RecordMicrophone = false,
                RecordSystemAudio = false,
                QualityPreset = QualityPreset.Balanced,
                MicrophoneId = "default",
                OutputFolder = outDir,
                MonitorIndex = secondary.Index
            };
            using var recording = new RecordingService(locator, encoders);
            try
            {
                await recording.StartAsync(settings, secondary);
                await Task.Delay(2000);
                await recording.StopAsync();
                var probe = await ProbeAsync(locator.Find()!, recording.OutputPath!);
                Console.WriteLine(probe.Summary);
                if (!probe.HasVideo)
                    failures.Add("secondary monitor: no video");
                else
                    Console.WriteLine("PASS");
            }
            catch (Exception ex)
            {
                failures.Add($"secondary monitor: {ex.Message}");
                Console.WriteLine($"FAIL: {ex.Message}");
            }
        }

        // NVIDIA Broadcast device selection if present
        var broadcast = mics.FirstOrDefault(m =>
            m.Name.Contains("Broadcast", StringComparison.OrdinalIgnoreCase));
        if (broadcast is not null)
        {
            await RunCase("NVIDIA Broadcast mic", new AppSettings
            {
                RecordMicrophone = true,
                RecordSystemAudio = false,
                QualityPreset = QualityPreset.Balanced,
                MicrophoneId = broadcast.Id
            });
        }

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        if (failures.Count == 0)
        {
            Console.WriteLine("ALL PASS");
            return 0;
        }

        foreach (var f in failures)
            Console.WriteLine("FAIL: " + f);
        return 1;
    }

    private static async Task<ProbeResult> ProbeAsync(string ffmpeg, string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var useProbe = File.Exists(ffprobe);

        var psi = new ProcessStartInfo
        {
            FileName = useProbe ? ffprobe : ffmpeg,
            Arguments = useProbe
                ? $"-v quiet -print_format json -show_streams \"{path}\""
                : $"-i \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        var result = new ProbeResult();
        if (useProbe && !string.IsNullOrWhiteSpace(stdout))
        {
            using var doc = JsonDocument.Parse(stdout);
            foreach (var stream in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                var codecType = stream.GetProperty("codec_type").GetString();
                if (codecType == "video")
                {
                    result.HasVideo = true;
                    if (stream.TryGetProperty("width", out var w)) result.Width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h)) result.Height = h.GetInt32();
                    result.VideoCodec = stream.GetProperty("codec_name").GetString() ?? "";
                }
                else if (codecType == "audio")
                {
                    result.HasAudio = true;
                    result.AudioCodec = stream.GetProperty("codec_name").GetString() ?? "";
                }
            }
        }
        else
        {
            result.HasVideo = stderr.Contains("Video:", StringComparison.OrdinalIgnoreCase);
            result.HasAudio = stderr.Contains("Audio:", StringComparison.OrdinalIgnoreCase);
            var match = System.Text.RegularExpressions.Regex.Match(stderr, @"(\d{3,5})x(\d{3,5})");
            if (match.Success)
            {
                result.Width = int.Parse(match.Groups[1].Value);
                result.Height = int.Parse(match.Groups[2].Value);
            }
        }

        return result;
    }

    private sealed class ProbeResult
    {
        public bool HasVideo { get; set; }
        public bool HasAudio { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string VideoCodec { get; set; } = "";
        public string AudioCodec { get; set; } = "";
        public string Summary =>
            $"streams: video={(HasVideo ? VideoCodec : "none")} audio={(HasAudio ? AudioCodec : "none")} size={Width}x{Height}";
    }
}
