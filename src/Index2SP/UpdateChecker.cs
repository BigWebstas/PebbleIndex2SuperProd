using System.Net.Http.Headers;
using System.Text.Json;

namespace Index2SP;

/// <summary>
/// Checks GitHub's latest release for this repo, and can fetch the build for this platform to a
/// local file. Never installs anything itself — that's still a manual step (the Windows
/// installer's own click-through, or running install.sh on Linux); this only saves the trip to a
/// browser to find the right asset.
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

    /// <param name="AssetUrl">Direct download URL for this platform's self-contained build, or
    /// null when the release has no matching asset (e.g. running on an unsupported OS).</param>
    public sealed record UpdateInfo(string Version, string Url, string? AssetUrl, string? AssetFileName);

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
        if (latestVersion <= currentVersion) return null;

        var (assetUrl, assetName) = PickAsset(root);
        return new UpdateInfo(latest, url, assetUrl, assetName);
    }

    /// <summary>Picks the self-contained build for the running OS — self-contained rather than
    /// the framework-dependent ("-fd-") variant, since it works regardless of what runtimes are
    /// already on the machine downloading it.</summary>
    private static (string? Url, string? Name) PickAsset(JsonElement releaseRoot)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        var prefix = OperatingSystem.IsWindows() ? "Index2SP-Setup-"
            : OperatingSystem.IsLinux() ? "Index2SP-linux-x64-"
            : null;
        if (prefix is null) return (null, null);

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is not { Length: > 0 } || url is not { Length: > 0 }) continue;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("-fd-", StringComparison.OrdinalIgnoreCase)) continue; // framework-dependent — skip
            return (url, name);
        }
        return (null, null);
    }

    /// <summary>Downloads this platform's build to a temp file and returns its path. Uses its own
    /// HttpClient (redirects allowed, no timeout cap) since release assets are tens of MB and
    /// served from a redirecting CDN URL — unlike every other call this class makes.</summary>
    public async Task<string> DownloadAssetAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (info.AssetUrl is null || info.AssetFileName is null)
            throw new UpdateCheckException("No downloadable build found for this platform in the release.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Index2SP", AppInfo.Version));

        var dest = Path.Combine(Path.GetTempPath(), info.AssetFileName);
        using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpdateCheckException($"Download failed: HTTP {(int)resp.StatusCode}.");

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst, ct);

        return dest;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class UpdateCheckException(string message) : Exception(message);
