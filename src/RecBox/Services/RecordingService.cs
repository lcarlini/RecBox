using System.Diagnostics;
using System.IO;
using System.Text;
using RecBox.Models;

namespace RecBox.Services;

public sealed class RecordingService : IDisposable
{
    private readonly FFmpegLocator _locator;
    private readonly EncoderSelector _encoderSelector;
    private readonly object _sync = new();

    private Process? _process;
    private AudioCapturePipeline? _audioPipeline;
    private StringBuilder _stderr = new();
    private CancellationTokenSource? _stderrCts;
    private DateTime _startedAt;
    private bool _useDdagrab = true;
    private string? _lastArgs;

    public RecordingService(
        FFmpegLocator locator,
        EncoderSelector encoderSelector)
    {
        _locator = locator;
        _encoderSelector = encoderSelector;
    }

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public string? OutputPath { get; private set; }
    public string? LastError { get; private set; }
    public string? LastArguments => _lastArgs;
    public string EncoderDisplayName => _encoderSelector.Selected.DisplayName;

    public TimeSpan Elapsed =>
        State is RecordingState.Recording or RecordingState.Stopping
            ? DateTime.UtcNow - _startedAt
            : TimeSpan.Zero;

    public event EventHandler? StateChanged;

    public async Task EnsureReadyAsync()
    {
        await _encoderSelector.ProbeAsync();
        _useDdagrab = _encoderSelector.SupportsDdagrab;
    }

    public async Task StartAsync(AppSettings settings, MonitorInfo monitor)
    {
        if (State is RecordingState.Recording or RecordingState.Stopping)
            throw new InvalidOperationException("A recording is already in progress.");

        var ffmpeg = _locator.Find();
        if (ffmpeg is null)
        {
            SetError("FFmpeg was not found. Place ffmpeg.exe in tools/ffmpeg or on your PATH. See the user guide for details.");
            throw new InvalidOperationException(LastError);
        }

        await EnsureReadyAsync();

        Directory.CreateDirectory(settings.OutputFolder);
        var fileName = $"RecBox_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        var outputPath = Path.Combine(settings.OutputFolder, fileName);

        LastError = null;
        var wantAudio = settings.RecordMicrophone || settings.RecordSystemAudio;

        // Start audio once (not part of GPU/GDI fallback).
        string? pipePath = null;
        if (wantAudio)
        {
            try
            {
                pipePath = StartAudioPipeline(settings);
            }
            catch (Exception ex)
            {
                CleanupAudio();
                SetError(ex.Message);
                throw;
            }
        }

        Exception? firstFailure = null;
        var tryDdagrab = _useDdagrab && _encoderSelector.SupportsDdagrab;

        if (tryDdagrab)
        {
            try
            {
                await StartProcessAsync(ffmpeg, BuildArguments(monitor, outputPath, useDdagrab: true, pipePath, settings.QualityPreset), outputPath);
                return;
            }
            catch (Exception ex)
            {
                firstFailure = ex;
                // Kill only FFmpeg; keep the audio pipe for the GDI fallback attempt.
                await AbortFfmpegOnlyAsync();
            }
        }

        try
        {
            await StartProcessAsync(ffmpeg, BuildArguments(monitor, outputPath, useDdagrab: false, pipePath, settings.QualityPreset), outputPath);
            _useDdagrab = false;
        }
        catch (Exception fallbackEx)
        {
            CleanupAudio();
            var detail = firstFailure is null
                ? fallbackEx.Message
                : $"{fallbackEx.Message} (GPU capture also failed: {Truncate(firstFailure.Message, 200)})";
            SetError($"Failed to start recording: {detail}");
            throw new InvalidOperationException(LastError, fallbackEx);
        }
    }

    private string StartAudioPipeline(AppSettings settings)
    {
        var pipeName = $"RecBoxAudio_{Environment.ProcessId}_{Guid.NewGuid():N}";
        _audioPipeline = new AudioCapturePipeline(pipeName);
        try
        {
            _audioPipeline.Start(settings);
        }
        catch (Exception ex)
        {
            CleanupAudio();
            throw new InvalidOperationException(
                $"Could not start audio capture: {ex.Message}",
                ex);
        }

        return _audioPipeline.PipePath;
    }

    private async Task StartProcessAsync(string ffmpeg, string arguments, string outputPath)
    {
        _lastArgs = arguments;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _stderr = new StringBuilder();
        _stderrCts = new CancellationTokenSource();

        if (!process.Start())
            throw new InvalidOperationException("Could not start FFmpeg.");

        _ = DrainStreamAsync(process.StandardOutput, _stderrCts.Token);
        _ = DrainStreamAsync(process.StandardError, _stderrCts.Token);

        await Task.Delay(1200);
        if (process.HasExited)
        {
            var err = _stderr.ToString().Trim();
            var message = string.IsNullOrWhiteSpace(err)
                ? $"FFmpeg exited immediately (code {process.ExitCode})."
                : Truncate(err, 800);
            try { process.Dispose(); } catch { /* ignore */ }
            _stderrCts?.Cancel();
            _stderrCts?.Dispose();
            _stderrCts = null;
            throw new InvalidOperationException(message);
        }

        lock (_sync)
        {
            _process = process;
            OutputPath = outputPath;
            _startedAt = DateTime.UtcNow;
            State = RecordingState.Recording;
        }

        process.Exited += OnProcessExited;
        RaiseStateChanged();
    }

    public async Task StopAsync()
    {
        if (State != RecordingState.Recording)
            return;

        SetState(RecordingState.Stopping);
        await StopInternalAsync(force: false);
    }

    private async Task AbortFfmpegOnlyAsync()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
            return;

        try
        {
            try { process.Exited -= OnProcessExited; } catch { /* ignore */ }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                await WaitForExitAsync(process, TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            try { process.Dispose(); } catch { /* ignore */ }
            State = RecordingState.Idle;
        }
    }

    private async Task StopInternalAsync(bool force)
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process is null)
        {
            CleanupAudio();
            SetState(RecordingState.Idle);
            return;
        }

        try
        {
            if (!force && !process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteAsync("q");
                    await process.StandardInput.FlushAsync();
                }
                catch
                {
                    force = true;
                }

                var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(10));
                if (!exited)
                    force = true;
            }

            if (force && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                await WaitForExitAsync(process, TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            CleanupAudio();
            CleanupProcess();
            SetState(RecordingState.Idle);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (State is RecordingState.Stopping or RecordingState.Idle)
            return;

        var err = _stderr.ToString().Trim();
        CleanupAudio();
        CleanupProcess();

        if (!string.IsNullOrWhiteSpace(err) &&
            (err.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
             err.Contains("Invalid", StringComparison.OrdinalIgnoreCase)))
        {
            SetError(Truncate(err, 800));
        }
        else
        {
            SetState(RecordingState.Idle);
        }
    }

    private void CleanupAudio()
    {
        try { _audioPipeline?.Stop(); } catch { /* ignore */ }
        try { _audioPipeline?.Dispose(); } catch { /* ignore */ }
        _audioPipeline = null;
    }

    private void CleanupProcess()
    {
        lock (_sync)
        {
            _stderrCts?.Cancel();
            _stderrCts?.Dispose();
            _stderrCts = null;

            if (_process is not null)
            {
                try { _process.Exited -= OnProcessExited; } catch { /* ignore */ }
                try { _process.Dispose(); } catch { /* ignore */ }
                _process = null;
            }
        }
    }

    private string BuildArguments(
        MonitorInfo monitor,
        string outputPath,
        bool useDdagrab,
        string? audioPipePath,
        QualityPreset preset)
    {
        var parts = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-stats" };
        var hasAudio = !string.IsNullOrEmpty(audioPipePath);

        if (useDdagrab)
        {
            var videoFilter =
                $"ddagrab=output_idx={monitor.Index}:framerate=60:draw_mouse=1,hwdownload,format=bgra[v]";

            if (hasAudio)
            {
                parts.AddRange([
                    "-f", "s16le",
                    "-ar", AudioCapturePipeline.SampleRate.ToString(),
                    "-ac", AudioCapturePipeline.Channels.ToString(),
                    "-i", audioPipePath!
                ]);
            }

            parts.AddRange([
                "-filter_complex", videoFilter,
                "-map", "[v]"
            ]);

            if (hasAudio)
                parts.AddRange(["-map", "0:a", "-c:a", "aac", "-b:a", "192k", "-async", "1"]);
            else
                parts.Add("-an");
        }
        else
        {
            if (hasAudio)
            {
                parts.AddRange([
                    "-f", "s16le",
                    "-ar", AudioCapturePipeline.SampleRate.ToString(),
                    "-ac", AudioCapturePipeline.Channels.ToString(),
                    "-i", audioPipePath!
                ]);
            }

            parts.AddRange([
                "-f", "gdigrab",
                "-framerate", "60",
                "-offset_x", monitor.BoundsX.ToString(),
                "-offset_y", monitor.BoundsY.ToString(),
                "-video_size", $"{monitor.Width}x{monitor.Height}",
                "-draw_mouse", "1",
                "-i", "desktop"
            ]);

            var videoIndex = hasAudio ? 1 : 0;
            parts.AddRange(["-map", $"{videoIndex}:v"]);

            if (hasAudio)
                parts.AddRange(["-map", "0:a", "-c:a", "aac", "-b:a", "192k", "-async", "1"]);
            else
                parts.Add("-an");
        }

        parts.AddRange(_encoderSelector.GetVideoEncodeArgs(preset, useHardwareFrames: false));
        parts.AddRange(["-movflags", "+faststart", outputPath]);

        return EscapeArgs(parts);
    }

    private static string EscapeArgs(IReadOnlyList<string> parts)
    {
        static string Escape(string value)
        {
            if (value.Length == 0)
                return "\"\"";

            if (value.StartsWith('[') && value.EndsWith(']'))
                return value;

            var needsQuotes =
                value.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '^' or '|') ||
                value.Contains('\\') ||
                (value.Contains('=') && value.Contains('['));

            if (!needsQuotes)
                return value;

            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        return string.Join(" ", parts.Select(Escape));
    }

    private async Task DrainStreamAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            var buffer = new char[1024];
            while (!token.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                    break;

                lock (_sync)
                {
                    _stderr.Append(buffer.AsSpan(0, read));
                    if (_stderr.Length > 32_000)
                        _stderr.Remove(0, _stderr.Length - 16_000);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch
        {
            // ignore stream errors on process exit
        }
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
    }

    private void SetState(RecordingState state)
    {
        State = state;
        RaiseStateChanged();
    }

    private void SetError(string message)
    {
        LastError = message;
        State = RecordingState.Error;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[^max..];

    public void Dispose()
    {
        try
        {
            StopInternalAsync(force: true).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore on dispose
        }
    }
}
