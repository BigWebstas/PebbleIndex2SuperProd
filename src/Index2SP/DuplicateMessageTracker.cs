namespace Index2SP;

/// <summary>
/// Remembers webhook message ids so the same message is only acted on once — a Pebble retry
/// after a slow tunnel would otherwise create the task twice. An id is claimed while its
/// webhook is processing, kept after it succeeds, and released if it fails so a retry can run.
/// Completed ids are forgotten after <see cref="Retention"/> to keep memory bounded.
/// </summary>
public sealed class DuplicateMessageTracker
{
    public enum Claim { New, InProgress, AlreadyProcessed }

    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _completed = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    public DuplicateMessageTracker(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Claims <paramref name="id"/> for processing. Only <see cref="Claim.New"/> means
    /// the caller owns it and must later call <see cref="Complete"/>.</summary>
    public Claim TryClaim(string id)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_inProgress.Contains(id)) return Claim.InProgress;
            if (_completed.ContainsKey(id)) return Claim.AlreadyProcessed;
            _inProgress.Add(id);
            return Claim.New;
        }
    }

    /// <summary>Ends processing of a claimed id: remembered on success, released on failure.</summary>
    public void Complete(string id, bool succeeded)
    {
        lock (_gate)
        {
            _inProgress.Remove(id);
            if (succeeded) _completed[id] = _now();
        }
    }

    private void PruneExpired()
    {
        var cutoff = _now() - Retention;
        foreach (var (id, at) in _completed)
            if (at < cutoff) _completed.Remove(id);
    }
}
