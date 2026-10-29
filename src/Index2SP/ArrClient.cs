using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Index2SP;

/// <summary>
/// Thin client for Sonarr's and Radarr's v3 REST APIs, which share the same shape (lookup →
/// add the looked-up object back with a root folder and quality profile). Used only as an
/// additional, best-effort destination for transcriptions the AI classifier reads as "add this
/// show / movie" — it never replaces the Super Productivity task.
/// </summary>
public sealed class ArrClient : IDisposable
{
    public enum Kind { Series, Movie }

    private readonly HttpClient _http;
    private readonly AppConfig.ArrConfig _config;
    private readonly Kind _kind;

    public ArrClient(AppConfig.ArrConfig config, Kind kind)
    {
        _config = config;
        _kind = kind;
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(config.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
        _http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
    }

    private string Name => _kind == Kind.Series ? "Sonarr" : "Radarr";
    private string Resource => _kind == Kind.Series ? "series" : "movie";

    public sealed record AddResult(string Title, int? Year, bool AlreadyInLibrary);

    /// <summary>Looks <paramref name="term"/> up and adds the best (first) match, monitored and
    /// searched for right away. A title already in the library is reported, not re-added.</summary>
    public async Task<AddResult?> AddAsync(string term, CancellationToken ct = default)
    {
        var results = await GetJsonAsync($"api/v3/{Resource}/lookup?term={Uri.EscapeDataString(term)}", ct);
        if (results is not JsonArray { Count: > 0 } array || array[0] is not JsonObject match) return null;

        var title = match["title"]?.GetValue<string>() ?? term;
        var year = match["year"]?.GetValue<int>() is > 0 and var y ? y : (int?)null;
        if (match["id"]?.GetValue<int>() is > 0) return new AddResult(title, year, true);

        var rootFolder = !string.IsNullOrWhiteSpace(_config.RootFolderPath)
            ? _config.RootFolderPath.Trim()
            : (await GetJsonAsync("api/v3/rootfolder", ct))?[0]?["path"]?.GetValue<string>()
              ?? throw new ArrApiException($"{Name} has no root folder configured.");
        var profileId = _config.QualityProfileId > 0
            ? _config.QualityProfileId
            : (await GetJsonAsync("api/v3/qualityprofile", ct))?[0]?["id"]?.GetValue<int>()
              ?? throw new ArrApiException($"{Name} has no quality profile configured.");

        match["rootFolderPath"] = rootFolder;
        match["qualityProfileId"] = profileId;
        match["monitored"] = true;
        match["addOptions"] = _kind == Kind.Series
            ? new JsonObject { ["searchForMissingEpisodes"] = true }
            : new JsonObject { ["searchForMovie"] = true };

        using var resp = await _http.PostAsJsonAsync($"api/v3/{Resource}", match, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ArrApiException($"{Name} returned HTTP {(int)resp.StatusCode} adding \"{title}\": " +
                                      Truncate(await resp.Content.ReadAsStringAsync(ct)));
        return new AddResult(title, year, false);
    }

    /// <summary>Probes the API key via the system status endpoint.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await GetJsonAsync("api/v3/system/status", ct);
            return $"OK — {Name} {status?["version"]?.GetValue<string>()} reachable at {_config.BaseUrl}";
        }
        catch (HttpRequestException)
        {
            throw new ArrApiException($"Cannot reach {Name} at {_config.BaseUrl}. Is it running?");
        }
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new ArrApiException($"{Name} rejected the API key (Settings → General → API Key).");
        if (!resp.IsSuccessStatusCode)
            throw new ArrApiException($"{Name} returned HTTP {(int)resp.StatusCode} on GET /{path}.");
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private static string Truncate(string s, int n = 300) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}

public sealed class ArrApiException(string message) : Exception(message);
