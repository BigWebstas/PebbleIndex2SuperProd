using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.Ggml;
using Xunit;

namespace Index2SP.Tests;

public class WhisperAsrClientTests
{
    [Theory]
    [InlineData("tiny", GgmlType.Tiny)]
    [InlineData("tiny.en", GgmlType.TinyEn)]
    [InlineData("base", GgmlType.Base)]
    [InlineData("base.en", GgmlType.BaseEn)]
    [InlineData("small", GgmlType.Small)]
    [InlineData("small.en", GgmlType.SmallEn)]
    [InlineData("medium", GgmlType.Medium)]
    [InlineData("large", GgmlType.LargeV3)]
    [InlineData(null, GgmlType.BaseEn)]
    public void EmbeddedWhisperEngine_ParseModelType_MapsCorrectly(string? input, GgmlType expected)
    {
        var result = EmbeddedWhisperEngine.ParseModelType(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EmbeddedWhisperEngine_GetModelFileName_ReturnsExpected()
    {
        Assert.Equal("ggml-base.en.bin", EmbeddedWhisperEngine.GetModelFileName(GgmlType.BaseEn));
        Assert.Equal("ggml-tiny.en.bin", EmbeddedWhisperEngine.GetModelFileName(GgmlType.TinyEn));
        Assert.Equal("ggml-small.bin", EmbeddedWhisperEngine.GetModelFileName(GgmlType.Small));
    }

    [Fact]
    public void WhisperAsrConfig_Mode_InfersEmbeddedByDefault()
    {
        var cfg = new AppConfig.WhisperAsrConfig();
        Assert.Equal("embedded", cfg.Mode);
    }

    [Fact]
    public void WhisperAsrConfig_Mode_InfersRemoteWhenCustomBaseUrlProvided()
    {
        var cfg = new AppConfig.WhisperAsrConfig
        {
            BaseUrl = "http://192.168.1.186:9092",
        };
        Assert.Equal("remote", cfg.Mode);
    }

    [Fact]
    public void WhisperAsrConfig_Mode_RespectsExplicitMode()
    {
        var cfg = new AppConfig.WhisperAsrConfig
        {
            Mode = "embedded",
            BaseUrl = "http://192.168.1.186:9092",
        };
        Assert.Equal("embedded", cfg.Mode);
    }

    [Fact]
    public async Task EmbeddedWhisper_CanTranscribeLocalAudio()
    {
        var tempModel = Path.Combine(Path.GetTempPath(), "test_ggml_tiny_en.bin");
        if (!File.Exists(tempModel) || new FileInfo(tempModel).Length == 0)
        {
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.TinyEn);
            using var fs = File.Create(tempModel);
            await modelStream.CopyToAsync(fs);
        }

        var config = new AppConfig.WhisperAsrConfig
        {
            Mode = "embedded",
            Model = "tiny.en",
            ModelPath = tempModel,
            Language = "en",
        };

        using var client = new WhisperAsrClient(config);

        var testMessage = await client.TestAsync();
        Assert.Contains("OK", testMessage);
        Assert.Contains("Embedded Whisper", testMessage);

        if (File.Exists("/tmp/jfk.wav"))
        {
            var wavBytes = await File.ReadAllBytesAsync("/tmp/jfk.wav");
            var result = await client.TranscribeAsync(wavBytes, "jfk.wav");
            Assert.NotNull(result);
            Assert.Contains("Americans", result);
        }
    }

    [Fact]
    public async Task EmbeddedWhisper_CanTranscribeM4aAudio()
    {
        if (!AudioDecoder.IsFfmpegAvailable) return;
        const string jfkPath = "/tmp/jfk.m4a";
        if (!File.Exists(jfkPath)) return;

        var tempModel = Path.Combine(Path.GetTempPath(), "test_ggml_tiny_en.bin");
        if (!File.Exists(tempModel) || new FileInfo(tempModel).Length == 0) return;

        var config = new AppConfig.WhisperAsrConfig
        {
            Mode = "embedded",
            Model = "tiny.en",
            ModelPath = tempModel,
            Language = "en",
        };

        using var client = new WhisperAsrClient(config);
        var m4aBytes = await File.ReadAllBytesAsync(jfkPath);
        var result = await client.TranscribeAsync(m4aBytes, "jfk.m4a");

        Assert.NotNull(result);
        Assert.Contains("Americans", result);
    }
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

    [Theory]
    [InlineData("""{"text": "Hello world"}""", "Hello world")]
    [InlineData("""{"transcription": "Custom transcription"}""", "Custom transcription")]
    [InlineData("""{"result": "Result text"}""", "Result text")]
    [InlineData("""{"segments": [{"text": "Part one"}, {"text": "Part two"}]}""", "Part one Part two")]
    [InlineData("""[{"text": "Segment A"}, {"text": "Segment B"}]""", "Segment A Segment B")]
    [InlineData("Plain text transcription", "Plain text transcription")]
    public void ExtractTranscriptionText_ParsesVariousFormats(string input, string expected)
    {
        var text = WhisperAsrClient.ExtractTranscriptionText(input);
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"text": ""}""")]
    [InlineData("""{"text": "   "}""")]
    [InlineData("""{"segments": []}""")]
    [InlineData("""{"language": "en"}""")]
    public void ExtractTranscriptionText_ReturnsNullOnBlank(string input)
    {
        var text = WhisperAsrClient.ExtractTranscriptionText(input);
        Assert.Null(text);
    }

    [Fact]
    public void DetectAudioMetadata_DetectsMagicBytesAndExtensions()
    {
        var wavBytes = new byte[12];
        wavBytes[0] = (byte)'R'; wavBytes[1] = (byte)'I'; wavBytes[2] = (byte)'F'; wavBytes[3] = (byte)'F';
        wavBytes[8] = (byte)'W'; wavBytes[9] = (byte)'A'; wavBytes[10] = (byte)'V'; wavBytes[11] = (byte)'E';

        var (wavName, wavMime) = WhisperAsrClient.DetectAudioMetadata(wavBytes, null);
        Assert.Equal("audio.wav", wavName);
        Assert.Equal("audio/wav", wavMime);

        var m4aBytes = new byte[12];
        m4aBytes[4] = (byte)'f'; m4aBytes[5] = (byte)'t'; m4aBytes[6] = (byte)'y'; m4aBytes[7] = (byte)'p';

        var (m4aName, m4aMime) = WhisperAsrClient.DetectAudioMetadata(m4aBytes, null);
        Assert.Equal("audio.m4a", m4aName);
        Assert.Equal("audio/m4a", m4aMime);

        var (customName, customMime) = WhisperAsrClient.DetectAudioMetadata(new byte[10], "recording.mp3");
        Assert.Equal("recording.mp3", customName);
        Assert.Equal("audio/mpeg", customMime);
    }

    [Fact]
    public void ToWav_CreatesValidHeaderAndData()
    {
        var pcm = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var wav = AudioDecoder.ToWav(pcm, sampleRate: 16000, channels: 1, bitsPerSample: 16);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);
    }

    [Fact]
    public void ToWav_RoundTripsWith_TryReadPcmWav()
    {
        var pcm = new byte[320];
        new Random(123).NextBytes(pcm);

        var wav = AudioDecoder.ToWav(pcm, sampleRate: 16000, channels: 1, bitsPerSample: 16);
        var success = AudioDecoder.TryReadPcmWav(wav, out var decoded);

        Assert.True(success);
        Assert.Equal(16000, decoded.Rate);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(pcm, decoded.Data);
    }

    [Fact]
    public void ToWav_FromDecodedAudio_RoundTrips()
    {
        var pcm = new byte[640];
        new Random(456).NextBytes(pcm);
        var original = new DecodedAudio(pcm, Rate: 24000, Width: 2, Channels: 1);

        var wav = AudioDecoder.ToWav(original);
        var success = AudioDecoder.TryReadPcmWav(wav, out var decoded);

        Assert.True(success);
        Assert.Equal(24000, decoded.Rate);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(pcm, decoded.Data);
    }

    [Fact]
    public async Task EnsureWavAsync_ReturnsSameForPcmWav()
    {
        var pcm = new byte[160];
        var wav = AudioDecoder.ToWav(pcm, 16000, 1, 16);

        var (audio, fileName, mime) = await AudioDecoder.EnsureWavAsync(wav, "original.wav");

        Assert.True(audio.Span.SequenceEqual(wav));
        Assert.Equal("original.wav", fileName);
        Assert.Equal("audio/wav", mime);
    }

    [Fact]
    public async Task EnsureWavAsync_HandlesEmptyAudio()
    {
        var (audio, fileName, mime) = await AudioDecoder.EnsureWavAsync(ReadOnlyMemory<byte>.Empty, "empty.wav");

        Assert.True(audio.IsEmpty);
        Assert.Equal("empty.wav", fileName);
        Assert.Equal("audio/wav", mime);
    }

    [Fact]
    public async Task EnsureWavAsync_TranscodesWithFfmpeg_WhenAvailable()
    {
        if (!AudioDecoder.IsFfmpegAvailable) return;

        var tempM4a = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.m4a");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -f lavfi -i sine=frequency=1000:duration=0.2 -c:a aac -b:a 64k {tempM4a}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            Assert.NotNull(proc);
            await proc.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);

            var m4aBytes = await File.ReadAllBytesAsync(tempM4a);
            var (wavAudio, wavName, wavMime) = await AudioDecoder.EnsureWavAsync(m4aBytes, "test.m4a");

            Assert.Equal("recording.wav", wavName);
            Assert.Equal("audio/wav", wavMime);
            Assert.True(AudioDecoder.TryReadPcmWav(wavAudio.Span, out var decoded));
            Assert.Equal(16000, decoded.Rate);
            Assert.Equal(1, decoded.Channels);
            Assert.Equal(2, decoded.Width);
            Assert.True(decoded.Data.Length > 0);
        }
        finally
        {
            try { if (File.Exists(tempM4a)) File.Delete(tempM4a); } catch { }
        }
    }

    [Fact]
    public async Task EnsureWavAsync_TranscodesRealM4aIfPresent()
    {
        if (!AudioDecoder.IsFfmpegAvailable) return;
        const string jfkPath = "/tmp/jfk.m4a";
        if (!File.Exists(jfkPath)) return;

        var m4aBytes = await File.ReadAllBytesAsync(jfkPath);
        var (wavAudio, wavName, wavMime) = await AudioDecoder.EnsureWavAsync(m4aBytes, "jfk.m4a");

        Assert.Equal("recording.wav", wavName);
        Assert.Equal("audio/wav", wavMime);
        Assert.True(AudioDecoder.TryReadPcmWav(wavAudio.Span, out var decoded));
        Assert.Equal(16000, decoded.Rate);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(2, decoded.Width);
        Assert.True(decoded.Data.Length > 0);
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
