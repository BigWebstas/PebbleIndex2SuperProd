using System.Text.Json;
using System.Text.Json.Serialization;

namespace Index2SP;

/// <summary>
/// User configuration, persisted as JSON at %APPDATA%\Index2SP\config.json.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Interface Kestrel binds for the inbound Pebble webhook. Use 127.0.0.1 when a
    /// local tunnel (cloudflared/ngrok) connects to it; use 0.0.0.0 to accept LAN / container traffic.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>TCP port for the inbound webhook listener.</summary>
    public int Port { get; set; } = 8787;

    /// <summary>Path Pebble should POST to, e.g. https://your-tunnel.example/pebble</summary>
    public string WebhookPath { get; set; } = "/pebble";

    /// <summary>
    /// Optional shared secret. When set, the inbound request must carry
    /// "Authorization: Bearer &lt;token&gt;" (configure it as a custom header in the Pebble webhook settings).
    /// A bare token without the "Bearer " prefix is also accepted.
    /// </summary>
    public string InboundAuthToken { get; set; } = "";

    /// <summary>Super Productivity title cap (the API rejects titles &gt; 300 chars after trim).</summary>
    public int TitleMaxLength { get; set; } = 300;

    /// <summary>When true, show Windows balloon notifications for each success / failure.</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>Seconds between background Super Productivity health checks (GET /health) that keep
    /// the tray status fresh. 0 disables the timer; minimum 15.</summary>
    public int HealthCheckSeconds { get; set; } = 60;

    /// <summary>Consecutive failed probes (across any of the health checks — Super Productivity,
    /// Joplin, Google Calendar, Beeper, Telegram, Whisper, AI classifier) needed before an outage
    /// notification and outage task fire. A single blip stays quiet. Clamped to 1–20.</summary>
    public int OutageFailureThreshold { get; set; } = 3;

    /// <summary>A webhook whose transcription equals this phrase (trimmed, case-insensitive) is
    /// treated as a connectivity test — it shows a notification instead of creating a task.
    /// This is what Pebble's "send test event" produces. Blank disables the check.</summary>
    public string TestEventPhrase { get; set; } = "Index webhook test event";

    /// <summary>Seconds between retry passes for the disk-backed outbox — tasks that couldn't be
    /// delivered to Super Productivity when their webhook arrived. Clamped to 10–3600.</summary>
    public int OutboxRetrySeconds { get; set; } = 60;

    /// <summary>Give up on a queued task after this many failed delivery attempts and move its file
    /// to outbox\failed\. 0 = retry forever (the default).</summary>
    public int OutboxMaxAttempts { get; set; } = 0;

    /// <summary>When true, periodically check GitHub for a newer release and surface it in the
    /// tray menu. Never downloads or installs anything — just a heads-up with a link.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public SuperProductivityConfig SuperProductivity { get; set; } = new();

    public AiClassifierConfig AiClassifier { get; set; } = new();

    public JoplinConfig Joplin { get; set; } = new();

    public GoogleCalendarConfig GoogleCalendar { get; set; } = new();

    public BeeperConfig Beeper { get; set; } = new();

    public WhisperConfig Whisper { get; set; } = new();

    public WebSearchConfig WebSearch { get; set; } = new();

    /// <summary>
    /// Optional: when the AI classifier decides a transcription is a web-search request
    /// ("search the web for...", "google...", "what is the latest version of..."), searches via
    /// whichever provider is selected for classification — Claude and Gemini each have a real
    /// built-in search tool; OpenAI's lives on its separate Responses API. Ollama has no search of
    /// its own, so it falls back to Claude when aiClassifier.apiKey is set, and skips otherwise.
    /// Sends the summary to one Telegram chat. Requires telegram.enabled.
    /// </summary>
    public sealed class WebSearchConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Telegram chat id (or "@channelusername") web-search summaries go to. Its own
        /// setting, independent of webhookReceipt's — the two can go to different chats.</summary>
        public string ChatId { get; set; } = "";

        /// <summary>How many searches Claude may run to answer one query. Clamped 1–10.</summary>
        public int MaxUses { get; set; } = 3;

        /// <summary>Seconds to wait after the search summary is ready before sending it — keeps
        /// it from landing in the same instant as the webhook-receipt message that follows right
        /// after. Clamped 0–60; 0 sends immediately.</summary>
        public int SendDelaySeconds { get; set; } = 5;
    }

    public WebhookReceiptConfig WebhookReceipt { get; set; } = new();

    /// <summary>
    /// Optional: send a short Telegram message for every webhook Index2SP receives, naming what
    /// the AI decided it was ("Webhook Received, Note Created") — a quick receipt independent of
    /// whether the underlying Super Productivity task or destination actually succeeds. Requires
    /// telegram.enabled.
    /// </summary>
    public sealed class WebhookReceiptConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Telegram chat id (or "@channelusername") receipts go to. Its own setting,
        /// independent of webSearch's — the two can go to different chats.</summary>
        public string ChatId { get; set; } = "";
    }

    public TelegramConfig Telegram { get; set; } = new();

    /// <summary>
    /// Optional: the bot connection used to deliver web-search summaries and webhook receipts —
    /// each of those picks its own destination chat (webSearch.chatId / webhookReceipt.chatId),
    /// so this only holds the bot itself. Create a bot via @BotFather to get a token, then
    /// message the target chat once and use the bot's getUpdates response (or @userinfobot /
    /// @RawDataBot) to find that chat's id.
    /// </summary>
    public sealed class TelegramConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Bot token from @BotFather, e.g. "123456789:AAF...".</summary>
        public string BotToken { get; set; } = "";

        /// <summary>Seconds to wait for Telegram before giving up. Clamped 2–30.</summary>
        public int TimeoutSeconds { get; set; } = 8;
    }

    /// <summary>
    /// Optional: when Pebble sends a webhook with an audio file but no transcription text (the
    /// case that's normally rejected with a 422), transcribe the audio locally instead of
    /// dropping it. Points at a local, OpenAI-compatible Whisper server — whisper.cpp's own
    /// `server` example, faster-whisper-server, LocalAI, etc. all implement the same
    /// POST /v1/audio/transcriptions contract. Never overrides a transcription Pebble already
    /// sent; only fills in for a genuinely audio-only webhook.
    /// </summary>
    public sealed class WhisperConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Base URL of the local Whisper server.</summary>
        public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

        /// <summary>Model name to request, if your server serves more than one. Blank uses
        /// whatever the server has loaded by default.</summary>
        public string Model { get; set; } = "";

        /// <summary>Seconds to wait for a transcription before giving up and falling back to the
        /// normal audio-only rejection. Transcription is slower than a classify call, so this has
        /// a higher ceiling than the other integrations. Clamped 5–120.</summary>
        public int TimeoutSeconds { get; set; } = 30;
    }

    /// <summary>
    /// Optional: when the AI classifier decides a transcription is a request to message someone
    /// ("send a message to Abbie say hello"), send it through Beeper Desktop's local API — which
    /// reaches whatever network (Telegram, WhatsApp, iMessage, etc.) that chat already uses. The
    /// Super Productivity task is still created either way — additive, never a replacement.
    /// Never guesses between ambiguous or missing matches: if the recipient name doesn't match
    /// exactly one existing single-person chat, nothing is sent and it's logged.
    /// </summary>
    public sealed class BeeperConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Beeper Desktop's local API.</summary>
        public string BaseUrl { get; set; } = "http://127.0.0.1:23373";

        /// <summary>Personal access token created in Beeper Desktop's API/developer settings.
        /// Needs read + write scope (search chats, send messages).</summary>
        public string ApiToken { get; set; } = "";

        /// <summary>Seconds to wait for Beeper before giving up. Clamped 2–30.</summary>
        public int TimeoutSeconds { get; set; } = 8;
    }

    /// <summary>
    /// Optional: when the AI classifier decides a transcription describes a dated/timed event
    /// ("dinner with parents on the 18th at 5pm"), also create it on Google Calendar. The Super
    /// Productivity task is still created either way — additive, never a replacement. Needs a
    /// one-time interactive sign-in from the tray (Google Calendar → Connect…) after clientId /
    /// clientSecret are set from a Google Cloud OAuth "Desktop app" client.
    /// </summary>
    public sealed class GoogleCalendarConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>OAuth client id from a Google Cloud "Desktop app" OAuth client
        /// (console.cloud.google.com → APIs &amp; Services → Credentials).</summary>
        public string ClientId { get; set; } = "";

        /// <summary>OAuth client secret from the same credential.</summary>
        public string ClientSecret { get; set; } = "";

        /// <summary>Refresh token from the tray's Connect flow. Blank = not connected.</summary>
        public string RefreshToken { get; set; } = "";

        /// <summary>Calendar to create events on. "primary" = the account's main calendar.</summary>
        public string CalendarId { get; set; } = "primary";

        /// <summary>Seconds to wait for Google before giving up. Clamped 2–30.</summary>
        public int TimeoutSeconds { get; set; } = 8;
    }

    /// <summary>
    /// Optional: when the AI classifier decides a transcription is a note rather than an
    /// actionable task, also send a copy to Joplin via its Web Clipper API. The Super
    /// Productivity task is still created either way — this is additive, never a replacement.
    /// Requires aiClassifier.enabled, and Joplin running with Tools → Options → Web Clipper →
    /// "Enable Web Clipper Service" turned on.
    /// </summary>
    public sealed class JoplinConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Joplin's Web Clipper API, shown on Tools → Options → Web Clipper.</summary>
        public string BaseUrl { get; set; } = "http://127.0.0.1:41184";

        /// <summary>Authorisation token from the same Web Clipper settings page.</summary>
        public string AuthToken { get; set; } = "";

        /// <summary>Notebook (folder) id to file notes under. Blank = Joplin's default
        /// (the last notebook selected in the app).</summary>
        public string NotebookId { get; set; } = "";

        /// <summary>Tag ids applied to every note sent to Joplin. Used as-is while
        /// aiClassifier.requireTags is off; used as the fallback when it's on but the AI
        /// didn't come back with a usable Joplin tag.</summary>
        public List<string> DefaultTagIds { get; set; } = new();

        /// <summary>Seconds to wait for Joplin before giving up. Clamped 2–30.</summary>
        public int TimeoutSeconds { get; set; } = 8;
    }

    /// <summary>
    /// Optional: have Claude read the transcription plus your existing Super Productivity
    /// projects/tags and pick the best fit for each new task, instead of always applying the
    /// static superProductivity.projectId / tagIds. Disabled by default. Any failure (no key,
    /// network, timeout, bad response) falls back to the static config — a task is always
    /// created either way.
    /// </summary>
    public sealed class AiClassifierConfig
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Which backend to use: "claude" (default), "gemini", "openai", or "ollama".
        /// Set from the tray (AI classifier → Provider). Unrecognized values fall back to
        /// "claude". Each provider needs its own credential below before it will actually run.</summary>
        public string Provider { get; set; } = "claude";

        /// <summary>Optional second backend to try when <see cref="Provider"/> fails to classify
        /// (no credential, network, timeout, or a malformed response) — same set of values as
        /// <see cref="Provider"/>, or blank for none. Only tried once the primary has already
        /// failed; if it also fails, the static config is used like any other classify failure.</summary>
        public string FallbackProvider { get; set; } = "";

        /// <summary>Anthropic API key. Get one at https://console.anthropic.com/ .</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Claude model id. The default is fast and cheap — plenty for picking a
        /// project/tag from a short list by title match.</summary>
        public string Model { get; set; } = "claude-haiku-4-5";

        /// <summary>Google AI Studio API key. Get one at https://aistudio.google.com/apikey .</summary>
        public string GeminiApiKey { get; set; } = "";

        /// <summary>Gemini model id.</summary>
        public string GeminiModel { get; set; } = "gemini-2.5-flash";

        /// <summary>OpenAI API key. Get one at https://platform.openai.com/api-keys .</summary>
        public string OpenAiApiKey { get; set; } = "";

        /// <summary>OpenAI model id.</summary>
        public string OpenAiModel { get; set; } = "gpt-4o-mini";

        /// <summary>Local Ollama server (Tools → no API key needed — it's your own machine).</summary>
        public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";

        /// <summary>Model name as shown by `ollama list`. Blank disables the Ollama provider even
        /// if selected — there's no sane default, since it depends entirely on what you've pulled
        /// locally, and not every local model supports tool calling. Ollama also can't force a
        /// tool call the way the hosted providers can, so a model that ignores the request just
        /// falls back to the static config, same as any other classify failure.</summary>
        public string OllamaModel { get; set; } = "";

        /// <summary>Seconds to wait for the provider before giving up and using the static
        /// config. Clamped to 2–30.</summary>
        public int TimeoutSeconds { get; set; } = 8;

        /// <summary>When true, the AI must pick at least one Super Productivity tag for every
        /// task (falling back to superProductivity.tagIds only if it still can't), and at least
        /// one Joplin tag for every item it flags as a note (falling back to joplin.defaultTagIds
        /// only if it still can't). When false, the AI isn't asked to pick tags at all — every
        /// task/note just gets the configured default tags.</summary>
        public bool RequireTags { get; set; } = false;

        /// <summary>When true (and only takes effect while <see cref="RequireTags"/> is also on),
        /// the configured superProductivity.tagIds default tags are added alongside whatever tags
        /// the AI picked, instead of the AI's picks replacing them. Off by default.</summary>
        public bool AlwaysAddDefaultTag { get; set; } = false;

        /// <summary>When true, a transcription routed to Joplin, Google Calendar, or Beeper (as
        /// a note, calendar event, or message) skips the Super Productivity task as long as that
        /// destination actually succeeds — avoiding a duplicate. If the destination attempt fails
        /// (or its integration isn't enabled), the Super Productivity task is still created as a
        /// fallback. Plain tasks and shopping-list items are unaffected — there's no other
        /// destination for those. Off by default (current behaviour: always additive).</summary>
        public bool ExclusiveRouting { get; set; } = false;
    }

    public sealed class SuperProductivityConfig
    {
        /// <summary>Base URL of the Super Productivity desktop Local REST API.</summary>
        public string BaseUrl { get; set; } = "http://127.0.0.1:3876";

        /// <summary>Access token from Super Productivity: Settings → Misc → Local REST API.
        /// Sent as "Authorization: Bearer &lt;token&gt;". Leave blank if your build does not require it.</summary>
        public string AccessToken { get; set; } = "";

        /// <summary>Optional project to file every created task under (must be an existing active project id).
        /// Blank => Super Productivity's default inbox.</summary>
        public string ProjectId { get; set; } = "";

        /// <summary>Optional tag ids applied to every created task.</summary>
        public List<string> TagIds { get; set; } = new();

        /// <summary>Optional: when the AI classifier (aiClassifier.enabled) detects a shopping /
        /// errands item, file it under this project instead of whatever project it would
        /// otherwise have picked. Blank = no override, the classifier's normal pick stands.
        /// Ignored while the classifier is disabled.</summary>
        public string ShoppingProjectId { get; set; } = "";

        /// <summary>Optional: one tag applied to every task created from a Pebble capture
        /// (e.g. a "voice-note" / "index" tag you can filter on in Super Productivity).
        /// Give the tag id directly here…</summary>
        public string CaptureTagId { get; set; } = "";

        /// <summary>…or give the tag's name here and Index2SP resolves it to an id via GET /tags
        /// (the tag must already exist in Super Productivity — the REST API cannot create tags).
        /// Ignored when CaptureTagId is set.</summary>
        public string CaptureTagName { get; set; } = "";
    }

    // ---- persistence -------------------------------------------------------

    [JsonIgnore]
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Index2SP");

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppConfig LoadOrCreate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            fresh.Save(path);
            return fresh;
        }

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                  ?? throw new InvalidDataException("config.json deserialized to null");
        cfg.Normalize();
        return cfg;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ListenAddress)) ListenAddress = "127.0.0.1";
        if (Port is <= 0 or > 65535) Port = 8787;
        if (string.IsNullOrWhiteSpace(WebhookPath)) WebhookPath = "/pebble";
        if (!WebhookPath.StartsWith('/')) WebhookPath = "/" + WebhookPath;
        WebhookPath = WebhookPath.TrimEnd('/');
        if (WebhookPath.Length == 0) WebhookPath = "/pebble";
        if (TitleMaxLength is < 10 or > 300) TitleMaxLength = 300;
        if (HealthCheckSeconds != 0)
            HealthCheckSeconds = Math.Clamp(HealthCheckSeconds, 15, 3600);
        OutageFailureThreshold = Math.Clamp(OutageFailureThreshold, 1, 20);
        OutboxRetrySeconds = Math.Clamp(OutboxRetrySeconds, 10, 3600);
        if (OutboxMaxAttempts < 0) OutboxMaxAttempts = 0;
        AiClassifier ??= new AiClassifierConfig();
        AiClassifier.TimeoutSeconds = Math.Clamp(AiClassifier.TimeoutSeconds, 2, 30);
        AiClassifier.Provider = AiClassifier.Provider?.Trim().ToLowerInvariant() switch
        {
            "gemini" => "gemini",
            "openai" => "openai",
            "ollama" => "ollama",
            _ => "claude",
        };
        AiClassifier.FallbackProvider = AiClassifier.FallbackProvider?.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude",
            "gemini" => "gemini",
            "openai" => "openai",
            "ollama" => "ollama",
            _ => "",
        };
        if (string.IsNullOrWhiteSpace(AiClassifier.GeminiModel)) AiClassifier.GeminiModel = "gemini-2.5-flash";
        if (string.IsNullOrWhiteSpace(AiClassifier.OpenAiModel)) AiClassifier.OpenAiModel = "gpt-4o-mini";
        if (string.IsNullOrWhiteSpace(AiClassifier.OllamaBaseUrl)) AiClassifier.OllamaBaseUrl = "http://127.0.0.1:11434";
        AiClassifier.OllamaBaseUrl = AiClassifier.OllamaBaseUrl.TrimEnd('/');
        Joplin ??= new JoplinConfig();
        Joplin.TimeoutSeconds = Math.Clamp(Joplin.TimeoutSeconds, 2, 30);
        if (string.IsNullOrWhiteSpace(Joplin.BaseUrl)) Joplin.BaseUrl = "http://127.0.0.1:41184";
        Joplin.BaseUrl = Joplin.BaseUrl.TrimEnd('/');
        Joplin.DefaultTagIds ??= new List<string>();
        GoogleCalendar ??= new GoogleCalendarConfig();
        GoogleCalendar.TimeoutSeconds = Math.Clamp(GoogleCalendar.TimeoutSeconds, 2, 30);
        if (string.IsNullOrWhiteSpace(GoogleCalendar.CalendarId)) GoogleCalendar.CalendarId = "primary";
        Beeper ??= new BeeperConfig();
        Beeper.TimeoutSeconds = Math.Clamp(Beeper.TimeoutSeconds, 2, 30);
        if (string.IsNullOrWhiteSpace(Beeper.BaseUrl)) Beeper.BaseUrl = "http://127.0.0.1:23373";
        Beeper.BaseUrl = Beeper.BaseUrl.TrimEnd('/');
        Whisper ??= new WhisperConfig();
        Whisper.TimeoutSeconds = Math.Clamp(Whisper.TimeoutSeconds, 5, 120);
        if (string.IsNullOrWhiteSpace(Whisper.BaseUrl)) Whisper.BaseUrl = "http://127.0.0.1:8000";
        Whisper.BaseUrl = Whisper.BaseUrl.TrimEnd('/');
        WebSearch ??= new WebSearchConfig();
        WebSearch.MaxUses = Math.Clamp(WebSearch.MaxUses, 1, 10);
        WebSearch.SendDelaySeconds = Math.Clamp(WebSearch.SendDelaySeconds, 0, 60);
        WebhookReceipt ??= new WebhookReceiptConfig();
        Telegram ??= new TelegramConfig();
        Telegram.TimeoutSeconds = Math.Clamp(Telegram.TimeoutSeconds, 2, 30);
        SuperProductivity ??= new SuperProductivityConfig();
        if (string.IsNullOrWhiteSpace(SuperProductivity.BaseUrl))
            SuperProductivity.BaseUrl = "http://127.0.0.1:3876";
        SuperProductivity.BaseUrl = SuperProductivity.BaseUrl.TrimEnd('/');
        SuperProductivity.TagIds ??= new List<string>();
    }

    /// <summary>Best-effort local URL to show the user (they still put the tunnel host in front).</summary>
    [JsonIgnore]
    public string LocalWebhookUrl => $"http://{ListenAddress}:{Port}{WebhookPath}";
}
