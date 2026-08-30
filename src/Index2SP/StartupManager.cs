using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Index2SP;

/// <summary>
/// "Run at login", per user, no admin rights:
///   Windows — HKCU\Software\Microsoft\Windows\CurrentVersion\Run
///   Linux   — ~/.config/autostart/index2sp.desktop  (XDG autostart)
/// The Windows installer writes the same Run value name so the tray checkbox stays in sync.
/// </summary>
public static class StartupManager
{
    private const string ValueName = "Index2SP";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsSupported =>
        !string.IsNullOrEmpty(ExecutablePath) && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    public static bool IsEnabled()
    {
        if (!IsSupported) return false;
        if (OperatingSystem.IsWindows()) return WindowsIsEnabled();
        if (OperatingSystem.IsLinux()) return LinuxIsEnabled();
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Run-at-login is not available on this platform.");
        if (OperatingSystem.IsWindows()) WindowsSetEnabled(enabled);
        else if (OperatingSystem.IsLinux()) LinuxSetEnabled(enabled);
    }

    // ---- Windows -------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static bool WindowsIsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(ValueName) is not string value || value.Length == 0) return false;
        return PathsEqual(value.Trim().Trim('"'), ExecutablePath!);
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsSetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // ---- Linux (XDG autostart) ---------------------------------------

    private static string LinuxDesktopFilePath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(configHome, "autostart", "index2sp.desktop");
        }
    }

    private static bool LinuxIsEnabled()
    {
        var path = LinuxDesktopFilePath;
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        return ExecutablePath is null || text.Contains(ExecutablePath, StringComparison.Ordinal);
    }

    private static void LinuxSetEnabled(bool enabled)
    {
        var path = LinuxDesktopFilePath;
        if (!enabled)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entry = string.Join('\n', new[]
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=Index2SP",
            "Comment=Pebble Index 01 webhook -> Super Productivity bridge",
            $"Exec=\"{ExecutablePath}\"",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            "",
        });
        File.WriteAllText(path, entry);
    }

    // ---- shared -----------------------------------------------------

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
