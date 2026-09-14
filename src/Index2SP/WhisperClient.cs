using System.Net.Http.Headers;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Thin client for a local, OpenAI-compatible Whisper transcription server. whisper.cpp's own
/// `server` example, faster-whisper-server, and LocalAI all implement the same
/// POST /v1/audio/transcriptions contract, so this targets that rather than any one of them by
/// name. Used only as a fallback when a Pebble webhook arrives with audio but no transcription
/// text — never overrides a transcription Pebble already sent.
/// </summary>
public sealed class WhisperClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppConfig.WhisperConfig _config;

    public WhisperClient(AppConfig.WhisperConfig config)
    {
        _config = config;
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(config.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
    }

    /// <summary>Transcribes one audio clip. Returns null when the server responded successfully
    /// but had no usable text — only actual failures (unreachable, non-2xx) throw.</summary>
    public async Task<string?> TranscribeAsync(byte[] audio, string? fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        content.Add(fileContent, "file", string.IsNullOrWhiteSpace(fileName) ? "audio.m4a" : fileName);
        if (!string.IsNullOrWhiteSpace(_config.Model))
            content.Add(new StringContent(_config.Model), "model");

        using var resp = await _http.PostAsync("v1/audio/transcriptions", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new WhisperApiException($"Whisper server returned HTTP {(int)resp.StatusCode}: {Truncate(body)}");

        var root = JsonSerializer.Deserialize<JsonElement>(body);
        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Best-effort reachability probe. There's no health endpoint standardized across
    /// implementations, so this hits the OpenAI-compatible model-list endpoint most of them
    /// (faster-whisper-server, LocalAI) expose; whisper.cpp's bare `server` example doesn't serve
    /// it and returns 404, which still proves the process itself is up and answering HTTP.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync("v1/models", ct);
        }
        catch (HttpRequestException)
        {
            throw new WhisperApiException($"Cannot reach {_config.BaseUrl}. Is the Whisper server running?");
        }
        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return $"OK — {_config.BaseUrl} is answering HTTP (no /v1/models endpoint to confirm further)";
            if (!resp.IsSuccessStatusCode)
                throw new WhisperApiException($"Whisper server returned HTTP {(int)resp.StatusCode} on GET /v1/models.");
            return "OK — Whisper server reachable";
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose() => _http.Dispose();
}

public sealed class WhisperApiException(string message) : Exception(message);
