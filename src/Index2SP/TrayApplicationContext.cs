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
    private readonly System.Windows.Forms.Timer _healthTimer;

    private AppConfig _config;
    private WebhookServer? _server;
    private LogForm? _logForm;

    private int _created;
    private int _failed;

    private SpHealth _spHealth = SpHealth.Unknown;
    private bool _healthCheckInFlight;

    private IReadOnlyList<SpNamedItem> _projectsCache = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _tagsCache = Array.Empty<SpNamedItem>();

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
            Icon = IconFactory.Create(listening: false, SpHealth.Unknown),
            Text = "Index2SP",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowLog();
        _tray.BalloonTipClicked += (_, _) => ShowLog();

        _healthTimer = new System.Windows.Forms.Timer();
        _healthTimer.Tick += async (_, _) => await RunHealthCheckAsync(manual: false);
        ConfigureHealthTimer();

        RebuildMenu();

        // Start once the message loop is pumping, so awaits resume on the UI thread.
        _marshal.BeginInvoke(new Action(() => _ = StartServerAsync(initial: true)));
    }

    private void ConfigureHealthTimer()
    {
        _healthTimer.Stop();
        var seconds = _config.HealthCheckSeconds;
        if (seconds > 0)
        {
            _healthTimer.Interval = seconds * 1000;
            _healthTimer.Start();
        }
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

        var projectItem = new ToolStripMenuItem("Default project");
        BuildProjectDropDown(projectItem);
        projectItem.DropDownOpening += async (_, _) =>
        {
            var list = await FetchNamedListAsync(sp => sp.GetProjectsAsync(), "projects");
            if (list is null) return;
            _projectsCache = list;
            try { BuildProjectDropDown(projectItem); } catch (Exception ex) { _log.Error("project menu", ex); }
        };
        menu.Items.Add(projectItem);

        var tagsItem = new ToolStripMenuItem("Default tags");
        BuildTagsDropDown(tagsItem);
        tagsItem.DropDownOpening += async (_, _) =>
        {
            var list = await FetchNamedListAsync(sp => sp.GetTagsAsync(), "tags");
            if (list is null) return;
            _tagsCache = list;
            try { BuildTagsDropDown(tagsItem); } catch (Exception ex) { _log.Error("tag menu", ex); }
        };
        menu.Items.Add(tagsItem);

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

    private string StatusLine()
    {
        if (_server?.IsRunning != true) return "Listener stopped";
        var sp = _spHealth switch
        {
            SpHealth.Ok => "SP reachable",
            SpHealth.Unreachable => "SP unreachable",
            _ => "SP not checked yet",
        };
        return $"Listening on {_config.ListenAddress}:{_config.Port}{_config.WebhookPath}  ·  {sp}";
    }

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
            _ = RunHealthCheckAsync(manual: false); // prime the SP status right away
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
            _spHealth = SpHealth.Unknown;
            ConfigureHealthTimer();
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

    private Task TestConnectionAsync() => RunHealthCheckAsync(manual: true);

    /// <summary>
    /// Probes the Super Productivity API. Runs on a timer (every <c>healthCheckSeconds</c>) to keep
    /// the tray status fresh, and on demand from the menu. Only logs / notifies on state changes;
    /// a manual run always notifies.
    /// </summary>
    private async Task RunHealthCheckAsync(bool manual)
    {
        if (!manual && (_healthCheckInFlight || _server?.IsRunning != true)) return;
        _healthCheckInFlight = true;
        try
        {
            SpHealth state;
            string message;
            try
            {
                using var sp = new SuperProductivityClient(_config.SuperProductivity);
                message = await sp.TestAsync();
                state = SpHealth.Ok;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                state = SpHealth.Unreachable;
            }

            var prev = _spHealth;
            _spHealth = state;

            if (state != prev)
            {
                if (state == SpHealth.Ok)
                    _log.Info(prev == SpHealth.Unreachable
                        ? $"Super Productivity connection restored — {message}"
                        : $"Super Productivity reachable — {message}");
                else
                    _log.Warn($"Super Productivity unreachable — {message}");

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable && _config.Notifications)
                    Notify("Super Productivity unreachable", message, ToolTipIcon.Warning);

                UpdateTrayState();
            }

            if (manual)
                Notify(
                    state == SpHealth.Ok ? "Super Productivity" : "Super Productivity — not reachable",
                    message,
                    state == SpHealth.Ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        finally
        {
            _healthCheckInFlight = false;
        }
    }

    // ---- default project / tags pickers -------------------------------

    private async Task<List<SpNamedItem>?> FetchNamedListAsync(
        Func<SuperProductivityClient, Task<IReadOnlyList<SpNamedItem>>> call, string what)
    {
        try
        {
            using var sp = new SuperProductivityClient(_config.SuperProductivity);
            var list = await call(sp);
            return list.ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Couldn't load {what} from Super Productivity", ex);
            Notify($"Couldn't load {what}", ex.Message, ToolTipIcon.Error);
            return null;
        }
    }

    private void BuildProjectDropDown(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var current = _config.SuperProductivity.ProjectId ?? "";

        var none = new ToolStripMenuItem("(Inbox / no project)")
        {
            CheckOnClick = false,
            Checked = current.Length == 0,
        };
        none.Click += (_, _) => SetDefaultProject("", "Inbox");
        parent.DropDownItems.Add(none);

        if (_projectsCache.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("Open this menu to load from Super Productivity…") { Enabled = false });
            if (current.Length > 0)
                parent.DropDownItems.Add(new ToolStripMenuItem($"current id: {current}") { Enabled = false, Checked = true });
            return;
        }

        parent.DropDownItems.Add(new ToolStripSeparator());
        foreach (var p in _projectsCache.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = p.Id;
            var title = p.Title;
            var item = new ToolStripMenuItem(title) { CheckOnClick = false, Checked = id == current };
            item.Click += (_, _) => SetDefaultProject(id, title);
            parent.DropDownItems.Add(item);
        }
    }

    private void SetDefaultProject(string id, string label)
    {
        try
        {
            _config.SuperProductivity.ProjectId = id;
            _config.Save(_configPath);
            _log.Info(id.Length == 0 ? "Default project cleared (inbox)" : $"Default project set to \"{label}\" [{id}]");
            Notify("Default project updated",
                id.Length == 0 ? "New tasks go to the inbox." : $"New tasks → {label}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save default project", ex);
            Notify("Couldn't save default project", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            RebuildMenu();
        }
    }

    private void BuildTagsDropDown(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var selected = new HashSet<string>(_config.SuperProductivity.TagIds ?? new List<string>(), StringComparer.Ordinal);

        if (_tagsCache.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("Open this menu to load from Super Productivity…") { Enabled = false });
            if (selected.Count > 0)
                parent.DropDownItems.Add(new ToolStripMenuItem($"{selected.Count} tag id(s) currently set") { Enabled = false });
            return;
        }

        foreach (var t in _tagsCache.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = t.Id;
            var title = t.Title;
            var item = new ToolStripMenuItem(title) { CheckOnClick = false, Checked = selected.Contains(id) };
            item.Click += (_, _) => ToggleDefaultTag(id, title);
            parent.DropDownItems.Add(item);
        }

        parent.DropDownItems.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem("Clear all") { Enabled = selected.Count > 0 };
        clear.Click += (_, _) =>
        {
            _config.SuperProductivity.TagIds.Clear();
            SaveTags("cleared all default tags");
        };
        parent.DropDownItems.Add(clear);
    }

    private void ToggleDefaultTag(string id, string label)
    {
        var tags = _config.SuperProductivity.TagIds ??= new List<string>();
        var removed = tags.Remove(id);
        if (!removed) tags.Add(id);
        SaveTags(removed ? $"removed tag \"{label}\"" : $"added tag \"{label}\"");
    }

    private void SaveTags(string what)
    {
        try
        {
            _config.Save(_configPath);
            _log.Info($"Default tags: {what} (now {_config.SuperProductivity.TagIds.Count})");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save default tags", ex);
            Notify("Couldn't save default tags", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            RebuildMenu();
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
        _tray.Icon = IconFactory.Create(running, _spHealth);
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
            _healthTimer.Dispose();
            _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _tray.Dispose();
            _logForm?.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Result of the background Super Productivity health check.</summary>
internal enum SpHealth { Unknown, Ok, Unreachable }

/// <summary>Draws the tray icon at runtime so the project ships without a binary .ico.</summary>
internal static class IconFactory
{
    public static Icon Create(bool listening, SpHealth health)
    {
        // ring shows the listener; dot shows the Super Productivity link
        var ringColor = listening
            ? Color.FromArgb(0x2E, 0xA0, 0x43)   // green
            : Color.FromArgb(0x8A, 0x8A, 0x8A);  // grey

        var dotColor = !listening
            ? Color.FromArgb(0x5A, 0x5A, 0x5A)               // grey
            : health switch
            {
                SpHealth.Ok => Color.FromArgb(0x1F, 0x6F, 0xEB),          // blue
                SpHealth.Unreachable => Color.FromArgb(0xE3, 0x6A, 0x17), // orange
                _ => Color.FromArgb(0x9A, 0x9A, 0x9A),                    // grey — not checked
            };

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var pen = new Pen(ringColor, 4f);
            g.DrawEllipse(pen, 4, 4, 24, 24);

            using var dot = new SolidBrush(dotColor);
            g.FillEllipse(dot, 12, 12, 8, 8);
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
