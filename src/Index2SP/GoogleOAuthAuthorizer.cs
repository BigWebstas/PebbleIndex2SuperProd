using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// One-time interactive Google sign-in for the "Desktop app" OAuth flow (RFC 8252): opens the
/// system browser to Google's consent screen, catches the redirect on a loopback listener bound
/// to an OS-assigned free port, and exchanges the returned code for a refresh token. Only the
/// refresh token is kept — access tokens are re-derived from it per call by
/// <see cref="GoogleCalendarClient"/>.
/// </summary>
public static class GoogleOAuthAuthorizer
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    // calendar.events alone can create/read events but returns 403 on GET calendarList (used
    // for the tray's "Default calendar" picker) — add the narrow read-only calendarlist scope
    // rather than widen to the full calendar scope, which would also grant deleting calendars.
    private const string Scope = "https://www.googleapis.com/auth/calendar.events " +
                                  "https://www.googleapis.com/auth/calendar.calendarlist.readonly";

    public sealed record AuthorizeResult(string RefreshToken);

    public static async Task<AuthorizeResult> AuthorizeAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new GoogleCalendarApiException("Set a client ID and client secret first.");

        var redirectUri = $"http://127.0.0.1:{GetFreeLoopbackPort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        try
        {
            var state = Guid.NewGuid().ToString("N");
            var authUrl = $"{AuthEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
                           $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                           "&response_type=code" +
                           $"&scope={Uri.EscapeDataString(Scope)}" +
                           "&access_type=offline&prompt=consent" +
                           $"&state={state}";

            OpenBrowser(authUrl);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
            var contextTask = listener.GetContextAsync();
            var delayTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            var completed = await Task.WhenAny(contextTask, delayTask);
            if (completed != contextTask)
                throw new GoogleCalendarApiException("Timed out waiting for the Google sign-in to complete.");

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            var returnedState = query["state"];
            var error = query["error"];

            RespondToBrowser(context, success: error is null && code is not null);

            if (error is not null)
                throw new GoogleCalendarApiException($"Google sign-in was denied or failed: {error}");
            if (code is null || returnedState != state)
                throw new GoogleCalendarApiException("Google sign-in response was invalid — try again.");

            return await ExchangeCodeAsync(clientId, clientSecret, code, redirectUri, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<AuthorizeResult> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
    {
        using var http = new HttpClient();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };

        using var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleCalendarApiException(
                $"Google token exchange failed (HTTP {(int)resp.StatusCode}): {(body.Length > 300 ? body[..300] + "…" : body)}");

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        if (!json.TryGetProperty("refresh_token", out var rtEl) || rtEl.GetString() is not { Length: > 0 } refreshToken)
            throw new GoogleCalendarApiException(
                "Google didn't return a refresh token. It only issues one on first consent — revoke Index2SP's " +
                "access at myaccount.google.com/permissions, then try Connect… again.");

        return new AuthorizeResult(refreshToken);
    }

    private static void RespondToBrowser(HttpListenerContext context, bool success)
    {
        var html = success
            ? "<html><body style=\"font-family:sans-serif\">Signed in — you can close this tab and return to Index2SP.</body></html>"
            : "<html><body style=\"font-family:sans-serif\">Sign-in failed — you can close this tab and return to Index2SP.</body></html>";
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
    }

    private static int GetFreeLoopbackPort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private static void OpenBrowser(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", new[] { url });
        else
            Process.Start("xdg-open", new[] { url });
    }
}
