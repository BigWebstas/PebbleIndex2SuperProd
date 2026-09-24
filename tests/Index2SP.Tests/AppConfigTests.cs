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
    public void Normalize_ClampsWhisperLiveTimeoutSeconds(int input, int expected)
    {
        File.WriteAllText(ConfigPath, $$"""{ "whisperLive": { "timeoutSeconds": {{input}} } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal(expected, config.WhisperLive.TimeoutSeconds);
    }

    [Fact]
    public void Normalize_DefaultsBlankWhisperLiveEndpoint()
    {
        File.WriteAllText(ConfigPath, """{ "whisperLive": { "host": "", "port": 0 } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.Equal("127.0.0.1", config.WhisperLive.Host);
        Assert.Equal(9090, config.WhisperLive.Port);
        Assert.Equal("ws://127.0.0.1:9090", config.WhisperLive.BaseUrl);
    }

    [Fact]
    public void Normalize_MigratesLegacyWhisperConfig()
    {
        File.WriteAllText(ConfigPath, """{ "whisper": { "enabled": true, "baseUrl": "tcp://192.168.1.50:10300", "model": "tiny", "timeoutSeconds": 45 } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.WhisperLive.Enabled);
        Assert.Equal("192.168.1.50", config.WhisperLive.Host);
        Assert.Equal(10300, config.WhisperLive.Port);
        Assert.Equal("tiny", config.WhisperLive.Model);
        Assert.Equal(45, config.WhisperLive.TimeoutSeconds);
    }

    [Fact]
    public void Normalize_MigratesLegacyWyomingConfig()
    {
        File.WriteAllText(ConfigPath, """{ "wyoming": { "enabled": true, "host": "192.168.1.60", "port": 10300, "model": "base", "timeoutSeconds": 40 } }""");

        var config = AppConfig.LoadOrCreate(ConfigPath);

        Assert.True(config.WhisperLive.Enabled);
        Assert.Equal("192.168.1.60", config.WhisperLive.Host);
        Assert.Equal(10300, config.WhisperLive.Port);
        Assert.Equal("base", config.WhisperLive.Model);
        Assert.Equal(40, config.WhisperLive.TimeoutSeconds);
    }

    [Fact]
    public void ParseEndpoint_HandlesVariousFormats()
    {
        Assert.Equal(("127.0.0.1", 9090), AppConfig.ParseEndpoint(""));
        Assert.Equal(("192.168.1.10", 9090), AppConfig.ParseEndpoint("192.168.1.10"));
        Assert.Equal(("192.168.1.10", 10400), AppConfig.ParseEndpoint("192.168.1.10:10400"));
        Assert.Equal(("10.0.0.1", 9090), AppConfig.ParseEndpoint("ws://10.0.0.1:9090"));
        Assert.Equal(("localhost", 9090), AppConfig.ParseEndpoint("http://localhost:9090/"));
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
}
