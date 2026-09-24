using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Index2SP;

/// <summary>
/// Client for local speech-to-text servers implementing the Wyoming protocol (e.g. wyoming-whisper,
/// wyoming-faster-whisper). Connects over TCP and streams JSONL events and PCM audio chunks.
/// Used only as a fallback when a Pebble webhook arrives with audio but no transcription
/// text — never overrides a transcription Pebble already sent.
/// </summary>
public class WyomingClient : IDisposable
{
    private readonly AppConfig.WyomingConfig _config;

    public IReadOnlyList<string> DiscoveredModels { get; private set; } = Array.Empty<string>();

    public WyomingClient(AppConfig.WyomingConfig config)
    {
        _config = config;
    }

    public virtual Task<string?> TranscribeAsync(byte[] audio, string? fileName, CancellationToken ct = default) =>
        TranscribeAsync(audio.AsMemory(), fileName, ct);

    /// <summary>
    /// Transcribes one audio clip. Automatically parses WAV audio or decodes compressed formats
    /// (e.g. Pebble's .m4a files) into PCM before sending over the Wyoming protocol.
    /// Returns null when the server returned no usable text.
    /// </summary>
    public virtual async Task<string?> TranscribeAsync(ReadOnlyMemory<byte> audio, string? fileName, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        var token = cts.Token;

        var decoded = await AudioDecoder.DecodeAsync(audio, fileName, token);
        if (decoded.Data.Length == 0) return null;

        using var client = new TcpClient();
        client.NoDelay = true;
        try
        {
            await client.ConnectAsync(_config.Host, _config.Port, token);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new WyomingApiException($"Cannot reach Wyoming server at {_config.Host}:{_config.Port}. Is the server running?", ex);
        }

        using var stream = client.GetStream();
        using var reader = new WyomingStreamReader(stream);

        // 1. transcribe event
        var transcribeData = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(_config.Model))
            transcribeData["name"] = _config.Model;
        if (!string.IsNullOrWhiteSpace(_config.Language))
            transcribeData["language"] = _config.Language;

        await WyomingProtocol.WriteEventAsync(stream, "transcribe", transcribeData.Count > 0 ? transcribeData : null, null, token);

        // 2. audio-start event
        await WyomingProtocol.WriteEventAsync(stream, "audio-start", new
        {
            rate = decoded.Rate,
            width = decoded.Width,
            channels = decoded.Channels
        }, null, token);

        // 3. audio-chunk events (zero-copy slicing directly into network stream)
        var chunkBytesCount = decoded.Width * decoded.Channels * 2048; // 2048 samples per chunk
        if (chunkBytesCount <= 0) chunkBytesCount = 4096;

        var chunkFormat = new
        {
            rate = decoded.Rate,
            width = decoded.Width,
            channels = decoded.Channels
        };

        var offset = 0;
        while (offset < decoded.Data.Length)
        {
            var len = Math.Min(chunkBytesCount, decoded.Data.Length - offset);
            var chunkMemory = decoded.Data.AsMemory(offset, len);
            await WyomingProtocol.WriteEventAsync(stream, "audio-chunk", chunkFormat, chunkMemory, token);
            offset += len;
        }

        // 4. audio-stop event
        await WyomingProtocol.WriteEventAsync(stream, "audio-stop", null, null, token);

        // 5. Read events until "transcript"
        while (!token.IsCancellationRequested)
        {
            var ev = await reader.ReadEventAsync(token);
            if (ev is null)
                throw new WyomingApiException($"Wyoming server at {_config.Host}:{_config.Port} closed connection without returning a transcript.");

            if (ev.Type == "transcript")
            {
                var text = ev.Data?.TryGetProperty("text", out var textProp) == true ? textProp.GetString() : null;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }

        throw new TimeoutException($"Timed out waiting for transcript from Wyoming server at {_config.Host}:{_config.Port}.");
    }

    /// <summary>
    /// Reachability and capability probe. Sends a 'describe' event, measures ping latency,
    /// parses advertised ASR models, and returns a descriptive summary.
    /// </summary>
    public virtual async Task<string> TestAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(_config.TimeoutSeconds, 10)));
        var token = cts.Token;

        using var client = new TcpClient();
        client.NoDelay = true;
        try
        {
            await client.ConnectAsync(_config.Host, _config.Port, token);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new WyomingApiException($"Cannot reach Wyoming server at {_config.Host}:{_config.Port}. Is the Wyoming server running?", ex);
        }

        using var stream = client.GetStream();
        using var reader = new WyomingStreamReader(stream);

        var sw = Stopwatch.StartNew();
        await WyomingProtocol.WriteEventAsync(stream, "describe", null, null, token);

        var ev = await reader.ReadEventAsync(token);
        sw.Stop();

        if (ev is null)
            throw new WyomingApiException($"Wyoming server at {_config.Host}:{_config.Port} closed connection without responding to describe.");

        if (ev.Type != "info")
            throw new WyomingApiException($"Wyoming server at {_config.Host}:{_config.Port} returned unexpected event type '{ev.Type}' in response to describe.");

        var modelNames = ExtractModelNames(ev.Data);
        DiscoveredModels = modelNames;

        var elapsedMs = sw.ElapsedMilliseconds;
        var latencyStr = elapsedMs > 0 ? $"{elapsedMs}ms" : "<1ms";

        if (modelNames.Count > 0)
            return $"OK — Wyoming server reachable in {latencyStr} ({string.Join(", ", modelNames.Distinct())})";

        return $"OK — Wyoming server reachable at {_config.Host}:{_config.Port} in {latencyStr}";
    }

    private static List<string> ExtractModelNames(JsonElement? data)
    {
        var modelNames = new List<string>();
        if (data?.TryGetProperty("asr", out var asrProp) == true && asrProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asrItem in asrProp.EnumerateArray())
            {
                if (asrItem.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        if (m.TryGetProperty("name", out var mn) && !string.IsNullOrWhiteSpace(mn.GetString()))
                            modelNames.Add(mn.GetString()!);
                    }
                }
                else if (asrItem.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                {
                    modelNames.Add(n.GetString()!);
                }
            }
        }
        return modelNames;
    }

    public void Dispose()
    {
        // No unmanaged resources
    }
}

/// <summary>
/// Backward-compatibility wrapper for code or tests referencing WhisperClient.
/// </summary>
public sealed class WhisperClient(AppConfig.WyomingConfig config) : WyomingClient(config);

/// <summary>
/// Wyoming protocol event parser and serializer.
/// </summary>
public static class WyomingProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Task WriteEventAsync(Stream stream, string type, object? data, CancellationToken ct) =>
        WriteEventAsync(stream, type, data, (ReadOnlyMemory<byte>?)null, ct);

    public static async Task WriteEventAsync(Stream stream, string type, object? data, ReadOnlyMemory<byte>? payload, CancellationToken ct)
    {
        byte[]? dataBytes = null;
        if (data is not null)
        {
            dataBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        }

        var header = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["version"] = "1.5.0",
        };

        if (dataBytes is not null && dataBytes.Length > 0)
        {
            header["data_length"] = dataBytes.Length;
        }

        if (payload.HasValue && payload.Value.Length > 0)
        {
            header["payload_length"] = payload.Value.Length;
        }

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        await stream.WriteAsync(headerBytes, ct);
        stream.WriteByte((byte)'\n');

        if (dataBytes is not null && dataBytes.Length > 0)
        {
            await stream.WriteAsync(dataBytes, ct);
        }

        if (payload.HasValue && payload.Value.Length > 0)
        {
            await stream.WriteAsync(payload.Value, ct);
        }

        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Reads one Wyoming event from a buffered <see cref="WyomingStreamReader"/>.
    /// </summary>
    public static Task<WyomingEvent?> ReadEventAsync(WyomingStreamReader reader, CancellationToken ct) =>
        reader.ReadEventAsync(ct);

    /// <summary>
    /// Reads one Wyoming event directly from a stream without over-reading.
    /// For repeated high-throughput event reads on the same connection, use <see cref="WyomingStreamReader"/>.
    /// </summary>
    public static async Task<WyomingEvent?> ReadEventAsync(Stream stream, CancellationToken ct)
    {
        var lineBytes = await ReadLineByteByByteAsync(stream, ct);
        if (lineBytes is null || lineBytes.Length == 0)
            return null;

        using var doc = JsonDocument.Parse(lineBytes);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString() ?? "";
        var dataLength = root.TryGetProperty("data_length", out var dl) ? dl.GetInt32() : 0;
        var payloadLength = root.TryGetProperty("payload_length", out var pl) ? pl.GetInt32() : 0;

        JsonDocument? dataDoc = null;
        if (dataLength > 0)
        {
            var dataBytes = new byte[dataLength];
            await ReadExactStreamAsync(stream, dataBytes, ct);
            dataDoc = JsonDocument.Parse(dataBytes);
        }
        else if (root.TryGetProperty("data", out var inlineData))
        {
            dataDoc = JsonDocument.Parse(inlineData.GetRawText());
        }

        byte[]? payload = null;
        if (payloadLength > 0)
        {
            payload = new byte[payloadLength];
            await ReadExactStreamAsync(stream, payload, ct);
        }

        return new WyomingEvent
        {
            Type = type,
            Data = dataDoc?.RootElement.Clone(),
            Payload = payload
        };
    }

    private static async Task<byte[]?> ReadLineByteByByteAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read == 0)
            {
                if (ms.Length == 0) return null;
                break;
            }
            if (buffer[0] == (byte)'\n')
                break;
            ms.WriteByte(buffer[0]);
        }
        var bytes = ms.ToArray();
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            return bytes[..^1];
        return bytes;
    }

    private static async Task ReadExactStreamAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0)
                throw new EndOfStreamException($"Stream closed prematurely after reading {totalRead} of {buffer.Length} bytes.");
            totalRead += read;
        }
    }
}

/// <summary>
/// Efficient buffered reader for Wyoming JSON lines and raw binary payloads.
/// Avoids single-byte async read loops and reuses buffered bytes across line and exact reads.
/// </summary>
public sealed class WyomingStreamReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[4096];
    private int _head = 0;
    private int _tail = 0;

    public WyomingStreamReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<WyomingEvent?> ReadEventAsync(CancellationToken ct)
    {
        var lineBytes = await ReadLineAsync(ct);
        if (lineBytes is null || lineBytes.Length == 0)
            return null;

        using var doc = JsonDocument.Parse(lineBytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp))
            return null;

        var type = typeProp.GetString() ?? "";
        int dataLength = root.TryGetProperty("data_length", out var dlProp) && dlProp.TryGetInt32(out var dl) ? dl : 0;
        int payloadLength = root.TryGetProperty("payload_length", out var plProp) && plProp.TryGetInt32(out var pl) ? pl : 0;

        JsonDocument? dataDoc = null;
        if (dataLength > 0)
        {
            var dataBytes = new byte[dataLength];
            await ReadExactAsync(dataBytes, ct);
            dataDoc = JsonDocument.Parse(dataBytes);
        }
        else if (root.TryGetProperty("data", out var inlineData))
        {
            dataDoc = JsonDocument.Parse(inlineData.GetRawText());
        }

        byte[]? payload = null;
        if (payloadLength > 0)
        {
            payload = new byte[payloadLength];
            await ReadExactAsync(payload, ct);
        }

        return new WyomingEvent
        {
            Type = type,
            Data = dataDoc?.RootElement.Clone(),
            Payload = payload
        };
    }

    private async Task<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();

        while (true)
        {
            // Search available bytes in buffer for '\n'
            var available = _tail - _head;
            if (available > 0)
            {
                var nlIndex = Array.IndexOf(_buffer, (byte)'\n', _head, available);
                if (nlIndex >= 0)
                {
                    var count = nlIndex - _head;
                    ms.Write(_buffer, _head, count);
                    _head = nlIndex + 1; // Advance past '\n'
                    if (_head == _tail)
                    {
                        _head = 0;
                        _tail = 0;
                    }
                    return StripCarriageReturn(ms.ToArray());
                }

                ms.Write(_buffer, _head, available);
                _head = 0;
                _tail = 0;
            }

            // Fill buffer from stream
            var read = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), ct);
            if (read == 0)
            {
                return ms.Length > 0 ? StripCarriageReturn(ms.ToArray()) : null;
            }

            _head = 0;
            _tail = read;
        }
    }

    private static byte[] StripCarriageReturn(byte[] bytes)
    {
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            return bytes[..^1];
        return bytes;
    }

    private async Task ReadExactAsync(byte[] destination, CancellationToken ct)
    {
        var totalRead = 0;

        // Drain any bytes already present in _buffer
        var available = _tail - _head;
        if (available > 0)
        {
            var toCopy = Math.Min(available, destination.Length);
            Buffer.BlockCopy(_buffer, _head, destination, 0, toCopy);
            _head += toCopy;
            totalRead += toCopy;
        }

        if (_head == _tail)
        {
            _head = 0;
            _tail = 0;
        }

        // Read remaining directly into destination
        while (totalRead < destination.Length)
        {
            var read = await _stream.ReadAsync(destination.AsMemory(totalRead, destination.Length - totalRead), ct);
            if (read == 0)
                throw new EndOfStreamException($"Stream closed prematurely after reading {totalRead} of {destination.Length} bytes.");
            totalRead += read;
        }
    }

    public void Dispose()
    {
        // Reader does not own the underlying stream
    }
}

public sealed class WyomingEvent
{
    public required string Type { get; init; }
    public JsonElement? Data { get; init; }
    public byte[]? Payload { get; init; }
}

public class WyomingApiException : Exception
{
    public WyomingApiException(string message) : base(message) { }
    public WyomingApiException(string message, Exception inner) : base(message, inner) { }
}

public class WyomingAudioException : Exception
{
    public WyomingAudioException(string message) : base(message) { }
    public WyomingAudioException(string message, Exception inner) : base(message, inner) { }
}

public class WhisperApiException : WyomingApiException
{
    public WhisperApiException(string message) : base(message) { }
    public WhisperApiException(string message, Exception inner) : base(message, inner) { }
}
