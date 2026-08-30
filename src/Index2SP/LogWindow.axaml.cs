using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Index2SP;

public partial class LogWindow : Window
{
    private readonly Logger _log;

    // Parameterless ctor for the Avalonia designer only.
    public LogWindow() : this(new Logger()) { }

    public LogWindow(Logger log)
    {
        InitializeComponent();
        _log = log;

        CopyBtn.Click += async (_, _) =>
        {
            if (Clipboard is not null) await Clipboard.SetTextAsync(Box.Text ?? string.Empty);
        };
        ClearBtn.Click += (_, _) => Box.Text = string.Empty;

        var sb = new StringBuilder();
        foreach (var entry in _log.Snapshot())
            sb.AppendLine(entry.ToString());
        Box.Text = sb.ToString();

        _log.EntryAdded += OnEntryAdded;
        Closed += (_, _) => _log.EntryAdded -= OnEntryAdded;
        Opened += (_, _) => ScrollToEnd();
    }

    private void OnEntryAdded(LogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Box.Text += entry + Environment.NewLine;
            ScrollToEnd();
        });
    }

    private void ScrollToEnd() => Box.CaretIndex = Box.Text?.Length ?? 0;
}
