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
    /// Transcribes audio bytes by POSTing to the external ASR webservice (supporting Parakeet,
    /// OpenAI-compatible /v1/audio/transcriptions, and /asr).
    /// </summary>
    public async Task<string?> TranscribeRemoteAsync(
        ReadOnlyMemory<byte> audioBytes,
        string? fileName = null,
        CancellationToken ct = default)
    {
        if (audioBytes.IsEmpty) return null;

        var baseUrl = _config.BaseUrl.TrimEnd('/');
        var (actualFileName, mediaType) = DetectAudioMetadata(audioBytes.Span, fileName);

        var format = (_config.Format ?? "auto").Trim().ToLowerInvariant();
        if (format == "auto")
        {
            if (baseUrl.EndsWith("/v1/audio/transcriptions", StringComparison.OrdinalIgnoreCase) ||
                baseUrl.EndsWith("/transcriptions", StringComparison.OrdinalIgnoreCase))
            {
                format = "openai";
            }
            else if (baseUrl.EndsWith("/asr", StringComparison.OrdinalIgnoreCase))
            {
                format = "asr";
            }
            else if ((_config.Model ?? "").Contains("parakeet", StringComparison.OrdinalIgnoreCase))
            {
                format = "openai";
            }
            else if ((_config.Model ?? "").StartsWith("base", StringComparison.OrdinalIgnoreCase) ||
                     (_config.Model ?? "").StartsWith("tiny", StringComparison.OrdinalIgnoreCase) ||
                     (_config.Model ?? "").StartsWith("small", StringComparison.OrdinalIgnoreCase) ||
                     (_config.Model ?? "").StartsWith("medium", StringComparison.OrdinalIgnoreCase) ||
                     (_config.Model ?? "").StartsWith("large", StringComparison.OrdinalIgnoreCase))
            {
                format = "asr";
            }
        }

        if (format == "openai")
        {
            return await TranscribeOpenAiFormatAsync(baseUrl, audioBytes, actualFileName, mediaType, ct);
        }

        if (format == "asr")
        {
            return await TranscribeAsrFormatAsync(baseUrl, audioBytes, actualFileName, mediaType, ct);
        }

        // Auto mode with generic host:port URL:
        // Try OpenAI format first (standard for Parakeet / Speaches / vLLM).
        // If 404 (endpoint not found), automatically fall back to /asr format.
        try
        {
            return await TranscribeOpenAiFormatAsync(baseUrl, audioBytes, actualFileName, mediaType, ct);
        }
        catch (WhisperAsrApiException ex) when (ex.Message.Contains("HTTP 404"))
        {
            _log?.Info($"Endpoint /v1/audio/transcriptions returned 404 at {baseUrl}, falling back to /asr endpoint...");
            return await TranscribeAsrFormatAsync(baseUrl, audioBytes, actualFileName, mediaType, ct);
        }
    }

    private async Task<string?> TranscribeOpenAiFormatAsync(
        string baseUrl,
        ReadOnlyMemory<byte> audioBytes,
        string actualFileName,
        string mediaType,
        CancellationToken ct)
    {
        var targetUri = baseUrl.EndsWith("/v1/audio/transcriptions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(baseUrl)
            : new Uri($"{baseUrl}/v1/audio/transcriptions");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        var token = cts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, targetUri);
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey.Trim());
        }

        using var formData = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioBytes.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        formData.Add(fileContent, "file", actualFileName);

        var modelName = !string.IsNullOrWhiteSpace(_config.Model) &&
                        !_config.Model.StartsWith("base", StringComparison.OrdinalIgnoreCase) &&
                        !_config.Model.StartsWith("tiny", StringComparison.OrdinalIgnoreCase) &&
                        !_config.Model.StartsWith("small", StringComparison.OrdinalIgnoreCase)
            ? _config.Model
            : "parakeet-tdt-0.6b";
        formData.Add(new StringContent(modelName), "model");
        formData.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(_config.Language))
        {
            formData.Add(new StringContent(_config.Language.Trim()), "language");
        }

        request.Content = formData;
        _log?.Info($"STT: POST {targetUri} [OpenAI/Parakeet format] ({audioBytes.Length} bytes, model='{modelName}', filename='{actualFileName}')");

        HttpResponseMessage response;
        try
        {
            response = await SharedHttpClient.SendAsync(request, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Warn($"STT: request timed out after {_config.TimeoutSeconds}s");
            throw new WhisperAsrApiException($"STT webservice request timed out after {_config.TimeoutSeconds}s at {targetUri}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            _log?.Warn($"STT: connection error: {ex.Message}");
            throw new WhisperAsrApiException($"Failed to reach STT webservice at {targetUri}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn($"STT returned HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
                throw new WhisperAsrApiException($"STT webservice at {targetUri} returned HTTP {(int)response.StatusCode}: {body}");
            }

            _log?.Info($"STT returned HTTP 200 ({body.Length} chars): {Truncate(body, 200)}");
            var text = ExtractTranscriptionText(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                _log?.Warn($"STT returned HTTP 200 but no transcription text could be extracted. Full body ({body.Length} chars): {Truncate(body, 500)}");
                return null;
            }

            return text;
        }
    }

    private async Task<string?> TranscribeAsrFormatAsync(
        string baseUrl,
        ReadOnlyMemory<byte> audioBytes,
        string actualFileName,
        string mediaType,
        CancellationToken ct)
    {
        var targetUrl = baseUrl.EndsWith("/asr", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/asr";

        var query = new List<string> { "task=transcribe", "output=json" };
        if (!string.IsNullOrWhiteSpace(_config.Language))
        {
            query.Add($"language={Uri.EscapeDataString(_config.Language.Trim())}");
        }

        var delimiter = targetUrl.Contains('?') ? "&" : "?";
        var uri = new Uri($"{targetUrl}{delimiter}{string.Join("&", query)}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        var token = cts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey.Trim());
        }

        using var formData = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioBytes.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        formData.Add(fileContent, "audio_file", actualFileName);

        request.Content = formData;
        _log?.Info($"STT: POST {uri} [/asr format] ({audioBytes.Length} bytes, filename='{actualFileName}')");

        HttpResponseMessage response;
        try
        {
            response = await SharedHttpClient.SendAsync(request, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Warn($"STT: request timed out after {_config.TimeoutSeconds}s");
            throw new WhisperAsrApiException($"STT webservice request timed out after {_config.TimeoutSeconds}s at {uri}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            _log?.Warn($"STT: connection error: {ex.Message}");
            throw new WhisperAsrApiException($"Failed to reach STT webservice at {uri}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Warn($"STT returned HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
                throw new WhisperAsrApiException($"STT webservice at {uri} returned HTTP {(int)response.StatusCode}: {body}");
            }

            _log?.Info($"STT returned HTTP 200 ({body.Length} chars): {Truncate(body, 200)}");
            var text = ExtractTranscriptionText(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                _log?.Warn($"STT returned HTTP 200 but no transcription text could be extracted. Full body ({body.Length} chars): {Truncate(body, 500)}");
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
    /// Remote reachability probe. Pings GET /health (falling back to GET /v1/models, GET /docs, GET /) and measures round-trip latency.
    /// </summary>
    public async Task<string> TestRemoteAsync(CancellationToken ct = default)
    {
        var baseUrl = _config.BaseUrl.TrimEnd('/');
        var sw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(_config.TimeoutSeconds, 10)));
        var token = cts.Token;

        var candidatePaths = new[] { "/health", "/v1/models", "/docs", "/" };
        Exception? lastEx = null;

        foreach (var path in candidatePaths)
        {
            try
            {
                sw.Restart();
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}{path}"));
                if (!string.IsNullOrWhiteSpace(_config.ApiKey))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey.Trim());
                }

                using var resp = await SharedHttpClient.SendAsync(req, token);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    return $"OK — STT webservice reachable in {sw.ElapsedMilliseconds}ms at {baseUrl} ({path})";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
            }
        }

        if (token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new WhisperAsrApiException($"STT webservice at {baseUrl} timed out during reachability test.");
        }

        if (lastEx != null)
        {
            throw new WhisperAsrApiException($"Cannot reach Whisper ASR webservice at {baseUrl}: {lastEx.Message}", lastEx);
        }

        throw new WhisperAsrApiException($"Cannot reach Whisper ASR webservice at {baseUrl}: no candidate endpoints responded with HTTP 200.");
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
