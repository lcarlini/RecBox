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
}

public sealed class EncoderSelector
{
    private readonly FFmpegLocator _locator;
    private EncoderInfo? _selected;
    private bool _probed;
    private bool _supportsDdagrab;

    public EncoderSelector(FFmpegLocator locator)
    {
        _locator = locator;
    }

    public EncoderInfo Selected => _selected ?? SoftwareEncoder;
    public bool SupportsDdagrab => _supportsDdagrab;
    public bool IsProbed => _probed;

    private static EncoderInfo SoftwareEncoder => new()
    {
        Kind = VideoEncoderKind.Libx264,
        CodecName = "libx264",
        DisplayName = "Software (x264)"
    };

    public async Task ProbeAsync()
    {
        if (_probed)
            return;

        var ffmpeg = _locator.Find();
        if (ffmpeg is null)
        {
            _selected = SoftwareEncoder;
            _probed = true;
            return;
        }

        try
        {
            var encodersOutput = await FFmpegLocator.RunProcessAsync(ffmpeg, "-hide_banner -encoders");
            _selected = PickBestEncoder(encodersOutput);

            var filtersOutput = await FFmpegLocator.RunProcessAsync(ffmpeg, "-hide_banner -filters");
            _supportsDdagrab = filtersOutput.Contains("ddagrab", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            _selected = SoftwareEncoder;
            _supportsDdagrab = false;
        }

        _probed = true;
    }

    private static EncoderInfo PickBestEncoder(string encodersOutput)
    {
        if (encodersOutput.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return new EncoderInfo
            {
                Kind = VideoEncoderKind.Nvenc,
                CodecName = "h264_nvenc",
                DisplayName = "NVIDIA NVENC"
            };
        }

        if (encodersOutput.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase))
        {
            return new EncoderInfo
            {
                Kind = VideoEncoderKind.Qsv,
                CodecName = "h264_qsv",
                DisplayName = "Intel Quick Sync"
            };
        }

        if (encodersOutput.Contains("h264_amf", StringComparison.OrdinalIgnoreCase))
        {
            return new EncoderInfo
            {
                Kind = VideoEncoderKind.Amf,
                CodecName = "h264_amf",
                DisplayName = "AMD AMF"
            };
        }

        return SoftwareEncoder;
    }

    public IReadOnlyList<string> GetVideoEncodeArgs(QualityPreset preset, bool useHardwareFrames)
    {
        var encoder = Selected;
        return encoder.Kind switch
        {
            VideoEncoderKind.Nvenc => GetNvencArgs(preset),
            VideoEncoderKind.Qsv => GetQsvArgs(preset, useHardwareFrames),
            VideoEncoderKind.Amf => GetAmfArgs(preset),
            _ => GetLibx264Args(preset)
        };
    }

    private static List<string> GetNvencArgs(QualityPreset preset)
    {
        // CQ: lower = higher quality
        var (cq, presetName) = preset switch
        {
            QualityPreset.LowFileSize => (32, "p4"),
            QualityPreset.MaximumQuality => (18, "p7"),
            _ => (24, "p5")
        };

        return
        [
            "-c:v", "h264_nvenc",
            "-preset", presetName,
            "-rc", "constqp",
            "-qp", cq.ToString(),
            "-pix_fmt", "yuv420p"
        ];
    }

    private static List<string> GetQsvArgs(QualityPreset preset, bool useHardwareFrames)
    {
        var globalQuality = preset switch
        {
            QualityPreset.LowFileSize => 32,
            QualityPreset.MaximumQuality => 18,
            _ => 24
        };

        var args = new List<string> { "-c:v", "h264_qsv", "-global_quality", globalQuality.ToString() };
        if (!useHardwareFrames)
            args.AddRange(["-pix_fmt", "nv12"]);
        return args;
    }

    private static List<string> GetAmfArgs(QualityPreset preset)
    {
        var quality = preset switch
        {
            QualityPreset.LowFileSize => "speed",
            QualityPreset.MaximumQuality => "quality",
            _ => "balanced"
        };

        var qp = preset switch
        {
            QualityPreset.LowFileSize => 32,
            QualityPreset.MaximumQuality => 18,
            _ => 24
        };

        return
        [
            "-c:v", "h264_amf",
            "-quality", quality,
            "-rc", "cqp",
            "-qp_i", qp.ToString(),
            "-qp_p", qp.ToString(),
            "-pix_fmt", "yuv420p"
        ];
    }

    private static List<string> GetLibx264Args(QualityPreset preset)
    {
        var (crf, x264Preset) = preset switch
        {
            QualityPreset.LowFileSize => (28, "veryfast"),
            QualityPreset.MaximumQuality => (18, "slow"),
            _ => (23, "medium")
        };

        return
        [
            "-c:v", "libx264",
            "-preset", x264Preset,
            "-crf", crf.ToString(),
            "-pix_fmt", "yuv420p"
        ];
    }
}
