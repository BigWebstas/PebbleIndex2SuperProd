using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Index2SP.Tests;

public class AppConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("index2sp-tests-").FullName;
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void LoadOrCreate_WritesDefaultsWhenFileMissing()
    {
        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(File.Exists(ConfigPath));
        Assert.Equal("/pebble", config.WebhookPath);
        Assert.Equal(8787, config.Port);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var original = AppConfig.LoadOrCreate(ConfigPath);
        original.Port = 9999;
        original.SuperProductivity.ProjectId = "proj-1";
        original.AiClassifier.Enabled = true;
        original.AiClassifier.ApiKey = "sk-test";
        original.Joplin.NotebookId = "notebook-1";
        original.Save(ConfigPath);

        var reloaded = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(9999, reloaded.Port);
        Assert.Equal("proj-1", reloaded.SuperProductivity.ProjectId);
        Assert.True(reloaded.AiClassifier.Enabled);
        Assert.Equal("sk-test", reloaded.AiClassifier.ApiKey);
        Assert.Equal("notebook-1", reloaded.Joplin.NotebookId);
    }

    [Theory]
    [InlineData("pebble", "/pebble")]
    [InlineData("/pebble/", "/pebble")]
    [InlineData("", "/pebble")]
    [InlineData("   ", "/pebble")]
    public void Normalize_FixesUpWebhookPath(string input, string expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "webhookPath": "{{input}}" }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.WebhookPath);
    }

    [Theory]
    [InlineData(5, 10)]   // below min clamps up
    [InlineData(9999, 3600)] // above max clamps down
    public void Normalize_ClampsOutboxRetrySeconds(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "outboxRetrySeconds": {{input}} }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.OutboxRetrySeconds);
    }

    [Fact]
    public void Normalize_NegativeOutboxMaxAttemptsBecomesZero()
    {
        File.WriteAllText(ConfigPath, """{ "outboxMaxAttempts": -5 }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(0, config.OutboxMaxAttempts);
    }

    [Theory]
    [InlineData(0, 0)]      // 0 disables the timer, left alone
    [InlineData(1, 15)]     // non-zero clamps up to the 15s floor
    [InlineData(99999, 3600)]
    public void Normalize_ClampsHealthCheckSeconds(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "healthCheckSeconds": {{input}} }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.HealthCheckSeconds);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(999, 20)]
    public void Normalize_ClampsOutageFailureThreshold(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "outageFailureThreshold": {{input}} }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.OutageFailureThreshold);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(999, 30)]
    public void Normalize_ClampsAiClassifierTimeoutSeconds(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "aiClassifier": { "timeoutSeconds": {{input}} } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.AiClassifier.TimeoutSeconds);
    }

    [Fact]
    public void Normalize_TrimsTrailingSlashFromBaseUrls()
    {
        File.WriteAllText(ConfigPath, """
        {
          "superProductivity": { "baseUrl": "http://127.0.0.1:3876/" },
          "joplin": { "baseUrl": "http://127.0.0.1:41184/" }
        }
        """);

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal("http://127.0.0.1:3876", config.SuperProductivity.BaseUrl);
        Assert.Equal("http://127.0.0.1:41184", config.Joplin.BaseUrl);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(999, 120)]
    public void Normalize_ClampsWhisperAsrTimeoutSeconds(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "whisperAsr": { "timeoutSeconds": {{input}} } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.WhisperAsr.TimeoutSeconds);
    }

    [Fact]
    public void Normalize_DefaultsBlankWhisperAsrEndpoint()
    {
        File.WriteAllText(ConfigPath, """{ "whisperAsr": { "baseUrl": "" } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal("http://127.0.0.1:9000", config.WhisperAsr.BaseUrl);
        Assert.Equal("127.0.0.1", config.WhisperAsr.Host);
        Assert.Equal(9000, config.WhisperAsr.Port);
    }

    [Fact]
    public void Normalize_MigratesLegacyWhisperLiveConfig()
    {
        File.WriteAllText(ConfigPath, """{ "whisperLive": { "enabled": true, "host": "192.168.1.70", "port": 9090, "timeoutSeconds": 25 } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.WhisperAsr.Enabled);
        Assert.Equal("http://192.168.1.70:9090", config.WhisperAsr.BaseUrl);
        Assert.Equal("192.168.1.70", config.WhisperAsr.Host);
        Assert.Equal(9090, config.WhisperAsr.Port);
        Assert.Equal(25, config.WhisperAsr.TimeoutSeconds);
    }

    [Fact]
    public void Normalize_MigratesLegacyWhisperConfig()
    {
        File.WriteAllText(ConfigPath, """{ "whisper": { "enabled": true, "baseUrl": "http://192.168.1.50:9000", "language": "es", "timeoutSeconds": 45 } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.WhisperAsr.Enabled);
        Assert.Equal("http://192.168.1.50:9000", config.WhisperAsr.BaseUrl);
        Assert.Equal("192.168.1.50", config.WhisperAsr.Host);
        Assert.Equal(9000, config.WhisperAsr.Port);
        Assert.Equal("es", config.WhisperAsr.Language);
        Assert.Equal(45, config.WhisperAsr.TimeoutSeconds);
    }

    [Fact]
    public void Normalize_MigratesLegacyWyomingConfig()
    {
        File.WriteAllText(ConfigPath, """{ "wyoming": { "enabled": true, "host": "192.168.1.60", "port": 10300, "timeoutSeconds": 40 } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.WhisperAsr.Enabled);
        Assert.Equal("http://192.168.1.60:10300", config.WhisperAsr.BaseUrl);
        Assert.Equal("192.168.1.60", config.WhisperAsr.Host);
        Assert.Equal(10300, config.WhisperAsr.Port);
        Assert.Equal(40, config.WhisperAsr.TimeoutSeconds);
    }

    [Fact]
    public void ParseEndpoint_HandlesVariousFormats()
    {
        Assert.Equal(("127.0.0.1", 9000), AppConfig.ParseEndpoint(""));
        Assert.Equal(("192.168.1.10", 9000), AppConfig.ParseEndpoint("192.168.1.10"));
        Assert.Equal(("192.168.1.10", 10400), AppConfig.ParseEndpoint("192.168.1.10:10400"));
        Assert.Equal(("10.0.0.1", 9000), AppConfig.ParseEndpoint("http://10.0.0.1:9000"));
        Assert.Equal(("localhost", 9000), AppConfig.ParseEndpoint("http://localhost:9000/"));
    }

    [Fact]
    public void LoadOrCreate_CheckForUpdatesDefaultsToTrue()
    {
        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.CheckForUpdates);
    }

    [Theory]
    [InlineData("GEMINI", "gemini")]
    [InlineData("openai", "openai")]
    [InlineData("not-a-real-provider", "")]
    [InlineData("", "")]
    public void Normalize_FixesUpAiFallbackProvider(string input, string expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "aiClassifier": { "fallbackProvider": "{{input}}" } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.AiClassifier.FallbackProvider);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(999, 10)]
    public void Normalize_ClampsWebSearchMaxUses(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "webSearch": { "maxUses": {{input}} } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.WebSearch.MaxUses);
    }

    [Theory]
    [InlineData(0, 0)]    // 0 sends immediately, left alone
    [InlineData(-5, 0)]
    [InlineData(999, 60)]
    public void Normalize_ClampsWebSearchSendDelaySeconds(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "webSearch": { "sendDelaySeconds": {{input}} } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.WebSearch.SendDelaySeconds);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(999, 30)]
    public void Normalize_ClampsTelegramTimeoutSeconds(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "telegram": { "timeoutSeconds": {{input}} } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.Telegram.TimeoutSeconds);
    }

    [Fact]
    public void SaveThenLoad_KeepsWebSearchAndWebhookReceiptChatIdsIndependent()
    {
        var original = AppConfig.LoadOrCreate(ConfigPath);
        original.Telegram.Enabled = true;
        original.Telegram.BotToken = "123:ABC";
        original.WebSearch.Enabled = true;
        original.WebSearch.ChatId = "-1001";
        original.WebhookReceipt.Enabled = true;
        original.WebhookReceipt.ChatId = "-1002";
        original.Save(ConfigPath);

        var reloaded = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(reloaded.Telegram.Enabled);
        Assert.Equal("123:ABC", reloaded.Telegram.BotToken);
        Assert.Equal("-1001", reloaded.WebSearch.ChatId);
        Assert.Equal("-1002", reloaded.WebhookReceipt.ChatId);
        Assert.NotEqual(reloaded.WebSearch.ChatId, reloaded.WebhookReceipt.ChatId);
    }

    [Fact]
    public void LoadOrCreate_PopulatesMissingDefaultsOnDisk_PreservesExistingValues()
    {
        // Existing user config with only a few custom settings
        File.WriteAllText(ConfigPath, """
        {
          "listenAddress": "0.0.0.0",
          "port": 9000,
          "superProductivity": {
            "baseUrl": "http://192.168.1.50:3876",
            "tagIds": ["tagA", "tagB"]
          }
        }
        """);

        var config = AppConfig.LoadOrCreate(ConfigPath);

        // In-memory properties
        Assert.Equal("0.0.0.0", config.ListenAddress);
        Assert.Equal(9000, config.Port);
        Assert.Equal("http://192.168.1.50:3876", config.SuperProductivity.BaseUrl);
        Assert.Equal(new[] { "tagA", "tagB" }, config.SuperProductivity.TagIds);
        Assert.Equal("", config.SuperProductivity.ShoppingProjectId);

        // Verify the file ON DISK was backfilled with missing properties
        var rawJson = File.ReadAllText(ConfigPath);
        var diskNode = JsonNode.Parse(rawJson) as JsonObject;
        Assert.NotNull(diskNode);

        // Preserved user settings
        Assert.Equal("0.0.0.0", diskNode["listenAddress"]?.GetValue<string>());
        Assert.Equal(9000, diskNode["port"]?.GetValue<int>());
        Assert.Equal("http://192.168.1.50:3876", diskNode["superProductivity"]?["baseUrl"]?.GetValue<string>());
        var tags = diskNode["superProductivity"]?["tagIds"]?.AsArray();
        Assert.NotNull(tags);
        Assert.Equal(2, tags.Count);
        Assert.Equal("tagA", tags[0]?.GetValue<string>());

        // Missing sub-property populated
        Assert.Equal("", diskNode["superProductivity"]?["shoppingProjectId"]?.GetValue<string>());

        // Missing top-level sections populated
        Assert.NotNull(diskNode["whisperAsr"]);
        Assert.False(diskNode["whisperAsr"]?["enabled"]?.GetValue<bool>());
        Assert.Equal("auto", diskNode["whisperAsr"]?["format"]?.GetValue<string>());
        Assert.NotNull(diskNode["aiClassifier"]);
        Assert.NotNull(diskNode["joplin"]);
        Assert.NotNull(diskNode["googleCalendar"]);
        Assert.NotNull(diskNode["beeper"]);
        Assert.NotNull(diskNode["webSearch"]);
        Assert.NotNull(diskNode["telegram"]);
    }

    [Fact]
    public void LoadOrCreate_PopulatesMissingNestedOptionsInExistingWhisperAsr()
    {
        File.WriteAllText(ConfigPath, """
        {
          "whisperAsr": {
            "enabled": true,
            "baseUrl": "http://192.168.1.140:5092",
            "apiKey": "AIzaSyALBXU0p"
          }
        }
        """);

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.WhisperAsr.Enabled);
        Assert.Equal("http://192.168.1.140:5092", config.WhisperAsr.BaseUrl);
        Assert.Equal("AIzaSyALBXU0p", config.WhisperAsr.ApiKey);

        // Verify disk file
        var rawJson = File.ReadAllText(ConfigPath);
        var diskNode = JsonNode.Parse(rawJson) as JsonObject;
        Assert.NotNull(diskNode);
        var whisperObj = diskNode["whisperAsr"]?.AsObject();
        Assert.NotNull(whisperObj);

        Assert.True(whisperObj["enabled"]?.GetValue<bool>());
        Assert.Equal("http://192.168.1.140:5092", whisperObj["baseUrl"]?.GetValue<string>());
        Assert.Equal("AIzaSyALBXU0p", whisperObj["apiKey"]?.GetValue<string>());
        Assert.Equal("remote", whisperObj["mode"]?.GetValue<string>());
        Assert.Equal("auto", whisperObj["format"]?.GetValue<string>());
        Assert.Equal("base.en", whisperObj["model"]?.GetValue<string>());
        Assert.Equal(30, whisperObj["timeoutSeconds"]?.GetValue<int>());
    }

    [Fact]
    public void LoadOrCreate_PrunesObsoleteAliasesFromDisk()
    {
        File.WriteAllText(ConfigPath, """
        {
          "whisperLive": {
            "enabled": true,
            "host": "192.168.1.70",
            "port": 9090
          },
          "audioOnlyFallback": true
        }
        """);

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.WhisperAsr.Enabled);
        Assert.Equal("http://192.168.1.70:9090", config.WhisperAsr.BaseUrl);

        var rawJson = File.ReadAllText(ConfigPath);
        var diskNode = JsonNode.Parse(rawJson) as JsonObject;
        Assert.NotNull(diskNode);

        // Obsolete aliases removed from disk
        Assert.Null(diskNode["whisperLive"]);
        Assert.Null(diskNode["audioOnlyFallback"]);

        // Migrated whisperAsr present on disk
        Assert.NotNull(diskNode["whisperAsr"]);
        Assert.True(diskNode["whisperAsr"]?["enabled"]?.GetValue<bool>());
        Assert.Equal("http://192.168.1.70:9090", diskNode["whisperAsr"]?["baseUrl"]?.GetValue<string>());
        Assert.Null(diskNode["whisperAsr"]?["host"]);
        Assert.Null(diskNode["whisperAsr"]?["port"]);
    }

    [Fact]
    public void PopulateMissingDefaults_DoesNotRewriteFileWhenAlreadyUpToDate()
    {
        // First call writes full default config
        AppConfig.LoadOrCreate(ConfigPath);
        var writeTimeFirst = File.GetLastWriteTimeUtc(ConfigPath);

        // Subsequent call with already-complete config should return false and leave file untouched
        var modified = AppConfig.PopulateMissingDefaults(ConfigPath);
        Assert.False(modified);
        Assert.Equal(writeTimeFirst, File.GetLastWriteTimeUtc(ConfigPath));
    }
}
