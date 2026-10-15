namespace RecBox.Models;

public sealed class MonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>GDI device name, e.g. \\.\DISPLAY1 — stable across restarts.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Native (physical) pixel size and position on the virtual desktop.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public int BoundsX { get; init; }
    public int BoundsY { get; init; }
    public int RefreshRate { get; init; }
    public bool IsPrimary { get; init; }

    public string DisplayName =>
        $"{Name} ({Width}×{Height})" + (IsPrimary ? " — Primary" : string.Empty);

    public bool SameGeometry(MonitorInfo other) =>
        Width == other.Width && Height == other.Height && BoundsX == other.BoundsX && BoundsY == other.BoundsY;

    public override string ToString() => DisplayName;
}
