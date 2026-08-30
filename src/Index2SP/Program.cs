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
        _instanceLock = new Mutex(initiallyOwned: true, "Index2SP.SingleInstance", out var isNew);
        if (!isNew)
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
