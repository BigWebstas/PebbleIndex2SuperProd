using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Index2SP;

/// <summary>
/// The tray icon, its menu, and the lifecycle of the webhook server. Owns all UI-thread state.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _configPath;
    private readonly Logger _log;
    private readonly NotifyIcon _tray;
    private readonly Control _marshal;

    private AppConfig _config;
    private WebhookServer? _server;
    private LogForm? _logForm;

    private int _created;
    private int _failed;

    public TrayApplicationContext(AppConfig config, string configPath, Logger log)
    {
        _config = config;
        _configPath = configPath;
        _log = log;

        // Hidden control whose handle is created on this (UI) thread, used to marshal
        // thread-pool callbacks from the webhook server back onto the UI thread.
        _marshal = new Control();
        _ = _marshal.Handle; // force handle creation on the UI thread

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Create(active: true),
            Text = "Index2SP",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowLog();
        _tray.BalloonTipClicked += (_, _) => ShowLog();

        RebuildMenu();

        // Start once the message loop is pumping, so awaits resume on the UI thread.
        _marshal.BeginInvoke(new Action(() => _ = StartServerAsync(initial: true)));
    }

    // ---- menu ------------------------------------------------------------

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();

        var status = new ToolStripMenuItem(StatusLine()) { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add($"Tasks created: {_created}   failed: {_failed}").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());

        var toggle = new ToolStripMenuItem(
            _server?.IsRunning == true ? "Stop listener" : "Start listener",
            null, async (_, _) => await ToggleServerAsync());
        menu.Items.Add(toggle);

        menu.Items.Add("Copy webhook URL", null, (_, _) => CopyWebhookUrl());
        menu.Items.Add("Test Super Productivity connection", null, async (_, _) => await TestConnectionAsync());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Edit config…", null, (_, _) => OpenConfig());
        menu.Items.Add("Reload config", null, async (_, _) => await ReloadConfigAsync());

        var startup = new ToolStripMenuItem("Start at login", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false,
            Enabled = StartupManager.IsSupported,
            Checked = StartupManager.IsSupported && SafeIsStartupEnabled(),
        };
        menu.Items.Add(startup);

        menu.Items.Add("View log", null, (_, _) => ShowLog());
        menu.Items.Add("Open log folder", null, (_, _) => OpenPath(_log.LogDirectory));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add($"Index2SP v{AppInfo.Version}").Enabled = false;
        menu.Items.Add("Quit", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = menu;
    }

    private string StatusLine() =>
        _server?.IsRunning == true
            ? $"Listening on {_config.ListenAddress}:{_config.Port}{_config.WebhookPath}"
            : "Listener stopped";

    // ---- actions -------------------------------------------------------

    private async Task StartServerAsync(bool initial = false)
    {
        try
        {
            _server = new WebhookServer(_config, _log);
            _server.TaskCreated += OnTaskCreated;
            _server.WebhookFailed += OnWebhookFailed;
            await _server.StartAsync();
            UpdateTrayState();
            if (!initial) Notify("Listener started", StatusLine(), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to start webhook listener", ex);
            _server = null;
            UpdateTrayState();
            Notify("Listener failed to start", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is null) return;
        _server.TaskCreated -= OnTaskCreated;
        _server.WebhookFailed -= OnWebhookFailed;
        await _server.StopAsync();
        _server = null;
        UpdateTrayState();
    }

    private async Task ToggleServerAsync()
    {
        if (_server?.IsRunning == true)
        {
            await StopServerAsync();
            Notify("Listener stopped", "No longer accepting Pebble webhooks", ToolTipIcon.Info);
        }
        else
        {
            await StartServerAsync();
        }
    }

    private async Task ReloadConfigAsync()
    {
        try
        {
            var reloaded = AppConfig.LoadOrCreate(_configPath);
            _config = reloaded;
            _log.Info("Config reloaded");
            await StopServerAsync();
            await StartServerAsync();
            Notify("Config reloaded", StatusLine(), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to reload config", ex);
            Notify("Config reload failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task TestConnectionAsync()
    {
        _log.Info("Testing Super Productivity connection…");
        try
        {
            using var sp = new SuperProductivityClient(_config.SuperProductivity);
            var msg = await sp.TestAsync();
            _log.Info(msg);
            Notify("Super Productivity", msg, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Connection test failed", ex);
            Notify("Super Productivity — not reachable", ex.Message, ToolTipIcon.Error);
        }
    }

    private void CopyWebhookUrl()
    {
        var url = _config.LocalWebhookUrl;
        try
        {
            Clipboard.SetText(url);
            Notify("Copied local webhook URL", $"{url}\nPut your HTTPS tunnel host in front of this in Pebble.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Clipboard copy failed", ex);
        }
    }

    private static bool SafeIsStartupEnabled()
    {
        try { return StartupManager.IsEnabled(); }
        catch { return false; }
    }

    private void ToggleStartup()
    {
        try
        {
            var enable = !StartupManager.IsEnabled();
            StartupManager.SetEnabled(enable);
            _log.Info($"Run at login {(enable ? "enabled" : "disabled")}");
            Notify("Start at login", enable ? "Index2SP will start when you log in." : "Index2SP will no longer start at login.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to change 'Start at login'", ex);
            Notify("Couldn't change 'Start at login'", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            RebuildMenu();
        }
    }

    private void OpenConfig()
    {
        if (!File.Exists(_configPath))
            _config.Save(_configPath);
        OpenPath(_configPath);
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* nothing we can do from the tray */ }
    }

    private void ShowLog()
    {
        if (_logForm is null || _logForm.IsDisposed)
        {
            _logForm = new LogForm(_log);
            _logForm.FormClosed += (_, _) => _logForm = null;
        }
        _logForm.Show();
        _logForm.BringToFront();
        _logForm.WindowState = FormWindowState.Normal;
        _logForm.Activate();
    }

    private void ExitApp()
    {
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        await StopServerAsync();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    // ---- server event handlers (fire on thread-pool threads) ------------

    private void RunOnUi(Action action)
    {
        if (_marshal.IsDisposed) return;
        if (_marshal.InvokeRequired) _marshal.BeginInvoke(action);
        else action();
    }

    private void OnTaskCreated(string title, string? taskId)
    {
        RunOnUi(() =>
        {
            _created++;
            RebuildMenu();
            if (_config.Notifications)
                Notify("Task created", title, ToolTipIcon.Info);
        });
    }

    private void OnWebhookFailed(string message)
    {
        RunOnUi(() =>
        {
            _failed++;
            RebuildMenu();
            if (_config.Notifications)
                Notify("Webhook failed", message, ToolTipIcon.Error);
        });
    }

    private void UpdateTrayState()
    {
        var running = _server?.IsRunning == true;
        _tray.Icon?.Dispose();
        _tray.Icon = IconFactory.Create(active: running);
        _tray.Text = Truncate($"Index2SP — {StatusLine()}", 63);
        RebuildMenu();
    }

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _tray.BalloonTipTitle = Truncate(title, 63);
            _tray.BalloonTipText = Truncate(text, 255);
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(5000);
        }
        catch { /* balloon tips are best-effort */ }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _tray.Dispose();
            _logForm?.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Draws the tray icon at runtime so the project ships without a binary .ico.</summary>
internal static class IconFactory
{
    public static Icon Create(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var ring = active ? Color.FromArgb(0x2E, 0xA0, 0x43) : Color.FromArgb(0x8A, 0x8A, 0x8A);
            using var pen = new Pen(ring, 4f);
            g.DrawEllipse(pen, 4, 4, 24, 24);

            using var dot = new SolidBrush(active ? Color.FromArgb(0x1F, 0x6F, 0xEB) : Color.FromArgb(0x5A, 0x5A, 0x5A));
            g.FillEllipse(dot, 13, 13, 6, 6);
        }

        var hicon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hicon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    public static extern bool DestroyIcon(IntPtr handle);
}
