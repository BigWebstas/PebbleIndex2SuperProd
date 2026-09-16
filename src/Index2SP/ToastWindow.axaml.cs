using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Index2SP;

/// <summary>
/// A minimal transient corner toast, used when a native notification daemon isn't available
/// (mainly Windows; on Linux <c>notify-send</c> is preferred).
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;

    private readonly bool _autoDismiss;

    public ToastWindow() : this("Index2SP", string.Empty, NotifyKind.Info, null, null) { }

    public ToastWindow(string title, string body, NotifyKind kind, string? actionLabel = null, Action? action = null,
        bool autoDismiss = true)
    {
        _autoDismiss = autoDismiss;
        InitializeComponent();

        TitleText.Text = title;
        BodyText.Text = body;
        Accent.Fill = kind switch
        {
            NotifyKind.Error => new SolidColorBrush(Color.FromRgb(0xE3, 0x4B, 0x2F)),
            NotifyKind.Warning => new SolidColorBrush(Color.FromRgb(0xE3, 0x6A, 0x17)),
            _ => new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
        };

        if (actionLabel is not null && action is not null)
        {
            // Plain Border, not a Button — FluentTheme's Button pointerover style targets a
            // template part directly and was overriding our explicit colors, making the button
            // seem to vanish on hover. A Border has no theme states to fight with.
            ActionButtonText.Text = actionLabel;
            ActionButton.IsVisible = true;
            var normal = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB));
            var hover = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFF));
            ActionButton.Background = normal;
            ActionButton.PointerEntered += (_, _) => ActionButton.Background = hover;
            ActionButton.PointerExited += (_, _) => ActionButton.Background = normal;
            // Handled here (not via a Click event) and marked Handled so the window's own
            // dismiss-on-click below never sees this press.
            ActionButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                action();
                SafeClose();
            };
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += (_, _) => SafeClose();

        // Pausing on hover matters most for an actionable toast — 6s isn't always enough time to
        // read it, move the mouse, and click before it would otherwise auto-dismiss underneath you.
        if (_autoDismiss)
        {
            PointerEntered += (_, _) => _timer.Stop();
            PointerExited += (_, _) => _timer.Start();
        }

        PointerPressed += (_, _) => SafeClose();
        Opened += OnOpened;
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
    }

    public static void Show(string title, string body, NotifyKind kind, string? actionLabel = null, Action? action = null)
    {
        _current?.SafeClose();
        var toast = new ToastWindow(title, body, kind, actionLabel, action);
        _current = toast;
        toast.Show();
    }

    /// <summary>Shows a toast that stays open until <see cref="Dismiss"/> is called — no 6s
    /// timer — so the caller can keep it up to date with <see cref="UpdateBody"/> across a
    /// long-running operation (e.g. a download's progress).</summary>
    public static ToastWindow ShowPersistent(string title, string body, NotifyKind kind)
    {
        _current?.SafeClose();
        var toast = new ToastWindow(title, body, kind, autoDismiss: false);
        _current = toast;
        toast.Show();
        return toast;
    }

    public void UpdateBody(string body) => BodyText.Text = body;

    public void Dismiss() => SafeClose();

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_autoDismiss) _timer.Start();
        try
        {
            var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen is null) return;

            var area = screen.WorkingArea;                 // physical pixels
            var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            var w = (int)(Bounds.Width * scale);
            var h = (int)(Bounds.Height * scale);
            var margin = (int)(16 * scale);
            Position = new PixelPoint(
                area.X + area.Width - w - margin,
                area.Y + area.Height - h - margin);
        }
        catch
        {
            // leave at the platform default position
        }
    }

    private void SafeClose()
    {
        _timer.Stop();
        try { Close(); } catch { /* already closing */ }
    }
}
