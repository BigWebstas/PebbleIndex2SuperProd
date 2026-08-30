using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Index2SP;

public partial class App : Application
{
    private TrayController? _tray;
    private Logger? _log;
    private readonly List<PosixSignalRegistration> _signals = new();
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
                foreach (var s in _signals) s.Dispose();
                _signals.Clear();
                _tray?.Dispose();
                log.Info("Index2SP exiting");
            };

            // Clean up on kill / systemctl stop / Ctrl+C — Avalonia's lifetime doesn't do this.
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
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            context.Cancel = true; // another signal is already being handled
            return;
        }

        context.Cancel = true; // we terminate the process ourselves, after draining

        _log?.Info($"Received {context.Signal} — stopping webhook listener and exiting");

        // Drain Kestrel off the signal thread with a hard timeout. Don't touch the Avalonia
        // dispatcher from here — calling Shutdown() deadlocks the UI thread.
        try
        {
            Task.Run(() => _tray?.StopServerForShutdown()).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log?.Warn($"shutdown drain: {ex.Message}");
        }

        _log?.Info("Index2SP exiting");

        // Last-resort hard kill if Environment.Exit stalls (e.g. a wedged ProcessExit handler).
        new Thread(() =>
        {
            Thread.Sleep(3000);
            try { Process.GetCurrentProcess().Kill(); } catch { /* nothing left to do */ }
        }) { IsBackground = true }.Start();

        Environment.Exit(0);
    }
}
