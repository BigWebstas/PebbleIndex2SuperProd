using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Index2SP.Tests;

public class WhisperLiveClientTests
{
    [Fact]
    public void TryReadPcmWav_ParsesValidWav()
    {
        var sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        var pcmSamples = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        var wav = CreateWavBytes(pcmSamples, sampleRate, channels, bitsPerSample);

        var success = AudioDecoder.TryReadPcmWav(wav, out var decoded);

        Assert.True(success);
        Assert.Equal(16000, decoded.Rate);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(pcmSamples, decoded.Data);
    }

    [Fact]
    public void TryReadPcmWav_RejectsNonWav()
    {
        var notWav = Encoding.UTF8.GetBytes("This is not a WAV file at all, just plain text.");

        var success = AudioDecoder.TryReadPcmWav(notWav, out var decoded);

        Assert.False(success);
        Assert.Null(decoded.Data);
    }

    [Fact]
    public void TryReadPcmWav_RejectsNonPcmWav()
    {
        // Format 6 == A-law, Format 7 == Mu-law (unsupported non-PCM/float format)
        var wav = CreateWavBytes(new byte[8], 16000, 1, 8, audioFormat: 6);

        var success = AudioDecoder.TryReadPcmWav(wav, out var decoded);

        Assert.False(success);
        Assert.Null(decoded.Data);
    }

    [Fact]
    public void TryReadPcmWav_Parses32BitFloatWav()
    {
        // 4 float samples: 0.0f, 1.0f, -1.0f, 0.5f
        var floats = new float[] { 0.0f, 1.0f, -1.0f, 0.5f };
        var floatBytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, floatBytes, 0, floatBytes.Length);

        // Format 3 == IEEE Float 32-bit
        var wav = CreateWavBytes(floatBytes, 16000, 1, 32, audioFormat: 3);

        var success = AudioDecoder.TryReadPcmWav(wav, out var decoded);

        Assert.True(success);
        Assert.NotNull(decoded.Data);
        Assert.Equal(16000, decoded.Rate);
        Assert.Equal(2, decoded.Width); // converted to 16-bit PCM
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(floats.Length * 2, decoded.Data.Length);

        // Verify converted samples: 0, 32767, -32768, ~16384
        var s0 = BitConverter.ToInt16(decoded.Data, 0);
        var s1 = BitConverter.ToInt16(decoded.Data, 2);
        var s2 = BitConverter.ToInt16(decoded.Data, 4);
        var s3 = BitConverter.ToInt16(decoded.Data, 6);

        Assert.Equal(0, s0);
        Assert.Equal(32767, s1);
        Assert.Equal(-32768, s2);
        Assert.InRange(s3, 16380, 16388);
    }

    [Fact]
    public async Task TestAsync_ReturnsOkWithServerReady()
    {
        using var server = new MockWebSocketServer();
        var port = server.Start();

        var serverTask = Task.Run(async () =>
        {
            var ws = await server.AcceptWebSocketAsync();
            var (handshake, msgType) = await server.ReceiveTextAsync(ws);
            Assert.Equal(WebSocketMessageType.Text, msgType);

            using var doc = JsonDocument.Parse(handshake);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("uid", out var uidProp));
            var uid = uidProp.GetString()!;
            Assert.Equal("small", root.GetProperty("model").GetString());
            Assert.Equal("transcribe", root.GetProperty("task").GetString());
            Assert.Equal("int16", root.GetProperty("audio_format").GetString());
            Assert.True(root.GetProperty("use_vad").GetBoolean());

            var reply = JsonSerializer.Serialize(new
            {
                uid,
                message = "SERVER_READY",
                backend = "faster_whisper",
            });
            await server.SendTextAsync(ws, reply);
            try
            {
                var buf = new byte[512];
                await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            }
            catch { }
        });

        try
        {
            var config = new AppConfig.WhisperLiveConfig
            {
                Host = "127.0.0.1",
                Port = port,
                Model = "small",
                UseVad = true,
                TimeoutSeconds = 5,
            };

            using var client = new WhisperLiveClient(config);
            var result = await client.TestAsync(CancellationToken.None);

            Assert.Contains("OK", result);
            Assert.Contains("faster_whisper", result);
            Assert.Contains("ms", result);
        }
        finally
        {
            await serverTask;
        }
    }

    [Fact]
    public async Task TestAsync_ThrowsWhenUnreachable()
    {
        var config = new AppConfig.WhisperLiveConfig
        {
            Host = "127.0.0.1",
            Port = 1, // Port 1 won't be listening
            TimeoutSeconds = 1,
        };

        using var client = new WhisperLiveClient(config);
        var ex = await Assert.ThrowsAsync<WhisperLiveApiException>(() => client.TestAsync());

        Assert.Contains("Cannot reach WhisperLive server", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_TranscribesAudioSuccessfully()
    {
        using var server = new MockWebSocketServer();
        var port = server.Start();

        var pcm = new byte[32000]; // 1 second of 16kHz 16-bit mono
        new Random(42).NextBytes(pcm);
        var wav = CreateWavBytes(pcm, 16000, 1, 16);

        var serverTask = Task.Run(async () =>
        {
            var ws = await server.AcceptWebSocketAsync();
            var (handshake, _) = await server.ReceiveTextAsync(ws);
            using var doc = JsonDocument.Parse(handshake);
            var uid = doc.RootElement.GetProperty("uid").GetString()!;

            // 1. Send SERVER_READY
            var readyMsg = JsonSerializer.Serialize(new
            {
                uid,
                message = "SERVER_READY",
                backend = "faster_whisper",
            });
            await server.SendTextAsync(ws, readyMsg);

            // 2. Read audio chunks until END_OF_AUDIO marker
            var totalAudioBytes = 0;
            while (ws.State == WebSocketState.Open)
            {
                var (bytes, isText) = await server.ReceiveBinaryOrMarkerAsync(ws);
                if (bytes == null) break;
                if (!isText)
                {
                    totalAudioBytes += bytes.Length;
                }
                else
                {
                    var marker = Encoding.UTF8.GetString(bytes);
                    if (marker == "END_OF_AUDIO")
                    {
                        break;
                    }
                }
            }

            Assert.Equal(32000, totalAudioBytes);

            // 3. Send transcription segments
            var segmentsMsg = JsonSerializer.Serialize(new
            {
                uid,
                segments = new[]
                {
                    new { text = "Remind me to buy groceries tomorrow", completed = true }
                }
            });
            await server.SendTextAsync(ws, segmentsMsg);

            // 4. Close socket
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        });

        try
        {
            var config = new AppConfig.WhisperLiveConfig
            {
                Host = "127.0.0.1",
                Port = port,
                Model = "small",
                Language = "en",
                TimeoutSeconds = 5,
            };

            using var client = new WhisperLiveClient(config);
            var result = await client.TranscribeAsync(wav, "sample.wav", CancellationToken.None);

            Assert.Equal("Remind me to buy groceries tomorrow", result);
        }
        finally
        {
            await serverTask;
        }
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsNullOnBlankTranscript()
    {
        using var server = new MockWebSocketServer();
        var port = server.Start();

        var pcm = new byte[32000];
        var wav = CreateWavBytes(pcm, 16000, 1, 16);

        var serverTask = Task.Run(async () =>
        {
            var ws = await server.AcceptWebSocketAsync();
            var (handshake, _) = await server.ReceiveTextAsync(ws);
            using var doc = JsonDocument.Parse(handshake);
            var uid = doc.RootElement.GetProperty("uid").GetString()!;

            await server.SendTextAsync(ws, JsonSerializer.Serialize(new
            {
                uid,
                message = "SERVER_READY"
            }));

            while (ws.State == WebSocketState.Open)
            {
                var (bytes, isText) = await server.ReceiveBinaryOrMarkerAsync(ws);
                if (bytes == null) break;
                if (isText && Encoding.UTF8.GetString(bytes) == "END_OF_AUDIO") break;
            }

            await server.SendTextAsync(ws, JsonSerializer.Serialize(new
            {
                uid,
                segments = new[]
                {
                    new { text = "   ", completed = true }
                }
            }));

            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        });

        try
        {
            var config = new AppConfig.WhisperLiveConfig
            {
                Host = "127.0.0.1",
                Port = port,
                TimeoutSeconds = 5,
            };

            using var client = new WhisperLiveClient(config);
            var result = await client.TranscribeAsync(wav, "sample.wav", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            await serverTask;
        }
    }

    [Fact]
    public async Task TranscribeAsync_PadsShortAudioToOneSecond()
    {
        using var server = new MockWebSocketServer();
        var port = server.Start();

        // 8000 bytes = 0.25 seconds of 16kHz 16-bit mono
        var shortPcm = new byte[8000];
        new Random(42).NextBytes(shortPcm);
        var wav = CreateWavBytes(shortPcm, 16000, 1, 16);

        var serverTask = Task.Run(async () =>
        {
            var ws = await server.AcceptWebSocketAsync();
            var (handshake, _) = await server.ReceiveTextAsync(ws);
            using var doc = JsonDocument.Parse(handshake);
            var uid = doc.RootElement.GetProperty("uid").GetString()!;

            await server.SendTextAsync(ws, JsonSerializer.Serialize(new
            {
                uid,
                message = "SERVER_READY"
            }));

            var totalBytes = 0;
            while (ws.State == WebSocketState.Open)
            {
                var (bytes, isText) = await server.ReceiveBinaryOrMarkerAsync(ws);
                if (bytes == null) break;
                if (!isText)
                {
                    totalBytes += bytes.Length;
                }
                else if (Encoding.UTF8.GetString(bytes) == "END_OF_AUDIO")
                {
                    break;
                }
            }

            // Must have been padded to at least 32,000 bytes (1 second)
            Assert.True(totalBytes >= 32000, $"Expected >= 32000 bytes, but got {totalBytes}");

            await server.SendTextAsync(ws, JsonSerializer.Serialize(new
            {
                uid,
                segments = new[]
                {
                    new { text = "Short audio test", completed = true }
                }
            }));

            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        });

        try
        {
            var config = new AppConfig.WhisperLiveConfig
            {
                Host = "127.0.0.1",
                Port = port,
                TimeoutSeconds = 5,
            };

            using var client = new WhisperLiveClient(config);
            var result = await client.TranscribeAsync(wav, "sample.wav", CancellationToken.None);

            Assert.Equal("Short audio test", result);
        }
        finally
        {
            await serverTask;
        }
    }

    private static byte[] CreateWavBytes(byte[] pcmData, int sampleRate, short channels, short bitsPerSample, short audioFormat = 1)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcmData.Length);
        writer.Write("WAVE"u8.ToArray());

        // fmt chunk
        writer.Write("fmt "u8.ToArray());
        writer.Write(16); // SubChunk1Size (16 for PCM)
        writer.Write(audioFormat);
        writer.Write(channels);
        writer.Write(sampleRate);
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        writer.Write(byteRate);
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // data chunk
        writer.Write("data"u8.ToArray());
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        return ms.ToArray();
    }

    private sealed class MockWebSocketServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _port;

        public int Start()
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            return _port;
        }

        public async Task<WebSocket> AcceptWebSocketAsync()
        {
            var context = await _listener.GetContextAsync();
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            return wsContext.WebSocket;
        }

        public async Task<(string Text, WebSocketMessageType Type)> ReceiveTextAsync(WebSocket ws)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);

            return (Encoding.UTF8.GetString(ms.ToArray()), res.MessageType);
        }

        public async Task<(byte[]? Data, bool IsTextMarker)> ReceiveBinaryOrMarkerAsync(WebSocket ws)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) return (null, false);
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);

            var bytes = ms.ToArray();
            var isMarker = res.MessageType == WebSocketMessageType.Binary &&
                           bytes.Length == 12 &&
                           Encoding.UTF8.GetString(bytes) == "END_OF_AUDIO";

            return (bytes, isMarker);
        }

        public async Task SendTextAsync(WebSocket ws, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
