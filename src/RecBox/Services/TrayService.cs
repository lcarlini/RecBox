using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace RecBox.Services;

public sealed class TrayService : IDisposable
{
    private readonly Window _window;
    private TaskbarIcon? _trayIcon;
    private bool _isExitRequested;
    private bool _suppressMinimizeToTray;
    private bool _shownTrayHint;

    public TrayService(Window window)
    {
        _window = window;
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? StartStopRequested;
    public event EventHandler? ExitRequested;

    public bool IsExitRequested => _isExitRequested;

    /// <summary>Balloons would be captured in the video, so the owner can suppress them while recording.</summary>
    public Func<bool>? SuppressBalloons { get; set; }

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "RecBox",
            Visibility = Visibility.Visible
        };

        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "recbox.ico");
            if (System.IO.File.Exists(iconPath))
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
            else
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _trayIcon.ContextMenu = BuildMenu();

        _window.StateChanged += OnWindowStateChanged;
    }

    public void UpdateRecordingState(bool isRecording)
    {
        if (_trayIcon is null)
            return;

        _trayIcon.ToolTipText = isRecording ? "RecBox — Recording" : "RecBox";
        if (_trayIcon.ContextMenu is ContextMenu menu && menu.Items.Count > 1 &&
            menu.Items[1] is MenuItem startStop)
        {
            startStop.Header = isRecording ? "Stop recording" : "Start recording";
        }
    }

    public void ShowBalloon(string title, string message)
    {
        if (SuppressBalloons?.Invoke() == true)
            return;
        _trayIcon?.ShowBalloonTip(title, message, BalloonIcon.Info);
    }

    public void ShowWindow()
    {
        _suppressMinimizeToTray = true;
        _window.Show();
        _window.ShowInTaskbar = true;
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _suppressMinimizeToTray = false;
    }

    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Called once the owner has decided to really exit (window close is no longer redirected to tray).</summary>
    public void MarkExiting() => _isExitRequested = true;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show RecBox" };
        show.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var startStop = new MenuItem { Header = "Start recording" };
        startStop.Click += (_, _) => StartStopRequested?.Invoke(this, EventArgs.Empty);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => RequestExit();

        menu.Items.Add(show);
        menu.Items.Add(startStop);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_suppressMinimizeToTray || _isExitRequested)
            return;

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.ShowInTaskbar = false;
            _window.Hide();
            if (!_shownTrayHint && SuppressBalloons?.Invoke() != true)
            {
                _shownTrayHint = true;
                ShowBalloon("RecBox", "Still running in the system tray. Double-click the icon to restore.");
            }
        }
    }

    public void Dispose()
    {
        _window.StateChanged -= OnWindowStateChanged;
        if (_trayIcon is not null)
        {
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }
}
