using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Index2SP;

public partial class App : Application
{
    private TrayController? _tray;
    private Logger? _log;
    private readonly List<PosixSignalRegistration> _signals = new();
    private Timer? _shutdownWatchdog;
    private int _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-only app: don't quit when the log window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var log = _log = new Logger();
            log.Info($"Index2SP v{AppInfo.Version} starting (pid {Environment.ProcessId}, {RuntimeInformation.OSDescription})");

            AppConfig config;
            try
            {
                config = AppConfig.LoadOrCreate(AppConfig.DefaultPath);
            }
            catch (Exception ex)
            {
                log.Error("Failed to read config.json — starting with defaults", ex);
                config = new AppConfig();
            }

            _tray = new TrayController(desktop, config, AppConfig.DefaultPath, log);

            desktop.Exit += (_, _) =>
            {
                _shutdownWatchdog?.Dispose();
                foreach (var s in _signals) s.Dispose();
                _signals.Clear();
                _tray?.Dispose();
                log.Info("Index2SP exiting");
            };

            RegisterSignal(PosixSignal.SIGTERM);
            RegisterSignal(PosixSignal.SIGINT);
            RegisterSignal(PosixSignal.SIGQUIT);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void RegisterSignal(PosixSignal signal)
    {
        try
        {
            _signals.Add(PosixSignalRegistration.Create(signal, OnPosixSignal));
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
        {
            // e.g. SIGQUIT on Windows — not fatal.
        }
    }

    private void OnPosixSignal(PosixSignalContext context)
    {
        // Handle it ourselves: stop Kestrel + the tray cleanly instead of the runtime
        // terminating the process where nothing gets a chance to shut down.
        context.Cancel = true;

        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
            return;

        _log?.Info($"Received {context.Signal} — shutting down");

        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });

        // Safety net if the UI thread is wedged and can't run the shutdown.
        _shutdownWatchdog = new Timer(_ => Environment.Exit(0), null, 5000, Timeout.Infinite);
    }
}
