using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Index2SP.Tests;

public class SuperSyncClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "i2sp-supersync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Produced by Super Productivity's own algorithm (hash-wasm argon2id + WebCrypto AES-GCM,
    // same parameters as @sp/sync-core) — pins cross-implementation compatibility.
    private const string SpCiphertext = "4QUqzCYxw8zjwEGHA8Coth4k9xQTaxGX+ob3NXYWWyEbdqeHR1leI5rTZqUyJz/SsN4/Ajzp7ywF/mYoipLb";

    [Fact]
    public void Decrypt_ReadsPayloadEncryptedBySuperProductivity()
    {
        Assert.Equal("{\"hello\":\"from SP\"}", SuperSyncClient.Decrypt(SpCiphertext, "pässwörd"));
    }

    [Fact]
    public void Decrypt_WrongPasswordThrows()
    {
        Assert.ThrowsAny<CryptographicException>(() => SuperSyncClient.Decrypt(SpCiphertext, "wrong"));
    }

    [Fact]
    public void Encrypt_RoundTripsAndHasTransportShape()
    {
        var c = SuperSyncClient.Encrypt("{\"a\":1}", "pw");
        Assert.Equal("{\"a\":1}", SuperSyncClient.Decrypt(c, "pw"));
        Assert.Matches("^[A-Za-z0-9+/]+={0,2}$", c);
        Assert.True(Convert.FromBase64String(c).Length >= 16 + 12 + 16);
    }

    [Fact]
    public void BuildAddTaskPayload_DefaultsToInboxAndKeepsDueFieldsExclusive()
    {
        var p = SuperSyncClient.BuildAddTaskPayload("t1", new SpTaskRequest
        {
            Title = "Buy milk", DueDay = "2026-10-01", DueWithTime = 123, HasPlannedTime = true,
        }, 42);

        var ap = p["actionPayload"]!;
        var task = ap["task"]!;
        Assert.Equal("INBOX_PROJECT", task["projectId"]!.GetValue<string>());
        Assert.Equal("INBOX_PROJECT", ap["workContextId"]!.GetValue<string>());
        Assert.Equal("PROJECT", ap["workContextType"]!.GetValue<string>());
        Assert.Equal(123, task["dueWithTime"]!.GetValue<long>());
        Assert.Null(task["dueDay"]);
        Assert.Null(task["notes"]);
        Assert.Empty(p["entityChanges"]!.AsArray());
    }

    [Fact]
    public void NewUuidV7_IsVersion7()
    {
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", SuperSyncClient.NewUuidV7());
    }

    [Fact]
    public async Task CreateTask_ClockDominatesEverythingDownloadedAndStatePersists()
    {
        using var server = new FakeSuperSync();
        server.Ops.Add(Op(1, "E_import", """{"E_import":5,"B_other":2}""", "SYNC_IMPORT"));
        server.Ops.Add(Op(2, "A_phone", """{"E_import":5,"A_phone":9}"""));
        server.SnapshotClock = """{"OLD_client":3}""";

        var cfg = new AppConfig.SuperSyncConfig
        {
            Enabled = true, BaseUrl = server.Url, AccessToken = "tok", EncryptionPassword = "pw",
        };
        var statePath = Path.Combine(_dir, "state.json");
        using (var client = new SuperSyncClient(cfg, statePath))
        {
            var result = await client.CreateTaskAsync(new SpTaskRequest { Title = "Hello", ProjectId = "P1" });
            Assert.True(result.ViaSuperSync);

            var op = server.Uploaded.Single();
            var clock = op["vectorClock"]!.AsObject();
            var me = op["clientId"]!.GetValue<string>();
            Assert.StartsWith("I2SP_", me);
            Assert.Equal(5, clock["E_import"]!.GetValue<long>());
            Assert.Equal(2, clock["B_other"]!.GetValue<long>());
            Assert.Equal(9, clock["A_phone"]!.GetValue<long>());
            Assert.Equal(3, clock["OLD_client"]!.GetValue<long>());
            Assert.Equal(1, clock[me]!.GetValue<long>());
            Assert.Equal("[Task Shared] addTask", op["actionType"]!.GetValue<string>());
            Assert.Equal(result.TaskId, op["entityId"]!.GetValue<string>());

            var payload = JsonNode.Parse(SuperSyncClient.Decrypt(op["payload"]!.GetValue<string>(), "pw"))!;
            Assert.Equal("Hello", payload["actionPayload"]!["task"]!["title"]!.GetValue<string>());
            Assert.Equal("P1", payload["actionPayload"]!["task"]!["projectId"]!.GetValue<string>());
        }

        // A second instance resumes from the saved cursor with the same client id and a higher counter.
        using (var client = new SuperSyncClient(cfg, statePath))
        {
            await client.CreateTaskAsync(new SpTaskRequest { Title = "Again" });
            var first = server.Uploaded[0];
            var second = server.Uploaded[1];
            var me = first["clientId"]!.GetValue<string>();
            Assert.Equal(me, second["clientId"]!.GetValue<string>());
            Assert.Equal(2, second["vectorClock"]![me]!.GetValue<long>());
            Assert.Equal("2", server.LastSinceSeq);
        }
    }

    [Fact]
    public async Task CreateTask_RejectedOpThrows()
    {
        using var server = new FakeSuperSync { Reject = "E2EE_REQUIRED" };
        using var client = new SuperSyncClient(new AppConfig.SuperSyncConfig
        {
            Enabled = true, BaseUrl = server.Url, AccessToken = "tok", EncryptionPassword = "pw",
        }, Path.Combine(_dir, "state.json"));

        var ex = await Assert.ThrowsAsync<SpApiException>(() => client.CreateTaskAsync(new SpTaskRequest { Title = "x" }));
        Assert.True(ex.Permanent);
    }

    private static JsonObject Op(long seq, string clientId, string clock, string opType = "CRT") => new()
    {
        ["serverSeq"] = seq,
        ["receivedAt"] = 0,
        ["op"] = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["clientId"] = clientId, ["opType"] = opType,
            ["vectorClock"] = JsonNode.Parse(clock), ["isPayloadEncrypted"] = true,
            ["payload"] = SuperSyncClient.Encrypt("{}", "pw"),
        },
    };

    /// <summary>Minimal in-process SuperSync: GET/POST /api/sync/ops.</summary>
    private sealed class FakeSuperSync : IDisposable
    {
        private readonly HttpListener _listener = new();
        public List<JsonObject> Ops { get; } = new();
        public List<JsonObject> Uploaded { get; } = new();
        public string? SnapshotClock { get; set; }
        public string? Reject { get; set; }
        public string? LastSinceSeq { get; private set; }
        public string Url { get; }

        public FakeSuperSync()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var res = ctx.Response;
                JsonObject reply;
                if (ctx.Request.Headers["Authorization"] != "Bearer tok")
                {
                    res.StatusCode = 401;
                    reply = new JsonObject();
                }
                else if (ctx.Request.HttpMethod == "GET")
                {
                    LastSinceSeq = ctx.Request.QueryString["sinceSeq"];
                    var since = long.Parse(LastSinceSeq!);
                    var arr = new JsonArray();
                    foreach (var o in Ops.Where(o => o["serverSeq"]!.GetValue<long>() > since))
                        arr.Add(o.DeepClone());
                    reply = new JsonObject
                    {
                        ["ops"] = arr, ["hasMore"] = false, ["latestSeq"] = Ops.Count,
                        ["snapshotVectorClock"] = SnapshotClock is null ? null : JsonNode.Parse(SnapshotClock),
                    };
                }
                else
                {
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
                    var op = body["ops"]![0]!.AsObject();
                    var ok = Reject is null;
                    if (ok)
                    {
                        Uploaded.Add((JsonObject)op.DeepClone());
                        Ops.Add(new JsonObject { ["serverSeq"] = (long)Ops.Count + 1, ["op"] = op.DeepClone() });
                    }
                    reply = new JsonObject
                    {
                        ["results"] = new JsonArray(new JsonObject
                        {
                            ["opId"] = op["id"]!.GetValue<string>(), ["accepted"] = ok, ["errorCode"] = Reject,
                        }),
                        ["latestSeq"] = Ops.Count,
                    };
                }
                var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(bytes);
                res.Close();
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }
}
