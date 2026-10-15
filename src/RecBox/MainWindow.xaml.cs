using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    public MainWindow()
    {
        InitializeComponent();

        _encoderSelector = new EncoderSelector(_ffmpegLocator);
        _recording = new RecordingService(_ffmpegLocator, _encoderSelector);
        _recording.StateChanged += (_, _) => Dispatcher.Invoke(UpdateUiForState);

        _settings = _settingsStore.Load();
        _tray = new TrayService(this);
        _tray.ShowRequested += (_, _) => _tray.ShowWindow();
        _tray.StartStopRequested += async (_, _) => await ToggleRecordingAsync();
        _tray.ExitRequested += async (_, _) => await ExitAsync();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_recording.State == RecordingState.Recording)
                TimerText.Text = _recording.Elapsed.ToString(@"hh\:mm\:ss");
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TrySetWindowIcon();
        _tray.Initialize();
        LoadDevicesIntoUi();
        ApplySettingsToUi();
        RefreshSystemAudioAvailability();

        await _recording.EnsureReadyAsync();
        UpdateUiForState();

        if (!_ffmpegLocator.IsAvailable())
        {
            ShowError("FFmpeg not found. Run run.ps1 once, or place ffmpeg.exe in tools/ffmpeg (see the user guide).");
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
            _settings.RecordSystemAudio = false;
            _settingsStore.Save(_settings);
        }
        else if (_recording.State is RecordingState.Idle or RecordingState.Error)
        {
            SystemAudioCheck.IsEnabled = true;
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
            foreach (QualityPreset preset in Enum.GetValues<QualityPreset>())
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
            var monitors = MonitorCombo.ItemsSource as IReadOnlyList<MonitorInfo>;
            if (monitors is { Count: > 0 })
            {
                var match = monitors.FirstOrDefault(m => m.Index == _settings.MonitorIndex)
                            ?? monitors.FirstOrDefault(m => m.IsPrimary)
                            ?? monitors[0];
                MonitorCombo.SelectedItem = match;
            }

            var mics = MicrophoneCombo.ItemsSource as IReadOnlyList<AudioDeviceInfo>;
            if (mics is { Count: > 0 })
            {
                MicrophoneCombo.SelectedItem =
                    mics.FirstOrDefault(m => m.Id == _settings.MicrophoneId) ?? mics[0];
            }

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
        _settingsStore.Save(_settings);
        UpdateMonitorSummary();
        UpdateSettingsEnabled();
    }

    private void UpdateMonitorSummary()
    {
        if (MonitorCombo.SelectedItem is MonitorInfo monitor)
            MonitorSummaryText.Text = $"Monitor: {monitor.DisplayName}";
        else
            MonitorSummaryText.Text = string.Empty;
    }

    private void OutputFolderBox_LostFocus(object sender, RoutedEventArgs e) => SettingsChanged(sender, e);

    private void CaptureSettingsFromUi()
    {
        if (MonitorCombo.SelectedItem is MonitorInfo monitor)
            _settings.MonitorIndex = monitor.Index;

        if (MicrophoneCombo.SelectedItem is AudioDeviceInfo mic)
            _settings.MicrophoneId = mic.Id;

        _settings.RecordMicrophone = MicCheck.IsChecked == true;
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
        _settingsStore.Save(_settings);

        var monitor = _monitorService.FindByIndex(_settings.MonitorIndex);
        if (monitor is null)
        {
            ShowError("No monitor available to record.");
            _isBusy = false;
            return;
        }

        try
        {
            await _recording.StartAsync(_settings, monitor);
            _timer.Start();
            _tray.UpdateRecordingState(true);
            _tray.ShowBalloon("RecBox", "Recording started. Minimize to hide this window from captures.");
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
            _timer.Stop();
            _tray.UpdateRecordingState(false);

            if (!string.IsNullOrEmpty(_recording.OutputPath) && File.Exists(_recording.OutputPath))
            {
                LastFileText.Text = $"Saved: {_recording.OutputPath}";
                _tray.ShowBalloon("RecBox", "Recording saved.");
            }
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

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureSettingsFromUi();
        var folder = _settings.OutputFolder;
        Directory.CreateDirectory(folder);

        if (!string.IsNullOrEmpty(_recording.OutputPath) && File.Exists(_recording.OutputPath))
        {
            Process.Start("explorer.exe", $"/select,\"{_recording.OutputPath}\"");
        }
        else
        {
            Process.Start("explorer.exe", folder);
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
                RecordLabel.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                TimerText.Text = _recording.Elapsed.ToString(@"hh\:mm\:ss");
                TimerText.Foreground = danger;
                RecordButton.IsEnabled = true;
                SetRecordButtonFill(recordGrad);
                StartPulse();
                break;
            case RecordingState.Stopping:
                StatusText.Text = "Stopping…";
                StatusText.Foreground = textBrush;
                StatusDot.Fill = muted;
                RecordLabel.Text = "Stopping";
                RecordLabel.Foreground = muted;
                RecordButton.IsEnabled = false;
                StopPulse();
                break;
            case RecordingState.Error:
                StatusText.Text = "Something went wrong";
                StatusText.Foreground = textBrush;
                StatusDot.Fill = danger;
                RecordLabel.Text = "Retry";
                RecordLabel.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
                RecordButton.IsEnabled = true;
                SetRecordButtonFill(tealGrad);
                StopPulse();
                if (!string.IsNullOrEmpty(_recording.LastError))
                    ShowError(_recording.LastError);
                break;
            default:
                StatusText.Text = "Ready to record";
                StatusText.Foreground = textBrush;
                StatusDot.Fill = ok;
                RecordLabel.Text = "Start";
                RecordLabel.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
                RecordButton.IsEnabled = true;
                SetRecordButtonFill(tealGrad);
                StopPulse();
                if (_recording.Elapsed == TimeSpan.Zero)
                    TimerText.Text = "00:00:00";
                TimerText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
                break;
        }

        EncoderText.Text = _ffmpegLocator.IsAvailable()
            ? $"Encoder: {_recording.EncoderDisplayName}"
            : "Encoder: FFmpeg not found";

        if (!string.IsNullOrEmpty(_recording.OutputPath) && state != RecordingState.Recording)
            LastFileText.Text = $"Last file: {_recording.OutputPath}";

        UpdateSettingsEnabled();
        _tray.UpdateRecordingState(state == RecordingState.Recording);
    }

    private void SetRecordButtonFill(System.Windows.Media.Brush fill)
    {
        RecordButton.ApplyTemplate();
        if (RecordButton.Template?.FindName("Outer", RecordButton) is System.Windows.Shapes.Ellipse outer)
            outer.Fill = fill;
    }

    private void StartPulse()
    {
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.BeginAnimation(WidthProperty, null);
        PulseRing.BeginAnimation(HeightProperty, null);

        var opacity = new DoubleAnimation(0.7, 0.0, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var size = new DoubleAnimation(88, 118, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

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
        var enabled = _recording.State is RecordingState.Idle or RecordingState.Error;
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
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_tray.IsExitRequested)
            return;

        if (_recording.State == RecordingState.Recording)
        {
            var result = System.Windows.MessageBox.Show(
                "A recording is in progress.\n\nYes — hide to tray and keep recording\nNo — stay on this window",
                "RecBox",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Close / Yes hides to tray so the window does not appear in recordings.
        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    private async Task ExitAsync()
    {
        if (_recording.State == RecordingState.Recording)
        {
            var result = System.Windows.MessageBox.Show(
                "A recording is in progress. Stop and exit?",
                "RecBox",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            await StopRecordingAsync();
        }

        _tray.Dispose();
        _recording.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
