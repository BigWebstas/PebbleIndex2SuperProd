using System.Diagnostics;

namespace Index2SP;

/// <summary>
/// Relaunches the app after a fatal crash so the webhook listener doesn't stay down until someone
/// notices. Crashes are recorded in <c>crashes.log</c>; more than <see cref="MaxRestarts"/> within
/// <see cref="Window"/> means something is broken at startup, so it stops and leaves the app down
/// rather than loop forever.
/// </summary>
public static class SelfRestart
{
    /// <summary>Passed to the relaunched process so it waits for the dying one to release the
    /// single-instance lock instead of giving up.</summary>
    public const string Flag = "--restarted";

    public const int MaxRestarts = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <param name="crashes">Recent crash times, including the one that just happened.</param>
    public static bool ShouldRestart(IEnumerable<DateTimeOffset> crashes, DateTimeOffset now) =>
        crashes.Count(c => now - c < Window) <= MaxRestarts;

    public static void TryRestart(Logger log)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var path = Path.Combine(AppConfig.ConfigDirectory, "crashes.log");

            var crashes = new List<DateTimeOffset>();
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                    if (DateTimeOffset.TryParse(line, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) && now - t < Window)
                        crashes.Add(t);
            crashes.Add(now);
            File.WriteAllLines(path, crashes.Select(c => c.ToString("O")));

            if (!ShouldRestart(crashes, now))
            {
                log.Error($"Crashed {crashes.Count} times in {Window.TotalMinutes:0} minutes — not restarting");
                return;
            }

            var exe = Environment.ProcessPath;
            if (exe is null) return;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            // Started as `dotnet Index2SP.dll`: the host is the process, so the dll has to be passed along.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            psi.ArgumentList.Add(Flag);

            Process.Start(psi);
            log.Warn("Restarting after a fatal crash");
        }
        catch (Exception ex)
        {
            log.Warn($"Could not restart after the crash: {ex.Message}");
        }
    }
}
