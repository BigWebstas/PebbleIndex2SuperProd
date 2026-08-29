using System.Text;
using System.Windows.Forms;

namespace Index2SP;

/// <summary>Simple read-only viewer for the in-memory log ring buffer.</summary>
public sealed class LogForm : Form
{
    private readonly Logger _log;
    private readonly TextBox _box;

    public LogForm(Logger log)
    {
        _log = log;

        Text = "Index2SP — Log";
        Width = 820;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimizeBox = true;

        _box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Consolas", 9f),
            BackColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            ForeColor = System.Drawing.Color.Gainsboro,
        };

        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft };
        var copy = new Button { Text = "Copy all", AutoSize = true };
        copy.Click += (_, _) => { try { Clipboard.SetText(_box.Text); } catch { /* ignore */ } };
        var clear = new Button { Text = "Clear view", AutoSize = true };
        clear.Click += (_, _) => _box.Clear();
        panel.Controls.Add(copy);
        panel.Controls.Add(clear);

        Controls.Add(_box);
        Controls.Add(panel);

        Load += (_, _) => Reload();
        _log.EntryAdded += OnEntryAdded;
        FormClosed += (_, _) => _log.EntryAdded -= OnEntryAdded;
    }

    private void Reload()
    {
        var sb = new StringBuilder();
        foreach (var e in _log.Snapshot())
            sb.AppendLine(e.ToString());
        _box.Text = sb.ToString();
        ScrollToEnd();
    }

    private void OnEntryAdded(LogEntry entry)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                _box.AppendText(entry + Environment.NewLine);
                ScrollToEnd();
            });
        }
        catch (InvalidOperationException) { /* handle went away */ }
    }

    private void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
    }
}
