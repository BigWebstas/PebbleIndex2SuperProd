using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Thin client for the Super Productivity desktop Local REST API
/// (http://127.0.0.1:3876 by default). Docs: super-productivity/docs/wiki/3.01-API.md
/// </summary>
public sealed class SuperProductivityClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppConfig.SuperProductivityConfig _config;

    public SuperProductivityClient(AppConfig.SuperProductivityConfig config)
    {
        _config = config;
        // Bypass any system proxy — the API is on loopback.
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(config.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrEmpty(config.AccessToken))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AccessToken);
    }

    public sealed record CreateResult(string? TaskId, string RawData);

    public async Task<CreateResult> CreateTaskAsync(SpTaskRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("tasks", request, JsonOpts, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new SpApiException("Super Productivity rejected the token (401). " +
                                     "Check superProductivity.accessToken in config.json.");

        SpEnvelope? envelope = null;
        try { envelope = JsonSerializer.Deserialize<SpEnvelope>(body, JsonOpts); }
        catch { /* fall through to status-code handling */ }

        if (envelope is { Ok: false })
        {
            var msg = envelope.Error?.Message ?? envelope.Error?.Code ?? "unknown error";
            throw new SpApiException($"Super Productivity error: {msg}");
        }

        if (!resp.IsSuccessStatusCode)
            throw new SpApiException($"Super Productivity returned HTTP {(int)resp.StatusCode}: {Truncate(body, 400)}");

        string? id = null;
        if (envelope is { Ok: true } && envelope.Data.ValueKind == JsonValueKind.Object &&
            envelope.Data.TryGetProperty("id", out var idProp))
        {
            id = idProp.GetString();
        }

        return new CreateResult(id, Truncate(body, 2000));
    }

    public Task<IReadOnlyList<SpNamedItem>> GetProjectsAsync(CancellationToken ct = default)
        => GetNamedListAsync("projects", includeArchived: false, ct);

    public Task<IReadOnlyList<SpNamedItem>> GetTagsAsync(CancellationToken ct = default)
        => GetNamedListAsync("tags", includeArchived: true, ct);

    private async Task<IReadOnlyList<SpNamedItem>> GetNamedListAsync(string path, bool includeArchived, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new SpApiException($"Super Productivity rejected the token (401) on GET /{path}.");
        if (!resp.IsSuccessStatusCode)
            throw new SpApiException($"GET /{path} returned HTTP {(int)resp.StatusCode}.");

        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts); }
        catch (JsonException ex) { throw new SpApiException($"GET /{path}: could not parse response ({ex.Message})"); }

        // Tolerate both { ok, data: [...] } and a bare [...]
        var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;
        if (arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<SpNamedItem>();

        var list = new List<SpNamedItem>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
            if (!includeArchived && el.TryGetProperty("isArchived", out var arch) &&
                arch.ValueKind == JsonValueKind.True) continue;

            var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            list.Add(new SpNamedItem(id, string.IsNullOrWhiteSpace(title) ? id : title!));
        }
        return list;
    }

    /// <summary>Probe the API. Tries GET /health (unauthenticated) then GET /tasks.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var health = await _http.GetAsync("health", ct);
            if (health.IsSuccessStatusCode)
                return $"OK — {_config.BaseUrl} reachable (GET /health {(int)health.StatusCode})";
        }
        catch (HttpRequestException)
        {
            throw new SpApiException($"Cannot reach {_config.BaseUrl}. Is Super Productivity running with " +
                                     "'Enable local REST API' turned on (Settings → Misc)?");
        }

        using var tasks = await _http.GetAsync("tasks", ct);
        if (tasks.StatusCode == HttpStatusCode.Unauthorized)
            throw new SpApiException("Reached the API but the token was rejected (401 on GET /tasks).");
        if (!tasks.IsSuccessStatusCode)
            throw new SpApiException($"Reached the API but GET /tasks returned HTTP {(int)tasks.StatusCode}.");

        return $"OK — {_config.BaseUrl} reachable and token accepted (GET /tasks 200)";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}

public sealed class SpApiException(string message) : Exception(message);
