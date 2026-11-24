using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
namespace NextGenIsland;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _wake;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one island at a time: a second launch just brings the first one forward.
        _single = new Mutex(true, "NextGenIsland.SingleInstance", out bool first);
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, "NextGenIsland.Wake");
        if (!first)
        {
            try { _wake.Set(); } catch { }
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_wake.WaitOne())
                Dispatcher.BeginInvoke(new Action(() => (Current.MainWindow as NextGenIsland.MainWindow)?.RestoreFromTray()));
        }) { IsBackground = true, Name = "NextGenIsland wake" };
        listener.Start();

        // Anything unexpected is written to the log; UI-thread errors are survived instead of closing the app.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("Unhandled UI error", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("Fatal error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("Background task error", args.Exception);
            args.SetObserved();
        };

        // Let animations run at up to 120 fps on high-refresh displays (WPF defaults to 60).
        try
        {
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 120 });
        }
        catch { }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppLog.Write("Started v" + typeof(App).Assembly.GetName().Version);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
