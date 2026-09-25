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

    [Fact]
    public void VerifyHmacSignature_ValidSha256Prefix_ReturnsTrue()
    {
        var secret = "super-secret-key-123";
        var body = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var hex = ComputeHmacSha256Hex(secret, body);

        var headers = new HeaderDictionary
        {
            ["X-Signature-SHA256"] = $"sha256={hex}"
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, body, secret);
        Assert.True(valid);
    }

    [Fact]
    public void VerifyHmacSignature_ValidHubSignatureHeader_ReturnsTrue()
    {
        var secret = "hub-secret-xyz";
        var body = Encoding.UTF8.GetBytes("sample webhook body bytes");
        var hex = ComputeHmacSha256Hex(secret, body);

        var headers = new HeaderDictionary
        {
            ["X-Hub-Signature-256"] = $"sha256={hex}"
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, body, secret);
        Assert.True(valid);
    }

    [Fact]
    public void VerifyHmacSignature_BareHexWithoutPrefix_ReturnsTrue()
    {
        var secret = "bare-secret-456";
        var body = Encoding.UTF8.GetBytes("simple body");
        var hex = ComputeHmacSha256Hex(secret, body);

        var headers = new HeaderDictionary
        {
            ["X-Signature"] = hex
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, body, secret);
        Assert.True(valid);
    }

    [Fact]
    public void VerifyHmacSignature_CaseInsensitiveHex_ReturnsTrue()
    {
        var secret = "case-test";
        var body = Encoding.UTF8.GetBytes("case sensitive data");
        var hexUpper = ComputeHmacSha256Hex(secret, body).ToUpperInvariant();

        var headers = new HeaderDictionary
        {
            ["X-Index-Signature"] = $"sha256={hexUpper}"
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, body, secret);
        Assert.True(valid);
    }

    [Fact]
    public void VerifyHmacSignature_TamperedBody_ReturnsFalse()
    {
        var secret = "tamper-proof";
        var originalBody = Encoding.UTF8.GetBytes("legitimate content");
        var tamperedBody = Encoding.UTF8.GetBytes("tampered content");
        var hex = ComputeHmacSha256Hex(secret, originalBody);

        var headers = new HeaderDictionary
        {
            ["X-Signature-SHA256"] = $"sha256={hex}"
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, tamperedBody, secret);
        Assert.False(valid);
    }

    [Fact]
    public void VerifyHmacSignature_WrongSecret_ReturnsFalse()
    {
        var realSecret = "real-key";
        var attackerSecret = "attacker-key";
        var body = Encoding.UTF8.GetBytes("some payload");
        var hex = ComputeHmacSha256Hex(attackerSecret, body);

        var headers = new HeaderDictionary
        {
            ["X-Signature-SHA256"] = $"sha256={hex}"
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, body, realSecret);
        Assert.False(valid);
    }

    [Fact]
    public void VerifyHmacSignature_MissingSignatureHeader_ReturnsFalse()
    {
        var secret = "secret-key";
        var body = Encoding.UTF8.GetBytes("some payload");
        var headers = new HeaderDictionary();

        var valid = WebhookServer.VerifyHmacSignature(headers, body, secret);
        Assert.False(valid);
    }

    [Fact]
    public void VerifyHmacSignature_MalformedHex_ReturnsFalse()
    {
        var secret = "secret-key";
        var body = Encoding.UTF8.GetBytes("some payload");
        var headers = new HeaderDictionary
        {
            ["X-Signature-SHA256"] = "sha256=not_a_valid_hex_string!!!"
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, body, secret);
        Assert.False(valid);
    }

    [Fact]
    public void VerifyHmacSignature_EmptySecret_ReturnsFalse()
    {
        var body = Encoding.UTF8.GetBytes("some payload");
        var headers = new HeaderDictionary
        {
            ["X-Signature-SHA256"] = "sha256=abcdef"
        };

        var valid = WebhookServer.VerifyHmacSignature(headers, body, "");
        Assert.False(valid);
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

    [Fact]
    public void IsAuthorized_WithHmacSecret_AcceptsValidSignature()
    {
        var config = new AppConfig { HmacSecret = "test-secret" };
        var server = new WebhookServer(config, new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("{\"memo\":\"buy milk\"}");
        var hex = ComputeHmacSha256Hex("test-secret", body);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Signature-SHA256"] = $"sha256={hex}";

        Assert.True(server.IsAuthorized(ctx.Request, body));
    }

    [Fact]
    public void IsAuthorized_WithHmacSecret_RejectsMissingOrBadSignature()
    {
        var config = new AppConfig { HmacSecret = "test-secret" };
        var server = new WebhookServer(config, new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("{\"memo\":\"buy milk\"}");

        var ctxMissing = new DefaultHttpContext();
        Assert.False(server.IsAuthorized(ctxMissing.Request, body));

        var ctxBad = new DefaultHttpContext();
        ctxBad.Request.Headers["X-Signature-SHA256"] = "sha256=00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        Assert.False(server.IsAuthorized(ctxBad.Request, body));
    }

    [Fact]
    public void IsAuthorized_WithInboundAuthTokenFallback_AcceptsHmacMatchingToken()
    {
        var config = new AppConfig { InboundAuthToken = "token-as-hmac-key", HmacSecret = "" };
        var server = new WebhookServer(config, new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("payload data");
        var hex = ComputeHmacSha256Hex("token-as-hmac-key", body);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Hub-Signature-256"] = $"sha256={hex}";

        Assert.True(server.IsAuthorized(ctx.Request, body));
    }

    [Fact]
    public void IsAuthorized_WithBothConfigured_RequiresBothValid()
    {
        var config = new AppConfig { InboundAuthToken = "bearer-token", HmacSecret = "hmac-key" };
        var server = new WebhookServer(config, new Logger(), null!, null!, null!);
        var body = Encoding.UTF8.GetBytes("secure payload");
        var hex = ComputeHmacSha256Hex("hmac-key", body);

        // Missing bearer token
        var ctxNoBearer = new DefaultHttpContext();
        ctxNoBearer.Request.Headers["X-Signature-SHA256"] = $"sha256={hex}";
        Assert.False(server.IsAuthorized(ctxNoBearer.Request, body));

        // Missing HMAC
        var ctxNoHmac = new DefaultHttpContext();
        ctxNoHmac.Request.Headers["Authorization"] = "Bearer bearer-token";
        Assert.False(server.IsAuthorized(ctxNoHmac.Request, body));

        // Both provided and valid
        var ctxBoth = new DefaultHttpContext();
        ctxBoth.Request.Headers["Authorization"] = "Bearer bearer-token";
        ctxBoth.Request.Headers["X-Signature-SHA256"] = $"sha256={hex}";
        Assert.True(server.IsAuthorized(ctxBoth.Request, body));
    }
}
