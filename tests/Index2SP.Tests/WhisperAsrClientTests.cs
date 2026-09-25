using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Index2SP.Tests;

public class WhisperAsrClientTests
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
    public async Task TestAsync_ReturnsOkWhenServiceHealthy()
    {
        using var server = new MockHttpServer();
        var port = server.Start((req, res) =>
        {
            if (req.Url?.AbsolutePath == "/health")
            {
                res.StatusCode = 200;
                res.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes("""{"status":"ok"}""");
                res.OutputStream.Write(bytes);
                res.Close();
            }
            else
            {
                res.StatusCode = 404;
                res.Close();
            }
        });

        var config = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            TimeoutSeconds = 5,
        };

        using var client = new WhisperAsrClient(config);
        var result = await client.TestAsync(CancellationToken.None);

        Assert.Contains("OK", result);
        Assert.Contains("reachable", result);
        Assert.Contains("ms", result);
    }

    [Fact]
    public async Task TestAsync_FallsBackToDocsWhenHealthIs404()
    {
        using var server = new MockHttpServer();
        var port = server.Start((req, res) =>
        {
            if (req.Url?.AbsolutePath == "/health")
            {
                res.StatusCode = 404;
                res.Close();
            }
            else if (req.Url?.AbsolutePath == "/docs")
            {
                res.StatusCode = 200;
                res.ContentType = "text/html";
                var bytes = Encoding.UTF8.GetBytes("<html>Swagger UI</html>");
                res.OutputStream.Write(bytes);
                res.Close();
            }
            else
            {
                res.StatusCode = 404;
                res.Close();
            }
        });

        var config = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            TimeoutSeconds = 5,
        };

        using var client = new WhisperAsrClient(config);
        var result = await client.TestAsync(CancellationToken.None);

        Assert.Contains("OK", result);
        Assert.Contains("reachable", result);
    }

    [Fact]
    public async Task TestAsync_ThrowsWhenUnreachable()
    {
        var config = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = "http://127.0.0.1:1", // Port 1 will not be listening
            TimeoutSeconds = 1,
        };

        using var client = new WhisperAsrClient(config);
        var ex = await Assert.ThrowsAsync<WhisperAsrApiException>(() => client.TestAsync());

        Assert.Contains("Cannot reach Whisper ASR webservice", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_PostsAudioFileAndReturnsTranscript()
    {
        using var server = new MockHttpServer();
        var port = server.Start((req, res) =>
        {
            Assert.Equal("POST", req.HttpMethod);
            Assert.Equal("/asr", req.Url?.AbsolutePath);
            Assert.Equal("transcribe", req.QueryString["task"]);
            Assert.Equal("json", req.QueryString["output"]);
            Assert.Equal("en", req.QueryString["language"]);

            // Ensure multipart/form-data received
            Assert.True(req.ContentType?.StartsWith("multipart/form-data"));

            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = reader.ReadToEnd();
            Assert.Contains("audio_file", body);

            res.StatusCode = 200;
            res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("""{"text":"Remind me to buy groceries tomorrow"}""");
            res.OutputStream.Write(bytes);
            res.Close();
        });

        var pcm = new byte[16000];
        new Random(42).NextBytes(pcm);
        var wav = CreateWavBytes(pcm, 16000, 1, 16);

        var config = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Language = "en",
            TimeoutSeconds = 5,
        };

        using var client = new WhisperAsrClient(config);
        var result = await client.TranscribeAsync(wav, "sample.wav", CancellationToken.None);

        Assert.Equal("Remind me to buy groceries tomorrow", result);
    }

    [Fact]
    public async Task TranscribeAsync_HandlesPlainTextResponse()
    {
        using var server = new MockHttpServer();
        var port = server.Start((req, res) =>
        {
            res.StatusCode = 200;
            res.ContentType = "text/plain";
            var bytes = Encoding.UTF8.GetBytes("Turn off the kitchen lights\n");
            res.OutputStream.Write(bytes);
            res.Close();
        });

        var audio = new byte[100];
        var config = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            TimeoutSeconds = 5,
        };

        using var client = new WhisperAsrClient(config);
        var result = await client.TranscribeAsync(audio, "test.m4a", CancellationToken.None);

        Assert.Equal("Turn off the kitchen lights", result);
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsNullOnBlankTranscript()
    {
        using var server = new MockHttpServer();
        var port = server.Start((req, res) =>
        {
            res.StatusCode = 200;
            res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("""{"text":"   "}""");
            res.OutputStream.Write(bytes);
            res.Close();
        });

        var audio = new byte[100];
        var config = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            TimeoutSeconds = 5,
        };

        using var client = new WhisperAsrClient(config);
        var result = await client.TranscribeAsync(audio, "test.m4a", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TranscribeAsync_ThrowsOnHttpError()
    {
        using var server = new MockHttpServer();
        var port = server.Start((req, res) =>
        {
            res.StatusCode = 500;
            res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("""{"detail":"Internal GPU error"}""");
            res.OutputStream.Write(bytes);
            res.Close();
        });

        var audio = new byte[100];
        var config = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            TimeoutSeconds = 5,
        };

        using var client = new WhisperAsrClient(config);
        var ex = await Assert.ThrowsAsync<WhisperAsrApiException>(() => client.TranscribeAsync(audio, "test.wav"));

        Assert.Contains("500", ex.Message);
        Assert.Contains("Internal GPU error", ex.Message);
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

    private sealed class MockHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private Action<HttpListenerRequest, HttpListenerResponse>? _handler;
        private volatile bool _stopped;

        public int Start(Action<HttpListenerRequest, HttpListenerResponse> handler)
        {
            _handler = handler;
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (!_stopped)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        _handler?.Invoke(context.Request, context.Response);
                    }
                    catch
                    {
                        if (_stopped) break;
                    }
                }
            });

            return port;
        }

        public void Dispose()
        {
            _stopped = true;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
