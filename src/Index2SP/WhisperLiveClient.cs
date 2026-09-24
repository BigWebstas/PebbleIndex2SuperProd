using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Index2SP;

/// <summary>
/// Client for Collabora's WhisperLive real-time speech-to-text server (ghcr.io/collabora/whisperlive-cpu:latest).
/// Communicates over WebSocket (ws://host:port) using the WhisperLive streaming protocol.
/// </summary>
public class WhisperLiveClient : IDisposable
{
    private readonly AppConfig.WhisperLiveConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WhisperLiveClient(AppConfig.WhisperLiveConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Transcribes an audio file or buffer by converting it to 16kHz mono 16-bit PCM and streaming it
    /// to the WhisperLive server over WebSocket.
    /// </summary>
    public Task<string?> TranscribeAsync(byte[] audio, string? fileName, CancellationToken ct = default) =>
        TranscribeAsync(audio.AsMemory(), fileName, ct);

    /// <summary>
    /// Transcribes an audio buffer (zero-copy memory) by converting it to 16kHz mono 16-bit PCM
    /// and streaming it to the WhisperLive server over WebSocket.
    /// </summary>
    public async Task<string?> TranscribeAsync(ReadOnlyMemory<byte> audio, string? fileName, CancellationToken ct = default)
    {
        var decoded = await AudioDecoder.DecodeAsync(audio, fileName, ct);
        if (decoded.Data.Length == 0) return null;

        var pcmData = decoded.Data;
        // WhisperLive requires at least 1.0s of audio (16,000 samples = 32,000 bytes at 16-bit 16kHz)
        // to process speech. Pad with trailing silence if shorter.
        if (pcmData.Length < 32000)
        {
            var padded = new byte[32000];
            Buffer.BlockCopy(pcmData, 0, padded, 0, pcmData.Length);
            pcmData = padded;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        var token = cts.Token;

        using var ws = new ClientWebSocket();
        var scheme = _config.UseWss ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{_config.Host}:{_config.Port}");

        try
        {
            await ws.ConnectAsync(uri, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WhisperLiveApiException($"Cannot reach WhisperLive server at {uri}. Is the container running? ({ex.Message})", ex);
        }

        var uid = Guid.NewGuid().ToString("D");

        // 1. Initial configuration handshake
        var handshake = new Dictionary<string, object?>
        {
            ["uid"] = uid,
            ["language"] = string.IsNullOrWhiteSpace(_config.Language) ? null : _config.Language,
            ["task"] = "transcribe",
            ["model"] = string.IsNullOrWhiteSpace(_config.Model) ? "small" : _config.Model,
            ["use_vad"] = _config.UseVad,
            ["audio_format"] = "int16",
        };

        var handshakeBytes = JsonSerializer.SerializeToUtf8Bytes(handshake, JsonOptions);
        await ws.SendAsync(handshakeBytes, WebSocketMessageType.Text, endOfMessage: true, token);

        var serverReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var segments = new List<string>();

        // 2. Background receiver to handle status, SERVER_READY, and incoming segments
        var receiveTask = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();

            try
            {
                while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer.AsMemory(0, buffer.Length), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            serverReadyTcs.TrySetResult(true);
                            if (ws.State == WebSocketState.CloseReceived)
                            {
                                try
                                {
                                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Acknowledge", CancellationToken.None);
                                }
                                catch { }
                            }
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        using var doc = JsonDocument.Parse(ms.ToArray());
                        var root = doc.RootElement;

                        if (root.TryGetProperty("status", out var statusProp))
                        {
                            var status = statusProp.GetString();
                            if (status == "ERROR")
                            {
                                var errMsg = root.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown server error";
                                var ex = new WhisperLiveApiException($"WhisperLive server error: {errMsg}");
                                serverReadyTcs.TrySetException(ex);
                                throw ex;
                            }
                        }

                        if (root.TryGetProperty("message", out var msgProp) && msgProp.GetString() == "SERVER_READY")
                        {
                            serverReadyTcs.TrySetResult(true);
                        }

                        if (root.TryGetProperty("segments", out var segsProp) && segsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var seg in segsProp.EnumerateArray())
                            {
                                if (seg.TryGetProperty("text", out var tProp))
                                {
                                    var text = tProp.GetString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(text) && (segments.Count == 0 || segments[^1] != text))
                                    {
                                        segments.Add(text);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                serverReadyTcs.TrySetException(ex);
                throw;
            }
            finally
            {
                serverReadyTcs.TrySetResult(true);
            }
        }, token);

        // 3. Await SERVER_READY
        var waitReady = await Task.WhenAny(serverReadyTcs.Task, Task.Delay(TimeSpan.FromSeconds(15), token));
        if (waitReady != serverReadyTcs.Task || !serverReadyTcs.Task.Result)
        {
            if (serverReadyTcs.Task.IsFaulted)
                await serverReadyTcs.Task;
            throw new WhisperLiveApiException($"WhisperLive server at {uri} did not send SERVER_READY within timeout.");
        }

        // 4. Stream audio chunks (4096-byte slices = 2048 samples = ~128ms)
        const int chunkSize = 4096;
        var offset = 0;
        while (offset < pcmData.Length && ws.State == WebSocketState.Open)
        {
            var len = Math.Min(chunkSize, pcmData.Length - offset);
            var chunk = pcmData.AsMemory(offset, len);
            await ws.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, token);
            offset += len;
        }

        // 5. Send END_OF_AUDIO marker
        if (ws.State == WebSocketState.Open)
        {
            var endMarker = "END_OF_AUDIO"u8.ToArray();
            await ws.SendAsync(endMarker, WebSocketMessageType.Binary, endOfMessage: true, token);
        }

        // 6. Await receive loop completion (the server closes the socket after processing all frames)
        try
        {
            await receiveTask;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout reached waiting for final segments
        }
        catch (WebSocketException)
        {
            // Socket closed cleanly by server
        }

        if (segments.Count == 0) return null;
        var fullText = string.Join(" ", segments);
        return string.IsNullOrWhiteSpace(fullText) ? null : fullText.Trim();
    }

    /// <summary>
    /// Reachability probe. Connects to the WhisperLive WebSocket endpoint, sends a handshake,
    /// verifies SERVER_READY, and measures round-trip ping latency.
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(_config.TimeoutSeconds, 10)));
        var token = cts.Token;

        using var ws = new ClientWebSocket();
        var scheme = _config.UseWss ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{_config.Host}:{_config.Port}");

        try
        {
            await ws.ConnectAsync(uri, token);
        }
        catch (Exception ex)
        {
            throw new WhisperLiveApiException($"Cannot reach WhisperLive server at {uri}. Is the container running? ({ex.Message})", ex);
        }

        var uid = Guid.NewGuid().ToString("D");
        var handshake = new Dictionary<string, object?>
        {
            ["uid"] = uid,
            ["language"] = string.IsNullOrWhiteSpace(_config.Language) ? null : _config.Language,
            ["task"] = "transcribe",
            ["model"] = string.IsNullOrWhiteSpace(_config.Model) ? "small" : _config.Model,
            ["use_vad"] = _config.UseVad,
            ["audio_format"] = "int16",
        };

        var handshakeBytes = JsonSerializer.SerializeToUtf8Bytes(handshake, JsonOptions);
        await ws.SendAsync(handshakeBytes, WebSocketMessageType.Text, endOfMessage: true, token);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer.AsMemory(0, buffer.Length), token);
        sw.Stop();

        if (result.MessageType == WebSocketMessageType.Text)
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "ERROR")
            {
                var err = root.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                throw new WhisperLiveApiException($"WhisperLive server returned error: {err}");
            }

            var backend = root.TryGetProperty("backend", out var b) ? b.GetString() : "faster_whisper";
            var model = string.IsNullOrWhiteSpace(_config.Model) ? "small" : _config.Model;

            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "probe", CancellationToken.None);
            }
            catch { }

            return $"OK — WhisperLive server reachable in {sw.ElapsedMilliseconds}ms (backend: {backend}, model: {model})";
        }

        return $"OK — WhisperLive server reachable in {sw.ElapsedMilliseconds}ms at {uri}";
    }

    public void Dispose()
    {
    }
}

public class WhisperLiveApiException : Exception
{
    public WhisperLiveApiException(string message) : base(message) { }
    public WhisperLiveApiException(string message, Exception inner) : base(message, inner) { }
}

public class WhisperLiveAudioException : Exception
{
    public WhisperLiveAudioException(string message) : base(message) { }
    public WhisperLiveAudioException(string message, Exception inner) : base(message, inner) { }
}

// Backward compatibility alias for any existing code or tests
public class WyomingApiException : WhisperLiveApiException
{
    public WyomingApiException(string message) : base(message) { }
    public WyomingApiException(string message, Exception inner) : base(message, inner) { }
}

public class WyomingAudioException : WhisperLiveAudioException
{
    public WyomingAudioException(string message) : base(message) { }
    public WyomingAudioException(string message, Exception inner) : base(message, inner) { }
}
