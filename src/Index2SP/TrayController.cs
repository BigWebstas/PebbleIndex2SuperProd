using System.Diagnostics;
using System.IO;
using System.Linq;
using Anthropic.Exceptions;
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
    private readonly DispatcherTimer _outboxTimer;
    private readonly Outbox _outbox;

    private AppConfig _config;
    private CaptureTagResolver _captureTag;
    private AiTaskClassifier _classifier;
    private WebhookServer? _server;
    private LogWindow? _logWindow;

    private int _created;
    private int _failed;
    private int _tests;
    private bool _outboxFlushInFlight;

    private SpHealth _spHealth = SpHealth.Unknown;
    private bool _healthCheckInFlight;

    private SpHealth _joplinHealth = SpHealth.Unknown;
    private bool _joplinHealthCheckInFlight;

    private SpHealth _googleHealth = SpHealth.Unknown;
    private bool _googleHealthCheckInFlight;

    private SpHealth _beeperHealth = SpHealth.Unknown;
    private bool _beeperHealthCheckInFlight;

    private SpHealth _telegramHealth = SpHealth.Unknown;
    private bool _telegramHealthCheckInFlight;

    private SpHealth _aiHealth = SpHealth.Unknown;
    private bool _aiHealthCheckInFlight;

    private SpHealth _whisperHealth = SpHealth.Unknown;
    private bool _whisperHealthCheckInFlight;

    private UpdateChecker.UpdateInfo? _updateAvailable;
    private bool _updateCheckInFlight;
    private string? _downloadedUpdatePath;
    private bool _updateDownloadInFlight;
    private readonly DispatcherTimer _updateTimer;

    private IReadOnlyList<SpNamedItem> _projects = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _tags = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _notebooks = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _joplinTags = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _calendars = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _ollamaModels = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _claudeModels = Array.Empty<SpNamedItem>();
    private IReadOnlyList<SpNamedItem> _geminiModels = Array.Empty<SpNamedItem>();

    private static readonly (string Id, string Label)[] AiModels =
    [
        ("claude-haiku-4-5", "Haiku (fast, cheap — default)"),
        ("claude-sonnet-5", "Sonnet (more capable)"),
        ("claude-opus-5", "Opus (most capable)"),
    ];

    private static readonly (string Id, string Label)[] GeminiModels =
    [
        ("gemini-2.5-flash", "Flash (fast, cheap — default)"),
        ("gemini-2.5-pro", "Pro (most capable)"),
    ];

    private static readonly (string Id, string Label)[] OpenAiModels =
    [
        ("gpt-4o-mini", "GPT-4o mini (fast, cheap — default)"),
        ("gpt-4o", "GPT-4o (more capable)"),
    ];

    private static readonly (string Id, string Label)[] AiProviders =
    [
        ("claude", "Claude"),
        ("gemini", "Gemini"),
        ("openai", "OpenAI"),
        ("ollama", "Ollama (local)"),
    ];

    public TrayController(IClassicDesktopStyleApplicationLifetime desktop, AppConfig config, string configPath, Logger log)
    {
        _desktop = desktop;
        _config = config;
        _configPath = configPath;
        _log = log;
        _captureTag = new CaptureTagResolver(config.SuperProductivity, log);
        _classifier = new AiTaskClassifier(config.AiClassifier, log);
        _outbox = new Outbox(AppConfig.ConfigDirectory, log);
        _outbox.ItemDelivered += OnOutboxDelivered;
        _outbox.ItemFailed += OnOutboxFailed;

        _tray = new TrayIcon
        {
            Icon = IconRenderer.Tray(listening: false, SpHealth.Unknown),
            ToolTipText = "Index2SP",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowLog();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });

        _healthTimer = new DispatcherTimer();
        _healthTimer.Tick += async (_, _) =>
        {
            await RunHealthCheckAsync(manual: false);
            await RunJoplinHealthCheckAsync(manual: false);
            await RunGoogleCalendarHealthCheckAsync(manual: false);
            await RunBeeperHealthCheckAsync(manual: false);
            await RunTelegramHealthCheckAsync(manual: false);
            await RunAiHealthCheckAsync(manual: false);
            await RunWhisperHealthCheckAsync(manual: false);
        };
        ConfigureHealthTimer();

        _outboxTimer = new DispatcherTimer();
        _outboxTimer.Tick += (_, _) => _ = FlushOutboxAsync();
        ConfigureOutboxTimer();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateTimer.Tick += (_, _) => _ = RunUpdateCheckAsync(manual: false);
        ConfigureUpdateTimer();

        RebuildMenu();

        // Start once the dispatcher loop is running so awaits resume on the UI thread.
        Dispatcher.UIThread.Post(() => _ = StartServerAsync(initial: true));
        // Drain anything left in the outbox from a previous run.
        Dispatcher.UIThread.Post(() => _ = FlushOutboxAsync());
        Dispatcher.UIThread.Post(() => _ = RunUpdateCheckAsync(manual: false));
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

    private void ConfigureUpdateTimer()
    {
        _updateTimer.Stop();
        if (_config.CheckForUpdates) _updateTimer.Start();
    }

    private void ConfigureOutboxTimer()
    {
        _outboxTimer.Stop();
        _outboxTimer.Interval = TimeSpan.FromSeconds(_config.OutboxRetrySeconds);
        _outboxTimer.Start();
    }

    // ---- menu ----------------------------------------------------------

    private void RebuildMenu()
    {
        var menu = new NativeMenu();

        var queued = _outbox.PendingCount;

        menu.Add(Disabled(StatusLine()));
        menu.Add(Disabled($"Tasks created: {_created}   failed: {_failed}   queued: {queued}   tests: {_tests}"));
        menu.Add(new NativeMenuItemSeparator());

        if (_updateAvailable is { } update)
        {
            if (_downloadedUpdatePath is not null)
                menu.Add(Action($"✓ v{update.Version} downloaded — click to install", LaunchDownloadedUpdate));
            else if (_updateDownloadInFlight)
                menu.Add(Disabled($"⬇ Downloading v{update.Version}…"));
            else if (update.AssetUrl is not null)
                menu.Add(Action($"⬆ Update available: v{update.Version} — click to download", () => _ = DownloadUpdateAsync(update)));
            else
                menu.Add(Action($"⬆ Update available: v{update.Version}", () => OpenPath(update.Url)));
            menu.Add(Action("Open release page", () => OpenPath(update.Url)));
            menu.Add(new NativeMenuItemSeparator());
        }

        menu.Add(Action(_server?.IsRunning == true ? "Stop listener" : "Start listener",
            () => _ = ToggleServerAsync()));
        menu.Add(Action("Copy webhook URL", CopyWebhookUrl));
        menu.Add(Action("Test all connections", () => _ = RunAllConnectionsTestAsync()));
        menu.Add(Action("Refresh projects, tags & notebooks", () => _ = RefreshListsAsync(notifyOnError: true)));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(new NativeMenuItem("Super Productivity") { Menu = BuildSuperProductivitySubmenu() });
        menu.Add(new NativeMenuItem("AI classifier") { Menu = BuildAiClassifierSubmenu() });
        menu.Add(new NativeMenuItem("Joplin notes") { Menu = BuildJoplinSubmenu() });
        menu.Add(new NativeMenuItem("Google Calendar") { Menu = BuildGoogleCalendarSubmenu() });
        menu.Add(new NativeMenuItem("Beeper messages") { Menu = BuildBeeperSubmenu() });
        menu.Add(new NativeMenuItem("Telegram bot") { Menu = BuildTelegramSubmenu() });
        menu.Add(new NativeMenuItem("Whisper transcription") { Menu = BuildWhisperSubmenu() });
        menu.Add(new NativeMenuItem("Web search") { Menu = BuildWebSearchSubmenu() });
        menu.Add(new NativeMenuItem("Webhook receipt") { Menu = BuildWebhookReceiptSubmenu() });
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

        var checkUpdates = new NativeMenuItem("Check for updates automatically")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _config.CheckForUpdates,
        };
        checkUpdates.Click += (_, _) => ToggleCheckForUpdates();
        menu.Add(checkUpdates);
        menu.Add(Action("Check for updates", () => _ = RunUpdateCheckAsync(manual: true)));
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
        var line = $"Listening on {_config.ListenAddress}:{_config.Port}{_config.WebhookPath}  ·  {sp}";

        if (!string.IsNullOrWhiteSpace(_config.Joplin.AuthToken))
        {
            var joplin = _joplinHealth switch
            {
                SpHealth.Ok => "Joplin reachable",
                SpHealth.Unreachable => "Joplin unreachable",
                _ => "Joplin not checked yet",
            };
            line += $"  ·  {joplin}";
        }

        if (!string.IsNullOrWhiteSpace(_config.GoogleCalendar.RefreshToken))
        {
            var google = _googleHealth switch
            {
                SpHealth.Ok => "Calendar reachable",
                SpHealth.Unreachable => "Calendar unreachable",
                _ => "Calendar not checked yet",
            };
            line += $"  ·  {google}";
        }

        if (!string.IsNullOrWhiteSpace(_config.Beeper.ApiToken))
        {
            var beeper = _beeperHealth switch
            {
                SpHealth.Ok => "Beeper reachable",
                SpHealth.Unreachable => "Beeper unreachable",
                _ => "Beeper not checked yet",
            };
            line += $"  ·  {beeper}";
        }

        if (_config.Telegram.Enabled)
        {
            var telegram = _telegramHealth switch
            {
                SpHealth.Ok => "Telegram reachable",
                SpHealth.Unreachable => "Telegram unreachable",
                _ => "Telegram not checked yet",
            };
            line += $"  ·  {telegram}";
        }

        if (_config.AiClassifier.Enabled)
        {
            var ai = _aiHealth switch
            {
                SpHealth.Ok => "AI reachable",
                SpHealth.Unreachable => "AI unreachable",
                _ => "AI not checked yet",
            };
            line += $"  ·  {ai}";
        }

        if (_config.Whisper.Enabled)
        {
            var whisper = _whisperHealth switch
            {
                SpHealth.Ok => "Whisper reachable",
                SpHealth.Unreachable => "Whisper unreachable",
                _ => "Whisper not checked yet",
            };
            line += $"  ·  {whisper}";
        }

        return line;
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

    private NativeMenu BuildSuperProductivitySubmenu()
    {
        var m = new NativeMenu();
        var queued = _outbox.PendingCount;

        m.Add(Action("Test connection", () => _ = RunHealthCheckAsync(manual: true)));
        if (queued > 0)
            m.Add(Action($"Retry {queued} queued task{(queued == 1 ? "" : "s")} now",
                () => _ = FlushOutboxAsync()));
        m.Add(new NativeMenuItemSeparator());

        m.Add(new NativeMenuItem("Default project") { Menu = BuildProjectSubmenu() });
        m.Add(new NativeMenuItem("Default tags") { Menu = BuildTagsSubmenu() });
        return m;
    }

    private NativeMenu BuildAiClassifierSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.AiClassifier;

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleAiEnabled();
        m.Add(enabled);

        m.Add(new NativeMenuItem("Provider") { Menu = BuildAiProviderSubmenu() });
        m.Add(new NativeMenuItem("Fallback provider") { Menu = BuildAiFallbackProviderSubmenu() });
        m.Add(new NativeMenuItem("Claude") { Menu = BuildClaudeSubmenu() });
        m.Add(new NativeMenuItem("Gemini") { Menu = BuildGeminiSubmenu() });
        m.Add(new NativeMenuItem("OpenAI") { Menu = BuildOpenAiSubmenu() });
        m.Add(new NativeMenuItem("Ollama") { Menu = BuildOllamaSubmenu() });
        m.Add(new NativeMenuItem("Shopping project") { Menu = BuildShoppingProjectSubmenu() });
        m.Add(new NativeMenuItemSeparator());

        var requireTags = new NativeMenuItem("Require at least one tag")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.RequireTags,
        };
        requireTags.Click += (_, _) => ToggleRequireTags();
        m.Add(requireTags);
        m.Add(Disabled(cfg.RequireTags
            ? "On: must pick an SP tag, and a Joplin tag for notes"
            : "Off: every task/note just gets its default tags"));

        var exclusiveRouting = new NativeMenuItem("Route to one destination only")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.ExclusiveRouting,
        };
        exclusiveRouting.Click += (_, _) => ToggleExclusiveRouting();
        m.Add(exclusiveRouting);
        m.Add(Disabled(cfg.ExclusiveRouting
            ? "On: SP is the fallback if it fails"
            : "Off: SP task is always created"));

        m.Add(new NativeMenuItemSeparator());
        m.Add(Action("Test connection", () => _ = RunAiHealthCheckAsync(manual: true)));
        m.Add(Disabled($"Using {DescribeProvider(cfg.Provider)} — {(HasCredentialFor(cfg.Provider) ? "credential is set" : "no credential set")}"));
        if (!string.IsNullOrWhiteSpace(cfg.FallbackProvider))
            m.Add(Disabled($"Falls back to {DescribeProvider(cfg.FallbackProvider)} if that fails"));
        return m;
    }

    private static string DescribeProvider(string provider) =>
        AiProviders.FirstOrDefault(p => p.Id == provider).Label ?? "Claude";

    private bool HasCredentialFor(string provider) => provider switch
    {
        "gemini" => !string.IsNullOrWhiteSpace(_config.AiClassifier.GeminiApiKey),
        "openai" => !string.IsNullOrWhiteSpace(_config.AiClassifier.OpenAiApiKey),
        "ollama" => !string.IsNullOrWhiteSpace(_config.AiClassifier.OllamaModel),
        _ => !string.IsNullOrWhiteSpace(_config.AiClassifier.ApiKey),
    };

    private NativeMenu BuildAiProviderSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.AiClassifier.Provider;

        foreach (var (id, label) in AiProviders)
        {
            var item = new NativeMenuItem(label)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetAiProvider(id, label);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildAiFallbackProviderSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.AiClassifier;
        var current = cfg.FallbackProvider;

        var none = new NativeMenuItem("(None)")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = string.IsNullOrWhiteSpace(current),
        };
        none.Click += (_, _) => SetAiFallbackProvider("", "(None)");
        m.Add(none);

        foreach (var (id, label) in AiProviders)
        {
            if (id == cfg.Provider) continue; // falling back to the primary provider is a no-op
            var item = new NativeMenuItem(label)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetAiFallbackProvider(id, label);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildClaudeSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.AiClassifier;

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.ApiKey) ? "Set API key…" : "Change API key…",
            () => _ = SetApiKeyAsync()));
        m.Add(new NativeMenuItem("Model") { Menu = BuildAiModelSubmenu() });
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.ApiKey) ? "No API key set" : "API key is set"));
        m.Add(Disabled(_claudeModels.Count > 0
            ? $"{_claudeModels.Count} models loaded from your account"
            : "Showing built-in defaults — Refresh projects, tags & notebooks to pull your account's real list"));
        return m;
    }

    private NativeMenu BuildGeminiSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.AiClassifier;

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.GeminiApiKey) ? "Set API key…" : "Change API key…",
            () => _ = SetGeminiApiKeyAsync()));
        m.Add(new NativeMenuItem("Model") { Menu = BuildGeminiModelSubmenu() });
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.GeminiApiKey) ? "No API key set" : "API key is set"));
        m.Add(Disabled(_geminiModels.Count > 0
            ? $"{_geminiModels.Count} models loaded from your account"
            : "Showing built-in defaults — Refresh projects, tags & notebooks to pull your account's real list"));
        return m;
    }

    private NativeMenu BuildOpenAiSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.AiClassifier;

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.OpenAiApiKey) ? "Set API key…" : "Change API key…",
            () => _ = SetOpenAiApiKeyAsync()));
        m.Add(new NativeMenuItem("Model") { Menu = BuildOpenAiModelSubmenu() });
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.OpenAiApiKey) ? "No API key set" : "API key is set"));
        return m;
    }

    private NativeMenu BuildOllamaSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.AiClassifier;

        m.Add(Action("Set server URL…", () => _ = SetOllamaBaseUrlAsync()));
        m.Add(new NativeMenuItem("Model") { Menu = BuildOllamaModelSubmenu() });
        m.Add(Disabled($"Server: {cfg.OllamaBaseUrl}"));
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.OllamaModel) ? "No model set" : $"Model: {cfg.OllamaModel}"));
        m.Add(Disabled("Local model must support tool calling — can't be forced, falls back if ignored"));
        return m;
    }

    private NativeMenu BuildOllamaModelSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.AiClassifier.OllamaModel ?? "";

        if (_ollamaModels.Count == 0)
        {
            m.Add(new NativeMenuItem("(run “Refresh projects, tags & notebooks” with the server reachable)") { IsEnabled = false });
            return m;
        }

        foreach (var model in _ollamaModels.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = model.Id;
            var title = model.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetOllamaModel(id, title);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildGeminiModelSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.AiClassifier.GeminiModel;
        var models = _geminiModels.Count > 0
            ? _geminiModels
            : GeminiModels.Select(x => new SpNamedItem(x.Id, x.Label)).ToList();

        foreach (var model in models)
        {
            var id = model.Id;
            var title = model.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetGeminiModel(id, title);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildOpenAiModelSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.AiClassifier.OpenAiModel;

        foreach (var (id, label) in OpenAiModels)
        {
            var item = new NativeMenuItem(label)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetOpenAiModel(id, label);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildShoppingProjectSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.SuperProductivity.ShoppingProjectId ?? "";

        var none = new NativeMenuItem("(no override — use the classifier's own pick)")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = current.Length == 0,
        };
        none.Click += (_, _) => SetShoppingProject("", "no override");
        m.Add(none);

        if (_projects.Count == 0)
        {
            m.Add(new NativeMenuItem("(run “Refresh projects, tags & notebooks”)") { IsEnabled = false });
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
            item.Click += (_, _) => SetShoppingProject(id, title);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildAiModelSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.AiClassifier.Model;
        var models = _claudeModels.Count > 0
            ? _claudeModels
            : AiModels.Select(x => new SpNamedItem(x.Id, x.Label)).ToList();

        foreach (var model in models)
        {
            var id = model.Id;
            var title = model.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetAiModel(id, title);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildJoplinSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.Joplin;

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleJoplinEnabled();
        m.Add(enabled);

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.AuthToken) ? "Set auth token…" : "Change auth token…",
            () => _ = SetJoplinTokenAsync()));
        m.Add(new NativeMenuItem("Default notebook") { Menu = BuildJoplinNotebookSubmenu() });
        m.Add(new NativeMenuItem("Default tag") { Menu = BuildJoplinTagsSubmenu() });
        m.Add(Action("Test Joplin connection", () => _ = RunJoplinHealthCheckAsync(manual: true)));
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.AuthToken) ? "No auth token set" : "Auth token is set"));
        return m;
    }

    private NativeMenu BuildJoplinTagsSubmenu()
    {
        var m = new NativeMenu();
        var selected = new HashSet<string>(_config.Joplin.DefaultTagIds ?? new List<string>(), StringComparer.Ordinal);

        if (_joplinTags.Count == 0)
        {
            m.Add(new NativeMenuItem("(run “Refresh projects, tags & notebooks”)") { IsEnabled = false });
            return m;
        }

        foreach (var t in _joplinTags.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = t.Id;
            var title = t.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = selected.Contains(id),
            };
            item.Click += (_, _) => ToggleJoplinDefaultTag(id, title);
            m.Add(item);
        }

        m.Add(new NativeMenuItemSeparator());
        var clear = new NativeMenuItem("Clear all") { IsEnabled = selected.Count > 0 };
        clear.Click += (_, _) =>
        {
            (_config.Joplin.DefaultTagIds ??= new List<string>()).Clear();
            SaveConfig("cleared all Joplin default tags");
        };
        m.Add(clear);
        return m;
    }

    private NativeMenu BuildJoplinNotebookSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.Joplin.NotebookId ?? "";

        var none = new NativeMenuItem("(Joplin's last-selected notebook)")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = current.Length == 0,
        };
        none.Click += (_, _) => SetJoplinNotebook("", "Joplin's last-selected notebook");
        m.Add(none);

        if (_notebooks.Count == 0)
        {
            m.Add(new NativeMenuItem("(run “Refresh projects, tags & notebooks”)") { IsEnabled = false });
            return m;
        }

        m.Add(new NativeMenuItemSeparator());
        foreach (var n in _notebooks.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = n.Id;
            var title = n.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetJoplinNotebook(id, title);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildGoogleCalendarSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.GoogleCalendar;
        var connected = !string.IsNullOrWhiteSpace(cfg.RefreshToken);

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleGoogleCalendarEnabled();
        m.Add(enabled);

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.ClientId) ? "Set client ID…" : "Change client ID…",
            () => _ = SetGoogleClientIdAsync()));
        m.Add(Action(string.IsNullOrWhiteSpace(cfg.ClientSecret) ? "Set client secret…" : "Change client secret…",
            () => _ = SetGoogleClientSecretAsync()));
        m.Add(Action(connected ? "Reconnect…" : "Connect…", () => _ = ConnectGoogleCalendarAsync()));
        if (connected)
            m.Add(Action("Disconnect", DisconnectGoogleCalendar));
        m.Add(new NativeMenuItem("Default calendar") { Menu = BuildGoogleCalendarPickerSubmenu() });
        m.Add(Action("Test Google Calendar connection", () => _ = RunGoogleCalendarHealthCheckAsync(manual: true)));
        m.Add(Disabled(connected ? "Connected" : "Not connected"));
        return m;
    }

    private NativeMenu BuildGoogleCalendarPickerSubmenu()
    {
        var m = new NativeMenu();
        var current = _config.GoogleCalendar.CalendarId;

        if (_calendars.Count == 0)
        {
            m.Add(new NativeMenuItem("(run “Refresh projects, tags & notebooks” once connected)") { IsEnabled = false });
            return m;
        }

        foreach (var c in _calendars.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var id = c.Id;
            var title = c.Title;
            var item = new NativeMenuItem(title)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = id == current,
            };
            item.Click += (_, _) => SetGoogleCalendarId(id, title);
            m.Add(item);
        }
        return m;
    }

    private NativeMenu BuildBeeperSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.Beeper;

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleBeeperEnabled();
        m.Add(enabled);

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.ApiToken) ? "Set API token…" : "Change API token…",
            () => _ = SetBeeperTokenAsync()));
        m.Add(Action("Test Beeper connection", () => _ = RunBeeperHealthCheckAsync(manual: true)));
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.ApiToken) ? "No API token set" : "API token is set"));
        m.Add(Disabled("Sends only when the recipient's name matches exactly one existing chat"));
        return m;
    }

    private NativeMenu BuildTelegramSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.Telegram;

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleTelegramEnabled();
        m.Add(enabled);

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.BotToken) ? "Set bot token…" : "Change bot token…",
            () => _ = SetTelegramBotTokenAsync()));
        m.Add(Action("Test connection", () => _ = RunTelegramHealthCheckAsync(manual: true)));
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.BotToken) ? "No bot token set" : "Bot token is set"));
        m.Add(Disabled("Just the bot connection — set each destination chat under Web search / Webhook receipt"));
        return m;
    }

    private NativeMenu BuildWhisperSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.Whisper;

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleWhisperEnabled();
        m.Add(enabled);

        m.Add(Action("Set server URL…", () => _ = SetWhisperBaseUrlAsync()));
        m.Add(Action("Set model…", () => _ = SetWhisperModelAsync()));
        m.Add(Action("Test connection", () => _ = RunWhisperHealthCheckAsync(manual: true)));
        m.Add(Disabled($"Server: {cfg.BaseUrl}"));
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.Model) ? "Model: server default" : $"Model: {cfg.Model}"));
        m.Add(Disabled("Local, OpenAI-compatible Whisper server — fallback only, when Pebble sends audio but no text"));
        return m;
    }

    private NativeMenu BuildWebSearchSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.WebSearch;

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleWebSearchEnabled();
        m.Add(enabled);

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.ChatId) ? "Set Telegram chat ID…" : "Change Telegram chat ID…",
            () => _ = SetWebSearchChatIdAsync()));
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.ChatId) ? "No chat ID set" : $"Chat ID: {cfg.ChatId}"));
        m.Add(Disabled("Searches via Claude's web search tool — needs a Claude API key and Telegram enabled"));
        return m;
    }

    private NativeMenu BuildWebhookReceiptSubmenu()
    {
        var m = new NativeMenu();
        var cfg = _config.WebhookReceipt;

        var enabled = new NativeMenuItem("Enabled")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = cfg.Enabled,
        };
        enabled.Click += (_, _) => ToggleWebhookReceiptEnabled();
        m.Add(enabled);

        m.Add(Action(string.IsNullOrWhiteSpace(cfg.ChatId) ? "Set Telegram chat ID…" : "Change Telegram chat ID…",
            () => _ = SetWebhookReceiptChatIdAsync()));
        m.Add(Disabled(string.IsNullOrWhiteSpace(cfg.ChatId) ? "No chat ID set" : $"Chat ID: {cfg.ChatId}"));
        m.Add(Disabled("Sends \"Webhook Received, ...\" for every capture — needs Telegram enabled"));
        return m;
    }

    // ---- server lifecycle --------------------------------------------

    private async Task StartServerAsync(bool initial = false)
    {
        try
        {
            _server = new WebhookServer(_config, _log, _captureTag, _outbox, _classifier);
            _server.TaskCreated += OnTaskCreated;
            _server.WebhookFailed += OnWebhookFailed;
            _server.TaskQueued += OnTaskQueued;
            _server.TestEventReceived += OnTestEventReceived;
            await _server.StartAsync();
            RefreshTray();
            if (!initial) Notify("Listener started", StatusLine(), NotifyKind.Info);
            _ = RunHealthCheckAsync(manual: false);
            _ = RunJoplinHealthCheckAsync(manual: false);
            _ = RunGoogleCalendarHealthCheckAsync(manual: false);
            _ = RunBeeperHealthCheckAsync(manual: false);
            _ = RunTelegramHealthCheckAsync(manual: false);
            _ = RunAiHealthCheckAsync(manual: false);
            _ = RunWhisperHealthCheckAsync(manual: false);
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
        _server.TaskQueued -= OnTaskQueued;
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
            _joplinHealth = SpHealth.Unknown;
            _googleHealth = SpHealth.Unknown;
            _beeperHealth = SpHealth.Unknown;
            _telegramHealth = SpHealth.Unknown;
            _aiHealth = SpHealth.Unknown;
            _whisperHealth = SpHealth.Unknown;
            _captureTag = new CaptureTagResolver(_config.SuperProductivity, _log);
            _classifier = new AiTaskClassifier(_config.AiClassifier, _log);
            ConfigureHealthTimer();
            ConfigureOutboxTimer();
            ConfigureUpdateTimer();
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

    /// <param name="forceRun">Bypasses the "listener must be running" gate without triggering
    /// the individual pop-up notification <paramref name="manual"/> normally would — used by
    /// <see cref="RunAllConnectionsTestAsync"/> so "Test all connections" always probes SP.</param>
    private async Task RunHealthCheckAsync(bool manual, bool forceRun = false)
    {
        if (!manual && !forceRun && (_healthCheckInFlight || _server?.IsRunning != true)) return;
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
                {
                    Notify("Super Productivity unreachable", message, NotifyKind.Warning);
                    _ = CreateOutageTaskAsync("Super Productivity", message);
                }

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

    /// <summary>Only runs when an auth token is configured — with none set there's nothing
    /// useful to probe, and Joplin may not even be installed.</summary>
    private async Task RunJoplinHealthCheckAsync(bool manual)
    {
        if (string.IsNullOrWhiteSpace(_config.Joplin.AuthToken))
        {
            if (manual) Notify("Joplin", "No auth token set — nothing to test.", NotifyKind.Error, force: true);
            return;
        }
        if (!manual && _joplinHealthCheckInFlight) return;
        _joplinHealthCheckInFlight = true;
        try
        {
            SpHealth state;
            string message;
            try
            {
                using var joplin = new JoplinClient(_config.Joplin);
                message = await joplin.TestAsync();
                state = SpHealth.Ok;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                state = SpHealth.Unreachable;
            }

            var prev = _joplinHealth;
            _joplinHealth = state;

            if (state != prev)
            {
                if (state == SpHealth.Ok)
                    _log.Info(prev == SpHealth.Unreachable
                        ? $"Joplin connection restored — {message}"
                        : $"Joplin reachable — {message}");
                else
                    _log.Warn($"Joplin unreachable — {message}");

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable)
                {
                    Notify("Joplin unreachable", message, NotifyKind.Warning);
                    _ = CreateOutageTaskAsync("Joplin", message);
                }

                RefreshTray();
            }

            if (manual)
                Notify(state == SpHealth.Ok ? "Joplin" : "Joplin — not reachable",
                    message, state == SpHealth.Ok ? NotifyKind.Info : NotifyKind.Error, force: true);
        }
        finally
        {
            _joplinHealthCheckInFlight = false;
        }
    }

    /// <summary>Only runs once connected — with no refresh token there's nothing to probe.</summary>
    private async Task RunGoogleCalendarHealthCheckAsync(bool manual)
    {
        if (string.IsNullOrWhiteSpace(_config.GoogleCalendar.RefreshToken))
        {
            if (manual) Notify("Google Calendar", "Not connected — run Connect… first.", NotifyKind.Error, force: true);
            return;
        }
        if (!manual && _googleHealthCheckInFlight) return;
        _googleHealthCheckInFlight = true;
        try
        {
            SpHealth state;
            string message;
            try
            {
                using var google = new GoogleCalendarClient(_config.GoogleCalendar);
                message = await google.TestAsync();
                state = SpHealth.Ok;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                state = SpHealth.Unreachable;
            }

            var prev = _googleHealth;
            _googleHealth = state;

            if (state != prev)
            {
                if (state == SpHealth.Ok)
                    _log.Info(prev == SpHealth.Unreachable
                        ? $"Google Calendar connection restored — {message}"
                        : $"Google Calendar reachable — {message}");
                else
                    _log.Warn($"Google Calendar unreachable — {message}");

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable)
                {
                    Notify("Google Calendar unreachable", message, NotifyKind.Warning);
                    _ = CreateOutageTaskAsync("Google Calendar", message);
                }

                RefreshTray();
            }

            if (manual)
                Notify(state == SpHealth.Ok ? "Google Calendar" : "Google Calendar — not reachable",
                    message, state == SpHealth.Ok ? NotifyKind.Info : NotifyKind.Error, force: true);
        }
        finally
        {
            _googleHealthCheckInFlight = false;
        }
    }

    /// <summary>Only runs when an API token is configured.</summary>
    private async Task RunBeeperHealthCheckAsync(bool manual)
    {
        if (string.IsNullOrWhiteSpace(_config.Beeper.ApiToken))
        {
            if (manual) Notify("Beeper", "No API token set — nothing to test.", NotifyKind.Error, force: true);
            return;
        }
        if (!manual && _beeperHealthCheckInFlight) return;
        _beeperHealthCheckInFlight = true;
        try
        {
            SpHealth state;
            string message;
            try
            {
                using var beeper = new BeeperClient(_config.Beeper);
                message = await beeper.TestAsync();
                state = SpHealth.Ok;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                state = SpHealth.Unreachable;
            }

            var prev = _beeperHealth;
            _beeperHealth = state;

            if (state != prev)
            {
                if (state == SpHealth.Ok)
                    _log.Info(prev == SpHealth.Unreachable
                        ? $"Beeper connection restored — {message}"
                        : $"Beeper reachable — {message}");
                else
                    _log.Warn($"Beeper unreachable — {message}");

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable)
                {
                    Notify("Beeper unreachable", message, NotifyKind.Warning);
                    _ = CreateOutageTaskAsync("Beeper", message);
                }

                RefreshTray();
            }

            if (manual)
                Notify(state == SpHealth.Ok ? "Beeper" : "Beeper — not reachable",
                    message, state == SpHealth.Ok ? NotifyKind.Info : NotifyKind.Error, force: true);
        }
        finally
        {
            _beeperHealthCheckInFlight = false;
        }
    }

    /// <summary>Only runs while Telegram is enabled with a bot token set.</summary>
    private async Task RunTelegramHealthCheckAsync(bool manual)
    {
        if (!_config.Telegram.Enabled || string.IsNullOrWhiteSpace(_config.Telegram.BotToken))
        {
            if (manual) Notify("Telegram", "No bot token set — nothing to test.", NotifyKind.Error, force: true);
            return;
        }
        if (!manual && _telegramHealthCheckInFlight) return;
        _telegramHealthCheckInFlight = true;
        try
        {
            SpHealth state;
            string message;
            try
            {
                using var telegram = new TelegramClient(_config.Telegram);
                message = await telegram.TestAsync();
                state = SpHealth.Ok;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                state = SpHealth.Unreachable;
            }

            var prev = _telegramHealth;
            _telegramHealth = state;

            if (state != prev)
            {
                if (state == SpHealth.Ok)
                    _log.Info(prev == SpHealth.Unreachable
                        ? $"Telegram connection restored — {message}"
                        : $"Telegram reachable — {message}");
                else
                    _log.Warn($"Telegram unreachable — {message}");

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable)
                {
                    Notify("Telegram unreachable", message, NotifyKind.Warning);
                    _ = CreateOutageTaskAsync("Telegram", message);
                }

                RefreshTray();
            }

            if (manual)
                Notify(state == SpHealth.Ok ? "Telegram" : "Telegram — not reachable",
                    message, state == SpHealth.Ok ? NotifyKind.Info : NotifyKind.Error, force: true);
        }
        finally
        {
            _telegramHealthCheckInFlight = false;
        }
    }

    /// <summary>Only runs while Whisper transcription is enabled.</summary>
    private async Task RunWhisperHealthCheckAsync(bool manual)
    {
        if (!_config.Whisper.Enabled)
        {
            if (manual) Notify("Whisper", "Disabled — nothing to test.", NotifyKind.Error, force: true);
            return;
        }
        if (!manual && _whisperHealthCheckInFlight) return;
        _whisperHealthCheckInFlight = true;
        try
        {
            SpHealth state;
            string message;
            try
            {
                using var whisper = new WhisperClient(_config.Whisper);
                message = await whisper.TestAsync();
                state = SpHealth.Ok;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                state = SpHealth.Unreachable;
            }

            var prev = _whisperHealth;
            _whisperHealth = state;

            if (state != prev)
            {
                if (state == SpHealth.Ok)
                    _log.Info(prev == SpHealth.Unreachable
                        ? $"Whisper connection restored — {message}"
                        : $"Whisper reachable — {message}");
                else
                    _log.Warn($"Whisper unreachable — {message}");

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable)
                {
                    Notify("Whisper unreachable", message, NotifyKind.Warning);
                    _ = CreateOutageTaskAsync("Whisper", message);
                }

                RefreshTray();
            }

            if (manual)
                Notify(state == SpHealth.Ok ? "Whisper" : "Whisper — not reachable",
                    message, state == SpHealth.Ok ? NotifyKind.Info : NotifyKind.Error, force: true);
        }
        finally
        {
            _whisperHealthCheckInFlight = false;
        }
    }

    /// <summary>Checks GitHub for a newer release. Unlike the other checks here, a manual click
    /// always runs even while automatic checking (<see cref="AppConfig.CheckForUpdates"/>) is
    /// off — there's no credential gate, it's just a GitHub API call. Re-notifies only on the
    /// transition into "an update exists", not on every recheck while one still does.</summary>
    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (!manual && !_config.CheckForUpdates) return;
        if (!manual && _updateCheckInFlight) return;
        _updateCheckInFlight = true;
        try
        {
            UpdateChecker.UpdateInfo? found;
            try
            {
                using var checker = new UpdateChecker();
                found = await checker.CheckAsync();
            }
            catch (Exception ex) when (ex is UpdateCheckException or HttpRequestException or TaskCanceledException)
            {
                _log.Warn($"Update check failed: {ex.Message}");
                if (manual) Notify("Check for updates", $"Couldn't check: {ex.Message}", NotifyKind.Error, force: true);
                return;
            }

            var wasKnown = _updateAvailable is not null;
            if (found?.Version != _updateAvailable?.Version) _downloadedUpdatePath = null;
            _updateAvailable = found;

            if (found is not null && !wasKnown)
            {
                _log.Info($"Update available: v{found.Version}");
                Notify("Update available", $"Index2SP v{found.Version} is available — see the tray menu.", NotifyKind.Info);
                RefreshTray();
            }
            else if (found is null && wasKnown)
            {
                RefreshTray();
            }

            if (manual)
                Notify(found is not null ? "Update available" : "Check for updates",
                    found is not null ? $"v{found.Version} is available." : $"You're up to date (v{AppInfo.Version}).",
                    NotifyKind.Info, force: true);
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    /// <summary>Downloads the release asset for this platform to a temp file. Doesn't install
    /// anything — <see cref="LaunchDownloadedUpdate"/> is a separate, explicit step.</summary>
    private async Task DownloadUpdateAsync(UpdateChecker.UpdateInfo update)
    {
        if (_updateDownloadInFlight) return;
        _updateDownloadInFlight = true;
        RebuildMenu();
        try
        {
            using var checker = new UpdateChecker();
            _log.Info($"Downloading update v{update.Version}…");
            var path = await checker.DownloadAssetAsync(update);
            _downloadedUpdatePath = path;
            _log.Info($"Downloaded update to {path}");
            Notify("Update ready", $"v{update.Version} downloaded — open the tray menu to install.", NotifyKind.Info, force: true);
        }
        catch (Exception ex) when (ex is UpdateCheckException or HttpRequestException or TaskCanceledException or IOException)
        {
            _log.Error("Update download failed", ex);
            Notify("Download failed", ex.Message, NotifyKind.Error, force: true);
        }
        finally
        {
            _updateDownloadInFlight = false;
            RebuildMenu();
        }
    }

    /// <summary>
    /// Windows: launches the downloaded installer normally — its own click-through UI still
    /// applies, this just saves the trip to a browser. Since it may need to overwrite the running
    /// exe, quits Index2SP right after handing off to it.
    /// Linux: there's no unattended installer, so this extracts the tarball and opens the folder
    /// so the user runs ./install.sh themselves, the same manual step as a browser download would
    /// have needed anyway.
    /// </summary>
    private void LaunchDownloadedUpdate()
    {
        if (_downloadedUpdatePath is not { } path || !File.Exists(path))
        {
            Notify("Update", "Nothing downloaded yet.", NotifyKind.Error, force: true);
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                _log.Info($"Launched update installer: {path}");
                _ = QuitAsync();
            }
            else
            {
                var dir = ExtractLinuxUpdateTarball(path);
                _log.Info($"Extracted update to {dir}");
                OpenPath(dir);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Couldn't launch the downloaded update", ex);
            Notify("Couldn't launch update", ex.Message, NotifyKind.Error, force: true);
        }
    }

    /// <summary>Extracts a downloaded .tar.gz next to itself (stripping the extension) via the
    /// system `tar` — universally available on Linux, and already what the tarball itself was
    /// built with, so no extra dependency for what's a one-off convenience extraction.</summary>
    private static string ExtractLinuxUpdateTarball(string tarGzPath)
    {
        var dir = tarGzPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? tarGzPath[..^".tar.gz".Length]
            : tarGzPath + "-extracted";
        Directory.CreateDirectory(dir);

        using var proc = Process.Start(new ProcessStartInfo("tar")
        {
            ArgumentList = { "-xzf", tarGzPath, "-C", dir },
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new IOException("Could not start tar.");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new IOException($"tar exited {proc.ExitCode}: {proc.StandardError.ReadToEnd()}");

        return dir;
    }

    /// <summary>Only runs while the AI classifier is enabled and its selected provider has a
    /// credential set.</summary>
    private async Task RunAiHealthCheckAsync(bool manual)
    {
        if (!_config.AiClassifier.Enabled)
        {
            if (manual) Notify("AI classifier", "Disabled — nothing to test.", NotifyKind.Error, force: true);
            return;
        }
        if (!manual && _aiHealthCheckInFlight) return;
        _aiHealthCheckInFlight = true;
        try
        {
            SpHealth state;
            string message;
            try
            {
                message = await _classifier.TestAsync();
                state = SpHealth.Ok;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                state = SpHealth.Unreachable;
            }

            var prev = _aiHealth;
            _aiHealth = state;

            if (state != prev)
            {
                if (state == SpHealth.Ok)
                    _log.Info(prev == SpHealth.Unreachable
                        ? $"AI classifier connection restored — {message}"
                        : $"AI classifier reachable — {message}");
                else
                    _log.Warn($"AI classifier unreachable — {message}");

                if (!manual && state == SpHealth.Unreachable && prev != SpHealth.Unreachable)
                {
                    Notify("AI classifier unreachable", message, NotifyKind.Warning);
                    _ = CreateOutageTaskAsync("AI classifier", message);
                }

                RefreshTray();
            }

            if (manual)
                Notify(state == SpHealth.Ok ? "AI classifier" : "AI classifier — not reachable",
                    message, state == SpHealth.Ok ? NotifyKind.Info : NotifyKind.Error, force: true);
        }
        finally
        {
            _aiHealthCheckInFlight = false;
        }
    }

    /// <summary>Probes every configured destination (SP always; Joplin/Google Calendar/Beeper/AI
    /// classifier only when a credential is set) and shows one combined result instead of
    /// several separate pop-ups.</summary>
    private async Task RunAllConnectionsTestAsync()
    {
        await RunHealthCheckAsync(manual: false, forceRun: true);
        var lines = new List<string> { $"Super Productivity: {DescribeHealth(_spHealth)}" };
        var allOk = _spHealth == SpHealth.Ok;

        if (!string.IsNullOrWhiteSpace(_config.Joplin.AuthToken))
        {
            await RunJoplinHealthCheckAsync(manual: false);
            lines.Add($"Joplin: {DescribeHealth(_joplinHealth)}");
            allOk &= _joplinHealth == SpHealth.Ok;
        }

        if (!string.IsNullOrWhiteSpace(_config.GoogleCalendar.RefreshToken))
        {
            await RunGoogleCalendarHealthCheckAsync(manual: false);
            lines.Add($"Google Calendar: {DescribeHealth(_googleHealth)}");
            allOk &= _googleHealth == SpHealth.Ok;
        }

        if (!string.IsNullOrWhiteSpace(_config.Beeper.ApiToken))
        {
            await RunBeeperHealthCheckAsync(manual: false);
            lines.Add($"Beeper: {DescribeHealth(_beeperHealth)}");
            allOk &= _beeperHealth == SpHealth.Ok;
        }

        if (_config.Telegram.Enabled)
        {
            await RunTelegramHealthCheckAsync(manual: false);
            lines.Add($"Telegram: {DescribeHealth(_telegramHealth)}");
            allOk &= _telegramHealth == SpHealth.Ok;
        }

        if (_config.AiClassifier.Enabled)
        {
            await RunAiHealthCheckAsync(manual: false);
            lines.Add($"AI classifier ({DescribeProvider(_config.AiClassifier.Provider)}): {DescribeHealth(_aiHealth)}");
            allOk &= _aiHealth == SpHealth.Ok;
        }

        if (_config.Whisper.Enabled)
        {
            await RunWhisperHealthCheckAsync(manual: false);
            lines.Add($"Whisper: {DescribeHealth(_whisperHealth)}");
            allOk &= _whisperHealth == SpHealth.Ok;
        }

        Notify(allOk ? "All connections OK" : "Some connections failed",
            string.Join("\n", lines), allOk ? NotifyKind.Info : NotifyKind.Error, force: true);
    }

    private static string DescribeHealth(SpHealth health) => health switch
    {
        SpHealth.Ok => "reachable",
        SpHealth.Unreachable => "unreachable",
        _ => "not checked",
    };

    /// <summary>
    /// Best-effort Super Productivity task for a destination going down, fired once per
    /// reachable/unknown → unreachable transition (not on every recheck while it stays down).
    /// If Super Productivity itself is the one that's unreachable, this attempt simply fails and
    /// logs — same as any other outage — there's no special-casing needed.
    /// </summary>
    private async Task CreateOutageTaskAsync(string name, string message)
    {
        try
        {
            var task = new SpTaskRequest
            {
                Title = $"Index2SP: {name} unreachable",
                Notes = $"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n\n{message}",
            };
            using var sp = new SuperProductivityClient(_config.SuperProductivity);
            var result = await sp.CreateTaskAsync(task, CancellationToken.None);
            _log.Info($"Outage task created in Super Productivity{(result.TaskId is null ? "" : $" ({result.TaskId})")} for {name}");
        }
        catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Could not create outage task for \"{name} unreachable\": {ex.Message}");
        }
    }

    // ---- outbox retry ----------------------------------------------

    private async Task FlushOutboxAsync()
    {
        if (_outboxFlushInFlight) return;
        _outboxFlushInFlight = true;
        try
        {
            await _outbox.FlushAsync(_config.SuperProductivity, _captureTag, _config.OutboxMaxAttempts);
        }
        catch (Exception ex)
        {
            _log.Warn($"Outbox flush error: {ex.Message}");
        }
        finally
        {
            _outboxFlushInFlight = false;
            RebuildMenu();
        }
    }

    private void OnOutboxDelivered(string title, string? taskId) => Dispatcher.UIThread.Post(() =>
    {
        _created++;
        RebuildMenu();
        Notify("Queued task delivered", title, NotifyKind.Info);
    });

    private void OnOutboxFailed(string title, string error) => Dispatcher.UIThread.Post(() =>
    {
        _failed++;
        RebuildMenu();
        Notify("Queued task gave up", $"{title}\n{error}", NotifyKind.Error, force: true);
    });

    // ---- default project / tags -------------------------------------

    private async Task RefreshListsAsync(bool notifyOnError)
    {
        var projects = await FetchAsync(c => c.GetProjectsAsync(), "projects", notifyOnError);
        if (projects is not null) _projects = projects;
        var tags = await FetchAsync(c => c.GetTagsAsync(), "tags", notifyOnError);
        if (tags is not null) _tags = tags;

        if (!string.IsNullOrWhiteSpace(_config.Joplin.AuthToken))
        {
            try
            {
                using var joplin = new JoplinClient(_config.Joplin);
                _notebooks = await joplin.GetFoldersAsync();
                _joplinTags = await joplin.GetTagsAsync();
            }
            catch (Exception ex) when (ex is JoplinApiException or HttpRequestException or TaskCanceledException)
            {
                _log.Warn($"Couldn't load Joplin notebooks/tags: {ex.Message}");
                if (notifyOnError) Notify("Couldn't load Joplin notebooks/tags", ex.Message, NotifyKind.Error);
            }
        }

        if (!string.IsNullOrWhiteSpace(_config.GoogleCalendar.RefreshToken))
        {
            try
            {
                using var google = new GoogleCalendarClient(_config.GoogleCalendar);
                _calendars = await google.GetCalendarsAsync();
            }
            catch (Exception ex) when (ex is GoogleCalendarApiException or HttpRequestException or TaskCanceledException)
            {
                _log.Warn($"Couldn't load Google calendars: {ex.Message}");
                if (notifyOnError) Notify("Couldn't load Google calendars", ex.Message, NotifyKind.Error);
            }
        }

        // Unconditional like the other model lists below — a local Ollama server has no
        // credential to gate on, and skipping this when nothing's configured yet is exactly
        // what left the model list unable to ever populate on first-time setup.
        try
        {
            using var ollama = new OllamaClient(_config.AiClassifier.OllamaBaseUrl, _config.AiClassifier.TimeoutSeconds);
            _ollamaModels = await ollama.GetModelsAsync();
        }
        catch (Exception ex) when (ex is OllamaApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Couldn't load Ollama models: {ex.Message}");
            if (notifyOnError) Notify("Couldn't load Ollama models", ex.Message, NotifyKind.Error);
        }

        if (!string.IsNullOrWhiteSpace(_config.AiClassifier.ApiKey))
        {
            try
            {
                _claudeModels = await _classifier.ListClaudeModelsAsync();
            }
            catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException)
            {
                _log.Warn($"Couldn't load Claude models: {ex.Message}");
                if (notifyOnError) Notify("Couldn't load Claude models", ex.Message, NotifyKind.Error);
            }
        }

        if (!string.IsNullOrWhiteSpace(_config.AiClassifier.GeminiApiKey))
        {
            try
            {
                _geminiModels = await _classifier.ListGeminiModelsAsync();
            }
            catch (Exception ex)
            {
                // Gemini's client throws a bare Exception on a non-2xx response (no dedicated
                // exception type exists for it in this codebase, same as its other call sites).
                _log.Warn($"Couldn't load Gemini models: {ex.Message}");
                if (notifyOnError) Notify("Couldn't load Gemini models", ex.Message, NotifyKind.Error);
            }
        }

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

    private void SetShoppingProject(string id, string label)
    {
        _config.SuperProductivity.ShoppingProjectId = id;
        SaveConfig(id.Length == 0
            ? "shopping project override cleared"
            : $"shopping project = \"{label}\" [{id}]");
        Notify("Shopping project updated",
            id.Length == 0 ? "Shopping items use the AI classifier's own pick." : $"Shopping items → {label}",
            NotifyKind.Info);
    }

    private void ToggleDefaultTag(string id, string label)
    {
        var tags = _config.SuperProductivity.TagIds ??= new List<string>();
        var removed = tags.Remove(id);
        if (!removed) tags.Add(id);
        SaveConfig(removed ? $"removed default tag \"{label}\"" : $"added default tag \"{label}\"");
    }

    // ---- AI classifier / Joplin ---------------------------------------

    private void ToggleAiEnabled()
    {
        var cfg = _config.AiClassifier;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"AI classifier {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetApiKeyAsync()
    {
        var value = await InputDialog.ShowAsync("Anthropic API key",
            "Paste a new Anthropic API key. Leave blank to keep the current one.\n" +
            "Get one at console.anthropic.com.");
        if (value is null || value.Trim().Length == 0) return;

        _config.AiClassifier.ApiKey = value.Trim();
        SaveConfig("AI classifier API key updated");
    }

    private void SetAiModel(string id, string label)
    {
        _config.AiClassifier.Model = id;
        SaveConfig($"AI classifier model = {label}");
    }

    private void SetAiProvider(string id, string label)
    {
        _config.AiClassifier.Provider = id;
        if (_config.AiClassifier.FallbackProvider == id) _config.AiClassifier.FallbackProvider = "";
        SaveConfig($"AI classifier provider = {label}");
    }

    private void SetAiFallbackProvider(string id, string label)
    {
        _config.AiClassifier.FallbackProvider = id;
        SaveConfig($"AI classifier fallback provider = {label}");
    }

    private async Task SetGeminiApiKeyAsync()
    {
        var value = await InputDialog.ShowAsync("Google Gemini API key",
            "Paste a Gemini API key from aistudio.google.com/apikey.\nLeave blank to keep the current one.");
        if (value is null || value.Trim().Length == 0) return;

        _config.AiClassifier.GeminiApiKey = value.Trim();
        SaveConfig("Gemini API key updated");
    }

    private void SetGeminiModel(string id, string label)
    {
        _config.AiClassifier.GeminiModel = id;
        SaveConfig($"Gemini model = {label}");
    }

    private async Task SetOpenAiApiKeyAsync()
    {
        var value = await InputDialog.ShowAsync("OpenAI API key",
            "Paste an API key from platform.openai.com/api-keys.\nLeave blank to keep the current one.");
        if (value is null || value.Trim().Length == 0) return;

        _config.AiClassifier.OpenAiApiKey = value.Trim();
        SaveConfig("OpenAI API key updated");
    }

    private void SetOpenAiModel(string id, string label)
    {
        _config.AiClassifier.OpenAiModel = id;
        SaveConfig($"OpenAI model = {label}");
    }

    private async Task SetOllamaBaseUrlAsync()
    {
        var value = await InputDialog.ShowAsync("Ollama server URL",
            "Paste the base URL of your Ollama server, e.g. http://127.0.0.1:11434.\n" +
            "Leave blank to keep the current one.", masked: false);
        if (value is null || value.Trim().Length == 0) return;

        _config.AiClassifier.OllamaBaseUrl = value.Trim();
        SaveConfig("Ollama server URL updated");
        _ = RefreshListsAsync(notifyOnError: true);
    }

    private void SetOllamaModel(string id, string label)
    {
        _config.AiClassifier.OllamaModel = id;
        SaveConfig($"Ollama model = {label}");
    }

    private void ToggleRequireTags()
    {
        var cfg = _config.AiClassifier;
        cfg.RequireTags = !cfg.RequireTags;
        SaveConfig($"AI classifier require-at-least-one-tag {(cfg.RequireTags ? "enabled" : "disabled")}");
    }

    private void ToggleExclusiveRouting()
    {
        var cfg = _config.AiClassifier;
        cfg.ExclusiveRouting = !cfg.ExclusiveRouting;
        SaveConfig($"AI classifier exclusive routing {(cfg.ExclusiveRouting ? "enabled" : "disabled")}");
    }

    private void ToggleJoplinEnabled()
    {
        var cfg = _config.Joplin;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"Joplin notes {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetJoplinTokenAsync()
    {
        var value = await InputDialog.ShowAsync("Joplin auth token",
            "Paste the Web Clipper auth token from Joplin → Tools → Options → Web Clipper.\n" +
            "Leave blank to keep the current one.");
        if (value is null || value.Trim().Length == 0) return;

        _config.Joplin.AuthToken = value.Trim();
        SaveConfig("Joplin auth token updated");
    }

    private void SetJoplinNotebook(string id, string label)
    {
        _config.Joplin.NotebookId = id;
        SaveConfig(id.Length == 0 ? "Joplin default notebook cleared" : $"Joplin default notebook = \"{label}\" [{id}]");
    }

    private void ToggleJoplinDefaultTag(string id, string label)
    {
        var tagIds = _config.Joplin.DefaultTagIds ??= new List<string>();
        var removed = tagIds.Remove(id);
        if (!removed) tagIds.Add(id);
        SaveConfig(removed ? $"removed Joplin default tag \"{label}\"" : $"added Joplin default tag \"{label}\"");
    }

    // ---- Google Calendar ------------------------------------------

    private void ToggleGoogleCalendarEnabled()
    {
        var cfg = _config.GoogleCalendar;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"Google Calendar {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetGoogleClientIdAsync()
    {
        var value = await InputDialog.ShowAsync("Google OAuth client ID",
            "Paste the client ID from a Google Cloud \"Desktop app\" OAuth client\n" +
            "(console.cloud.google.com → APIs & Services → Credentials).\n" +
            "Leave blank to keep the current one.", masked: false);
        if (value is null || value.Trim().Length == 0) return;

        _config.GoogleCalendar.ClientId = value.Trim();
        SaveConfig("Google Calendar client ID updated");
    }

    private async Task SetGoogleClientSecretAsync()
    {
        var value = await InputDialog.ShowAsync("Google OAuth client secret",
            "Paste the client secret from the same OAuth client.\nLeave blank to keep the current one.");
        if (value is null || value.Trim().Length == 0) return;

        _config.GoogleCalendar.ClientSecret = value.Trim();
        SaveConfig("Google Calendar client secret updated");
    }

    private async Task ConnectGoogleCalendarAsync()
    {
        var cfg = _config.GoogleCalendar;
        if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
        {
            Notify("Google Calendar", "Set a client ID and client secret first.", NotifyKind.Error, force: true);
            return;
        }

        Notify("Google Calendar", "Opening your browser to sign in…", NotifyKind.Info, force: true);
        try
        {
            var result = await GoogleOAuthAuthorizer.AuthorizeAsync(cfg.ClientId, cfg.ClientSecret, CancellationToken.None);
            cfg.RefreshToken = result.RefreshToken;
            SaveConfig("Google Calendar connected");
            Notify("Google Calendar connected", "You're signed in.", NotifyKind.Info, force: true);
            _ = RefreshListsAsync(notifyOnError: false);
        }
        catch (Exception ex)
        {
            _log.Error("Google Calendar sign-in failed", ex);
            Notify("Google Calendar sign-in failed", ex.Message, NotifyKind.Error, force: true);
        }
    }

    private void DisconnectGoogleCalendar()
    {
        _config.GoogleCalendar.RefreshToken = "";
        _googleHealth = SpHealth.Unknown;
        _calendars = Array.Empty<SpNamedItem>();
        SaveConfig("Google Calendar disconnected");
        Notify("Google Calendar", "Disconnected.", NotifyKind.Info);
    }

    private void SetGoogleCalendarId(string id, string label)
    {
        _config.GoogleCalendar.CalendarId = id;
        SaveConfig($"Google Calendar default calendar = \"{label}\" [{id}]");
    }

    // ---- Beeper -----------------------------------------------------

    private void ToggleBeeperEnabled()
    {
        var cfg = _config.Beeper;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"Beeper messages {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetBeeperTokenAsync()
    {
        var value = await InputDialog.ShowAsync("Beeper API token",
            "Paste a personal access token from Beeper Desktop's API/developer settings\n" +
            "(needs read + write scope). Leave blank to keep the current one.");
        if (value is null || value.Trim().Length == 0) return;

        _config.Beeper.ApiToken = value.Trim();
        SaveConfig("Beeper API token updated");
    }

    // ---- Telegram -------------------------------------------------------

    private void ToggleTelegramEnabled()
    {
        var cfg = _config.Telegram;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"Telegram bot {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetTelegramBotTokenAsync()
    {
        var value = await InputDialog.ShowAsync("Telegram bot token",
            "Paste a bot token from @BotFather, e.g. 123456789:AAF...\n" +
            "Leave blank to keep the current one.");
        if (value is null || value.Trim().Length == 0) return;

        _config.Telegram.BotToken = value.Trim();
        SaveConfig("Telegram bot token updated");
    }

    // ---- Whisper ------------------------------------------------------

    private void ToggleWhisperEnabled()
    {
        var cfg = _config.Whisper;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"Whisper transcription {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetWhisperBaseUrlAsync()
    {
        var value = await InputDialog.ShowAsync("Whisper server URL",
            "Paste the base URL of your local Whisper server, e.g. http://127.0.0.1:8000.\n" +
            "Leave blank to keep the current one.", masked: false);
        if (value is null || value.Trim().Length == 0) return;

        _config.Whisper.BaseUrl = value.Trim();
        SaveConfig("Whisper server URL updated");
    }

    private async Task SetWhisperModelAsync()
    {
        var value = await InputDialog.ShowAsync("Whisper model",
            "Model name to request, if your server serves more than one.\n" +
            "Leave blank to use whatever the server has loaded by default.", masked: false);
        if (value is null) return;

        _config.Whisper.Model = value.Trim();
        SaveConfig(string.IsNullOrWhiteSpace(value) ? "Whisper model cleared (server default)" : "Whisper model updated");
    }

    // ---- Web search -----------------------------------------------------

    private void ToggleWebSearchEnabled()
    {
        var cfg = _config.WebSearch;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"Web search {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetWebSearchChatIdAsync()
    {
        var value = await InputDialog.ShowAsync("Web search Telegram chat ID",
            "The numeric chat ID (or \"@channelusername\") web-search summaries go to — message " +
            "the bot from that chat once, then check its getUpdates response, or ask " +
            "@userinfobot.\nLeave blank to keep the current one.", masked: false);
        if (value is null || value.Trim().Length == 0) return;

        _config.WebSearch.ChatId = value.Trim();
        SaveConfig("Web search chat ID updated");
    }

    // ---- Webhook receipt --------------------------------------------

    private void ToggleWebhookReceiptEnabled()
    {
        var cfg = _config.WebhookReceipt;
        cfg.Enabled = !cfg.Enabled;
        SaveConfig($"Webhook receipt {(cfg.Enabled ? "enabled" : "disabled")}");
    }

    private async Task SetWebhookReceiptChatIdAsync()
    {
        var value = await InputDialog.ShowAsync("Webhook receipt Telegram chat ID",
            "The numeric chat ID (or \"@channelusername\") receipts go to — message the bot from " +
            "that chat once, then check its getUpdates response, or ask @userinfobot.\n" +
            "Leave blank to keep the current one.", masked: false);
        if (value is null || value.Trim().Length == 0) return;

        _config.WebhookReceipt.ChatId = value.Trim();
        SaveConfig("Webhook receipt chat ID updated");
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

    private void ToggleCheckForUpdates()
    {
        _config.CheckForUpdates = !_config.CheckForUpdates;
        ConfigureUpdateTimer();
        SaveConfig($"Check for updates automatically {(_config.CheckForUpdates ? "enabled" : "disabled")}");
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
        _outboxTimer.Stop();
        _updateTimer.Stop();
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

    private void OnTaskQueued(string title) => Dispatcher.UIThread.Post(() =>
    {
        RebuildMenu();
        Notify("Task queued for retry", $"Super Productivity unreachable — will keep trying.\n{title}",
            NotifyKind.Warning);
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
        _outboxTimer.Stop();
        _updateTimer.Stop();
        try { StopServerAsync().GetAwaiter().GetResult(); } catch { /* shutting down */ }
        try { _tray.IsVisible = false; _tray.Dispose(); } catch { /* ignore */ }
        try { _logWindow?.Close(); } catch { /* ignore */ }
    }
}
