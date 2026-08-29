using Microsoft.Win32;

namespace Index2SP;

/// <summary>
/// Manages the "run at Windows login" setting via the per-user Run key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). No admin rights required.
/// The Inno Setup installer writes the same value name, so the tray checkbox stays in sync.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Index2SP";

    /// <summary>The real host executable, correct for both framework-dependent and single-file builds.</summary>
    private static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsSupported => OperatingSystem.IsWindows() && !string.IsNullOrEmpty(ExecutablePath);

    public static bool IsEnabled()
    {
        if (!IsSupported) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(ValueName) is not string value || value.Length == 0) return false;
        return PathsEqual(Unquote(value), ExecutablePath!);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!IsSupported)
            throw new InvalidOperationException("Run-at-login is only available on Windows.");

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
            key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string Unquote(string s) => s.Trim().Trim('"').Trim();

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
