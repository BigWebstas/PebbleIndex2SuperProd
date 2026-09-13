using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Index2SP;

/// <summary>
/// Thin client for Joplin's local Web Clipper API (Tools → Options → Web Clipper).
/// Used only as an additional, best-effort archive for transcriptions the AI classifier marks
/// as a note — it never replaces the Super Productivity task.
/// </summary>
public sealed class JoplinClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppConfig.JoplinConfig _config;

    public JoplinClient(AppConfig.JoplinConfig config)
    {
        _config = config;
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(config.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
    }

    private sealed class CreateNoteBody
    {
        [JsonPropertyName("title")] public required string Title { get; set; }
        [JsonPropertyName("body")] public required string Body { get; set; }
        [JsonPropertyName("parent_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParentId { get; set; }

        // Joplin's clipper API takes a comma-separated list of tag TITLES here (not ids) and
        // creates any that don't already exist — passing an id creates a garbage tag literally
        // named after that id, confirmed against a live Joplin instance.
        [JsonPropertyName("tags")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tags { get; set; }
    }

    public async Task CreateNoteAsync(string title, string body, IReadOnlyList<string>? tagTitles = null, CancellationToken ct = default)
    {
        var payload = new CreateNoteBody
        {
            Title = title,
            Body = body,
            ParentId = string.IsNullOrWhiteSpace(_config.NotebookId) ? null : _config.NotebookId.Trim(),
            Tags = tagTitles is { Count: > 0 } ? string.Join(',', tagTitles) : null,
        };

        using var resp = await _http.PostAsJsonAsync($"notes?token={Uri.EscapeDataString(_config.AuthToken)}", payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new JoplinApiException($"Joplin returned HTTP {(int)resp.StatusCode}: {(text.Length > 300 ? text[..300] + "…" : text)}");
        }
    }

    /// <summary>Notebooks (folders), for the tray's "Default notebook" picker.</summary>
    public Task<IReadOnlyList<SpNamedItem>> GetFoldersAsync(CancellationToken ct = default)
        => GetNamedListAsync("folders", ct);

    /// <summary>Existing Joplin tags, for the AI classifier and the tray's "Default tag" picker.</summary>
    public Task<IReadOnlyList<SpNamedItem>> GetTagsAsync(CancellationToken ct = default)
        => GetNamedListAsync("tags", ct);

    private async Task<IReadOnlyList<SpNamedItem>> GetNamedListAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{path}?token={Uri.EscapeDataString(_config.AuthToken)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new JoplinApiException($"Joplin returned HTTP {(int)resp.StatusCode} on GET /{path}.");

        var root = JsonSerializer.Deserialize<JsonElement>(body);
        var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items)
            ? items
            : root;
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<SpNamedItem>();

        var list = new List<SpNamedItem>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
            var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            list.Add(new SpNamedItem(id, string.IsNullOrWhiteSpace(title) ? id : title!));
        }
        return list;
    }

    /// <summary>Probe the Web Clipper service, then the token. Mirrors
    /// <see cref="SuperProductivityClient.TestAsync"/>.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var ping = await _http.GetAsync("ping", ct);
            if (!ping.IsSuccessStatusCode)
                throw new JoplinApiException($"Reached {_config.BaseUrl} but GET /ping returned HTTP {(int)ping.StatusCode}.");
        }
        catch (HttpRequestException)
        {
            throw new JoplinApiException($"Cannot reach {_config.BaseUrl}. Is Joplin running with " +
                                          "the Web Clipper service enabled (Tools → Options → Web Clipper)?");
        }

        using var folders = await _http.GetAsync($"folders?token={Uri.EscapeDataString(_config.AuthToken)}", ct);
        if (!folders.IsSuccessStatusCode)
            throw new JoplinApiException($"Reached Joplin but the auth token was rejected (HTTP {(int)folders.StatusCode} on GET /folders).");

        return $"OK — {_config.BaseUrl} reachable and token accepted (GET /folders {(int)folders.StatusCode})";
    }

    public void Dispose() => _http.Dispose();
}

public sealed class JoplinApiException(string message) : Exception(message);
