using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Konscious.Security.Cryptography;

namespace Index2SP;

/// <summary>
/// Creates Super Productivity tasks by uploading operations straight to a SuperSync server
/// (https://sync.super-productivity.com or self-hosted) — no running desktop app needed.
///
/// Mirrors what SP itself uploads for TaskSharedActions.addTask: one CRT/TASK operation whose
/// payload is <c>{ actionPayload, entityChanges: [] }</c>, end-to-end encrypted with the account's
/// encryption password (SuperSync rejects plaintext ops). Protocol source of truth:
/// super-productivity packages/super-sync-server and packages/sync-core.
///
/// Before each upload it downloads every op it hasn't seen and merges their vector clocks, so
/// its own op causally follows everything on the server. Skipping that would make the op look
/// concurrent with the latest full-state import, and SP clients silently drop those.
/// </summary>
public sealed class SuperSyncClient : IDisposable
{
    // Must match @sp/shared-schema CURRENT_SCHEMA_VERSION.
    private const int SchemaVersion = 4;
    private const string InboxProjectId = "INBOX_PROJECT";

    // Argon2id + AES-GCM parameters: a cross-platform contract in @sp/sync-core
    // (encryption/argon2.ts, encryption/web-crypto.ts). Wire format: base64(salt|iv|ciphertext+tag).
    private const int SaltLength = 16, IvLength = 12, KeyLength = 32, TagLength = 16;
    private const int Argon2Iterations = 3, Argon2MemoryKiB = 65536, Argon2Parallelism = 1;

    // One lock for every instance: several SuperProductivityClients (webhook, outbox, crash
    // reporter) may upload at once, and they all share the one state file.
    private static readonly SemaphoreSlim StateLock = new(1, 1);

    private readonly HttpClient _http;
    private readonly AppConfig.SuperSyncConfig _config;
    private readonly string _statePath;

    public SuperSyncClient(AppConfig.SuperSyncConfig config, string? statePath = null)
    {
        _config = config;
        _statePath = statePath ?? Path.Combine(AppConfig.ConfigDirectory, "supersync-state.json");
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AccessToken);
    }

    /// <summary>Enabled, with both an access token and an encryption password set.</summary>
    public static bool IsConfigured(AppConfig.SuperSyncConfig c) =>
        c.Enabled && !string.IsNullOrWhiteSpace(c.AccessToken) && !string.IsNullOrEmpty(c.EncryptionPassword);

    public async Task<SuperProductivityClient.CreateResult> CreateTaskAsync(SpTaskRequest request, CancellationToken ct = default)
    {
        await StateLock.WaitAsync(ct);
        try
        {
            var state = LoadState();
            await CatchUpAsync(state, ct);

            var taskId = NewNanoId();
            var clock = new Dictionary<string, long>(state.VectorClock);
            clock[state.ClientId] = clock.GetValueOrDefault(state.ClientId) + 1;

            var payload = BuildAddTaskPayload(taskId, request, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var opId = NewUuidV7();
            var op = new JsonObject
            {
                ["id"] = opId,
                ["clientId"] = state.ClientId,
                ["actionType"] = "[Task Shared] addTask",
                ["opType"] = "CRT",
                ["entityType"] = "TASK",
                ["entityId"] = taskId,
                ["entityIds"] = new JsonArray(taskId),
                ["payload"] = Encrypt(payload.ToJsonString(), _config.EncryptionPassword),
                ["isPayloadEncrypted"] = true,
                ["vectorClock"] = ToJson(clock),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["schemaVersion"] = SchemaVersion,
            };
            var body = new JsonObject
            {
                ["ops"] = new JsonArray(op),
                ["clientId"] = state.ClientId,
                ["lastKnownServerSeq"] = state.LastServerSeq,
                // Same request id on a retry lets the server replay its cached result.
                ["requestId"] = opId.Replace("-", ""),
            };

            using var resp = await _http.PostAsJsonAsync("api/sync/ops", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            ThrowOnHttpError(resp, text, "upload");

            var result = JsonNode.Parse(text)?["results"]?[0];
            var accepted = result?["accepted"]?.GetValue<bool>() == true;
            var errorCode = result?["errorCode"]?.GetValue<string>();
            if (!accepted && errorCode != "DUPLICATE_OPERATION")
            {
                var msg = result?["error"]?.GetValue<string>() ?? errorCode ?? "no result";
                // Validation / E2EE rejections won't pass on retry; conflicts, quota and
                // "download the latest state first" might after the next catch-up.
                var permanent = errorCode is "VALIDATION_FAILED" or "INVALID_PAYLOAD" or "E2EE_REQUIRED"
                    or "INVALID_CLIENT_ID" or "INVALID_SCHEMA_VERSION" or "INVALID_VECTOR_CLOCK";
                throw new SpApiException($"SuperSync rejected the task: {msg}", permanent);
            }

            state.VectorClock = clock;
            SaveState(state);
            return new SuperProductivityClient.CreateResult(taskId, Truncate(text, 2000), ViaSuperSync: true);
        }
        finally
        {
            StateLock.Release();
        }
    }

    /// <summary>Checks the token (downloads new ops) and, when the account has any encrypted op
    /// to try it on, the encryption password too.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        await StateLock.WaitAsync(ct);
        try
        {
            var state = LoadState();
            var sample = await CatchUpAsync(state, ct);
            if (sample is null)
                return $"OK — {_config.BaseUrl} token accepted (no encrypted ops to check the password against yet)";

            try { Decrypt(sample, _config.EncryptionPassword); }
            catch (CryptographicException)
            {
                throw new SpApiException("SuperSync token accepted, but the encryption password doesn't decrypt " +
                                         "this account's data. Use the same password as in Super Productivity's sync settings.",
                                         permanent: true);
            }
            return $"OK — {_config.BaseUrl} token and encryption password accepted";
        }
        finally
        {
            StateLock.Release();
        }
    }

    // ---- sync state ------------------------------------------------------------------

    private sealed class SyncState
    {
        public string ClientId { get; set; } = "";
        /// <summary>Which server + token the cursor and clock belong to.</summary>
        public string Account { get; set; } = "";
        public long LastServerSeq { get; set; }
        public Dictionary<string, long> VectorClock { get; set; } = new();
    }

    /// <summary>Downloads every op past the saved cursor and folds its vector clock into the
    /// state. Returns one encrypted payload seen along the way (for password checks), if any.</summary>
    private async Task<string?> CatchUpAsync(SyncState state, CancellationToken ct)
    {
        string? encryptedSample = null;
        var restarted = false;
        while (true)
        {
            using var resp = await _http.GetAsync($"api/sync/ops?sinceSeq={state.LastServerSeq}&limit=1000", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            ThrowOnHttpError(resp, text, "download");

            var root = JsonNode.Parse(text)!;
            if (root["gapDetected"]?.GetValue<bool>() == true && state.LastServerSeq > 0 && !restarted)
            {
                // Server history was reset or pruned past our cursor — rebuild from scratch.
                // Our own counter survives: it's either in the state or in the server's ops.
                restarted = true;
                state.LastServerSeq = 0;
                continue;
            }

            MergeClock(state.VectorClock, root["snapshotVectorClock"]);
            long maxSeq = state.LastServerSeq;
            foreach (var entry in root["ops"]?.AsArray() ?? new JsonArray())
            {
                var op = entry?["op"];
                MergeClock(state.VectorClock, op?["vectorClock"]);
                maxSeq = Math.Max(maxSeq, entry?["serverSeq"]?.GetValue<long>() ?? 0);
                if (encryptedSample is null && op?["isPayloadEncrypted"]?.GetValue<bool>() == true &&
                    op["payload"] is JsonValue p && p.TryGetValue<string>(out var s))
                    encryptedSample = s;
            }

            var hasMore = root["hasMore"]?.GetValue<bool>() == true;
            state.LastServerSeq = hasMore ? maxSeq : Math.Max(maxSeq, root["latestSeq"]?.GetValue<long>() ?? maxSeq);
            SaveState(state);
            if (!hasMore) return encryptedSample;
        }
    }

    private SyncState LoadState()
    {
        SyncState? state = null;
        try
        {
            if (File.Exists(_statePath))
                state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_statePath), AppConfig.JsonOptions);
        }
        catch (JsonException) { /* corrupt — start over; catch-up rebuilds the clock */ }

        state ??= new SyncState();
        if (string.IsNullOrEmpty(state.ClientId))
            state.ClientId = "I2SP_" + RandomString(8, Base62);
        state.VectorClock ??= new();

        var account = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(_config.BaseUrl + "\n" + _config.AccessToken)))[..16];
        if (state.Account != account)
        {
            state.Account = account;
            state.LastServerSeq = 0;
            state.VectorClock = new();
        }
        return state;
    }

    private void SaveState(SyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, AppConfig.JsonOptions));
        File.Move(tmp, _statePath, overwrite: true);
    }

    private static void MergeClock(Dictionary<string, long> into, JsonNode? clock)
    {
        if (clock is not JsonObject obj) return;
        foreach (var (id, v) in obj)
        {
            if (v is JsonValue value && value.TryGetValue<long>(out var n))
                into[id] = Math.Max(into.GetValueOrDefault(id), n);
        }
    }

    private static JsonObject ToJson(Dictionary<string, long> clock)
    {
        var obj = new JsonObject();
        foreach (var (id, n) in clock) obj[id] = n;
        return obj;
    }

    // ---- op payload ------------------------------------------------------------------

    /// <summary>The MultiEntityPayload SP captures for TaskSharedActions.addTask — the action
    /// minus type/meta, with a full Task (DEFAULT_TASK fields + ours).</summary>
    public static JsonObject BuildAddTaskPayload(string taskId, SpTaskRequest r, long createdMs)
    {
        var projectId = string.IsNullOrWhiteSpace(r.ProjectId) ? InboxProjectId : r.ProjectId;
        var tagIds = new JsonArray();
        foreach (var t in r.TagIds ?? new List<string>()) tagIds.Add(t);

        var task = new JsonObject
        {
            ["id"] = taskId,
            ["projectId"] = projectId,
            ["subTaskIds"] = new JsonArray(),
            ["timeSpentOnDay"] = new JsonObject(),
            ["timeSpent"] = 0,
            ["timeEstimate"] = r.TimeEstimate ?? 0,
            ["isDone"] = false,
            ["title"] = r.Title,
            ["tagIds"] = tagIds,
            ["created"] = createdMs,
            ["attachments"] = new JsonArray(),
        };
        if (!string.IsNullOrEmpty(r.Notes)) task["notes"] = r.Notes;
        // SP keeps dueWithTime and dueDay mutually exclusive; the timed one wins.
        if (r.DueWithTime is { } due)
        {
            task["dueWithTime"] = due;
            if (r.HasPlannedTime is { } planned) task["hasPlannedTime"] = planned;
        }
        else if (!string.IsNullOrEmpty(r.DueDay))
        {
            task["dueDay"] = r.DueDay;
        }

        return new JsonObject
        {
            ["actionPayload"] = new JsonObject
            {
                ["task"] = task,
                ["workContextId"] = projectId,
                ["workContextType"] = "PROJECT",
                ["isAddToBacklog"] = false,
                ["isAddToBottom"] = false,
            },
            ["entityChanges"] = new JsonArray(),
        };
    }

    // ---- encryption ------------------------------------------------------------------

    public static string Encrypt(string plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(DeriveKey(password, salt), TagLength))
            aes.Encrypt(iv, data, cipher, tag);

        var buf = new byte[SaltLength + IvLength + cipher.Length + TagLength];
        salt.CopyTo(buf, 0);
        iv.CopyTo(buf, SaltLength);
        cipher.CopyTo(buf, SaltLength + IvLength);
        tag.CopyTo(buf, SaltLength + IvLength + cipher.Length);
        return Convert.ToBase64String(buf);
    }

    /// <summary>Argon2-format payloads only; throws CryptographicException on a wrong password.</summary>
    public static string Decrypt(string base64, string password)
    {
        var buf = Convert.FromBase64String(base64);
        if (buf.Length < SaltLength + IvLength + TagLength)
            throw new CryptographicException("Encrypted payload too short");
        var salt = buf.AsSpan(0, SaltLength);
        var iv = buf.AsSpan(SaltLength, IvLength);
        var cipher = buf.AsSpan(SaltLength + IvLength, buf.Length - SaltLength - IvLength - TagLength);
        var tag = buf.AsSpan(buf.Length - TagLength);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(DeriveKey(password, salt.ToArray()), TagLength))
            aes.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = Argon2Iterations,
            MemorySize = Argon2MemoryKiB,
            DegreeOfParallelism = Argon2Parallelism,
        };
        return argon.GetBytes(KeyLength);
    }

    // ---- ids -------------------------------------------------------------------------

    private const string Base62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string NanoIdAlphabet = "useandom-26T198340PX75pxJACKVERYMINDBUSHWOLF_GQZbfghjklqvwyzrict";

    /// <summary>Same shape as SP's task ids (nanoid, 21 chars).</summary>
    private static string NewNanoId() => RandomString(21, NanoIdAlphabet);

    private static string RandomString(int length, string alphabet)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>RFC 9562 UUIDv7 — what SP uses for op ids (time-ordered).</summary>
    public static string NewUuidV7()
    {
        var b = RandomNumberGenerator.GetBytes(16);
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++) b[i] = (byte)(ms >> (8 * (5 - i)));
        b[6] = (byte)(0x70 | (b[6] & 0x0F));
        b[8] = (byte)(0x80 | (b[8] & 0x3F));
        var hex = Convert.ToHexString(b).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    // ---- http ------------------------------------------------------------------------

    private void ThrowOnHttpError(HttpResponseMessage resp, string body, string what)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SpApiException($"SuperSync rejected the access token ({(int)resp.StatusCode}). " +
                                     "Check superProductivity.superSync.accessToken in config.json.", permanent: true);
        throw new SpApiException($"SuperSync {what} returned HTTP {(int)resp.StatusCode}: {Truncate(body, 400)}");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}
