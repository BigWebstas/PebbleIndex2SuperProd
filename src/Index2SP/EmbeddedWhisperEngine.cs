using System.Diagnostics;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace Index2SP;

/// <summary>
/// Embedded in-process speech-to-text engine using Whisper.net / whisper.cpp.
/// Runs completely locally with zero external processes, containers, or Python runtimes.
/// Automatically downloads GGML models on demand to the local application data directory.
/// </summary>
public static class EmbeddedWhisperEngine
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static WhisperFactory? _cachedFactory;
    private static string? _cachedModelPath;

    /// <summary>
    /// Directory where downloaded GGML Whisper models are stored.
    /// Defaults to %APPDATA%\Index2SP\models (Windows) or ~/.config/Index2SP/models (Linux).
    /// </summary>
    public static string ModelsDirectory =>
        Path.Combine(AppConfig.ConfigDirectory, "models");

    /// <summary>
    /// Parses a model name string (e.g. "base.en", "tiny", "small") into a GgmlType enum value.
    /// </summary>
    public static GgmlType ParseModelType(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return GgmlType.BaseEn;
        var normalized = modelName.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(".", "");
        return normalized switch
        {
            "tiny" => GgmlType.Tiny,
            "tinyen" => GgmlType.TinyEn,
            "base" => GgmlType.Base,
            "baseen" => GgmlType.BaseEn,
            "small" => GgmlType.Small,
            "smallen" => GgmlType.SmallEn,
            "medium" => GgmlType.Medium,
            "mediumen" => GgmlType.MediumEn,
            "largev1" => GgmlType.LargeV1,
            "largev2" => GgmlType.LargeV2,
            "large" or "largev3" => GgmlType.LargeV3,
            _ => GgmlType.BaseEn,
        };
    }

    /// <summary>
    /// Determines the standard filename for a GGML model type (e.g. "ggml-base.en.bin").
    /// </summary>
    public static string GetModelFileName(GgmlType type) => type switch
    {
        GgmlType.Tiny => "ggml-tiny.bin",
        GgmlType.TinyEn => "ggml-tiny.en.bin",
        GgmlType.Base => "ggml-base.bin",
        GgmlType.BaseEn => "ggml-base.en.bin",
        GgmlType.Small => "ggml-small.bin",
        GgmlType.SmallEn => "ggml-small.en.bin",
        GgmlType.Medium => "ggml-medium.bin",
        GgmlType.MediumEn => "ggml-medium.en.bin",
        GgmlType.LargeV1 => "ggml-large-v1.bin",
        GgmlType.LargeV2 => "ggml-large-v2.bin",
        GgmlType.LargeV3 => "ggml-large-v3.bin",
        _ => "ggml-base.en.bin",
    };

    /// <summary>
    /// Ensures that the specified model exists on disk, downloading it from Hugging Face if necessary.
    /// </summary>
    public static async Task<string> EnsureModelDownloadedAsync(
        string? modelName,
        string? customPath = null,
        Logger? log = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            return customPath;

        var ggmlType = ParseModelType(modelName);
        var fileName = GetModelFileName(ggmlType);
        var modelsDir = ModelsDirectory;
        Directory.CreateDirectory(modelsDir);
        var targetPath = Path.Combine(modelsDir, fileName);

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            return targetPath;

        log?.Info($"Embedded Whisper: model '{fileName}' not found locally. Downloading from Hugging Face...");

        var tempPath = targetPath + $".{Guid.NewGuid():N}.download";
        try
        {
            using var stream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                ggmlType, QuantizationType.NoQuantization, ct);
            using var fileStream = File.Create(tempPath);
            await stream.CopyToAsync(fileStream, ct);
            await fileStream.FlushAsync(ct);
            fileStream.Close();

            File.Move(tempPath, targetPath, overwrite: true);
            var sizeMb = new FileInfo(targetPath).Length / (1024.0 * 1024.0);
            log?.Info($"Embedded Whisper: model '{fileName}' downloaded successfully ({sizeMb:F1} MB)");
            return targetPath;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw new WhisperAsrApiException(
                $"Failed to download Whisper model '{fileName}' from Hugging Face. Please ensure you have internet access or specify a local modelPath in config.json ({ex.Message})", ex);
        }
    }

    /// <summary>
    /// Returns a cached WhisperFactory for the given model path, reusing previously loaded weights in RAM.
    /// </summary>
    public static async Task<WhisperFactory> GetFactoryAsync(
        string? modelName,
        string? customPath = null,
        Logger? log = null,
        CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        try
        {
            var modelPath = await EnsureModelDownloadedAsync(modelName, customPath, log, ct);
            if (_cachedFactory != null && _cachedModelPath == modelPath)
            {
                return _cachedFactory;
            }

            _cachedFactory?.Dispose();
            _cachedFactory = null;
            _cachedModelPath = null;

            log?.Info($"Embedded Whisper: loading model weights from {modelPath}...");
            var sw = Stopwatch.StartNew();
            _cachedFactory = WhisperFactory.FromPath(modelPath);
            _cachedModelPath = modelPath;
            sw.Stop();
            log?.Info($"Embedded Whisper: model loaded in {sw.ElapsedMilliseconds}ms");
            return _cachedFactory;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Transcribes the given audio bytes directly in-process via Whisper.net.
    /// </summary>
    public static async Task<string?> TranscribeAsync(
        AppConfig.WhisperAsrConfig config,
        ReadOnlyMemory<byte> audioBytes,
        string? fileName = null,
        Logger? log = null,
        CancellationToken ct = default)
    {
        if (audioBytes.IsEmpty) return null;

        var (wavMemory, _, _) = await AudioDecoder.EnsureWavAsync(audioBytes, fileName, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var token = cts.Token;

        var factory = await GetFactoryAsync(config.Model, config.ModelPath, log, token);

        var builder = factory.CreateBuilder();
        if (!string.IsNullOrWhiteSpace(config.Language))
        {
            builder.WithLanguage(config.Language.Trim());
        }
        else
        {
            builder.WithLanguage("auto");
        }

        using var processor = builder.Build();
        using var wavStream = new MemoryStream(wavMemory.ToArray());

        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();

        await foreach (var segment in processor.ProcessAsync(wavStream, token))
        {
            if (!string.IsNullOrWhiteSpace(segment.Text))
            {
                sb.Append(segment.Text);
            }
        }
        sw.Stop();

        var text = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            log?.Info($"Embedded Whisper: finished in {sw.ElapsedMilliseconds}ms with empty transcription");
            return null;
        }

        log?.Info($"Embedded Whisper: transcribed in {sw.ElapsedMilliseconds}ms ({text.Length} chars): \"{text}\"");
        return text;
    }

    /// <summary>
    /// Tests model availability, downloading the model if necessary and verifying that the native runtime loads.
    /// </summary>
    public static async Task<string> TestAsync(
        AppConfig.WhisperAsrConfig config,
        Logger? log = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var factory = await GetFactoryAsync(config.Model, config.ModelPath, log, ct);

        // Run a tiny 0.1s silence inference to verify native bindings execute flawlessly
        var silentPcm = new byte[3200]; // 0.1s at 16kHz 16-bit mono
        var testWav = AudioDecoder.ToWav(silentPcm, 16000, 1, 16);
        using var testStream = new MemoryStream(testWav);

        var builder = factory.CreateBuilder().WithLanguage("en");
        using var processor = builder.Build();

        await foreach (var _ in processor.ProcessAsync(testStream, ct)) { }
        sw.Stop();

        var modelName = string.IsNullOrWhiteSpace(config.Model) ? "base.en" : config.Model;
        var modelFile = _cachedModelPath != null && File.Exists(_cachedModelPath) ? new FileInfo(_cachedModelPath) : null;
        var sizeInfo = modelFile != null ? $" ({modelFile.Length / (1024.0 * 1024.0):F1} MB)" : "";

        return $"OK — Embedded Whisper loaded model '{modelName}'{sizeInfo} and verified inference in {sw.ElapsedMilliseconds}ms";
    }

    /// <summary>
    /// Disposes any cached factory and releases loaded model weights from memory.
    /// </summary>
    public static void DisposeLoadedModel()
    {
        Lock.Wait();
        try
        {
            _cachedFactory?.Dispose();
            _cachedFactory = null;
            _cachedModelPath = null;
        }
        finally
        {
            Lock.Release();
        }
    }
}
