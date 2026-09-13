using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Thin client for Beeper Desktop's local API (confirmed live against a running instance —
/// GET /v1/info is unauthenticated and self-describes the API; everything else needs a bearer
/// token, created in Beeper Desktop's own settings). Used only as an additional, best-effort
/// destination for transcriptions the AI classifier reads as "message someone" — it never
/// replaces the Super Productivity task.
/// </summary>
public sealed class BeeperClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppConfig.BeeperConfig _config;

    public BeeperClient(AppConfig.BeeperConfig config)
    {
        _config = config;
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(config.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
        if (!string.IsNullOrEmpty(config.ApiToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiToken);
    }

    public sealed record ChatMatch(string ChatId, string Title, string Network);

    /// <summary>Existing single-person chats whose title/participant names match
    /// <paramref name="query"/> (e.g. a first name like "Abbie"). Searches by participant name so
    /// it matches on the actual person, not just however the chat happens to be titled.</summary>
    public async Task<IReadOnlyList<ChatMatch>> SearchSingleChatsAsync(string query, CancellationToken ct = default)
    {
        var url = $"v1/chats/search?query={Uri.EscapeDataString(query)}&scope=participants&type=single&limit=5";
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new BeeperApiException("Beeper rejected the API token. Create a new one in Beeper Desktop's API settings.");
        if (!resp.IsSuccessStatusCode)
            throw new BeeperApiException($"Beeper returned HTTP {(int)resp.StatusCode} on GET /v1/chats/search.");

        var root = JsonSerializer.Deserialize<JsonElement>(body);
        var list = new List<ChatMatch>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in items.EnumerateArray())
            {
                if (!el.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
                var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? id : id;
                var network = el.TryGetProperty("network", out var nEl) ? nEl.GetString() ?? "" : "";
                list.Add(new ChatMatch(id, title, network));
            }
        }
        return list;
    }

    /// <summary>Sends a plain-text message to an existing chat. Returns the pending message id.</summary>
    public async Task<string?> SendMessageAsync(string chatId, string text, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"v1/chats/{Uri.EscapeDataString(chatId)}/messages", new { text }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new BeeperApiException("Beeper rejected the API token. Create a new one in Beeper Desktop's API settings.");
        if (!resp.IsSuccessStatusCode)
            throw new BeeperApiException($"Beeper returned HTTP {(int)resp.StatusCode}: {Truncate(body)}");

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return json.TryGetProperty("pendingMessageID", out var idEl) ? idEl.GetString() : null;
    }

    /// <summary>Probes the app (unauthenticated /v1/info), then the token.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        JsonElement info;
        try
        {
            using var resp = await _http.GetAsync("v1/info", ct);
            if (!resp.IsSuccessStatusCode)
                throw new BeeperApiException($"Reached {_config.BaseUrl} but GET /v1/info returned HTTP {(int)resp.StatusCode}.");
            info = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException)
        {
            throw new BeeperApiException($"Cannot reach {_config.BaseUrl}. Is Beeper Desktop running?");
        }

        if (string.IsNullOrWhiteSpace(_config.ApiToken))
            throw new BeeperApiException("Reached Beeper Desktop but no API token is set.");

        using var accountsResp = await _http.GetAsync("v1/accounts", ct);
        if (accountsResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new BeeperApiException("Reached Beeper Desktop but the API token was rejected.");
        if (!accountsResp.IsSuccessStatusCode)
            throw new BeeperApiException($"Reached Beeper Desktop but GET /v1/accounts returned HTTP {(int)accountsResp.StatusCode}.");

        var version = info.TryGetProperty("app", out var app) && app.TryGetProperty("version", out var v) ? v.GetString() : "?";
        return $"OK — Beeper Desktop {version} reachable and token accepted";
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose() => _http.Dispose();
}

public sealed class BeeperApiException(string message) : Exception(message);
