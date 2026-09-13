using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Index2SP;

/// <summary>
/// A single-field secret-entry prompt for the tray menu (Anthropic API key, Joplin auth token).
/// Never pre-fills the existing value — OK with a blank field means "keep what's already set".
/// </summary>
public partial class InputDialog : Window
{
    private readonly TaskCompletionSource<string?> _result = new();

    public InputDialog() : this("", "") { }

    public InputDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;

        OkBtn.Click += (_, _) => Finish(ValueBox.Text);
        CancelBtn.Click += (_, _) => Finish(null);
        ValueBox.KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Closed += (_, _) => _result.TrySetResult(null);
        Opened += (_, _) => ValueBox.Focus();
    }

    private void Finish(string? value)
    {
        _result.TrySetResult(value);
        Close();
    }

    /// <summary>Shows the dialog and returns the entered text, or null if cancelled/closed.
    /// An empty string is returned as-is — the caller decides what blank means.</summary>
    public static Task<string?> ShowAsync(string title, string prompt)
    {
        var dialog = new InputDialog(title, prompt);
        Dispatcher.UIThread.Post(() =>
        {
            dialog.Show();
            dialog.Activate();
        });
        return dialog._result.Task;
    }
}
