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
    private readonly Logger? _log;

    public WhisperAsrClient(AppConfig.WhisperAsrConfig config, Logger? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log;
    }

    /// <summary>
    /// Transcribes the given audio bytes using either embedded in-process Whisper (whisper.cpp)
    /// or by POSTing to an external Whisper ASR webservice depending on Mode.
    /// Returns the trimmed transcribed text, or null if the transcription was empty or whitespace.
    /// </summary>
    public async Task<string?> TranscribeAsync(
        ReadOnlyMemory<byte> audioBytes,
        string? fileName = null,
        CancellationToken ct = default)
    {
        if (audioBytes.IsEmpty) return null;

        if (string.Equals(_config.Mode, "remote", StringComparison.OrdinalIgnoreCase))
        {
            return await TranscribeRemoteAsync(audioBytes, fileName, ct);
        }

        return await EmbeddedWhisperEngine.TranscribeAsync(_config, audioBytes, fileName, _log, ct);
    }

    /// <summary>
    /// Transcribes audio bytes by POSTing to the external /asr endpoint of onerahmet/openai-whisper-asr-webservice.
    /// </summary>
    public async Task<string?> TranscribeRemoteAsync(
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

        var (actualFileName, mediaType) = DetectAudioMetadata(audioBytes.Span, fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        using var formData = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(audioBytes.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        formData.Add(fileContent, "audio_file", actualFileName);
        request.Content = formData;

        _log?.Info($"Whisper ASR: POST {uri} ({audioBytes.Length} bytes, filename='{actualFileName}', mime='{mediaType}')");

        HttpResponseMessage response;
        try
        {
            response = await SharedHttpClient.SendAsync(request, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Warn($"Whisper ASR: request timed out after {_config.TimeoutSeconds}s");
            throw new WhisperAsrApiException($"Whisper ASR webservice request timed out after {_config.TimeoutSeconds}s. If your server is running on CPU, consider increasing whisperAsr.timeoutSeconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            _log?.Warn($"Whisper ASR: connection error: {ex.Message}");
            throw new WhisperAsrApiException($"Failed to reach Whisper ASR webservice at {baseUrl}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn($"Whisper ASR returned HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
                throw new WhisperAsrApiException($"Whisper ASR webservice at {baseUrl} returned HTTP {(int)response.StatusCode}: {body}");
            }

            _log?.Info($"Whisper ASR returned HTTP 200 ({body.Length} chars): {Truncate(body, 200)}");

            var text = ExtractTranscriptionText(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                _log?.Warn($"Whisper ASR returned HTTP 200 but no transcription text could be extracted. Full body ({body.Length} chars): {Truncate(body, 500)}");
                return null;
            }

            return text;
        }
    }

    /// <summary>
    /// Inspects audio bytes and filename to determine the appropriate filename and MIME type for multipart upload.
    /// Recognizes WAV (RIFF/WAVE) and M4A/MP4 (ftyp) by magic bytes, defaulting to audio.m4a for Pebble audio.
    /// </summary>
    public static (string FileName, string MediaType) DetectAudioMetadata(ReadOnlySpan<byte> bytes, string? originalFileName)
    {
        if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
            var mime = ext switch
            {
                ".m4a" => "audio/m4a",
                ".mp3" => "audio/mpeg",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                ".wav" => "audio/wav",
                _ => null,
            };
            if (mime != null) return (Path.GetFileName(originalFileName), mime);
        }

        // Check RIFF header for WAV: RIFF....WAVE
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E')
        {
            return ("audio.wav", "audio/wav");
        }

        // Check ftyp header for MP4/M4A: ....ftyp
        if (bytes.Length >= 8 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
        {
            return ("audio.m4a", "audio/m4a");
        }

        // Default fallback for Pebble voice clips is M4A
        return ("audio.m4a", "audio/m4a");
    }

    /// <summary>
    /// Extracts transcribed text from JSON (root "text", "transcription", "result", or "segments[].text")
    /// or from plain text responses.
    /// </summary>
    public static string? ExtractTranscriptionText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var trimmed = body.Trim();

        // 1. Try parsing JSON if it starts with { or [
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // Check standard top-level text properties
                    if (doc.RootElement.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    {
                        var text = textProp.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                    if (doc.RootElement.TryGetProperty("transcription", out var trProp) && trProp.ValueKind == JsonValueKind.String)
                    {
                        var text = trProp.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                    if (doc.RootElement.TryGetProperty("result", out var resProp) && resProp.ValueKind == JsonValueKind.String)
                    {
                        var text = resProp.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }

                    // Check segments array (e.g. Faster-Whisper, WhisperX, or OpenAI segments)
                    if (doc.RootElement.TryGetProperty("segments", out var segmentsProp) && segmentsProp.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var seg in segmentsProp.EnumerateArray())
                        {
                            if (seg.ValueKind == JsonValueKind.Object &&
                                seg.TryGetProperty("text", out var segTextProp) &&
                                segTextProp.ValueKind == JsonValueKind.String)
                            {
                                var segText = segTextProp.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(segText))
                                {
                                    if (sb.Length > 0) sb.Append(' ');
                                    sb.Append(segText);
                                }
                            }
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object &&
                            item.TryGetProperty("text", out var itemTextProp) &&
                            itemTextProp.ValueKind == JsonValueKind.String)
                        {
                            var segText = itemTextProp.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(segText))
                            {
                                if (sb.Length > 0) sb.Append(' ');
                                sb.Append(segText);
                            }
                        }
                        else if (item.ValueKind == JsonValueKind.String)
                        {
                            var segText = item.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(segText))
                            {
                                if (sb.Length > 0) sb.Append(' ');
                                sb.Append(segText);
                            }
                        }
                    }
                    if (sb.Length > 0) return sb.ToString();
                }

                // If valid JSON had no usable text or segments, treat as empty transcript
                return null;
            }
            catch (JsonException)
            {
                // Not valid JSON, fall through to plain text
            }
        }

        // 2. Plain text
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    /// <summary>
    /// Probe to test Whisper availability. Tests model loading in embedded mode, or HTTP reachability in remote mode.
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        if (string.Equals(_config.Mode, "remote", StringComparison.OrdinalIgnoreCase))
        {
            return await TestRemoteAsync(ct);
        }

        return await EmbeddedWhisperEngine.TestAsync(_config, _log, ct);
    }

    /// <summary>
    /// Remote reachability probe. Pings GET /health (falling back to GET /docs) and measures round-trip latency.
    /// </summary>
    public async Task<string> TestRemoteAsync(CancellationToken ct = default)
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
