using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace Index2SP;

public partial class LogWindow : Window
{
    private readonly Logger _log;

    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24));

    // Entries arriving in a burst (several log lines in the same tick, e.g. an AI classify
    // call) are coalesced into one batch of inline runs instead of one relayout per line —
    // OnEntryAdded can fire from any thread, so this buffer is lock-protected.
    private readonly object _pendingLock = new();
    private readonly List<LogEntry> _pending = new();
    private bool _flushScheduled;

    // Backs "Copy all" — SelectableTextBlock.Text reads back empty once Inlines are used
    // directly, so the plain text is kept separately instead of reassembling it from the runs.
    private readonly StringBuilder _allText = new();

    // Parameterless ctor for the Avalonia designer only.
    public LogWindow() : this(new Logger()) { }

    public LogWindow(Logger log)
    {
        InitializeComponent();
        _log = log;

        CopyBtn.Click += async (_, _) =>
        {
            if (Clipboard is not null) await Clipboard.SetTextAsync(_allText.ToString());
        };
        ClearBtn.Click += (_, _) =>
        {
            Box.Inlines?.Clear();
            _allText.Clear();
        };

        AppendEntries(_log.Snapshot());

        _log.EntryAdded += OnEntryAdded;
        Closed += (_, _) => _log.EntryAdded -= OnEntryAdded;
        Opened += (_, _) => ScrollToEnd();
    }

    private void OnEntryAdded(LogEntry entry)
    {
        lock (_pendingLock)
        {
            _pending.Add(entry);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        Dispatcher.UIThread.Post(FlushPending);
    }

    private void FlushPending()
    {
        List<LogEntry> batch;
        lock (_pendingLock)
        {
            batch = new List<LogEntry>(_pending);
            _pending.Clear();
            _flushScheduled = false;
        }
        if (batch.Count == 0) return;

        AppendEntries(batch);
        ScrollToEnd();
    }

    /// <summary>Colors each line by level (error/warn) instead of one flat color, so problems
    /// stand out while scanning — Info stays the theme's normal text color.</summary>
    private void AppendEntries(IEnumerable<LogEntry> entries)
    {
        var inlines = Box.Inlines ??= new InlineCollection();
        foreach (var entry in entries)
        {
            if (inlines.Count > 0) inlines.Add(new LineBreak());

            var run = new Run(entry.ToString());
            // Only set Foreground for error/warn — an explicit null local value can paint with no
            // brush at all instead of falling back to the inherited theme color, so Info leaves
            // the property untouched rather than assigning null to it.
            if (BrushForLevel(entry.Level) is { } brush) run.Foreground = brush;
            inlines.Add(run);

            _allText.Append(entry).Append(Environment.NewLine);
        }
    }

    private static IBrush? BrushForLevel(LogLevel level) => level switch
    {
        LogLevel.Error => ErrorBrush,
        LogLevel.Warn => WarnBrush,
        _ => null, // inherit the control's normal foreground
    };

    private void ScrollToEnd() => Scroll.ScrollToEnd();
}
