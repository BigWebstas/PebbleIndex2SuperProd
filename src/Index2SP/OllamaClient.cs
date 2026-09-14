using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Thin client for a local Ollama server's model-listing endpoint. Used by the AI classifier's
/// Ollama backend for both the tray's model picker and its health check — Ollama has no API key,
/// so bare reachability isn't a meaningful test; checking the configured model is actually
/// pulled is.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;

    public OllamaClient(string baseUrl, int timeoutSeconds)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:11434" : baseUrl.TrimEnd('/');
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(normalized + "/"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
    }

    /// <summary>Locally-pulled models (as shown by `ollama list`), for the tray's model picker
    /// and the health check.</summary>
    public async Task<IReadOnlyList<SpNamedItem>> GetModelsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/tags", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new OllamaApiException($"Ollama returned HTTP {(int)resp.StatusCode} on GET /api/tags.");

        var root = JsonSerializer.Deserialize<JsonElement>(body);
        var list = new List<SpNamedItem>();
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in models.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } n
                    ? n
                    : el.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
                if (name is { Length: > 0 })
                    list.Add(new SpNamedItem(name, name));
            }
        }
        return list;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class OllamaApiException(string message) : Exception(message);
