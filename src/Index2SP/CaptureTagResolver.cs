namespace Index2SP;

/// <summary>
/// Resolves the configured capture tag — given directly as an id, or by name via
/// <c>GET /tags</c> — and applies it to a task. The name→id lookup is cached after the first
/// success. Shared by the live webhook path and the retry outbox so both tag their tasks
/// the same way.
/// </summary>
public sealed class CaptureTagResolver
{
    private readonly AppConfig.SuperProductivityConfig _config;
    private readonly Logger _log;
    private string? _resolvedId;

    public CaptureTagResolver(AppConfig.SuperProductivityConfig config, Logger log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>Adds the configured capture tag to <paramref name="task"/> if one is configured
    /// and can be resolved. Never throws — a lookup failure just leaves the tag off.</summary>
    public async Task ApplyAsync(SuperProductivityClient sp, SpTaskRequest task, CancellationToken ct = default)
    {
        var tagId = await ResolveAsync(sp, ct);
        if (tagId is null) return;

        task.TagIds ??= new List<string>();
        if (!task.TagIds.Contains(tagId))
            task.TagIds.Add(tagId);
    }

    private async Task<string?> ResolveAsync(SuperProductivityClient sp, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.CaptureTagId))
            return _config.CaptureTagId.Trim();

        if (string.IsNullOrWhiteSpace(_config.CaptureTagName))
            return null;

        if (_resolvedId is not null)
            return _resolvedId;

        try
        {
            var name = _config.CaptureTagName.Trim();
            var tags = await sp.GetTagsAsync(ct);
            var match = tags.FirstOrDefault(t => string.Equals(t.Title, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _log.Warn($"captureTagName \"{name}\" not found in Super Productivity — task will not carry that tag");
                return null;
            }

            _log.Info($"Resolved captureTagName \"{name}\" -> {match.Id}");
            return _resolvedId = match.Id;
        }
        catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Could not resolve captureTagName: {ex.Message}");
            return null;
        }
    }
}
