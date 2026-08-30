using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Index2SP;

public partial class App : Application
{
    private TrayController? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-only app: don't quit when the log window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var log = new Logger();
            log.Info($"Index2SP v{AppInfo.Version} starting (pid {Environment.ProcessId}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription})");

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
                _tray?.Dispose();
                log.Info("Index2SP exiting");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
