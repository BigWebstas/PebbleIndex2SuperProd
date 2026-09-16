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
public sealed class Logger : IDisposable
{
    private const int MemoryCapacity = 500;

    private readonly string _logDir;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly object _fileLock = new();

    // Kept open across writes instead of opening/closing the file on every Info/Warn/Error call;
    // only reopened when the day rolls over. AutoFlush means each line still reaches the OS right
    // away, so this doesn't trade away the "logging survives a crash" property of AppendAllText.
    private StreamWriter? _fileWriter;
    private string? _fileWriterDate;

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
            var date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            lock (_fileLock)
            {
                if (_fileWriter is null || _fileWriterDate != date)
                {
                    _fileWriter?.Dispose();
                    var file = Path.Combine(_logDir, $"index2sp-{date}.log");
                    _fileWriter = new StreamWriter(file, append: true, Encoding.UTF8) { AutoFlush = true };
                    _fileWriterDate = date;
                }
                _fileWriter.WriteLine(entry.ToString());
            }
        }
        catch
        {
            // logging must never throw into the request path
        }

        EntryAdded?.Invoke(entry);
    }

    public void Dispose()
    {
        lock (_fileLock) { _fileWriter?.Dispose(); _fileWriter = null; }
    }
}
