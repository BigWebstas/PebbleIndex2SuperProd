using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace Index2SP;

internal static class Program
{
    private static Mutex? _instanceLock;

    [STAThread]
    public static int Main(string[] args)
    {
        // A relaunch after a crash can start while the dying process still holds the lock.
        var restarted = args.Contains(SelfRestart.Flag);
        _instanceLock = new Mutex(initiallyOwned: false, "Index2SP.SingleInstance");
        bool owned;
        try { owned = _instanceLock.WaitOne(restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero); }
        // The previous process died holding it — the lock is ours now.
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            Console.Error.WriteLine("Index2SP is already running (check the system tray).");
            return 1;
        }

        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            _instanceLock.ReleaseMutex();
        }
    }

    // Used by the Avalonia design-time tooling as well.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
