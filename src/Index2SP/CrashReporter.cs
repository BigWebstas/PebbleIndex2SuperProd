namespace Index2SP;

/// <summary>
/// Best-effort diagnostic for an otherwise-uncaught exception: logs it, and tries to create a
/// Super Productivity task carrying the error, so a crash (or a bug the UI-thread hook recovers
/// from) leaves a visible trace even if nobody was watching the log file at the time.
/// </summary>
public static class CrashReporter
{
    /// <summary>Non-fatal path (<c>Dispatcher.UIThread.UnhandledException</c>) — the app keeps
    /// running after this, so the report can go out fully async on the client's normal timeout.</summary>
    public static void ReportRecovered(AppConfig config, Logger log, Exception ex, string context)
    {
        log.Error($"Unhandled exception on {context} — recovered, app keeps running", ex);
        _ = SendAsync(config, log, ex, context, CancellationToken.None);
    }

    /// <summary>Fatal path (<c>AppDomain.UnhandledException</c>) — the process can terminate the
    /// instant this handler returns, so this blocks with a short hard timeout instead of
    /// firing off a task that would otherwise never get the chance to complete.</summary>
    public static void ReportFatal(AppConfig config, Logger log, Exception ex, string context)
    {
        log.Error($"Unhandled exception on {context} — process is terminating", ex);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            SendAsync(config, log, ex, context, cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort only — the process is already going down, nothing left to do
        }
    }

    private static async Task SendAsync(AppConfig config, Logger log, Exception ex, string context, CancellationToken ct)
    {
        try
        {
            var task = new SpTaskRequest
            {
                Title = $"Index2SP crashed: {Truncate(ex.Message, 200)}",
                Notes = $"Context: {context}\nTime: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\nVersion: {AppInfo.Version}\n\n{ex}",
            };

            using var sp = new SuperProductivityClient(config.SuperProductivity);
            var result = await sp.CreateTaskAsync(task, ct);
            log.Info($"Crash report task created in Super Productivity{(result.TaskId is null ? "" : $" ({result.TaskId})")}");
        }
        catch (Exception reportEx)
        {
            log.Warn($"Could not create a Super Productivity crash-report task: {reportEx.Message}");
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
