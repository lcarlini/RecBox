using System.Diagnostics;
using System.IO;

namespace RecBox.Services;

public sealed class FFmpegLocator
{
    private string? _cachedPath;

    public string? Find()
    {
        if (!string.IsNullOrEmpty(_cachedPath) && File.Exists(_cachedPath))
            return _cachedPath;

        foreach (var candidate in GetCandidates())
        {
            if (File.Exists(candidate))
            {
                _cachedPath = candidate;
                return candidate;
            }
        }

        var fromPath = FindOnPath("ffmpeg.exe");
        if (fromPath is not null)
        {
            _cachedPath = fromPath;
            return fromPath;
        }

        return null;
    }

    public bool IsAvailable() => Find() is not null;

    private static IEnumerable<string> GetCandidates()
    {
        var baseDir = AppContext.BaseDirectory;

        yield return Path.Combine(baseDir, "ffmpeg.exe");
        yield return Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe");

        // Dev layout: repo root / tools / ffmpeg
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", "ffmpeg.exe");
            dir = dir.Parent;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(segment.Trim('"'), fileName);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // ignore invalid PATH segments
            }
        }

        return null;
    }

    public static async Task<string> RunProcessAsync(string exePath, string arguments, int timeoutMs = 15000)
        => (await RunProcessWithExitCodeAsync(exePath, arguments, timeoutMs)).Output;

    public static async Task<(int ExitCode, string Output)> RunProcessWithExitCodeAsync(string exePath, string arguments, int timeoutMs = 15000)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        ChildProcessJob.Assign(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException("FFmpeg timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }
}
