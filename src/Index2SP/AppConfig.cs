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

    public SuperProductivityConfig SuperProductivity { get; set; } = new();

    public AiClassifierConfig AiClassifier { get; set; } = new();

    public JoplinConfig Joplin { get; set; } = new();

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

        /// <summary>Anthropic API key. Get one at https://console.anthropic.com/ . Leave blank to
        /// keep the classifier disabled even if Enabled is true.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Model id. The default is fast and cheap — plenty for picking a project/tag
        /// from a short list by title match.</summary>
        public string Model { get; set; } = "claude-haiku-4-5";

        /// <summary>Seconds to wait for Claude before giving up and using the static config.
        /// Clamped to 2–30.</summary>
        public int TimeoutSeconds { get; set; } = 8;

        /// <summary>When true, the AI must pick at least one Super Productivity tag for every
        /// task (falling back to superProductivity.tagIds only if it still can't), and at least
        /// one Joplin tag for every item it flags as a note (falling back to joplin.defaultTagIds
        /// only if it still can't). When false, the AI isn't asked to pick tags at all — every
        /// task/note just gets the configured default tags.</summary>
        public bool RequireTags { get; set; } = false;
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
        OutboxRetrySeconds = Math.Clamp(OutboxRetrySeconds, 10, 3600);
        if (OutboxMaxAttempts < 0) OutboxMaxAttempts = 0;
        AiClassifier ??= new AiClassifierConfig();
        AiClassifier.TimeoutSeconds = Math.Clamp(AiClassifier.TimeoutSeconds, 2, 30);
        Joplin ??= new JoplinConfig();
        Joplin.TimeoutSeconds = Math.Clamp(Joplin.TimeoutSeconds, 2, 30);
        if (string.IsNullOrWhiteSpace(Joplin.BaseUrl)) Joplin.BaseUrl = "http://127.0.0.1:41184";
        Joplin.BaseUrl = Joplin.BaseUrl.TrimEnd('/');
        Joplin.DefaultTagIds ??= new List<string>();
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
