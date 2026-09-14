using System.Net.Http.Headers;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Checks GitHub's latest release for this repo. Never downloads or installs anything — just
/// tells the caller whether a newer tag exists and where to get it.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/BigWebstas/PebbleIndex2SuperProd/releases/latest";

    private readonly HttpClient _http;

    public UpdateChecker()
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Index2SP", AppInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public sealed record UpdateInfo(string Version, string Url);

    /// <summary>Null when already on the latest release (or ahead of it, e.g. a local build).</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ReleasesUrl, ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpdateCheckException($"GitHub returned HTTP {(int)resp.StatusCode} checking for updates.");

        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(body); }
        catch (JsonException ex) { throw new UpdateCheckException($"Could not parse GitHub's response: {ex.Message}"); }

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)) return null;

        var latest = tag.TrimStart('v', 'V');
        if (!Version.TryParse(latest, out var latestVersion)) return null;
        if (!Version.TryParse(AppInfo.Version, out var currentVersion)) return null;

        return latestVersion > currentVersion ? new UpdateInfo(latest, url) : null;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class UpdateCheckException(string message) : Exception(message);
