using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Index2SP.Tests;

public class WyomingClientTests
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
    public async Task WyomingStreamReader_ReadsMultipleEventsInSingleBuffer()
    {
        using var ms = new MemoryStream();

        // Write three events into the stream
        await WyomingProtocol.WriteEventAsync(ms, "event-one", new { key = "val1" }, CancellationToken.None);
        await WyomingProtocol.WriteEventAsync(ms, "event-two", new { key = "val2" }, new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
        await WyomingProtocol.WriteEventAsync(ms, "event-three", new { key = "val3" }, CancellationToken.None);

        ms.Position = 0;

        using var reader = new WyomingStreamReader(ms);

        var e1 = await reader.ReadEventAsync(CancellationToken.None);
        Assert.NotNull(e1);
        Assert.Equal("event-one", e1.Type);
        Assert.Equal("val1", e1.Data?.GetProperty("key").GetString());
        Assert.Null(e1.Payload);

        var e2 = await reader.ReadEventAsync(CancellationToken.None);
        Assert.NotNull(e2);
        Assert.Equal("event-two", e2.Type);
        Assert.Equal("val2", e2.Data?.GetProperty("key").GetString());
        Assert.NotNull(e2.Payload);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, e2.Payload);

        var e3 = await reader.ReadEventAsync(CancellationToken.None);
        Assert.NotNull(e3);
        Assert.Equal("event-three", e3.Type);
        Assert.Equal("val3", e3.Data?.GetProperty("key").GetString());
        Assert.Null(e3.Payload);

        var e4 = await reader.ReadEventAsync(CancellationToken.None);
        Assert.Null(e4); // EOF
    }

    [Fact]
    public async Task WyomingProtocol_WriteAndReadEvent_RoundTripsCorrectly()
    {
        using var ms = new MemoryStream();

        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var data = new { rate = 16000, width = 2, channels = 1 };

        await WyomingProtocol.WriteEventAsync(ms, "audio-chunk", data, payload, CancellationToken.None);

        ms.Position = 0;
        var ev = await WyomingProtocol.ReadEventAsync(ms, CancellationToken.None);

        Assert.NotNull(ev);
        Assert.Equal("audio-chunk", ev.Type);
        Assert.NotNull(ev.Data);
        Assert.Equal(16000, ev.Data.Value.GetProperty("rate").GetInt32());
        Assert.Equal(2, ev.Data.Value.GetProperty("width").GetInt32());
        Assert.Equal(1, ev.Data.Value.GetProperty("channels").GetInt32());
        Assert.NotNull(ev.Payload);
        Assert.Equal(payload, ev.Payload);
    }

    [Fact]
    public async Task TestAsync_ReturnsOkWithAsrModels()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var buffered = new BufferedStream(stream);

            var describeEv = await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None);
            Assert.NotNull(describeEv);
            Assert.Equal("describe", describeEv.Type);

            var infoData = new
            {
                asr = new[]
                {
                    new { name = "faster-whisper-medium" }
                }
            };
            await WyomingProtocol.WriteEventAsync(buffered, "info", infoData, null, CancellationToken.None);
        });

        try
        {
            var config = new AppConfig.WyomingConfig
            {
                Host = "127.0.0.1",
                Port = port,
                TimeoutSeconds = 5,
            };

            using var wyoming = new WyomingClient(config);
            var result = await wyoming.TestAsync(CancellationToken.None);

            Assert.Contains("faster-whisper-medium", result);
            Assert.Contains("OK", result);
            Assert.Contains("ms", result);
            Assert.Single(wyoming.DiscoveredModels);
            Assert.Equal("faster-whisper-medium", wyoming.DiscoveredModels[0]);
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task TestAsync_PopulatesNestedDiscoveredModelsAndMeasuresLatency()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var buffered = new BufferedStream(stream);

            var describeEv = await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None);
            Assert.NotNull(describeEv);
            Assert.Equal("describe", describeEv.Type);

            var infoData = new
            {
                asr = new object[]
                {
                    new
                    {
                        name = "whisper",
                        models = new[]
                        {
                            new { name = "tiny.en" },
                            new { name = "base.en" }
                        }
                    }
                }
            };
            await WyomingProtocol.WriteEventAsync(buffered, "info", infoData, null, CancellationToken.None);
        });

        try
        {
            var config = new AppConfig.WyomingConfig
            {
                Host = "127.0.0.1",
                Port = port,
                TimeoutSeconds = 5,
            };

            using var wyoming = new WyomingClient(config);
            var result = await wyoming.TestAsync(CancellationToken.None);

            Assert.Contains("OK", result);
            Assert.Contains("ms", result);
            Assert.Contains("tiny.en", result);
            Assert.Contains("base.en", result);
            Assert.Equal(2, wyoming.DiscoveredModels.Count);
            Assert.Contains("tiny.en", wyoming.DiscoveredModels);
            Assert.Contains("base.en", wyoming.DiscoveredModels);
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task TestAsync_ThrowsWhenUnreachable()
    {
        // Pick an unbound port
        var config = new AppConfig.WyomingConfig
        {
            Host = "127.0.0.1",
            Port = 1, // Port 1 should not have a Wyoming server
            TimeoutSeconds = 1,
        };

        using var wyoming = new WyomingClient(config);
        var ex = await Assert.ThrowsAsync<WyomingApiException>(() => wyoming.TestAsync());

        Assert.Contains("Cannot reach Wyoming server", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_TranscribesAudioSuccessfully()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sampleRate = 16000;
        var pcm = new byte[4096];
        new Random(42).NextBytes(pcm);
        var wav = CreateWavBytes(pcm, sampleRate, 1, 16);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var buffered = new BufferedStream(stream);

            // 1. Expect transcribe
            var trEv = await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None);
            Assert.NotNull(trEv);
            Assert.Equal("transcribe", trEv.Type);
            Assert.Equal("tiny", trEv.Data?.GetProperty("name").GetString());
            Assert.Equal("en", trEv.Data?.GetProperty("language").GetString());

            // 2. Expect audio-start
            var startEv = await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None);
            Assert.NotNull(startEv);
            Assert.Equal("audio-start", startEv.Type);

            // 3. Expect audio-chunk(s)
            var chunkEv = await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None);
            Assert.NotNull(chunkEv);
            Assert.Equal("audio-chunk", chunkEv.Type);
            Assert.NotNull(chunkEv.Payload);

            // 4. Expect audio-stop
            var stopEv = await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None);
            Assert.NotNull(stopEv);
            Assert.Equal("audio-stop", stopEv.Type);

            // 5. Send transcript
            var transcriptData = new { text = "Remind me to buy groceries tomorrow" };
            await WyomingProtocol.WriteEventAsync(buffered, "transcript", transcriptData, null, CancellationToken.None);
        });

        try
        {
            var config = new AppConfig.WyomingConfig
            {
                Host = "127.0.0.1",
                Port = port,
                Model = "tiny",
                Language = "en",
                TimeoutSeconds = 5,
            };

            using var wyoming = new WyomingClient(config);
            var result = await wyoming.TranscribeAsync(wav, "sample.wav", CancellationToken.None);

            Assert.Equal("Remind me to buy groceries tomorrow", result);
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsNullOnBlankTranscript()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var wav = CreateWavBytes(new byte[1024], 16000, 1, 16);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var buffered = new BufferedStream(stream);

            await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None); // transcribe
            await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None); // start
            await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None); // chunk
            await WyomingProtocol.ReadEventAsync(buffered, CancellationToken.None); // stop

            var transcriptData = new { text = "   " };
            await WyomingProtocol.WriteEventAsync(buffered, "transcript", transcriptData, null, CancellationToken.None);
        });

        try
        {
            var config = new AppConfig.WyomingConfig
            {
                Host = "127.0.0.1",
                Port = port,
                TimeoutSeconds = 5,
            };

            using var wyoming = new WyomingClient(config);
            var result = await wyoming.TranscribeAsync(wav, "sample.wav", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            listener.Stop();
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
}
