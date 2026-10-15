using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RecBox.Models;

namespace RecBox.Services;

/// <summary>
/// Records a monitor with FFmpeg (gdigrab) into crash-safe MKV segments.
/// If FFmpeg stops for any reason while recording (secure desktop, lock screen, driver hiccup,
/// crash, hang) a new segment is started automatically; on stop all segments are joined into one
/// MP4 and verified before any MKV is deleted.
/// </summary>
public sealed class RecordingService : IDisposable
{
    public const int FrameRate = 30;

    private const long MinFreeBytesToStart = 1024L * 1024 * 1024;
    private const long MinFreeBytesWhileRecording = 400L * 1024 * 1024;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(12);
    // gdigrab delivers ~30 frames/s; a few seconds without one means the capture or encoder froze.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);
    // NVIDIA Broadcast can take ~10 s to warm up; opening it while FFmpeg runs can freeze the video.
    private static readonly TimeSpan AudioOpenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(20);

    private readonly FFmpegLocator _locator;
    private readonly EncoderSelector _encoders;
    private readonly MonitorService _monitors;
    private readonly object _sync = new();

    private Session? _session;
    private Task? _supervisor;
    private volatile bool _suspended;
    private volatile bool _starting;
    private int _lastInterruptions;

    public RecordingService(FFmpegLocator locator, EncoderSelector encoders, MonitorService? monitors = null)
    {
        _locator = locator;
        _encoders = encoders;
        _monitors = monitors ?? new MonitorService();
    }

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public string? OutputPath { get; private set; }
    public IReadOnlyList<string> OutputFiles { get; private set; } = [];
    public string? LastError { get; private set; }
    public string? LastWarning { get; private set; }

    /// <summary>Live note while recording (e.g. capture interrupted and resuming).</summary>
    public string? StatusMessage { get; private set; }

    public string? LastArguments { get; private set; }
    public string? LastAudioStats { get; private set; }
    public int SegmentCount => _session?.Manifest.Segments.Count ?? 0;
    public int Interruptions => _session?.Interruptions ?? _lastInterruptions;
    public string EncoderDisplayName => (_session?.Encoder ?? _encoders.Selected).DisplayName;
    public MonitorInfo? RecordingMonitor => _session?.Monitor;

    public TimeSpan Elapsed
    {
        get
        {
            var s = _session;
            return s is not null && State is RecordingState.Recording or RecordingState.Stopping
                ? DateTime.UtcNow - s.StartedUtc
                : TimeSpan.Zero;
        }
    }

    public event EventHandler? StateChanged;

    public Task EnsureReadyAsync() => _encoders.ProbeAsync();

    public async Task StartAsync(AppSettings settings, MonitorInfo monitor)
    {
        lock (_sync)
        {
            if (_starting || State is RecordingState.Recording or RecordingState.Stopping || _supervisor is { IsCompleted: false })
                throw new InvalidOperationException("A recording is already in progress.");
            _starting = true;
        }

        LastError = null;
        LastWarning = null;
        StatusMessage = null;
        OutputPath = null;
        OutputFiles = [];
        _lastInterruptions = 0;

        Session? session = null;
        try
        {
            var ffmpeg = _locator.Find()
                         ?? throw new InvalidOperationException("FFmpeg was not found. Run run.cmd once, or place ffmpeg.exe in tools\\ffmpeg.");

            await EnsureReadyAsync();

            var folder = PrepareOutputFolder(settings.OutputFolder);
            var free = NativeMethods.GetFreeBytes(folder);
            if (free >= 0 && free < MinFreeBytesToStart)
                throw new InvalidOperationException(
                    $"Not enough free disk space in {folder} ({free / 1024 / 1024} MB free). Free up at least 1 GB or choose another folder.");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var finalPath = RecordingFinalizer.UniquePath(Path.Combine(folder, $"RecBox_{stamp}.mp4"));
            session = new Session
            {
                Ffmpeg = ffmpeg,
                Settings = settings.Clone(),
                Monitor = monitor,
                Folder = folder,
                BaseName = Path.GetFileNameWithoutExtension(finalPath),
                Encoder = _encoders.Selected,
                StartedUtc = DateTime.UtcNow,
                Manifest = new SessionManifest
                {
                    Id = $"{stamp}_{Guid.NewGuid():N}",
                    FinalPath = finalPath,
                    StartedUtc = DateTime.UtcNow,
                    OwnerProcessId = Environment.ProcessId
                }
            };

            AppLog.Info($"Start: monitor {monitor.DeviceName} {monitor.Width}x{monitor.Height}@({monitor.BoundsX},{monitor.BoundsY}), " +
                        $"mic={settings.RecordMicrophone} ({settings.MicrophoneId}), system={settings.RecordSystemAudio}, " +
                        $"preset={settings.QualityPreset}, encoders={string.Join("/", _encoders.Available.Select(e => e.CodecName))}");

            if (settings.RecordMicrophone || settings.RecordSystemAudio)
            {
                session.Audio = new AudioCapturePipeline();
                await session.Audio.StartAsync(settings, AudioOpenTimeout);
            }

            SessionStore.Save(session.Manifest);

            var (segment, error) = await StartSegmentAsync(session, isFirst: true);
            if (segment is null)
                throw new InvalidOperationException($"Failed to start recording: {error}");

            if (session.Audio is not null)
            {
                foreach (var slow in session.Audio.PendingSources)
                    session.Warnings.Add($"{slow} was slow to respond when recording started; the first seconds of it may be silent.");
            }

            session.Current = segment;
            _session = session;
            State = RecordingState.Recording;
            _supervisor = Task.Run(() => SuperviseAsync(session));
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            AppLog.Error("Start failed", ex);
            if (session is not null)
            {
                await DisposeAudioAsync(session.Audio);
                SessionStore.Delete(session.Manifest.Id);
            }

            _session = null;
            LastError = ex is InvalidOperationException ? ex.Message : $"Failed to start recording: {ex.Message}";
            State = RecordingState.Error;
            RaiseStateChanged();
            throw new InvalidOperationException(LastError, ex);
        }
        finally
        {
            _starting = false;
        }
    }

    public async Task StopAsync()
    {
        var session = _session;
        var supervisor = _supervisor;
        if (session is null || supervisor is null)
            return;

        AppLog.Info("Stop requested.");
        if (State == RecordingState.Recording)
        {
            State = RecordingState.Stopping;
            RaiseStateChanged();
        }

        session.Stop.Cancel();
        await supervisor;
    }

    /// <summary>Display layout changed: restart the segment if the recorded monitor moved/resized.</summary>
    public void NotifyDisplaySettingsChanged()
    {
        var session = _session;
        if (session is null || State != RecordingState.Recording)
            return;

        var current = _monitors.Find(session.Monitor.DeviceName, session.Monitor.Index);
        if (current is null || !current.SameGeometry(session.Monitor) ||
            !string.Equals(current.DeviceName, session.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("Display settings changed for the recorded monitor; restarting capture segment.");
            session.Interrupt(SegmentOutcome.Restart);
        }
    }

    public void NotifySuspend()
    {
        if (_session is null)
            return;
        AppLog.Info("System suspending; closing current segment.");
        _suspended = true;
        _session.Interrupt(SegmentOutcome.Restart);
    }

    public void NotifyResume()
    {
        if (!_suspended)
            return;
        AppLog.Info("System resumed.");
        _suspended = false;
    }

    /// <summary>
    /// Synchronous best-effort stop for Windows logoff/shutdown or process exit: closes FFmpeg so the
    /// MKV is finalized. The session manifest stays on disk and is converted on next start.
    /// </summary>
    public void EmergencyStop()
    {
        var session = _session;
        if (session is null)
            return;

        AppLog.Warn("Emergency stop (shutdown/exit).");
        session.Stop.Cancel();
        var seg = session.Current;
        if (seg is null)
            return;

        try
        {
            if (!seg.Process.HasExited)
            {
                try
                {
                    seg.Process.StandardInput.Write('q');
                    seg.Process.StandardInput.Flush();
                }
                catch
                {
                    // ignore
                }

                if (!seg.Process.WaitForExit(5000))
                    seg.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }

        UpdateSegmentRecord(session, seg);
        SessionStore.Save(session.Manifest);
    }

    /// <summary>Converts recordings left behind by a crash or power loss.</summary>
    public async Task<FinalizeResult> RecoverOrphanedSessionsAsync()
    {
        var combined = new FinalizeResult();
        foreach (var manifest in SessionStore.LoadOrphans(_session?.Manifest.Id))
        {
            try
            {
                AppLog.Info($"Recovering session {manifest.Id} ({manifest.Segments.Count} segment(s)).");
                var result = await RecordingFinalizer.FinalizeAsync(_locator.Find(), manifest);
                combined.Outputs.AddRange(result.Outputs);
                combined.Warnings.AddRange(result.Warnings);
                SessionStore.Delete(manifest.Id);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Recovery of {manifest.Id} failed", ex);
            }
        }

        return combined;
    }

    private async Task SuperviseAsync(Session s)
    {
        // Short backoff: when the screen comes back (unlock, UAC closed) recording resumes within ~2 s.
        var backoff = TimeSpan.FromSeconds(0.5);
        var nextDiskCheck = DateTime.UtcNow;
        string? stopWarning = null;

        try
        {
            while (true)
            {
                var seg = s.Current;
                if (seg is not null)
                {
                    var outcome = await WatchSegmentAsync(s, seg);
                    AppLog.Info($"Segment {seg.Number} outcome: {outcome}");
                    var graceful = outcome is SegmentOutcome.Stop or SegmentOutcome.Restart or SegmentOutcome.DiskLow;
                    await EndSegmentAsync(s, seg, graceful);
                    s.Current = null;

                    if (outcome == SegmentOutcome.Stop)
                        break;

                    if (outcome == SegmentOutcome.DiskLow)
                    {
                        stopWarning = "Recording stopped automatically because the disk is almost full. Your video was saved.";
                        break;
                    }

                    if (outcome is SegmentOutcome.Exited or SegmentOutcome.Stalled)
                    {
                        s.Interruptions++;
                        var why = SanitizeFfmpegMessage(seg.StderrText());
                        AppLog.Warn($"Segment {seg.Number} ended unexpectedly ({outcome}, exit {SafeExitCode(seg.Process)}): {why}");
                        SetStatus("Screen capture was interrupted — resuming automatically. Nothing recorded so far is lost.");
                    }
                }

                if (s.Stop.IsCancellationRequested)
                    break;

                if (_suspended)
                {
                    SetStatus("Paused while the PC is asleep — will resume automatically.");
                    while (_suspended && !s.Stop.IsCancellationRequested)
                        await DelayAsync(TimeSpan.FromMilliseconds(500), s.Stop.Token);
                    continue;
                }

                if (DateTime.UtcNow >= nextDiskCheck)
                {
                    nextDiskCheck = DateTime.UtcNow.AddSeconds(5);
                    if (IsDiskLow(s.Folder))
                    {
                        stopWarning = "Recording stopped automatically because the disk is almost full. Your video was saved.";
                        break;
                    }
                }

                RefreshMonitor(s);

                var (next, error) = await StartSegmentAsync(s, isFirst: false);
                if (next is null)
                {
                    AppLog.Warn($"Segment restart failed, retrying in {backoff.TotalSeconds:F0}s: {error}");
                    SetStatus("Waiting for the screen to become available (e.g. lock screen or admin prompt) — recording resumes automatically.");
                    await DelayAsync(backoff, s.Stop.Token);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 2));
                    continue;
                }

                backoff = TimeSpan.FromSeconds(0.5);
                s.Current = next;
                SetStatus($"Recording resumed after an interruption ({s.Interruptions}×). Everything is being saved.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Supervisor failure", ex);
        }
        finally
        {
            if (s.Current is not null)
            {
                try { await EndSegmentAsync(s, s.Current, graceful: true); } catch (Exception ex) { AppLog.Error("End segment", ex); }
                s.Current = null;
            }

            if (s.Audio is not null)
            {
                LastAudioStats = s.Audio.Stats;
                AppLog.Info($"Audio: {LastAudioStats}");
            }

            await DisposeAudioAsync(s.Audio);

            if (State != RecordingState.Stopping)
            {
                State = RecordingState.Stopping;
                RaiseStateChanged();
            }

            await FinalizeSessionAsync(s, stopWarning);
        }
    }

    private async Task FinalizeSessionAsync(Session s, string? stopWarning)
    {
        StatusMessage = null;
        var warnings = new List<string>();
        if (stopWarning is not null)
            warnings.Add(stopWarning);
        if (s.Interruptions > 0)
            warnings.Add($"Screen capture was interrupted {s.Interruptions}× and resumed automatically; the video was joined into one file.");
        warnings.AddRange(s.Warnings);

        try
        {
            var result = await RecordingFinalizer.FinalizeAsync(s.Ffmpeg, s.Manifest);
            warnings.AddRange(result.Warnings);
            OutputFiles = result.Outputs;
            OutputPath = result.Outputs.FirstOrDefault();
            SessionStore.Delete(s.Manifest.Id);

            LastWarning = warnings.Count > 0 ? string.Join(" ", warnings.Distinct()) : null;
            if (OutputPath is null)
            {
                LastError = "No video was recorded. Check the log in %AppData%\\RecBox\\logs.";
                State = RecordingState.Error;
            }
            else
            {
                State = RecordingState.Idle;
            }
        }
        catch (Exception ex)
        {
            // Manifest stays on disk; the next start recovers the files.
            AppLog.Error("Finalize failed", ex);
            OutputPath = s.Manifest.Segments.Select(x => x.Path).FirstOrDefault(File.Exists);
            LastWarning = "Your recording is saved as MKV in the output folder; it will be converted to MP4 the next time RecBox starts.";
            State = OutputPath is null ? RecordingState.Error : RecordingState.Idle;
            LastError = OutputPath is null ? ex.Message : null;
        }
        finally
        {
            _lastInterruptions = s.Interruptions;
            _session = null;
            RaiseStateChanged();
        }
    }

    private async Task<SegmentOutcome> WatchSegmentAsync(Session s, RunningSegment seg)
    {
        var exitTask = seg.Process.WaitForExitAsync();
        var stopTask = Task.Delay(Timeout.Infinite, s.Stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        var interruptTask = s.ResetInterrupt();
        var nextSave = DateTime.UtcNow.AddSeconds(5);
        var nextDiskCheck = DateTime.UtcNow.AddSeconds(5);

        while (true)
        {
            await Task.WhenAny(exitTask, stopTask, interruptTask, Task.Delay(1000));

            if (s.Stop.IsCancellationRequested)
                return SegmentOutcome.Stop;
            if (exitTask.IsCompleted)
                return SegmentOutcome.Exited;
            if (interruptTask.IsCompleted)
                return interruptTask.Result;

            var now = DateTime.UtcNow;
            if (now - seg.LastProgressUtc > StallTimeout)
                return SegmentOutcome.Stalled;

            if (now >= nextSave)
            {
                nextSave = now.AddSeconds(5);
                UpdateSegmentRecord(s, seg);
                SessionStore.Save(s.Manifest);
            }

            if (now >= nextDiskCheck)
            {
                nextDiskCheck = now.AddSeconds(5);
                if (IsDiskLow(s.Folder))
                    return SegmentOutcome.DiskLow;
            }
        }
    }

    private async Task<(RunningSegment? Segment, string Error)> StartSegmentAsync(Session s, bool isFirst)
    {
        var error = "Unknown error";
        foreach (var encoder in _encoders.FallbackChain(s.Encoder))
        {
            if (!isFirst && s.Stop.IsCancellationRequested)
                break;

            var (segment, err) = await TryLaunchAsync(s, encoder);
            if (segment is not null)
            {
                if (encoder.CodecName != s.Encoder.CodecName)
                {
                    AppLog.Warn($"Switched encoder {s.Encoder.CodecName} -> {encoder.CodecName}");
                    if (!isFirst)
                        s.Warnings.Add($"Switched to {encoder.DisplayName} after the {s.Encoder.DisplayName} encoder failed.");
                    s.Encoder = encoder;
                }

                return (segment, string.Empty);
            }

            error = err;
            AppLog.Warn($"Launch with {encoder.CodecName} failed: {err}");

            // Capture problems (lock screen, UAC prompt) are not fixed by another encoder.
            if (!isFirst && !LooksLikeEncoderError(err))
                break;
        }

        return (null, error);
    }

    private async Task<(RunningSegment? Segment, string Error)> TryLaunchAsync(Session s, EncoderInfo encoder)
    {
        var number = s.NextPartNumber++;
        var path = Path.Combine(EnsureWritableFolder(s), $"{s.BaseName}_part{number:00}.mkv");
        var epochUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pipePath = s.Audio?.BeginSegment(epochUnixMs);
        var args = BuildArguments(s.Monitor, path, pipePath, encoder, s.Settings.QualityPreset, epochUnixMs);
        LastArguments = args;
        AppLog.Info($"Segment {number}: ffmpeg {args}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = s.Ffmpeg,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false)
            }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false");
        }
        catch (Exception ex)
        {
            process.Dispose();
            s.Audio?.EndSegment();
            s.NextPartNumber--;
            return (null, $"Could not start FFmpeg: {ex.Message}");
        }

        ChildProcessJob.Assign(process);
        try { process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { /* ignore */ }

        var seg = new RunningSegment(number, path, process, encoder, s.Monitor, pipePath is not null);
        seg.StartReaders();

        var ready = await Task.WhenAny(seg.FirstFrame.Task, process.WaitForExitAsync(), Task.Delay(StartupTimeout));
        if (ready == seg.FirstFrame.Task && !process.HasExited)
        {
            s.Manifest.Segments.Add(seg.Record);
            SessionStore.Save(s.Manifest);
            return (seg, string.Empty);
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
        }

        await seg.WaitReadersAsync();
        s.Audio?.EndSegment();
        var message = SanitizeFfmpegMessage(seg.StderrText());
        if (string.IsNullOrWhiteSpace(message))
            message = process.HasExited
                ? $"FFmpeg exited immediately (code {SafeExitCode(process)})."
                : "FFmpeg did not start capturing in time.";

        process.Dispose();
        TryDelete(path);
        s.NextPartNumber--;
        return (null, message);
    }

    private async Task EndSegmentAsync(Session s, RunningSegment seg, bool graceful)
    {
        var process = seg.Process;
        try
        {
            if (!process.HasExited && graceful)
            {
                // Finish the audio first: the writer delivers every sample up to this moment
                // (FFmpeg may be holding it back behind the video encoder), then sends EOF.
                // Only then stop the video, so the end of the recording keeps its sound.
                if (s.Audio is not null)
                {
                    await s.Audio.DrainSegmentAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(150);
                }

                try
                {
                    await process.StandardInput.WriteAsync('q');
                    await process.StandardInput.FlushAsync();
                }
                catch
                {
                    // FFmpeg may already be closing
                }

                await WaitForExitAsync(process, GracefulStopTimeout);
            }

            if (!process.HasExited)
            {
                AppLog.Warn($"Segment {seg.Number}: FFmpeg did not exit, killing it (MKV stays playable).");
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            await seg.WaitReadersAsync();
            s.Audio?.EndSegment();
            UpdateSegmentRecord(s, seg);

            if (!File.Exists(seg.Path) || new FileInfo(seg.Path).Length < RecordingFinalizer.MinUsefulBytes)
            {
                TryDelete(seg.Path);
                s.Manifest.Segments.Remove(seg.Record);
            }

            SessionStore.Save(s.Manifest);
            AppLog.Info($"Segment {seg.Number} closed: {seg.Record.Seconds:F1}s, {seg.Frames} frames, exit {SafeExitCode(process)}");
            try { process.Dispose(); } catch { /* ignore */ }
        }
    }

    private static void UpdateSegmentRecord(Session s, RunningSegment seg)
    {
        if (seg.OutTimeSeconds > seg.Record.Seconds)
            seg.Record.Seconds = seg.OutTimeSeconds;
    }

    private void RefreshMonitor(Session s)
    {
        var current = _monitors.Find(s.Monitor.DeviceName, s.Monitor.Index);
        if (current is null)
            return;

        if (!string.Equals(current.DeviceName, s.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            s.Warnings.Add($"The selected monitor was disconnected; recording continued on {current.DisplayName}.");
            AppLog.Warn($"Monitor {s.Monitor.DeviceName} gone; using {current.DeviceName}.");
        }

        s.Monitor = current;
    }

    private static string PrepareOutputFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            folder = SettingsStore.DefaultOutputFolder();

        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".recbox_write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return folder;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"RecBox cannot write to the output folder \"{folder}\" ({ex.Message}). Choose another folder.", ex);
        }
    }

    /// <summary>If the output drive disappears mid-recording, keep recording into the default folder.</summary>
    private static string EnsureWritableFolder(Session s)
    {
        try
        {
            return PrepareOutputFolder(s.Folder);
        }
        catch
        {
            var fallback = SettingsStore.DefaultOutputFolder();
            if (!string.Equals(fallback, s.Folder, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn($"Output folder {s.Folder} not writable; using {fallback}.");
                s.Warnings.Add($"The output folder became unavailable; the rest was saved in {fallback}.");
                s.Folder = PrepareOutputFolder(fallback);
                s.Manifest.FinalPath = RecordingFinalizer.UniquePath(Path.Combine(s.Folder, Path.GetFileName(s.Manifest.FinalPath)));
            }

            return s.Folder;
        }
    }

    private static bool IsDiskLow(string folder)
    {
        var free = NativeMethods.GetFreeBytes(folder);
        return free >= 0 && free < MinFreeBytesWhileRecording;
    }

    internal static string BuildArguments(MonitorInfo monitor, string outputPath, string? audioPipePath, EncoderInfo encoder, QualityPreset preset, long epochUnixMs)
    {
        var parts = new List<string> { "-hide_banner", "-nostats", "-loglevel", "warning", "-progress", "pipe:1", "-y" };
        var hasAudio = !string.IsNullOrEmpty(audioPipePath);

        // gdigrab stamps frames with wall-clock time (Unix µs). With -copyts the video is shifted so
        // that t=0 is the shared epoch, and the audio writer emits sample N for epoch + N/48000 s,
        // so both streams share one timeline. FFmpeg's default per-input zeroing would instead make
        // audio late by however long screen capture took to start.
        // The video is also delayed by the audio pipeline latency (capture packet + jitter buffer).
        var videoOffset = (AudioCapturePipeline.PipelineLatencySeconds - epochUnixMs / 1000.0)
            .ToString("F3", CultureInfo.InvariantCulture);
        parts.Add("-copyts");

        if (hasAudio)
        {
            parts.AddRange([
                "-f", "s16le",
                "-ar", AudioCapturePipeline.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", AudioCapturePipeline.Channels.ToString(CultureInfo.InvariantCulture),
                "-i", audioPipePath!
            ]);
        }

        parts.AddRange([
            "-itsoffset", videoOffset,
            "-f", "gdigrab",
            "-framerate", FrameRate.ToString(CultureInfo.InvariantCulture),
            "-offset_x", monitor.BoundsX.ToString(CultureInfo.InvariantCulture),
            "-offset_y", monitor.BoundsY.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{monitor.Width}x{monitor.Height}",
            "-draw_mouse", "1",
            "-i", "desktop"
        ]);

        parts.AddRange(["-map", $"{(hasAudio ? 1 : 0)}:v"]);
        if (hasAudio)
            parts.AddRange(["-map", "0:a", "-c:a", "aac", "-b:a", "192k"]);
        else
            parts.Add("-an");

        parts.AddRange(EncoderSelector.GetVideoEncodeArgs(encoder, preset));

        // Keyframe every second, streaming-style Matroska with 250 ms clusters flushed per packet:
        // if FFmpeg or the PC dies abruptly, only the last fraction of a second is lost.
        parts.AddRange([
            "-g", FrameRate.ToString(CultureInfo.InvariantCulture),
            "-max_muxing_queue_size", "4096",
            "-live", "1",
            "-cluster_time_limit", "250",
            "-flush_packets", "1",
            "-f", "matroska",
            outputPath
        ]);

        return string.Join(" ", parts.Select(QuoteArg));
    }

    private static string QuoteArg(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        return value.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '^' or '|' or '\\')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private static bool LooksLikeEncoderError(string message) =>
        Regex.IsMatch(message,
            "nvenc|qsv|amf|libx264|encoder|OpenEncodeSession|No capable devices|Cannot load|InitializeEncoder|mfx|CUDA",
            RegexOptions.IgnoreCase);

    internal static string SanitizeFfmpegMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = string.Join(" ", raw
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("frame=", StringComparison.OrdinalIgnoreCase)));
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (text.Contains("Failed to capture image", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("BitBlt", StringComparison.OrdinalIgnoreCase))
            return "The screen could not be captured (lock screen or administrator prompt?).";

        return text.Length <= 400 ? text : text[^400..];
    }

    /// <summary>A hung Windows audio service must never keep the video from being saved.</summary>
    private static async Task DisposeAudioAsync(AudioCapturePipeline? audio)
    {
        if (audio is null)
            return;

        var dispose = Task.Run(audio.Dispose);
        if (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))) != dispose)
            AppLog.Warn("Audio devices did not close within 5 s; continuing to save the video.");
        else if (dispose.IsFaulted)
            AppLog.Error("Audio dispose", dispose.Exception);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token); } catch (OperationCanceledException) { /* stop requested */ }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "running"; }
        catch { return "?"; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private void SetStatus(string? message)
    {
        StatusMessage = message;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("StateChanged handler", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            EmergencyStop();
            if (_session?.Audio is { } audio)
                Task.Run(audio.Dispose).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore on dispose
        }
    }

    private enum SegmentOutcome
    {
        Stop,
        Exited,
        Stalled,
        Restart,
        DiskLow
    }

    private sealed class Session
    {
        private TaskCompletionSource<SegmentOutcome> _interrupt = NewInterrupt();

        public required string Ffmpeg { get; init; }
        public required AppSettings Settings { get; init; }
        public required MonitorInfo Monitor { get; set; }
        public required string Folder { get; set; }
        public required string BaseName { get; init; }
        public required EncoderInfo Encoder { get; set; }
        public required DateTime StartedUtc { get; init; }
        public required SessionManifest Manifest { get; init; }
        public AudioCapturePipeline? Audio { get; set; }
        public RunningSegment? Current { get; set; }
        public CancellationTokenSource Stop { get; } = new();
        public int NextPartNumber { get; set; } = 1;
        public int Interruptions { get; set; }
        public List<string> Warnings { get; } = new();

        public Task<SegmentOutcome> ResetInterrupt()
        {
            var tcs = NewInterrupt();
            Volatile.Write(ref _interrupt, tcs);
            return tcs.Task;
        }

        public void Interrupt(SegmentOutcome outcome) => Volatile.Read(ref _interrupt).TrySetResult(outcome);

        private static TaskCompletionSource<SegmentOutcome> NewInterrupt() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RunningSegment
    {
        private readonly StringBuilder _stderr = new();
        private Task? _stdoutTask;
        private Task? _stderrTask;

        public RunningSegment(int number, string path, Process process, EncoderInfo encoder, MonitorInfo monitor, bool hasAudio)
        {
            Number = number;
            Path = path;
            Process = process;
            Record = new SegmentRecord
            {
                Path = path,
                EncoderCodec = encoder.CodecName,
                Width = monitor.Width,
                Height = monitor.Height,
                HasAudio = hasAudio
            };
        }

        public int Number { get; }
        public string Path { get; }
        public Process Process { get; }
        public SegmentRecord Record { get; }
        public TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime LastProgressUtc { get; private set; } = DateTime.UtcNow;
        public long Frames { get; private set; }
        public double OutTimeSeconds { get; private set; }

        public void StartReaders()
        {
            _stdoutTask = Task.Run(ReadProgressAsync);
            _stderrTask = Task.Run(ReadStderrAsync);
        }

        public async Task WaitReadersAsync()
        {
            var tasks = new[] { _stdoutTask, _stderrTask }.Where(t => t is not null).Cast<Task>().ToArray();
            if (tasks.Length > 0)
                await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(3000));
        }

        public string StderrText()
        {
            lock (_stderr)
                return _stderr.ToString();
        }

        private async Task ReadProgressAsync()
        {
            try
            {
                var reader = Process.StandardOutput;
                long lastFrames = -1;
                while (await reader.ReadLineAsync() is { } line)
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = line[..eq];
                    var value = line[(eq + 1)..].Trim();

                    if (key == "frame" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
                    {
                        Frames = frames;
                        if (frames > lastFrames)
                        {
                            lastFrames = frames;
                            LastProgressUtc = DateTime.UtcNow;
                        }

                        if (frames > 0)
                            FirstFrame.TrySetResult();
                    }
                    else if (key == "out_time_us" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us > 0)
                    {
                        OutTimeSeconds = us / 1_000_000.0;
                    }                }
            }
            catch
            {
                // process ended
            }
        }

        private async Task ReadStderrAsync()
        {
            try
            {
                var reader = Process.StandardError;
                while (await reader.ReadLineAsync() is { } line)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(line);
                        if (_stderr.Length > 16_000)
                            _stderr.Remove(0, _stderr.Length - 8_000);
                    }
                }
            }
            catch
            {
                // process ended
            }
        }
    }
}
