using System.Collections.Concurrent;
using System.Text;

namespace Index2SP;

public enum LogLevel { Info, Warn, Error }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{Level.ToString().ToUpperInvariant()}] {Message}";
}

/// <summary>
/// Thread-safe logger: appends to a daily file under %APPDATA%\Index2SP\logs and keeps the
/// last N entries in memory for the tray "View log" window.
/// </summary>
public sealed class Logger
{
    private const int MemoryCapacity = 500;

    private readonly string _logDir;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly object _fileLock = new();

    public event Action<LogEntry>? EntryAdded;

    public Logger()
    {
        _logDir = Path.Combine(AppConfig.ConfigDirectory, "logs");
        Directory.CreateDirectory(_logDir);
    }

    public string LogDirectory => _logDir;

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex}");

    public IReadOnlyList<LogEntry> Snapshot() => _recent.ToArray();

    private void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);

        _recent.Enqueue(entry);
        while (_recent.Count > MemoryCapacity && _recent.TryDequeue(out _)) { }

        try
        {
            var file = Path.Combine(_logDir, $"index2sp-{DateTimeOffset.Now:yyyy-MM-dd}.log");
            lock (_fileLock)
            {
                File.AppendAllText(file, entry + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never throw into the request path
        }

        EntryAdded?.Invoke(entry);
    }
}
