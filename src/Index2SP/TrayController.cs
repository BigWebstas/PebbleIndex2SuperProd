using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Index2SP;

/// <summary>
/// Owns the tray icon, its native menu, the webhook server lifecycle and the background
/// Super Productivity health check. All members touch UI state, so everything runs on the
/// Avalonia UI thread; webhook-server callbacks are marshalled back with the dispatcher.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly string _configPath;
    private readonly Logger _log;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _healthTimer;

    private AppConfig _config;
    private WebhookServer? _server;
    private LogWindow? _logWindow;

    private int _created;
    private int _failed;
    private int _tests;

    private SpHealth _spHealth = SpHealth.Unknown;
    private bool _healthCheckInFlight;

    private IReadOnlyList<SpNamedItem> _projects = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _tags = Array.Empty<SpNamedItem>();

    public TrayController(IClassicDesktopStyleApplicationLifetime desktop, AppConfig config, string configPath, Logger log)
    {
        _desktop = desktop;
        _config = config;
        _configPath = configPath;
        _log = log;

        _tray = new TrayIcon
        {
            Icon = IconRenderer.Tray(listening: false, SpHealth.Unknown),
            ToolTipText = "Index2SP",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowLog();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });

        _healthTimer = new DispatcherTimer();
        _healthTimer.Tick += async (_, _) => await RunHealthCheckAsync(manual: false);
        ConfigureHealthTimer();

        RebuildMenu();

        // Start once the dispatcher loop is running so awaits resume on the UI thread.
        Dispatcher.UIThread.Post(() => _ = StartServerAsync(initial: true));
    }

    private void ConfigureHealthTimer()
    {
        _healthTimer.Stop();
        var seconds = _config.HealthCheckSeconds;
        if (seconds > 0)
        {
            _healthTimer.Interval = TimeSpan.FromSeconds(seconds);
            _healthTimer.Start();
        }
    }

    // ---- menu ----------------------------------------------------------

    private void RebuildMenu()
    {
        var menu = new NativeMenu();

        menu.Add(Disabled(StatusLine()));
        menu.Add(Disabled($"Tasks created: {_created}   failed: {_failed}   tests: {_tests}"));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Action(_server?.IsRunning == true ? "Stop listener" : "Start listener",
            () => _ = ToggleServerAsync()));
        menu.Add(Action("Copy webhook URL", CopyWebhookUrl));
        menu.Add(Action("Test Super Productivity connection", () => _ = RunHealthCheckAsync(manual: true)));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(new NativeMenuItem("Default project") { Menu = BuildProjectSubmenu() });
        menu.Add(new NativeMenuItem("Default tags") { Menu = BuildTagsSubmenu() });
        menu.Add(Action("Refresh projects & tags", () => _ = RefreshListsAsync(notifyOnError: true)));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Action("Edit config…", OpenConfig));
        menu.Add(Action("Reload config", () => _ = ReloadConfigAsync()));

        var startup = new NativeMenuItem("Start at login")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = StartupManager.IsSupported && SafeStartupEnabled(),
            IsEnabled = StartupManager.IsSupported,
        };
        startup.Click += (_, _) => ToggleStartup();
        menu.Add(startup);

        menu.Add(Action("View log", ShowLog));
        menu.Add(Action("Open log folder", () => OpenPath(_log.LogDirectory)));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(Disabled($"Index2SP v{AppInfo.Version}"));
        menu.Add(Action("Quit", Quit));

        _tray.Menu = menu;
    }

    private static NativeMenuItem Disabled(string header) => new(header) { IsEnabled = false };

    private static NativeMenuItem Action(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
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

    private NativeMenu BuildProjectSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.SuperProductivity.ProjectId ?? "";

        var none = new NativeMenuItem("(Inbox / no project)")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = current.Length == 0,
        };
        none.Click += (_, _) => SetDefaultProject("", "Inbox");
        m.Add(none);

        if (_projects.Count == 0)
        {
            m.Add(new NativeMenuItem("(run “Refresh projects & tags”)") { IsEnabled = false });
            return m;
        }

        m.Add(new NativeMenuItemSeparator());
        foreach (var p in _projects.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = p.Id;
            var title = p.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetDefaultProject(id, title);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildTagsSubmenu()
    {
        var m = new NativeMenu();
        var selected = new HashSet<string>(_config.SuperProductivity.TagIds ?? new List<string>(), StringComparer.Ordinal);

        if (_tags.Count == 0)
        {
            m.Add(new NativeMenuItem("(run “Refresh projects & tags”)") { IsEnabled = false });
            return m;
        }

        foreach (var t in _tags.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = t.Id;
            var title = t.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = selected.Contains(id),
            };
            item.Click += (_, _) => ToggleDefaultTag(id, title);
            m.Add(item);
        }

        m.Add(new NativeMenuItemSeparator());
        var clear = new NativeMenuItem("Clear all") { IsEnabled = selected.Count > 0 };
        clear.Click += (_, _) =>
        {
            (_config.SuperProductivity.TagIds ??= new List<string>()).Clear();
            SaveConfig("cleared all default tags");
        };
        m.Add(clear);
        return m;
    }

    // ---- server lifecycle --------------------------------------------

    private async Task StartServerAsync(bool initial = false)
    {
        try
        {
            _server = new WebhookServer(_config, _log);
            _server.TaskCreated += OnTaskCreated;
            _server.WebhookFailed += OnWebhookFailed;
            _server.TestEventReceived += OnTestEventReceived;
            await _server.StartAsync();
            RefreshTray();
            if (!initial) Notify("Listener started", StatusLine(), NotifyKind.Info);
            _ = RunHealthCheckAsync(manual: false);
            _ = RefreshListsAsync(notifyOnError: false);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to start webhook listener", ex);
            _server = null;
            RefreshTray();
            Notify("Listener failed to start", ex.Message, NotifyKind.Error);
        }
    }

    /// <summary>
    /// Synchronously drains the webhook server. Safe to call from a non-UI thread (e.g. a POSIX
    /// signal handler) — does not touch the dispatcher or any Avalonia object.
    /// </summary>
    public void StopServerForShutdown()
    {
        var server = _server;
        if (server is null) return;
        try { server.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.Warn($"webhook server stop: {ex.Message}"); }
    }

    private async Task StopServerAsync()
    {
        if (_server is null) return;
        _server.TaskCreated -= OnTaskCreated;
        _server.WebhookFailed -= OnWebhookFailed;
        _server.TestEventReceived -= OnTestEventReceived;
        await _server.StopAsync();
        _server = null;
        RefreshTray();
    }

    private async Task ToggleServerAsync()
    {
        if (_server?.IsRunning == true)
        {
            await StopServerAsync();
            Notify("Listener stopped", "No longer accepting Pebble webhooks", NotifyKind.Info);
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
            _config = AppConfig.LoadOrCreate(_configPath);
            _log.Info("Config reloaded");
            _spHealth = SpHealth.Unknown;
            ConfigureHealthTimer();
            await StopServerAsync();
            await StartServerAsync();
            Notify("Config reloaded", StatusLine(), NotifyKind.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to reload config", ex);
            Notify("Config reload failed", ex.Message, NotifyKind.Error);
        }
    }

    // ---- health check -----------------------------------------------

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

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable)
                    Notify("Super Productivity unreachable", message, NotifyKind.Warning);

                RefreshTray();
            }

            if (manual)
                Notify(state == SpHealth.Ok ? "Super Productivity" : "Super Productivity — not reachable",
                    message, state == SpHealth.Ok ? NotifyKind.Info : NotifyKind.Error, force: true);
        }
        finally
        {
            _healthCheckInFlight = false;
        }
    }

    // ---- default project / tags -------------------------------------

    private async Task RefreshListsAsync(bool notifyOnError)
    {
        var projects = await FetchAsync(c => c.GetProjectsAsync(), "projects", notifyOnError);
        if (projects is not null) _projects = projects;
        var tags = await FetchAsync(c => c.GetTagsAsync(), "tags", notifyOnError);
        if (tags is not null) _tags = tags;
        RebuildMenu();
    }

    private async Task<IReadOnlyList<SpNamedItem>?> FetchAsync(
        Func<SuperProductivityClient, Task<IReadOnlyList<SpNamedItem>>> call, string what, bool notifyOnError)
    {
        try
        {
            using var sp = new SuperProductivityClient(_config.SuperProductivity);
            return await call(sp);
        }
        catch (Exception ex)
        {
            _log.Warn($"Couldn't load {what}: {ex.Message}");
            if (notifyOnError) Notify($"Couldn't load {what}", ex.Message, NotifyKind.Error);
            return null;
        }
    }

    private void SetDefaultProject(string id, string label)
    {
        _config.SuperProductivity.ProjectId = id;
        SaveConfig(id.Length == 0 ? "default project cleared (inbox)" : $"default project = \"{label}\" [{id}]");
        Notify("Default project updated",
            id.Length == 0 ? "New tasks go to the inbox." : $"New tasks → {label}", NotifyKind.Info);
    }

    private void ToggleDefaultTag(string id, string label)
    {
        var tags = _config.SuperProductivity.TagIds ??= new List<string>();
        var removed = tags.Remove(id);
        if (!removed) tags.Add(id);
        SaveConfig(removed ? $"removed default tag \"{label}\"" : $"added default tag \"{label}\"");
    }

    private void SaveConfig(string what)
    {
        try
        {
            _config.Save(_configPath);
            _log.Info($"Config saved: {what}");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save config", ex);
            Notify("Couldn't save config", ex.Message, NotifyKind.Error);
        }
        finally
        {
            RebuildMenu();
        }
    }

    // ---- run at login ----------------------------------------------

    private static bool SafeStartupEnabled()
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
            Notify("Start at login", enable
                ? "Index2SP will start when you sign in."
                : "Index2SP will no longer start at login.", NotifyKind.Info);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to change 'Start at login'", ex);
            Notify("Couldn't change 'Start at login'", ex.Message, NotifyKind.Error);
        }
        finally
        {
            RebuildMenu();
        }
    }

    // ---- misc actions --------------------------------------------

    private void CopyWebhookUrl()
    {
        var url = _config.LocalWebhookUrl;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var clipboard = (_logWindow as TopLevel ?? _desktop.MainWindow)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(url);
                Notify("Webhook URL", clipboard is not null
                    ? $"{url}  (copied)\nPrepend your HTTPS tunnel host for Pebble."
                    : $"{url}\nPrepend your HTTPS tunnel host for Pebble.", NotifyKind.Info);
            }
            catch (Exception ex)
            {
                _log.Error("Clipboard copy failed", ex);
                Notify("Webhook URL", url, NotifyKind.Info);
            }
        });
    }

    private void OpenConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) _config.Save(_configPath);
        }
        catch (Exception ex)
        {
            _log.Error("Couldn't create config.json", ex);
        }
        OpenPath(_configPath);
    }

    private void OpenPath(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", new[] { path });
            else
                Process.Start("xdg-open", new[] { path });
        }
        catch (Exception ex)
        {
            _log.Error($"Couldn't open {path}", ex);
            Notify("Couldn't open", path, NotifyKind.Error);
        }
    }

    private void ShowLog()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_logWindow is null)
            {
                _logWindow = new LogWindow(_log);
                _logWindow.Closed += (_, _) => _logWindow = null;
            }

            _logWindow.Show();
            _logWindow.Activate();
        });
    }

    private void Quit() => _ = QuitAsync();

    private async Task QuitAsync()
    {
        _healthTimer.Stop();
        await StopServerAsync();
        _tray.IsVisible = false;
        _desktop.Shutdown();
    }

    // ---- webhook-server callbacks (thread-pool threads) ------------

    private void OnTaskCreated(string title, string? taskId) => Dispatcher.UIThread.Post(() =>
    {
        _created++;
        RebuildMenu();
        Notify("Task created", title, NotifyKind.Info);
    });

    private void OnWebhookFailed(string message) => Dispatcher.UIThread.Post(() =>
    {
        _failed++;
        RebuildMenu();
        Notify("Webhook failed", message, NotifyKind.Error);
    });

    private void OnTestEventReceived(string remote) => Dispatcher.UIThread.Post(() =>
    {
        _tests++;
        RebuildMenu();
        Notify("Test received", $"Pebble webhook is reaching Index2SP (from {remote}). No task created.",
            NotifyKind.Info, force: true);
    });

    // ---- helpers -------------------------------------------------

    private void RefreshTray()
    {
        _tray.Icon = IconRenderer.Tray(_server?.IsRunning == true, _spHealth);
        _tray.ToolTipText = $"Index2SP — {StatusLine()}";
        RebuildMenu();
    }

    private void Notify(string title, string body, NotifyKind kind, bool force = false)
    {
        if (!force && kind == NotifyKind.Info && !_config.Notifications) return;
        Notifier.Show(_log, title, body, kind);
    }

    public void Dispose()
    {
        _healthTimer.Stop();
        try { StopServerAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
        try { _tray.IsVisible = false; _tray.Dispose(); } catch { /* ignore */ }
        try { _logWindow?.Close(); } catch { /* ignore */ }
    }
}
