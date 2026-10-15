namespace RecBox.Models;

public sealed class AppSettings
{
    public int MonitorIndex { get; set; }
    public string MicrophoneId { get; set; } = "default";
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    public QualityPreset QualityPreset { get; set; } = QualityPreset.Balanced;
    public string OutputFolder { get; set; } = string.Empty;
    public bool MinimizeToTray { get; set; } = true;
}
