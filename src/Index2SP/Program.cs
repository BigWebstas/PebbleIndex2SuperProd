using System.Threading;
using System.Windows.Forms;

namespace Index2SP;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        _singleInstance = new Mutex(initiallyOwned: true, "Index2SP.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Index2SP is already running (check the system tray).", "Index2SP",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var log = new Logger();
        log.Info($"Index2SP v{AppInfo.Version} starting (pid {Environment.ProcessId})");

        AppConfig config;
        try
        {
            config = AppConfig.LoadOrCreate(AppConfig.DefaultPath);
        }
        catch (Exception ex)
        {
            log.Error("Failed to read config.json", ex);
            MessageBox.Show($"config.json could not be read:\n\n{ex.Message}\n\n{AppConfig.DefaultPath}",
                "Index2SP", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.ThreadException += (_, e) => log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        using var ctx = new TrayApplicationContext(config, AppConfig.DefaultPath, log);
        Application.Run(ctx);

        log.Info("Index2SP exiting");
        _singleInstance.ReleaseMutex();
    }
}
