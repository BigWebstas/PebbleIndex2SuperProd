using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Index2SP;

public readonly record struct DecodedAudio(byte[] Data, int Rate, int Width, int Channels);

/// <summary>
/// Decodes audio clips (WAV or Pebble's M4A voice notes) into raw linear PCM samples
/// formatted for the Wyoming protocol.
/// </summary>
public static class AudioDecoder
{
    private static readonly byte[] RiffHeader = "RIFF"u8.ToArray();
    private static readonly byte[] WaveHeader = "WAVE"u8.ToArray();
    private static readonly byte[] FmtChunk = "fmt "u8.ToArray();
    private static readonly byte[] DataChunk = "data"u8.ToArray();

    private static bool? _isFfmpegAvailable;

    /// <summary>
    /// Checks whether ffmpeg is available on the system PATH.
    /// </summary>
    public static bool IsFfmpegAvailable
    {
        get
        {
            if (_isFfmpegAvailable.HasValue) return _isFfmpegAvailable.Value;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(1500);
                    _isFfmpegAvailable = p.ExitCode == 0;
                    return _isFfmpegAvailable.Value;
                }
            }
            catch
            {
                // Process failed to start
            }

            _isFfmpegAvailable = false;
            return false;
        }
    }

    public static Task<DecodedAudio> DecodeAsync(byte[] audio, string? fileName, CancellationToken ct = default) =>
        DecodeAsync(audio.AsMemory(), fileName, ct);

    public static async Task<DecodedAudio> DecodeAsync(ReadOnlyMemory<byte> audio, string? fileName, CancellationToken ct = default)
    {
        if (audio.IsEmpty)
            throw new ArgumentException("Audio buffer is empty.", nameof(audio));

        // 1. If it's already an uncompressed PCM or IEEE float WAV, extract/convert the PCM bytes directly without external tools.
        if (TryReadPcmWav(audio.Span, out var wavAudio))
        {
            return wavAudio;
        }

        // 2. Decode compressed audio (M4A, MP3, AAC, etc.) using ffmpeg.
        return await DecodeWithFfmpegAsync(audio, fileName, ct);
    }

    /// <summary>
    /// Parses a standard RIFF/WAVE header and extracts raw PCM bytes, sample rate, width, and channel count.
    /// Supports both integer PCM (Format 1) and 32-bit IEEE Float (Format 3).
    /// </summary>
    public static bool TryReadPcmWav(ReadOnlySpan<byte> bytes, out DecodedAudio audio)
    {
        audio = default;
        if (bytes.Length < 44) return false;
        if (!bytes[..4].SequenceEqual(RiffHeader) || !bytes[8..12].SequenceEqual(WaveHeader))
            return false;

        var idx = 12;
        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = -1;

        while (idx + 8 <= bytes.Length)
        {
            var chunkId = bytes.Slice(idx, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(idx + 4, 4));
            var nextIdx = idx + 8 + chunkSize + (chunkSize % 2);

            if (chunkId.SequenceEqual(FmtChunk) && chunkSize >= 16 && idx + 8 + 16 <= bytes.Length)
            {
                var fmt = bytes.Slice(idx + 8, chunkSize);
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(fmt[..2]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt.Slice(14, 2));
            }
            else if (chunkId.SequenceEqual(DataChunk))
            {
                dataOffset = idx + 8;
                dataLength = chunkSize;
                if (dataLength < 0 || dataOffset + dataLength > bytes.Length)
                {
                    dataLength = bytes.Length - dataOffset;
                }
            }

            if (nextIdx <= idx) break; // Avoid infinite loop on malformed chunks
            idx = nextIdx;
        }

        // Format 1 == Linear PCM
        if (audioFormat == 1 && channels > 0 && sampleRate > 0 && bitsPerSample is 8 or 16 or 24 or 32 && dataOffset >= 0 && dataLength > 0)
        {
            var pcm = bytes.Slice(dataOffset, dataLength).ToArray();
            audio = new DecodedAudio(pcm, Rate: sampleRate, Width: bitsPerSample / 8, Channels: channels);
            return true;
        }

        // Format 3 == IEEE Float (32-bit float -> convert to 16-bit PCM in memory)
        if (audioFormat == 3 && channels > 0 && sampleRate > 0 && bitsPerSample == 32 && dataOffset >= 0 && dataLength > 0)
        {
            var sampleCount = dataLength / 4;
            var pcm = new byte[sampleCount * 2];
            var floatSpan = MemoryMarshal.Cast<byte, float>(bytes.Slice(dataOffset, sampleCount * 4));
            var shortSpan = MemoryMarshal.Cast<byte, short>(pcm);

            for (int i = 0; i < floatSpan.Length; i++)
            {
                var clamped = Math.Clamp(floatSpan[i], -1.0f, 1.0f);
                shortSpan[i] = clamped < 0
                    ? (short)Math.Clamp(clamped * 32768f, -32768f, 32767f)
                    : (short)(clamped * 32767f);
            }

            audio = new DecodedAudio(pcm, Rate: sampleRate, Width: 2, Channels: channels);
            return true;
        }

        return false;
    }

    private static async Task<DecodedAudio> DecodeWithFfmpegAsync(ReadOnlyMemory<byte> audio, string? fileName, CancellationToken ct)
    {
        if (!IsFfmpegAvailable)
        {
            throw new WyomingAudioException(
                $"Audio format '{(string.IsNullOrEmpty(fileName) ? "unknown" : Path.GetExtension(fileName))}' requires ffmpeg to decode to PCM, but ffmpeg was not found on PATH. Please install ffmpeg or supply uncompressed WAV audio.");
        }

        // Attempt 1: Stream directly via pipe:0 -> pipe:1
        try
        {
            var pcm = await RunFfmpegAsync(["-loglevel", "error", "-i", "pipe:0", "-f", "s16le", "-acodec", "pcm_s16le", "-ac", "1", "-ar", "16000", "pipe:1"], audio, ct);
            if (pcm.Length > 0)
                return new DecodedAudio(pcm, Rate: 16000, Width: 2, Channels: 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some MP4/M4A files have the moov atom at the end, which requires seeking and cannot be piped through pipe:0.
            // Fall back to writing a temporary file.
        }

        // Attempt 2: Write to a temp file and decode
        var ext = !string.IsNullOrWhiteSpace(fileName) ? Path.GetExtension(fileName) : ".m4a";
        if (string.IsNullOrWhiteSpace(ext)) ext = ".m4a";
        var tempFile = Path.Combine(Path.GetTempPath(), $"index2sp_{Guid.NewGuid():N}{ext}");

        try
        {
            await File.WriteAllBytesAsync(tempFile, audio.ToArray(), ct);
            var pcm = await RunFfmpegAsync(["-loglevel", "error", "-i", tempFile, "-f", "s16le", "-acodec", "pcm_s16le", "-ac", "1", "-ar", "16000", "pipe:1"], null, ct);
            if (pcm.Length > 0)
                return new DecodedAudio(pcm, Rate: 16000, Width: 2, Channels: 1);

            throw new WyomingAudioException("ffmpeg produced empty PCM audio when decoding.");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task<byte[]> RunFfmpegAsync(string[] arguments, ReadOnlyMemory<byte>? stdinData, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardInput = stdinData.HasValue,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                   ?? throw new WyomingAudioException("Failed to launch ffmpeg process.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _isFfmpegAvailable = false;
            throw new WyomingAudioException(
                "ffmpeg was not found on PATH. An external decoder (ffmpeg) is required to transcribe compressed audio formats such as .m4a. Please install ffmpeg or supply uncompressed WAV audio.", ex);
        }

        using (proc)
        {
            using var stdoutStream = new MemoryStream();
            using var stderrStream = new MemoryStream();

            var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdoutStream, ct);
            var stderrTask = proc.StandardError.BaseStream.CopyToAsync(stderrStream, ct);
            Task stdinTask = Task.CompletedTask;

            if (stdinData.HasValue)
            {
                stdinTask = Task.Run(async () =>
                {
                    try
                    {
                        await proc.StandardInput.BaseStream.WriteAsync(stdinData.Value, ct);
                        await proc.StandardInput.BaseStream.FlushAsync(ct);
                    }
                    catch (IOException)
                    {
                        // Process may have exited early or closed pipe
                    }
                    finally
                    {
                        try { proc.StandardInput.Close(); } catch { }
                    }
                }, ct);
            }

            await Task.WhenAll(stdoutTask, stderrTask, stdinTask);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                var stderr = System.Text.Encoding.UTF8.GetString(stderrStream.ToArray());
                throw new WyomingAudioException($"ffmpeg failed with exit code {proc.ExitCode}: {stderr.Trim()}");
            }

            return stdoutStream.ToArray();
        }
    }
}
