using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Index2SP;

/// <summary>
/// Encrypts sensitive config values (tokens, API keys, secrets) at rest with AES-256-GCM, so
/// config.json never holds them in plain text. The key is a random 256-bit value in
/// secret.key beside config.json (owner-only permissions on Linux/macOS; %APPDATA% is already
/// per-user on Windows). This keeps secrets out of a config.json that gets copied, synced,
/// shared, or screenshotted — it does not stop something already running as the same user,
/// which can read secret.key too.
/// </summary>
internal static class SecretProtector
{
    /// <summary>Marks an encrypted value. Anything without it is treated as plain text the user
    /// typed into config.json by hand, and gets encrypted on the next save.</summary>
    public const string Prefix = "enc:v1:";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly object KeyLock = new();
    private static byte[]? _key;
    private static string? _keyPathLoaded;

    /// <summary>Overridable for tests so they don't touch the real config directory.</summary>
    internal static string KeyPath { get; set; } = Path.Combine(AppConfig.ConfigDirectory, "secret.key");

    /// <summary>Count of values on this thread that couldn't be decrypted (missing or changed
    /// secret.key) since the last <see cref="ResetFailures"/>.</summary>
    [ThreadStatic] private static int _failures;

    public static void ResetFailures() => _failures = 0;
    public static int Failures => _failures;

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain)
    {
        // Blanks stay blank so "not configured" is still obvious in the file; already-encrypted
        // values (including ones we couldn't decrypt) are written back untouched.
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain;

        var key = GetKey(create: true)!;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var blob = new byte[NonceSize + TagSize + plainBytes.Length];
        nonce.CopyTo(blob, 0);
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plainBytes, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Decrypts a value written by <see cref="Protect"/>. Plain text passes through.
    /// A value that can't be decrypted is returned as-is (so a later save doesn't destroy it
    /// while secret.key is missing) and counted in <see cref="Failures"/>.</summary>
    public static string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        try
        {
            var key = GetKey(create: false) ?? throw new CryptographicException("secret.key not found");
            var blob = Convert.FromBase64String(value[Prefix.Length..]);
            if (blob.Length < NonceSize + TagSize) throw new CryptographicException("value too short");
            var plain = new byte[blob.Length - NonceSize - TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
        {
            _failures++;
            return value;
        }
    }

    private static byte[]? GetKey(bool create)
    {
        lock (KeyLock)
        {
            var path = KeyPath;
            if (_key is not null && _keyPathLoaded == path) return _key;

            if (!File.Exists(path))
            {
                if (!create) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fresh = RandomNumberGenerator.GetBytes(32);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                try
                {
                    using var writer = new StreamWriter(path, Encoding.ASCII, options);
                    writer.Write(Convert.ToBase64String(fresh));
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process created it first — fall through and use theirs.
                }
            }

            var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (key.Length != 32) throw new CryptographicException("secret.key is not a 256-bit key");
            _key = key;
            _keyPathLoaded = path;
            return key;
        }
    }
}

/// <summary>Applied to sensitive <see cref="AppConfig"/> string properties: decrypts on read,
/// encrypts on write. The in-memory config always holds plain text.</summary>
internal sealed class SecretJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value is null ? null : SecretProtector.Unprotect(value);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SecretProtector.Protect(value));
}
