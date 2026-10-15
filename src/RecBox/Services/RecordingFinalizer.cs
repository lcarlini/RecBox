using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecBox.Services;

public sealed class SegmentRecord
{
    public string Path { get; set; } = string.Empty;
    public string EncoderCodec { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAudio { get; set; }
    public double Seconds { get; set; }
}

/// <summary>Persisted while recording so a crash / power loss can be recovered on next start.</summary>
public sealed class SessionManifest
{
    public string Id { get; set; } = string.Empty;
    public string FinalPath { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public int OwnerProcessId { get; set; }
    public List<SegmentRecord> Segments { get; set; } = new();
}

public static class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Lock = new();

    public static string Directory { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecBox", "sessions");

    public static void Save(SessionManifest manifest)
    {
        try
        {
            lock (Lock)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = PathFor(manifest.Id);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not save session manifest: {ex.Message}");
        }
    }

    public static void Delete(string id)
    {
        try
        {
            lock (Lock)
                File.Delete(PathFor(id));
        }
        catch
        {
            // ignore
        }
    }

    public static IReadOnlyList<SessionManifest> LoadOrphans(string? excludeId = null)
    {
        var list = new List<SessionManifest>();
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return list;

            foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(file), JsonOptions);
                    if (manifest is null || string.IsNullOrEmpty(manifest.Id) || manifest.Id == excludeId)
                        continue;
                    if (IsOwnerAlive(manifest.OwnerProcessId))
                        continue;
                    list.Add(manifest);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Unreadable session manifest {file}: {ex.Message}");
                }
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static bool IsOwnerAlive(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId)
            return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("RecBox", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string PathFor(string id) => System.IO.Path.Combine(Directory, id + ".json");
}

public sealed class FinalizeResult
{
    public List<string> Outputs { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Turns crash-safe MKV segments into MP4. Never deletes a source segment unless the MP4
/// was written completely and its duration was verified.
/// </summary>
public static class RecordingFinalizer
{
    public const long MinUsefulBytes = 8_000;

    public static async Task<FinalizeResult> FinalizeAsync(string? ffmpeg, SessionManifest manifest)
    {
        var result = new FinalizeResult();
        var segments = new List<SegmentRecord>();

        foreach (var seg in manifest.Segments)
        {
            if (!File.Exists(seg.Path))
                continue;
            if (new FileInfo(seg.Path).Length < MinUsefulBytes)
            {
                TryDelete(seg.Path);
                continue;
            }

            segments.Add(seg);
        }

        if (segments.Count == 0)
            return result;

        // Segments can only be joined losslessly if they share codec and resolution.
        var groups = new List<List<SegmentRecord>>();
        foreach (var seg in segments)
        {
            var last = groups.LastOrDefault();
            if (last is not null && Compatible(last[0], seg))
                last.Add(seg);
            else
                groups.Add([seg]);
        }

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var target = groups.Count == 1
                ? manifest.FinalPath
                : System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(manifest.FinalPath)!,
                    $"{System.IO.Path.GetFileNameWithoutExtension(manifest.FinalPath)}_part{g + 1}.mp4");
            target = UniquePath(target);

            var output = await FinalizeGroupAsync(ffmpeg, group, target, result.Warnings);
            result.Outputs.AddRange(output);
        }

        if (groups.Count > 1)
            result.Warnings.Add($"The recording was saved as {groups.Count} files because the capture settings changed mid-recording (monitor or encoder change).");

        return result;
    }

    private static async Task<List<string>> FinalizeGroupAsync(string? ffmpeg, List<SegmentRecord> group, string target, List<string> warnings)
    {
        var sources = group.Select(s => s.Path).ToList();
        var expected = 0.0;
        foreach (var seg in group)
            expected += seg.Seconds > 0 ? seg.Seconds : (ffmpeg is null ? 0 : await ProbeDurationAsync(ffmpeg, seg.Path) ?? 0);

        if (ffmpeg is null)
            return KeepMkv(group, target, warnings, "FFmpeg not found");

        var totalBytes = sources.Sum(p => new FileInfo(p).Length);
        var free = NativeMethods.GetFreeBytes(System.IO.Path.GetDirectoryName(target)!);
        if (free >= 0 && free < totalBytes + 100L * 1024 * 1024)
            return KeepMkv(group, target, warnings, "not enough free disk space to convert");

        var temp = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(target)!,
            System.IO.Path.GetFileNameWithoutExtension(target) + ".saving.mp4");
        TryDelete(temp);

        string? listFile = null;
        string args;
        if (sources.Count == 1)
        {
            args = $"-y -hide_banner -loglevel error -i \"{sources[0]}\" -map 0 -c copy -movflags +faststart -f mp4 \"{temp}\"";
        }
        else
        {
            listFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"recbox_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var path in sources)
                sb.Append("file '").Append(path.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
            await File.WriteAllTextAsync(listFile, sb.ToString(), new UTF8Encoding(false));
            args = $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{listFile}\" -map 0 -c copy -movflags +faststart -f mp4 \"{temp}\"";
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(120 + expected * 0.5);
            var (ok, error) = await RunAsync(ffmpeg, args, timeout);
            if (!ok)
            {
                TryDelete(temp);
                AppLog.Warn($"MP4 conversion failed: {error}");
                return KeepMkv(group, target, warnings, "MP4 conversion failed");
            }

            var actual = await ProbeDurationAsync(ffmpeg, temp);
            var minimum = Math.Max(0, expected * 0.9 - 2);
            if (actual is null || actual.Value <= 0 || actual.Value < minimum)
            {
                AppLog.Warn($"MP4 verification failed: duration {actual?.ToString("F1") ?? "?"}s, expected about {expected:F1}s");
                TryDelete(temp);
                return KeepMkv(group, target, warnings, "MP4 verification failed");
            }

            File.Move(temp, target, overwrite: false);
            foreach (var path in sources)
                TryDelete(path);

            AppLog.Info($"Saved {target} ({actual:F1}s from {sources.Count} segment(s), expected {expected:F1}s)");
            return [target];
        }
        finally
        {
            if (listFile is not null)
                TryDelete(listFile);
        }
    }

    private static List<string> KeepMkv(List<SegmentRecord> group, string target, List<string> warnings, string reason)
    {
        warnings.Add($"Saved as MKV ({reason}). The video is complete and playable in VLC or the Windows Media Player app.");

        if (group.Count != 1)
            return group.Select(g => g.Path).ToList();

        var mkv = UniquePath(System.IO.Path.ChangeExtension(target, ".mkv"));
        try
        {
            File.Move(group[0].Path, mkv);
            return [mkv];
        }
        catch
        {
            return [group[0].Path];
        }
    }

    private static bool Compatible(SegmentRecord a, SegmentRecord b) =>
        a.EncoderCodec == b.EncoderCodec && a.Width == b.Width && a.Height == b.Height && a.HasAudio == b.HasAudio;

    public static async Task<double?> ProbeDurationAsync(string ffmpeg, string path)
    {
        try
        {
            var output = await FFmpegLocator.RunProcessAsync(ffmpeg, $"-hide_banner -i \"{path}\"", 30_000);
            var match = Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!match.Success)
                return null;
            return int.Parse(match.Groups[1].Value) * 3600 +
                   int.Parse(match.Groups[2].Value) * 60 +
                   double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(bool Ok, string Error)> RunAsync(string ffmpeg, string args, TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            ChildProcessJob.Assign(process);
            var errTask = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "timed out");
            }

            var err = await errTask;
            return (process.ExitCode == 0, err.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
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
}
