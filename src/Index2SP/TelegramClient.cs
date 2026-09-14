using System.Net.Http.Json;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Thin client for the Telegram Bot API. Used only as a fixed-destination notifier — web-search
/// summaries and webhook receipts — never for the general "send a message to X" feature, which
/// stays on Beeper since it needs recipient search across whatever chats already exist.
/// </summary>
public sealed class TelegramClient : IDisposable
{
    private readonly HttpClient _http;

    public TelegramClient(AppConfig.TelegramConfig config)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"https://api.telegram.org/bot{config.BotToken}/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
    }

    public async Task SendMessageAsync(string chatId, string text, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("sendMessage", new { chat_id = chatId, text }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new TelegramApiException($"Telegram returned HTTP {(int)resp.StatusCode}: {Truncate(body)}");

        var root = JsonSerializer.Deserialize<JsonElement>(body);
        if (!(root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True))
        {
            var desc = root.TryGetProperty("description", out var d) ? d.GetString() : "unknown error";
            throw new TelegramApiException($"Telegram rejected the message: {desc}");
        }
    }

    /// <summary>Probes the bot token via getMe.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync("getMe", ct);
        }
        catch (HttpRequestException)
        {
            throw new TelegramApiException("Cannot reach api.telegram.org.");
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new TelegramApiException($"Telegram rejected the bot token (HTTP {(int)resp.StatusCode} on getMe).");

            var root = JsonSerializer.Deserialize<JsonElement>(body);
            var username = root.TryGetProperty("result", out var r) && r.TryGetProperty("username", out var u)
                ? u.GetString()
                : null;
            return $"OK — Telegram bot reachable{(string.IsNullOrEmpty(username) ? "" : $" (@{username})")}";
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose() => _http.Dispose();
}

public sealed class TelegramApiException(string message) : Exception(message);
