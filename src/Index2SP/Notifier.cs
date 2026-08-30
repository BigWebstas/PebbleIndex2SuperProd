using System.Diagnostics;
using Avalonia.Threading;

namespace Index2SP;

public enum NotifyKind { Info, Warning, Error }

/// <summary>
/// Cross-platform notifications: <c>notify-send</c> (libnotify) on Linux when present,
/// otherwise a small in-app corner toast (Windows, or Linux without a notification daemon).
/// </summary>
internal static class Notifier
{
    private static bool? _notifySend;

    public static void Show(Logger log, string title, string body, NotifyKind kind)
    {
        try
        {
            if (OperatingSystem.IsLinux() && NotifySendAvailable())
            {
                var urgency = kind switch
                {
                    NotifyKind.Error => "critical",
                    NotifyKind.Warning => "normal",
                    _ => "low",
                };
                using var _ = Process.Start(new ProcessStartInfo("notify-send")
                {
                    ArgumentList = { "--app-name=Index2SP", $"--urgency={urgency}", "--expire-time=6000", title, body },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try { ToastWindow.Show(title, body, kind); }
                catch (Exception ex) { log.Error("toast failed", ex); }
            });
        }
        catch (Exception ex)
        {
            log.Error($"notification failed ({title})", ex);
        }
    }

    private static bool NotifySendAvailable()
    {
        if (_notifySend is { } cached) return cached;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("notify-send")
            {
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return (_notifySend = false).Value;
            p.WaitForExit(2000);
            return (_notifySend = true).Value;
        }
        catch
        {
            return (_notifySend = false).Value;
        }
    }
}
