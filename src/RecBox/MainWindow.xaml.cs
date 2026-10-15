using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RecBox.Models;
using RecBox.Services;
using WinForms = System.Windows.Forms;

namespace RecBox;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly MonitorService _monitorService = new();
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly FFmpegLocator _ffmpegLocator = new();
    private readonly EncoderSelector _encoderSelector;
    private readonly RecordingService _recording;
    private readonly TrayService _tray;
    private readonly DispatcherTimer _timer;

    private AppSettings _settings;
    private bool _loadingUi;
    private bool _isBusy;
    private bool _keepAwake;

    public MainWindow()
    {
        InitializeComponent();

        _encoderSelector = new EncoderSelector(_ffmpegLocator);
        _recording = new RecordingService(_ffmpegLocator, _encoderSelector, _monitorService);
        _recording.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateUiForState);

        _settings = _settingsStore.Load();
        _tray = new TrayService(this)
        {
            SuppressBalloons = () => _recording.State is RecordingState.Recording or RecordingState.Stopping
        };
        _tray.ShowRequested += (_, _) => _tray.ShowWindow();
        _tray.StartStopRequested += async (_, _) => await ToggleRecordingAsync();
        _tray.ExitRequested += async (_, _) => await ExitAsync();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_recording.State == RecordingState.Recording)
                TimerText.Text = _recording.Elapsed.ToString(@"hh\:mm\:ss");
        };

        SourceInitialized += (_, _) => ApplyCaptureExclusion();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void BringToFront() => _tray.ShowWindow();

    public void EmergencyStop()
    {
        try
        {
            _recording.EmergencyStop();
        }
        catch (Exception ex)
        {
            AppLog.Error("Emergency stop failed", ex);
        }
    }

    private void ApplyCaptureExclusion()
    {
        // Keeps RecBox out of its own recordings even when the window is open.
        // RECBOX_ALLOW_CAPTURE=1 disables this (used by automated tests).
        if (!_settings.HideWindowFromCapture || Environment.GetEnvironmentVariable("RECBOX_ALLOW_CAPTURE") == "1")
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture))
            AppLog.Info($"SetWindowDisplayAffinity not applied (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TrySetWindowIcon();
        _tray.Initialize();
        LoadDevicesIntoUi();
        ApplySettingsToUi();
        RefreshSystemAudioAvailability();

        if (!_ffmpegLocator.IsAvailable())
        {
            ShowError("FFmpeg not found. Run run.cmd once, or place ffmpeg.exe in tools\\ffmpeg (see the user guide).");
        }

        EncoderText.Text = "Checking encoders…";
        try
        {
            await _recording.EnsureReadyAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Encoder probe", ex);
        }

        UpdateUiForState();
        await RecoverPreviousSessionsAsync();
    }

    private async Task RecoverPreviousSessionsAsync()
    {
        try
        {
            var recovered = await _recording.RecoverOrphanedSessionsAsync();
            if (recovered.Outputs.Count == 0)
                return;

            LastFileText.Text = $"Recovered from last session: {recovered.Outputs[0]}";
            WarningText.Text = "RecBox closed unexpectedly last time. Your recording was recovered and saved.";
            WarningText.Visibility = Visibility.Visible;
            _tray.ShowBalloon("RecBox", "A recording from the last session was recovered and saved.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Recovery", ex);
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "recbox.ico");
            if (File.Exists(ico))
                Icon = BitmapFrame.Create(new Uri(ico));
        }
        catch
        {
            // optional chrome polish
        }
    }

    private void RefreshSystemAudioAvailability()
    {
        var available = _audioDeviceService.IsSystemAudioAvailable();
        SystemAudioHint.Visibility = available ? Visibility.Collapsed : Visibility.Visible;

        if (!available)
        {
            SystemAudioCheck.IsChecked = false;
            SystemAudioCheck.IsEnabled = false;
        }
        else if (_recording.State is RecordingState.Idle or RecordingState.Error)
        {
            SystemAudioCheck.IsEnabled = true;
            SystemAudioCheck.IsChecked = _settings.RecordSystemAudio;
        }
    }

    private void LoadDevicesIntoUi()
    {
        _loadingUi = true;
        try
        {
            MonitorCombo.ItemsSource = _monitorService.GetMonitors();
            MicrophoneCombo.ItemsSource = _audioDeviceService.GetMicrophones();

            QualityCombo.Items.Clear();
            foreach (var preset in Enum.GetValues<QualityPreset>())
            {
                QualityCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{preset.ToDisplayName()} — {preset.ToDescription()}",
                    Tag = preset
                });
            }
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ApplySettingsToUi()
    {
        _loadingUi = true;
        try
        {
            if (MonitorCombo.ItemsSource is IReadOnlyList<MonitorInfo> { Count: > 0 } monitors)
            {
                MonitorCombo.SelectedItem =
                    monitors.FirstOrDefault(m => !string.IsNullOrEmpty(_settings.MonitorDeviceName) &&
                                                 string.Equals(m.DeviceName, _settings.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                    ?? monitors.FirstOrDefault(m => m.Index == _settings.MonitorIndex)
                    ?? monitors.FirstOrDefault(m => m.IsPrimary)
                    ?? monitors[0];
            }

            if (MicrophoneCombo.ItemsSource is IReadOnlyList<AudioDeviceInfo> { Count: > 0 } mics)
                MicrophoneCombo.SelectedItem = mics.FirstOrDefault(m => m.Id == _settings.MicrophoneId) ?? mics[0];

            MicCheck.IsChecked = _settings.RecordMicrophone;
            SystemAudioCheck.IsChecked = _settings.RecordSystemAudio;
            OutputFolderBox.Text = _settings.OutputFolder;

            foreach (ComboBoxItem item in QualityCombo.Items)
            {
                if (item.Tag is QualityPreset preset && preset == _settings.QualityPreset)
                {
                    QualityCombo.SelectedItem = item;
                    break;
                }
            }

            if (QualityCombo.SelectedItem is null && QualityCombo.Items.Count > 0)
                QualityCombo.SelectedIndex = 1;

            UpdateMonitorSummary();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;

        CaptureSettingsFromUi();
        SaveSettings();
        UpdateMonitorSummary();
        UpdateSettingsEnabled();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Settings save failed: {ex.Message}");
        }
    }

    private void UpdateMonitorSummary()
    {
        var monitor = _recording.State == RecordingState.Recording && _recording.RecordingMonitor is { } active
            ? active
            : MonitorCombo.SelectedItem as MonitorInfo;
        MonitorSummaryText.Text = monitor is null ? string.Empty : $"Monitor: {monitor.DisplayName}";
    }

    private void OutputFolderBox_LostFocus(object sender, RoutedEventArgs e) => SettingsChanged(sender, e);

    private void CaptureSettingsFromUi()
    {
        if (MonitorCombo.SelectedItem is MonitorInfo monitor)
        {
            _settings.MonitorIndex = monitor.Index;
            _settings.MonitorDeviceName = monitor.DeviceName;
        }

        if (MicrophoneCombo.SelectedItem is AudioDeviceInfo mic)
            _settings.MicrophoneId = mic.Id;

        _settings.RecordMicrophone = MicCheck.IsChecked == true;
        if (SystemAudioCheck.IsEnabled)
            _settings.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
        _settings.OutputFolder = string.IsNullOrWhiteSpace(OutputFolderBox.Text)
            ? SettingsStore.DefaultOutputFolder()
            : OutputFolderBox.Text.Trim();

        if (QualityCombo.SelectedItem is ComboBoxItem { Tag: QualityPreset preset })
            _settings.QualityPreset = preset;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose where RecBox saves recordings",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_settings.OutputFolder)
                ? _settings.OutputFolder
                : SettingsStore.DefaultOutputFolder()
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            OutputFolderBox.Text = dialog.SelectedPath;
            SettingsChanged(sender, e);
        }
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
        => await ToggleRecordingAsync();

    private async Task ToggleRecordingAsync()
    {
        if (_isBusy)
            return;

        AppLog.Info($"Record button / tray toggle (state {_recording.State}).");
        if (_recording.State == RecordingState.Recording)
            await StopRecordingAsync();
        else if (_recording.State is RecordingState.Idle or RecordingState.Error)
            await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        _isBusy = true;
        ClearError();
        CaptureSettingsFromUi();
        if (!_audioDeviceService.IsSystemAudioAvailable())
            _settings.RecordSystemAudio = false;
        SaveSettings();

        RecordButton.IsEnabled = false;
        StatusText.Text = "Starting…";

        try
        {
            var monitor = _monitorService.Find(_settings.MonitorDeviceName, _settings.MonitorIndex);
            if (monitor is null)
            {
                ShowError("No monitor available to record.");
                return;
            }

            var recordSettings = _settings.Clone();
            if (!SystemAudioCheck.IsEnabled)
                recordSettings.RecordSystemAudio = false;

            await _recording.StartAsync(recordSettings, monitor);
            _timer.Start();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _isBusy = false;
            UpdateUiForState();
        }
    }

    private async Task StopRecordingAsync()
    {
        _isBusy = true;
        try
        {
            await _recording.StopAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Stop", ex);
            ShowError(ex.Message);
        }
        finally
        {
            _isBusy = false;
            UpdateUiForState();
        }

        if (!string.IsNullOrEmpty(_recording.OutputPath) && File.Exists(_recording.OutputPath))
        {
            _tray.ShowBalloon("RecBox", string.IsNullOrEmpty(_recording.LastWarning)
                ? "Recording saved."
                : _recording.LastWarning);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureSettingsFromUi();
            var folder = _settings.OutputFolder;
            Directory.CreateDirectory(folder);

            if (!string.IsNullOrEmpty(_recording.OutputPath) && File.Exists(_recording.OutputPath))
                Process.Start("explorer.exe", $"/select,\"{_recording.OutputPath}\"");
            else
                Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception ex)
        {
            ShowError($"Could not open the folder: {ex.Message}");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            return;
        try { DragMove(); } catch { /* ignore */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void UpdateUiForState()
    {
        var state = _recording.State;
        var textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var danger = (System.Windows.Media.Brush)FindResource("DangerBrush");
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var ok = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        var tealGrad = (System.Windows.Media.Brush)FindResource("TealButtonGradient");
        var recordGrad = (System.Windows.Media.Brush)FindResource("RecordButtonGradient");

        switch (state)
        {
            case RecordingState.Recording:
                StatusText.Text = "Recording";
                StatusText.Foreground = textBrush;
                StatusDot.Fill = danger;
                RecordLabel.Text = "Stop";
                RecordLabel.Foreground = danger;
                TimerText.Text = _recording.Elapsed.ToString(@"hh\:mm\:ss");
                TimerText.Foreground = danger;
                RecordButton.IsEnabled = !_isBusy;
                SetRecordButtonFill(recordGrad);
                StartPulse();
                if (!_timer.IsEnabled)
                    _timer.Start();
                break;
            case RecordingState.Stopping:
                StatusText.Text = "Saving video…";
                StatusText.Foreground = textBrush;
                StatusDot.Fill = muted;
                RecordLabel.Text = "Saving";
                RecordLabel.Foreground = muted;
                RecordButton.IsEnabled = false;
                StopPulse();
                break;
            case RecordingState.Error:
                StatusText.Text = "Could not record";
                StatusText.Foreground = textBrush;
                StatusDot.Fill = danger;
                RecordLabel.Text = "Retry";
                RecordLabel.Foreground = accent;
                RecordButton.IsEnabled = !_isBusy;
                SetRecordButtonFill(tealGrad);
                StopPulse();
                _timer.Stop();
                if (!string.IsNullOrEmpty(_recording.LastError))
                    ShowError(_recording.LastError);
                break;
            default:
                StatusText.Text = _isBusy ? "Starting…" : "Ready to record";
                StatusText.Foreground = textBrush;
                StatusDot.Fill = ok;
                RecordLabel.Text = "Start";
                RecordLabel.Foreground = accent;
                RecordButton.IsEnabled = !_isBusy;
                SetRecordButtonFill(tealGrad);
                StopPulse();
                _timer.Stop();
                TimerText.Foreground = accent;
                break;
        }

        EncoderText.Text = _ffmpegLocator.IsAvailable()
            ? $"Encoder: {_recording.EncoderDisplayName}"
            : "Encoder: FFmpeg not found";

        if (!string.IsNullOrEmpty(_recording.OutputPath) && state is RecordingState.Idle or RecordingState.Error)
        {
            LastFileText.Text = _recording.OutputFiles.Count > 1
                ? $"Saved {_recording.OutputFiles.Count} files: {string.Join(", ", _recording.OutputFiles.Select(Path.GetFileName))}"
                : $"Saved: {_recording.OutputPath}";
        }

        var note = state is RecordingState.Recording or RecordingState.Stopping
            ? _recording.StatusMessage
            : _recording.LastWarning;
        if (!string.IsNullOrEmpty(note))
        {
            WarningText.Text = note;
            WarningText.Visibility = Visibility.Visible;
        }
        else if (state is RecordingState.Recording or RecordingState.Stopping)
        {
            WarningText.Visibility = Visibility.Collapsed;
        }

        UpdateKeepAwake(state is RecordingState.Recording or RecordingState.Stopping);
        UpdateMonitorSummary();
        UpdateSettingsEnabled();
        _tray.UpdateRecordingState(state == RecordingState.Recording);
    }

    /// <summary>Prevents sleep and display-off/lock (which would interrupt capture) while recording.</summary>
    private void UpdateKeepAwake(bool recording)
    {
        if (recording == _keepAwake)
            return;
        _keepAwake = recording;
        NativeMethods.SetThreadExecutionState(recording
            ? NativeMethods.ExecutionState.Continuous | NativeMethods.ExecutionState.SystemRequired | NativeMethods.ExecutionState.DisplayRequired
            : NativeMethods.ExecutionState.Continuous);
    }

    private void SetRecordButtonFill(System.Windows.Media.Brush fill)
    {
        RecordButton.ApplyTemplate();
        if (RecordButton.Template?.FindName("Outer", RecordButton) is System.Windows.Shapes.Ellipse outer)
            outer.Fill = fill;
    }

    private void StartPulse()
    {
        var opacity = new DoubleAnimation(0.7, 0.0, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };
        var size = new DoubleAnimation(88, 118, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };

        PulseRing.BeginAnimation(OpacityProperty, opacity);
        PulseRing.BeginAnimation(WidthProperty, size);
        PulseRing.BeginAnimation(HeightProperty, size);
    }

    private void StopPulse()
    {
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.BeginAnimation(WidthProperty, null);
        PulseRing.BeginAnimation(HeightProperty, null);
        PulseRing.Opacity = 0;
        PulseRing.Width = 108;
        PulseRing.Height = 108;
    }

    private void UpdateSettingsEnabled()
    {
        var enabled = _recording.State is RecordingState.Idle or RecordingState.Error && !_isBusy;
        MonitorCombo.IsEnabled = enabled;
        MicrophoneCombo.IsEnabled = enabled && MicCheck.IsChecked == true;
        MicCheck.IsEnabled = enabled;
        SystemAudioCheck.IsEnabled = enabled && _audioDeviceService.IsSystemAudioAvailable();
        QualityCombo.IsEnabled = enabled;
        OutputFolderBox.IsEnabled = enabled;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        WarningText.Text = string.Empty;
        WarningText.Visibility = Visibility.Collapsed;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _recording.NotifyDisplaySettingsChanged();
            if (_recording.State is RecordingState.Idle or RecordingState.Error)
            {
                LoadDevicesIntoUi();
                ApplySettingsToUi();
            }
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            _recording.NotifySuspend();
        else if (e.Mode == PowerModes.Resume)
            _recording.NotifyResume();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_tray.IsExitRequested)
            return;

        // Close hides to tray so the window never ends a recording by accident.
        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    private async Task ExitAsync()
    {
        if (_recording.State is RecordingState.Recording or RecordingState.Stopping)
        {
            var result = System.Windows.MessageBox.Show(
                "A recording is in progress. Stop, save the video and exit?",
                "RecBox",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            if (_recording.State == RecordingState.Recording)
                await StopRecordingAsync();
            else
                await _recording.StopAsync();
        }

        _tray.MarkExiting();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        UpdateKeepAwake(false);
        _tray.Dispose();
        _recording.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
