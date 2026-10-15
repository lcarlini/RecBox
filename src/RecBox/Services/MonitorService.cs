using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using RecBox.Models;

namespace RecBox.Services;

public sealed class MonitorService
{
    /// <summary>
    /// Monitors in native pixels. Screen.Bounds is DPI-virtualized for WPF (system-DPI-aware)
    /// processes, so a 1920×1080 display at 125% would report 1536×864 and be captured only partially.
    /// EnumDisplaySettings always returns the real mode.
    /// </summary>
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var screens = Screen.AllScreens;
        var list = new List<MonitorInfo>(screens.Length);

        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            int x = screen.Bounds.X, y = screen.Bounds.Y, w = screen.Bounds.Width, h = screen.Bounds.Height, hz = 0;

            var dm = new NativeMethods.DEVMODE { dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODE>() };
            if (NativeMethods.EnumDisplaySettings(screen.DeviceName, NativeMethods.EnumCurrentSettings, ref dm) &&
                dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            {
                x = dm.dmPositionX;
                y = dm.dmPositionY;
                w = dm.dmPelsWidth;
                h = dm.dmPelsHeight;
                hz = dm.dmDisplayFrequency;
            }

            // H.264 4:2:0 needs even dimensions.
            w &= ~1;
            h &= ~1;

            var number = Regex.Match(screen.DeviceName ?? string.Empty, @"(\d+)$");
            var name = number.Success ? $"Display {number.Groups[1].Value}" : $"Display {i + 1}";

            list.Add(new MonitorInfo
            {
                Index = i,
                Name = name,
                DeviceName = screen.DeviceName ?? string.Empty,
                Width = w,
                Height = h,
                BoundsX = x,
                BoundsY = y,
                RefreshRate = hz,
                IsPrimary = screen.Primary
            });
        }

        return list;
    }

    public MonitorInfo? Find(string? deviceName, int index)
    {
        var monitors = GetMonitors();
        if (!string.IsNullOrEmpty(deviceName))
        {
            var byName = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        return monitors.FirstOrDefault(m => m.Index == index)
               ?? monitors.FirstOrDefault(m => m.IsPrimary)
               ?? monitors.FirstOrDefault();
    }

    public MonitorInfo? FindByIndex(int index) => Find(null, index);
}
