namespace RecBox.Models;

public enum QualityPreset
{
    LowFileSize = 0,
    Balanced = 1,
    MaximumQuality = 2
}

public static class QualityPresetExtensions
{
    public static string ToDisplayName(this QualityPreset preset) => preset switch
    {
        QualityPreset.LowFileSize => "Low file size",
        QualityPreset.Balanced => "Balanced",
        QualityPreset.MaximumQuality => "Maximum quality",
        _ => preset.ToString()
    };

    public static string ToDescription(this QualityPreset preset) => preset switch
    {
        QualityPreset.LowFileSize => "Smaller files, acceptable visual quality",
        QualityPreset.Balanced => "Good balance of quality and file size",
        QualityPreset.MaximumQuality => "Highest quality, larger files",
        _ => string.Empty
    };
}
