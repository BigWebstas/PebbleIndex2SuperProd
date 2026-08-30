using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Index2SP;

/// <summary>Result of the background Super Productivity health check.</summary>
public enum SpHealth { Unknown, Ok, Unreachable }

/// <summary>
/// Renders the tray icon at runtime with Skia (works on every platform Avalonia supports),
/// so the project ships without binary icon assets.
/// </summary>
internal static class IconRenderer
{
    public static WindowIcon Tray(bool listening, SpHealth health)
    {
        var ring = listening
            ? Color.FromRgb(0x2E, 0xA0, 0x43)   // green — listener running
            : Color.FromRgb(0x8A, 0x8A, 0x8A);  // grey — stopped

        var dot = !listening
            ? Color.FromRgb(0x5A, 0x5A, 0x5A)
            : health switch
            {
                SpHealth.Ok => Color.FromRgb(0x1F, 0x6F, 0xEB),          // blue — SP reachable
                SpHealth.Unreachable => Color.FromRgb(0xE3, 0x6A, 0x17), // orange — SP down
                _ => Color.FromRgb(0x9A, 0x9A, 0x9A),                    // grey — not checked
            };

        using var rtb = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var center = new Point(16, 16);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(ring), 4), center, 12, 12);
            ctx.DrawEllipse(new SolidColorBrush(dot), null, center, 4.5, 4.5);
        }

        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
