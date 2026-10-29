using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Thin client for Home Assistant's REST API. Used only as an additional, best-effort destination
/// for transcriptions the AI classifier reads as "turn off the kitchen lights" — it never replaces
/// the Super Productivity task. Because a spoken sentence drives real devices, only the domains
/// and services in <see cref="Services"/> can be reached — nothing the AI returns is forwarded as-is.
/// </summary>
public sealed class HomeAssistantClient : IDisposable
{
    /// <summary>Actions the AI may ask for, by domain → the HA service each one calls. Anything not
    /// listed (locks, alarms, climate, arbitrary services) is deliberately unreachable.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Services = new()
    {
        ["light"] = Standard,
        ["switch"] = Standard,
        ["fan"] = Standard,
        ["input_boolean"] = Standard,
        ["cover"] = new() { ["open"] = "open_cover", ["close"] = "close_cover" },
        ["scene"] = new() { ["turn_on"] = "turn_on" },
        ["script"] = new() { ["turn_on"] = "turn_on" },
    };

    private static Dictionary<string, string> Standard => new()
    {
        ["turn_on"] = "turn_on", ["turn_off"] = "turn_off", ["toggle"] = "toggle",
    };

    public static readonly IReadOnlyList<string> Actions = new[] { "turn_on", "turn_off", "toggle", "open", "close" };

    private readonly HttpClient _http;
    private readonly AppConfig.HomeAssistantConfig _config;

    public HomeAssistantClient(AppConfig.HomeAssistantConfig config)
    {
        _config = config;
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(config.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken);
    }

    /// <summary>Controllable entities (id + friendly name), limited to the domains above.</summary>
    public async Task<IReadOnlyList<SpNamedItem>> GetEntitiesAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/states", ct);
        Check(resp, "GET /api/states");

        var root = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<SpNamedItem>();
        if (root.ValueKind != JsonValueKind.Array) return list;

        foreach (var el in root.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
            if (!Services.ContainsKey(DomainOf(id))) continue;
            var name = el.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("friendly_name", out var nEl)
                ? nEl.GetString()
                : null;
            list.Add(new SpNamedItem(id, string.IsNullOrWhiteSpace(name) ? id : name!));
        }
        return list;
    }

    /// <summary>Whether <paramref name="action"/> may be applied to <paramref name="entityId"/>.</summary>
    public static bool IsAllowed(string entityId, string action) =>
        Services.TryGetValue(DomainOf(entityId), out var actions) && actions.ContainsKey(action);

    public async Task CallAsync(string entityId, string action, CancellationToken ct = default)
    {
        if (!IsAllowed(entityId, action))
            throw new HomeAssistantApiException($"\"{action}\" isn't allowed on {entityId}.");

        var domain = DomainOf(entityId);
        var path = $"api/services/{domain}/{Services[domain][action]}";
        using var resp = await _http.PostAsJsonAsync(path, new { entity_id = entityId }, ct);
        Check(resp, $"POST /{path}");
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("api/", ct);
            Check(resp, "GET /api/");
            return $"OK — Home Assistant reachable at {_config.BaseUrl}";
        }
        catch (HttpRequestException)
        {
            throw new HomeAssistantApiException($"Cannot reach Home Assistant at {_config.BaseUrl}.");
        }
    }

    private static string DomainOf(string entityId) => entityId.Split('.', 2)[0];

    private static void Check(HttpResponseMessage resp, string what)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new HomeAssistantApiException("Home Assistant rejected the access token (Profile → Security → Long-lived access tokens).");
        if (!resp.IsSuccessStatusCode)
            throw new HomeAssistantApiException($"Home Assistant returned HTTP {(int)resp.StatusCode} on {what}.");
    }

    public void Dispose() => _http.Dispose();
}

public sealed class HomeAssistantApiException(string message) : Exception(message);
