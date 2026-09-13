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
    }

    public async Task CreateNoteAsync(string title, string body, CancellationToken ct = default)
    {
        var payload = new CreateNoteBody
        {
            Title = title,
            Body = body,
            ParentId = string.IsNullOrWhiteSpace(_config.NotebookId) ? null : _config.NotebookId.Trim(),
        };

        using var resp = await _http.PostAsJsonAsync($"notes?token={Uri.EscapeDataString(_config.AuthToken)}", payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new JoplinApiException($"Joplin returned HTTP {(int)resp.StatusCode}: {(text.Length > 300 ? text[..300] + "…" : text)}");
        }
    }

    /// <summary>Notebooks (folders), for the tray's "Default notebook" picker.</summary>
    public async Task<IReadOnlyList<SpNamedItem>> GetFoldersAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"folders?token={Uri.EscapeDataString(_config.AuthToken)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new JoplinApiException($"Joplin returned HTTP {(int)resp.StatusCode} on GET /folders.");

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

    public void Dispose() => _http.Dispose();
}

public sealed class JoplinApiException(string message) : Exception(message);
