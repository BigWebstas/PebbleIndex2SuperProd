using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Index2SP.Tests;

public class WebhookHmacTests
{
    private static byte[] ComputeHmacSha256(string secret, byte[] body)
    {
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
    }

    private static string ComputeHmacSha256Hex(string secret, byte[] body)
    {
        return Convert.ToHexString(ComputeHmacSha256(secret, body)).ToLowerInvariant();
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static HeaderDictionary SignedHeaders(string secret, byte[] body, long? timestamp = null,
        string delivery = "d-1", string trigger = "single-click-hold", bool isTest = false, string version = "1")
    {
        var ts = (timestamp ?? Now.ToUnixTimeSeconds()).ToString();
        var prefix = Encoding.UTF8.GetBytes($"v{version}\n{ts}\n{delivery}\n{trigger}\n{(isTest ? "1" : "0")}\n");
        var sig = Convert.ToHexString(ComputeHmacSha256(secret, [.. prefix, .. body])).ToLowerInvariant();
        var headers = new HeaderDictionary
        {
            ["X-Index-Webhook-Version"] = version,
            ["X-Index-Timestamp"] = ts,
            ["X-Index-Delivery"] = delivery,
            ["X-Index-Trigger"] = trigger,
            ["X-Index-Signature"] = sig
        };
        if (isTest) headers["X-Index-Test"] = "true";
        return headers;
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var body = Encoding.UTF8.GetBytes("multipart body");
        Assert.True(WebhookServer.VerifyHmacSignature(SignedHeaders("s3cret", body), body, "s3cret", Now));
    }

    [Fact]
    public void Verify_TestEvent_ReturnsTrue()
    {
        var body = Encoding.UTF8.GetBytes("multipart body");
        var headers = SignedHeaders("s3cret", body, trigger: "test-event", isTest: true);
        Assert.True(WebhookServer.VerifyHmacSignature(headers, body, "s3cret", Now));
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsFalse()
    {
        var headers = SignedHeaders("s3cret", Encoding.UTF8.GetBytes("original"));
        Assert.False(WebhookServer.VerifyHmacSignature(headers, Encoding.UTF8.GetBytes("tampered"), "s3cret", Now));
    }

    [Fact]
    public void Verify_WrongSecretOrEmptySecret_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("b");
        var headers = SignedHeaders("s3cret", body);
        Assert.False(WebhookServer.VerifyHmacSignature(headers, body, "other", Now));
        Assert.False(WebhookServer.VerifyHmacSignature(headers, body, "", Now));
    }

    [Fact]
    public void Verify_ChangedSignedHeader_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("b");
        var headers = SignedHeaders("s3cret", body);
        headers["X-Index-Trigger"] = "double-click-hold";
        Assert.False(WebhookServer.VerifyHmacSignature(headers, body, "s3cret", Now));
    }

    [Fact]
    public void Verify_StaleOrMalformedTimestamp_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("b");
        var stale = SignedHeaders("s3cret", body, timestamp: Now.ToUnixTimeSeconds() - 301);
        Assert.False(WebhookServer.VerifyHmacSignature(stale, body, "s3cret", Now));

        var bad = SignedHeaders("s3cret", body);
        bad["X-Index-Timestamp"] = "yesterday";
        Assert.False(WebhookServer.VerifyHmacSignature(bad, body, "s3cret", Now));
    }

    [Fact]
    public void Verify_UnsupportedVersionOrMissingSignature_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("b");
        Assert.False(WebhookServer.VerifyHmacSignature(SignedHeaders("s3cret", body, version: "2"), body, "s3cret", Now));
        Assert.False(WebhookServer.VerifyHmacSignature(new HeaderDictionary(), body, "s3cret", Now));
    }

    [Fact]
    public void PopulateMissingDefaults_PopulatesHmacSecret_WhenMissingOnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("index2sp-hmac-test-").FullName;
        try
        {
            var configPath = Path.Combine(dir, "config.json");
            File.WriteAllText(configPath, """
            {
                "listenAddress": "127.0.0.1",
                "port": 8787,
                "webhookPath": "/pebble"
            }
            """);

            var updated = AppConfig.PopulateMissingDefaults(configPath);
            Assert.True(updated);

            var savedJson = File.ReadAllText(configPath);
            var node = JsonNode.Parse(savedJson)?.AsObject();
            Assert.NotNull(node);
            Assert.True(node.ContainsKey("hmacSecret"));
            Assert.Equal("", node["hmacSecret"]?.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static HttpRequest SignedRequest(string secret, byte[] body, string delivery)
    {
        var ctx = new DefaultHttpContext();
        foreach (var h in SignedHeaders(secret, body, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), delivery))
            ctx.Request.Headers[h.Key] = h.Value;
        return ctx.Request;
    }

    [Fact]
    public void IsAuthorized_WithHmacSecret_AcceptsValidSignatureOnceThenRejectsReplay()
    {
        var server = new WebhookServer(new AppConfig { HmacSecret = "hmac-key" }, new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.True(server.IsAuthorized(SignedRequest("hmac-key", body, "d-1"), body));
        Assert.False(server.IsAuthorized(SignedRequest("hmac-key", body, "d-1"), body));
        Assert.True(server.IsAuthorized(SignedRequest("hmac-key", body, "d-2"), body));
    }

    [Fact]
    public void IsAuthorized_WithHmacSecret_RejectsMissingOrBadSignature()
    {
        var server = new WebhookServer(new AppConfig { HmacSecret = "hmac-key" }, new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.False(server.IsAuthorized(new DefaultHttpContext().Request, body));
        Assert.False(server.IsAuthorized(SignedRequest("wrong", body, "d-1"), body));
    }

    [Fact]
    public void IsAuthorized_WithInboundAuthTokenFallback_AcceptsHmacMatchingToken()
    {
        var server = new WebhookServer(new AppConfig { InboundAuthToken = "token-as-key" }, new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.True(server.IsAuthorized(SignedRequest("token-as-key", body, "d-1"), body));
    }

    [Fact]
    public void IsAuthorized_WithBothConfigured_RequiresBothValid()
    {
        var server = new WebhookServer(new AppConfig { InboundAuthToken = "bearer-token", HmacSecret = "hmac-key" },
            new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("secure payload");

        var noBearer = SignedRequest("hmac-key", body, "d-1");
        Assert.False(server.IsAuthorized(noBearer, body));

        var noHmac = new DefaultHttpContext();
        noHmac.Request.Headers["Authorization"] = "Bearer bearer-token";
        Assert.False(server.IsAuthorized(noHmac.Request, body));

        var both = SignedRequest("hmac-key", body, "d-2");
        both.Headers["Authorization"] = "Bearer bearer-token";
        Assert.True(server.IsAuthorized(both, body));
    }
}
