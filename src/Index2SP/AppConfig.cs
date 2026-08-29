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

    public SuperProductivityConfig SuperProductivity { get; set; } = new();

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
