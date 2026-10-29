using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// A write-ahead journal for webhooks being processed. Each claimed webhook is saved under
/// <c>%APPDATA%\Index2SP\inbox\</c> before any work starts and removed once processing ends (success
/// or failure — a failed attempt is the sender's to retry). Whatever is still there at startup was
/// cut off by a crash or kill, so <see cref="TakePending"/> hands it back for one more run. An entry
/// that is too old, unreadable, or already replayed <see cref="MaxAttempts"/> times (a webhook that
/// itself crashes the app) is moved to <c>inbox\failed\</c> instead, so it can't cause a crash loop.
/// </summary>
public sealed class Inbox
{
    public const int MaxAttempts = 2;
    public static readonly TimeSpan MaxAge = DuplicateMessageTracker.Retention;

    public sealed record Item(string Id, PebblePayload Payload, DateTimeOffset ReceivedAt, int Attempts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dir;
    private readonly string _failedDir;
    private readonly Logger _log;
    private readonly Func<DateTimeOffset> _now;

    public Inbox(string configDir, Logger log, Func<DateTimeOffset>? now = null)
    {
        _dir = Path.Combine(configDir, "inbox");
        _failedDir = Path.Combine(_dir, "failed");
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { _log.Error("Could not create the inbox directory", ex); }
    }

    /// <summary>Best-effort: a journal that can't be written must never block the webhook itself.</summary>
    public void Save(string id, PebblePayload payload) =>
        Write(new Item(id, payload, _now(), Attempts: 0));

    public void Remove(string id)
    {
        try { File.Delete(PathFor(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not clear inbox entry for '{id}': {ex.Message}");
        }
    }

    /// <summary>Entries left over from a previous run that are worth one more attempt, with their
    /// attempt count already bumped on disk so a crash during replay counts against them.</summary>
    public List<Item> TakePending()
    {
        var pending = new List<Item>();
        if (!Directory.Exists(_dir)) return pending;

        foreach (var file in Directory.EnumerateFiles(_dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            Item? item = null;
            try { item = JsonSerializer.Deserialize<Item>(File.ReadAllText(file), JsonOpts); }
            catch (Exception ex) when (ex is IOException or JsonException) { }

            if (item is null || item.Payload is null || string.IsNullOrEmpty(item.Id))
            {
                Quarantine(file, "unreadable");
            }
            else if (_now() - item.ReceivedAt > MaxAge)
            {
                Quarantine(file, "too old to replay");
            }
            else if (item.Attempts >= MaxAttempts)
            {
                Quarantine(file, $"already replayed {item.Attempts} time(s) without finishing");
            }
            else
            {
                var next = item with { Attempts = item.Attempts + 1 };
                Write(next);
                pending.Add(next);
            }
        }
        return pending;
    }

    private void Write(Item item)
    {
        try
        {
            var path = PathFor(item.Id);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(item, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not journal webhook '{item.Id}': {ex.Message}");
        }
    }

    private void Quarantine(string file, string reason)
    {
        try
        {
            Directory.CreateDirectory(_failedDir);
            File.Move(file, Path.Combine(_failedDir, Path.GetFileName(file)), overwrite: true);
            _log.Warn($"Inbox entry {Path.GetFileName(file)} not replayed ({reason}) — moved to inbox\\failed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not quarantine inbox entry {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    // Message ids are sender-controlled — hash them rather than trust them as file names.
    private string PathFor(string id) =>
        Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
}
