using System.Windows.Forms;
using RecBox.Models;

namespace RecBox.Services;

public sealed class MonitorService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var screens = Screen.AllScreens;
        var list = new List<MonitorInfo>(screens.Length);

        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var name = string.IsNullOrWhiteSpace(screen.DeviceName)
                ? $"Monitor {i + 1}"
                : $"Display {i + 1}";

            list.Add(new MonitorInfo
            {
                Index = i,
                Name = name,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                BoundsX = screen.Bounds.X,
                BoundsY = screen.Bounds.Y,
                IsPrimary = screen.Primary
            });
        }

        return list;
    }

    public MonitorInfo? FindByIndex(int index)
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.Index == index) ?? monitors.FirstOrDefault();
    }
}
