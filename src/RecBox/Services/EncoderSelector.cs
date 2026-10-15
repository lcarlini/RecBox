using RecBox.Models;

namespace RecBox.Services;

public enum VideoEncoderKind
{
    Nvenc,
    Qsv,
    Amf,
    Libx264
}

public sealed class EncoderInfo
{
    public VideoEncoderKind Kind { get; init; }
    public string CodecName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}

public sealed class EncoderSelector
{
    private static readonly EncoderInfo[] HardwareCandidates =
    [
        new() { Kind = VideoEncoderKind.Nvenc, CodecName = "h264_nvenc", DisplayName = "NVIDIA NVENC" },
        new() { Kind = VideoEncoderKind.Qsv, CodecName = "h264_qsv", DisplayName = "Intel Quick Sync" },
        new() { Kind = VideoEncoderKind.Amf, CodecName = "h264_amf", DisplayName = "AMD AMF" }
    ];

    public static readonly EncoderInfo Software = new()
    {
        Kind = VideoEncoderKind.Libx264,
        CodecName = "libx264",
        DisplayName = "Software (x264)"
    };

    private readonly FFmpegLocator _locator;
    private readonly object _gate = new();
    private Task? _probeTask;
    private IReadOnlyList<EncoderInfo> _available = [Software];

    public EncoderSelector(FFmpegLocator locator)
    {
        _locator = locator;
    }

    /// <summary>Working encoders, best first. Always ends with software x264.</summary>
    public IReadOnlyList<EncoderInfo> Available => _available;

    public EncoderInfo Selected => _available[0];

    public bool IsProbed => _probeTask?.IsCompletedSuccessfully == true;

    public Task ProbeAsync()
    {
        lock (_gate)
            return _probeTask ??= ProbeCoreAsync();
    }

    private async Task ProbeCoreAsync()
    {
        var ffmpeg = _locator.Find();
        if (ffmpeg is null)
            return;

        var list = new List<EncoderInfo>();
        string encoders;
        try
        {
            encoders = await FFmpegLocator.RunProcessAsync(ffmpeg, "-hide_banner -encoders");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Encoder listing failed: {ex.Message}");
            return;
        }

        // Builds list hardware encoders even when no such GPU exists, so do a real test encode.
        foreach (var candidate in HardwareCandidates)
        {
            if (!encoders.Contains(candidate.CodecName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (await TestEncodeAsync(ffmpeg, candidate))
                list.Add(candidate);
        }

        list.Add(Software);
        _available = list;
        AppLog.Info($"Encoders available: {string.Join(", ", list.Select(e => e.CodecName))}");
    }

    private static async Task<bool> TestEncodeAsync(string ffmpeg, EncoderInfo encoder)
    {
        var args = "-hide_banner -loglevel error -f lavfi -i color=c=black:s=1280x720:r=30 -frames:v 15 " +
                   string.Join(" ", GetVideoEncodeArgs(encoder, QualityPreset.Balanced)) +
                   " -f null -";
        try
        {
            var (exitCode, output) = await FFmpegLocator.RunProcessWithExitCodeAsync(ffmpeg, args, 20_000);
            if (exitCode == 0)
                return true;

            AppLog.Info($"Encoder {encoder.CodecName} unavailable: {output.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Encoder {encoder.CodecName} probe failed: {ex.Message}");
            return false;
        }
    }

    internal void OverrideAvailableForTests(IReadOnlyList<EncoderInfo> encoders)
    {
        lock (_gate)
            _probeTask ??= Task.CompletedTask;
        _available = encoders.Count > 0 ? encoders : [Software];
    }

    /// <summary>Encoders to try in order, starting from <paramref name="preferred"/>.</summary>
    public IReadOnlyList<EncoderInfo> FallbackChain(EncoderInfo? preferred)
    {
        var list = _available.ToList();
        if (preferred is null)
            return list;

        var index = list.FindIndex(e => e.CodecName == preferred.CodecName);
        return index < 0 ? list : list.Skip(index).ToList();
    }

    public static IReadOnlyList<string> GetVideoEncodeArgs(EncoderInfo encoder, QualityPreset preset) => encoder.Kind switch
    {
        VideoEncoderKind.Nvenc => GetNvencArgs(preset),
        VideoEncoderKind.Qsv => GetQsvArgs(preset),
        VideoEncoderKind.Amf => GetAmfArgs(preset),
        _ => GetLibx264Args(preset)
    };

    private static List<string> GetNvencArgs(QualityPreset preset)
    {
        var (cq, presetName) = preset switch
        {
            QualityPreset.LowFileSize => (32, "p4"),
            QualityPreset.MaximumQuality => (19, "p6"),
            _ => (25, "p5")
        };

        return ["-c:v", "h264_nvenc", "-preset", presetName, "-rc", "vbr", "-cq", cq.ToString(), "-b:v", "0", "-pix_fmt", "yuv420p"];
    }

    private static List<string> GetQsvArgs(QualityPreset preset)
    {
        var q = preset switch
        {
            QualityPreset.LowFileSize => 32,
            QualityPreset.MaximumQuality => 19,
            _ => 25
        };

        return ["-c:v", "h264_qsv", "-global_quality", q.ToString(), "-pix_fmt", "nv12"];
    }

    private static List<string> GetAmfArgs(QualityPreset preset)
    {
        var (quality, qp) = preset switch
        {
            QualityPreset.LowFileSize => ("speed", 32),
            QualityPreset.MaximumQuality => ("quality", 19),
            _ => ("balanced", 25)
        };

        return ["-c:v", "h264_amf", "-quality", quality, "-rc", "cqp", "-qp_i", qp.ToString(), "-qp_p", qp.ToString(), "-pix_fmt", "yuv420p"];
    }

    private static List<string> GetLibx264Args(QualityPreset preset)
    {
        // Real-time screen capture: slower x264 presets drop frames on typical CPUs.
        var (crf, x264Preset) = preset switch
        {
            QualityPreset.LowFileSize => (28, "veryfast"),
            QualityPreset.MaximumQuality => (19, "faster"),
            _ => (23, "veryfast")
        };

        return ["-c:v", "libx264", "-preset", x264Preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p"];
    }
}
