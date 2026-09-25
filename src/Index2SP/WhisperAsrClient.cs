using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Client for onerahmet/openai-whisper-asr-webservice (https://github.com/ahmetoner/whisper-asr-webservice).
/// Communicates over HTTP REST (default: http://127.0.0.1:9000) using the /asr endpoint.
/// Accepts any audio format supported by ffmpeg (including .m4a and .wav) directly via multipart/form-data.
/// </summary>
public class WhisperAsrClient : IDisposable
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly AppConfig.WhisperAsrConfig _config;

    public WhisperAsrClient(AppConfig.WhisperAsrConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Transcribes the given audio bytes by POSTing to the /asr endpoint.
    /// Returns the trimmed transcribed text, or null if the transcription was empty or whitespace.
    /// </summary>
    public async Task<string?> TranscribeAsync(
        ReadOnlyMemory<byte> audioBytes,
        string? fileName = null,
        CancellationToken ct = default)
    {
        if (audioBytes.IsEmpty) return null;

        var baseUrl = _config.BaseUrl.TrimEnd('/');
        var query = new List<string> { "task=transcribe", "output=json" };
        if (!string.IsNullOrWhiteSpace(_config.Language))
        {
            query.Add($"language={Uri.EscapeDataString(_config.Language.Trim())}");
        }

        var uri = new Uri($"{baseUrl}/asr?{string.Join("&", query)}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        var token = cts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        using var formData = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(audioBytes.ToArray());
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        var mediaType = ext switch
        {
            ".m4a" => "audio/m4a",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "audio/wav",
        };
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var actualFileName = string.IsNullOrWhiteSpace(fileName) ? "audio.wav" : Path.GetFileName(fileName);
        formData.Add(fileContent, "audio_file", actualFileName);
        request.Content = formData;

        HttpResponseMessage response;
        try
        {
            response = await SharedHttpClient.SendAsync(request, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new WhisperAsrApiException($"Failed to reach Whisper ASR webservice at {baseUrl}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                throw new WhisperAsrApiException($"Whisper ASR webservice at {baseUrl} returned HTTP {(int)response.StatusCode}: {body}");
            }

            // Attempt to parse JSON response: {"text": "..."}
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var textProp))
                {
                    var text = textProp.GetString()?.Trim();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
            catch (JsonException)
            {
                // Fallback in case the server returned plain text directly
            }

            var trimmed = body.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    /// <summary>
    /// Reachability probe. Pings GET /health (falling back to GET /docs) and measures round-trip latency.
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var baseUrl = _config.BaseUrl.TrimEnd('/');
        var sw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(_config.TimeoutSeconds, 10)));
        var token = cts.Token;

        var healthUri = new Uri($"{baseUrl}/health");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, healthUri);
            using var resp = await SharedHttpClient.SendAsync(req, token);
            sw.Stop();

            if (resp.IsSuccessStatusCode)
            {
                return $"OK — Whisper ASR webservice reachable in {sw.ElapsedMilliseconds}ms at {baseUrl}";
            }

            // If /health was not found (404), try /docs as fallback
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                sw.Restart();
                using var docsReq = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/docs"));
                using var docsResp = await SharedHttpClient.SendAsync(docsReq, token);
                sw.Stop();

                if (docsResp.IsSuccessStatusCode)
                {
                    return $"OK — Whisper ASR webservice reachable in {sw.ElapsedMilliseconds}ms at {baseUrl}";
                }
            }

            throw new WhisperAsrApiException($"Whisper ASR webservice returned HTTP {(int)resp.StatusCode} for {healthUri}");
        }
        catch (Exception ex) when (ex is not WhisperAsrApiException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new WhisperAsrApiException($"Cannot reach Whisper ASR webservice at {baseUrl}. Is the Docker container running? ({ex.Message})", ex);
        }
    }

    public void Dispose()
    {
    }
}

public class WhisperAsrApiException : Exception
{
    public WhisperAsrApiException(string message) : base(message) { }
    public WhisperAsrApiException(string message, Exception inner) : base(message, inner) { }
}

public class WhisperAsrAudioException : Exception
{
    public WhisperAsrAudioException(string message) : base(message) { }
    public WhisperAsrAudioException(string message, Exception inner) : base(message, inner) { }
}

// Backward-compatibility aliases for legacy code referencing WhisperLive/Wyoming exceptions
public class WhisperLiveApiException : WhisperAsrApiException
{
    public WhisperLiveApiException(string message) : base(message) { }
    public WhisperLiveApiException(string message, Exception inner) : base(message, inner) { }
}

public class WhisperLiveAudioException : WhisperAsrAudioException
{
    public WhisperLiveAudioException(string message) : base(message) { }
    public WhisperLiveAudioException(string message, Exception inner) : base(message, inner) { }
}

public class WyomingApiException : WhisperAsrApiException
{
    public WyomingApiException(string message) : base(message) { }
    public WyomingApiException(string message, Exception inner) : base(message, inner) { }
}

public class WyomingAudioException : WhisperAsrAudioException
{
    public WyomingAudioException(string message) : base(message) { }
    public WyomingAudioException(string message, Exception inner) : base(message, inner) { }
}
