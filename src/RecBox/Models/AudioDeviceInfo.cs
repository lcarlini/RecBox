namespace RecBox.Models;

public sealed class AudioDeviceInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }

    public string DisplayName => IsDefault ? $"{Name} (Default)" : Name;
}
