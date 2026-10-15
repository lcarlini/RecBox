namespace RecBox.Models;

public sealed class AppSettings
{
    public int MonitorIndex { get; set; }
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string MicrophoneId { get; set; } = "default";
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    public QualityPreset QualityPreset { get; set; } = QualityPreset.Balanced;
    public string OutputFolder { get; set; } = string.Empty;
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Keep the RecBox window itself out of recordings.</summary>
    public bool HideWindowFromCapture { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
