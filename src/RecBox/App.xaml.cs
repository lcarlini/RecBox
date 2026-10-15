using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RecBox.Services;

namespace RecBox;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\RecBox.SingleInstance";
    private const string ActivateEventName = @"Local\RecBox.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Two recorders fighting over the same devices/files is never what the user wants.
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch
            {
                // first instance still starting
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("Fatal unhandled exception", args.ExceptionObject as Exception);
            (MainWindow as MainWindow)?.EmergencyStop();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppLog.Info($"RecBox {typeof(App).Assembly.GetName().Version} starting (pid {Environment.ProcessId}).");
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var listener = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
        })
        {
            IsBackground = true,
            Name = "RecBox activation listener"
        };
        listener.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI glitch must never take down a running recording.
        AppLog.Error("UI exception (handled)", e.Exception);
        e.Handled = true;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        AppLog.Info($"Windows session ending ({e.ReasonSessionEnding}).");
        (MainWindow as MainWindow)?.EmergencyStop();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("RecBox exiting.");
        try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
