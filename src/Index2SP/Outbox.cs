using System.Text.Json;

namespace Index2SP;

/// <summary>
/// A disk-backed queue of tasks that could not be delivered to Super Productivity when their
/// webhook arrived (SP not running, REST API off, transient error). Each item is a JSON file
/// under <c>%APPDATA%\Index2SP\outbox\</c>; <see cref="FlushAsync"/> retries them oldest-first
/// and deletes each file once its task is created. Items the API permanently rejects — or that
/// exceed the attempt cap — are moved to <c>outbox\failed\</c> and left for inspection.
/// </summary>
public sealed class Outbox
{
    private readonly string _dir;
    private readonly string _failedDir;
    private readonly Logger _log;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Raised on the flushing thread after a queued task is finally created.</summary>
    public event Action<string, string?>? ItemDelivered;   // (title, taskId)
    /// <summary>Raised on the flushing thread when an item is given up on (moved to failed\).</summary>
    public event Action<string, string>? ItemFailed;       // (title, error)

    public Outbox(string configDir, Logger log)
    {
        _dir = Path.Combine(configDir, "outbox");
        _failedDir = Path.Combine(_dir, "failed");
        _log = log;
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { _log.Error("Could not create the outbox directory", ex); }
    }

    /// <summary>Number of tasks currently waiting to be delivered.</summary>
    public int PendingCount
    {
        get
        {
            try { return Directory.EnumerateFiles(_dir, "*.json").Count(); }
            catch { return 0; }
        }
    }

    /// <summary>Persist a task for later delivery. Safe to call from any thread; never throws.</summary>
    public void Enqueue(SpTaskRequest task)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var now = DateTimeOffset.UtcNow;
            var item = new OutboxItem
            {
                Id = $"{now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}",
                EnqueuedAt = now,
                Task = task,
            };
            var path = Path.Combine(_dir, item.Id + ".json");
            WriteAtomic(path, item);
            _log.Warn($"Queued task for retry (Super Productivity unavailable): \"{task.Title}\"");
        }
        catch (Exception ex)
        {
            _log.Error($"Could not queue task \"{task.Title}\" — it is lost", ex);
        }
    }

    /// <summary>
    /// Try to deliver every pending item, oldest first. Stops the pass at the first transient
    /// failure (Super Productivity is probably down — the rest would fail too) and returns; the
    /// caller re-invokes on its retry timer. Overlapping calls are ignored.
    /// </summary>
    public async Task FlushAsync(
        AppConfig.SuperProductivityConfig spConfig,
        CaptureTagResolver captureTag,
        int maxAttempts,
        CancellationToken ct = default)
    {
        if (!await _flushGate.WaitAsync(0, ct)) return;
        try
        {
            string[] files;
            try { files = Directory.GetFiles(_dir, "*.json"); }
            catch { return; }
            if (files.Length == 0) return;
            Array.Sort(files, StringComparer.Ordinal);

            using var sp = new SuperProductivityClient(spConfig);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                OutboxItem? item = null;
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    item = JsonSerializer.Deserialize<OutboxItem>(json, JsonOpts);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Error($"Outbox file {Path.GetFileName(file)} is unreadable — moving to failed\\", ex);
                    MoveToFailed(file);
                    continue;
                }

                if (item is null || item.Task is null || string.IsNullOrWhiteSpace(item.Task.Title))
                {
                    _log.Error($"Outbox file {Path.GetFileName(file)} has no task — moving to failed\\");
                    MoveToFailed(file);
                    continue;
                }

                try
                {
                    // CancellationToken.None: a flaky tunnel disconnecting the original caller must
                    // not abort a delivery that would otherwise succeed. The HTTP client's own
                    // 15 s timeout bounds the call.
                    await captureTag.ApplyAsync(sp, item.Task, CancellationToken.None);
                    var result = await sp.CreateTaskAsync(item.Task, CancellationToken.None);
                    TryDelete(file);
                    _log.Info($"Delivered queued task{(result.TaskId is null ? "" : $" {result.TaskId}")}: " +
                              $"\"{item.Task.Title}\" (attempt {item.Attempts + 1})");
                    ItemDelivered?.Invoke(item.Task.Title, result.TaskId);
                }
                catch (SpApiException ex) when (ex.Permanent)
                {
                    _log.Error($"Super Productivity rejected queued task \"{item.Task.Title}\" — moving to failed\\: {ex.Message}");
                    RecordAttempt(item, ex);
                    PersistQuietly(file, item);
                    MoveToFailed(file);
                    ItemFailed?.Invoke(item.Task.Title, ex.Message);
                }
                catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
                {
                    RecordAttempt(item, ex);

                    if (maxAttempts > 0 && item.Attempts >= maxAttempts)
                    {
                        _log.Error($"Queued task \"{item.Task.Title}\" still failing after {item.Attempts} " +
                                   $"attempts — moving to failed\\: {ex.Message}");
                        PersistQuietly(file, item);
                        MoveToFailed(file);
                        ItemFailed?.Invoke(item.Task.Title, ex.Message);
                        continue;
                    }

                    PersistQuietly(file, item);
                    _log.Warn($"Queued task \"{item.Task.Title}\" retry {item.Attempts} failed, " +
                              $"will retry later: {ex.Message}");
                    break;
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private static void RecordAttempt(OutboxItem item, Exception ex)
    {
        item.Attempts++;
        item.LastAttemptAt = DateTimeOffset.UtcNow;
        item.LastError = ex.Message;
    }

    private static void WriteAtomic(string path, OutboxItem item)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Best-effort rewrite of an item's state — a failure here just means a stale
    /// attempt count on disk, not a lost task.</summary>
    private void PersistQuietly(string path, OutboxItem item)
    {
        try { WriteAtomic(path, item); }
        catch (Exception ex) { _log.Warn($"Could not update outbox file {Path.GetFileName(path)}: {ex.Message}"); }
    }

    private void MoveToFailed(string file)
    {
        try
        {
            Directory.CreateDirectory(_failedDir);
            File.Move(file, Path.Combine(_failedDir, Path.GetFileName(file)), overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not move {Path.GetFileName(file)} to failed\\ — deleting instead", ex);
            TryDelete(file);
        }
    }

    private void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) { _log.Warn($"Could not delete outbox file {Path.GetFileName(file)}: {ex.Message}"); }
    }
}
