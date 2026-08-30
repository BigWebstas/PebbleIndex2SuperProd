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

    public ToastWindow() : this("Index2SP", string.Empty, NotifyKind.Info) { }

    public ToastWindow(string title, string body, NotifyKind kind)
    {
        InitializeComponent();

        TitleText.Text = title;
        BodyText.Text = body;
        Accent.Fill = kind switch
        {
            NotifyKind.Error => new SolidColorBrush(Color.FromRgb(0xE3, 0x4B, 0x2F)),
            NotifyKind.Warning => new SolidColorBrush(Color.FromRgb(0xE3, 0x6A, 0x17)),
            _ => new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += (_, _) => SafeClose();

        PointerPressed += (_, _) => SafeClose();
        Opened += OnOpened;
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
    }

    public static void Show(string title, string body, NotifyKind kind)
    {
        _current?.SafeClose();
        var toast = new ToastWindow(title, body, kind);
        _current = toast;
        toast.Show();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _timer.Start();
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
