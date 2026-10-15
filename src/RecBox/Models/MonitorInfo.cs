namespace RecBox.Models;

public sealed class MonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int BoundsX { get; init; }
    public int BoundsY { get; init; }
    public bool IsPrimary { get; init; }

    public string DisplayName =>
        $"{Name} ({Width}×{Height})" + (IsPrimary ? " — Primary" : string.Empty);
}
