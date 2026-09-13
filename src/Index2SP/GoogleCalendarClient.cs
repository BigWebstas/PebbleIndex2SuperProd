using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Thin client for the Google Calendar API v3. Used only as an additional, best-effort
/// destination for transcriptions the AI classifier marks as a dated/timed event — it never
/// replaces the Super Productivity task. Every call refreshes an access token from the stored
/// refresh token first; Google access tokens are short-lived (~1h) and this client is always
/// constructed fresh per use, so there's nothing worth caching.
/// </summary>
public sealed class GoogleCalendarClient : IDisposable
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ApiBase = "https://www.googleapis.com/calendar/v3/";

    private readonly HttpClient _http;
    private readonly AppConfig.GoogleCalendarConfig _config;

    public GoogleCalendarClient(AppConfig.GoogleCalendarConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.RefreshToken))
            throw new GoogleCalendarApiException("Not connected — run Google Calendar → Connect… from the tray first.");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["client_secret"] = _config.ClientSecret,
            ["refresh_token"] = _config.RefreshToken,
            ["grant_type"] = "refresh_token",
        };

        using var resp = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleCalendarApiException($"Google token refresh failed (HTTP {(int)resp.StatusCode}): {Truncate(body)}");

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return json.TryGetProperty("access_token", out var tok) && tok.GetString() is { Length: > 0 } token
            ? token
            : throw new GoogleCalendarApiException("Google token refresh response had no access_token.");
    }

    /// <summary>Calendars the signed-in account can see, for the tray's "Default calendar" picker.</summary>
    public async Task<IReadOnlyList<SpNamedItem>> GetCalendarsAsync(CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "users/me/calendarList");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new GoogleCalendarApiException($"Google Calendar returned HTTP {(int)resp.StatusCode} on GET calendarList.");

        var root = JsonSerializer.Deserialize<JsonElement>(body);
        var list = new List<SpNamedItem>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in items.EnumerateArray())
            {
                if (!el.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
                var summary = el.TryGetProperty("summary", out var sEl) ? sEl.GetString() : null;
                list.Add(new SpNamedItem(id, string.IsNullOrWhiteSpace(summary) ? id : summary!));
            }
        }
        return list;
    }

    /// <summary>Creates one event. Returns the new event's id.</summary>
    public async Task<string?> CreateEventAsync(
        string summary, string? description, DateTimeOffset start, DateTimeOffset? end, bool allDay, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var calendarId = string.IsNullOrWhiteSpace(_config.CalendarId) ? "primary" : _config.CalendarId.Trim();
        var effectiveEnd = end ?? start.AddHours(1);

        object startObj, endObj;
        if (allDay)
        {
            // All-day events use a date-only field, and Google's convention is an exclusive end
            // date — a one-day event's end date is the day after its start date.
            startObj = new { date = start.ToString("yyyy-MM-dd") };
            endObj = new { date = (end ?? start.AddDays(1)).ToString("yyyy-MM-dd") };
        }
        else
        {
            startObj = new { dateTime = start.ToString("yyyy-MM-ddTHH:mm:sszzz") };
            endObj = new { dateTime = effectiveEnd.ToString("yyyy-MM-ddTHH:mm:sszzz") };
        }

        var payload = new { summary, description, start = startObj, end = endObj };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}calendars/{Uri.EscapeDataString(calendarId)}/events")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new GoogleCalendarApiException($"Google Calendar returned HTTP {(int)resp.StatusCode}: {Truncate(body)}");

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return json.TryGetProperty("id", out var idEl2) ? idEl2.GetString() : null;
    }

    /// <summary>Refreshes a token, then confirms the configured calendar is reachable.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var calendarId = string.IsNullOrWhiteSpace(_config.CalendarId) ? "primary" : _config.CalendarId.Trim();

        // Calendars.get (GET /calendars/{id}) needs the full calendar/calendar.readonly scope —
        // ours is calendar.events + calendar.calendarlist.readonly, which only covers the
        // CalendarList resource. Use CalendarList.get instead; same validation, no extra scope.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}users/me/calendarList/{Uri.EscapeDataString(calendarId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleCalendarApiException($"Reached Google but GET calendar \"{calendarId}\" returned HTTP {(int)resp.StatusCode}.");

        return $"OK — connected, calendar \"{calendarId}\" accessible";
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose() => _http.Dispose();
}

public sealed class GoogleCalendarApiException(string message) : Exception(message);
