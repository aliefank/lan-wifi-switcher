using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LanWifiSwitcher;

/// <summary>
/// Builds small tray icons that visually distinguish LAN vs Wi‑Fi mode.
/// </summary>
public static class IconFactory
{
    public static Icon CreateForMode(ConnectionMode mode)
    {
        return mode switch
        {
            ConnectionMode.Lan => CreateIcon(Color.FromArgb(30, 120, 200), "LAN", drawLanGlyph: true),
            ConnectionMode.Wifi => CreateIcon(Color.FromArgb(30, 150, 90), "WIFI", drawLanGlyph: false),
            ConnectionMode.Both => CreateIcon(Color.FromArgb(180, 120, 30), "BOTH", drawLanGlyph: true),
            ConnectionMode.Neither => CreateIcon(Color.FromArgb(140, 140, 140), "OFF", drawLanGlyph: false),
            _ => CreateIcon(Color.FromArgb(100, 100, 100), "?", drawLanGlyph: false)
        };
    }

    private static Icon CreateIcon(Color accent, string label, bool drawLanGlyph)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(accent))
            {
                g.FillEllipse(bg, 1, 1, size - 3, size - 3);
            }

            using (var border = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f))
            {
                g.DrawEllipse(border, 1, 1, size - 3, size - 3);
            }

            if (drawLanGlyph)
            {
                // Simple RJ45-ish rectangle.
                using var pen = new Pen(Color.White, 2f);
                g.DrawRectangle(pen, 10, 9, 12, 10);
                g.DrawLine(pen, 12, 21, 12, 24);
                g.DrawLine(pen, 16, 21, 16, 24);
                g.DrawLine(pen, 20, 21, 20, 24);
            }
            else
            {
                // Wi‑Fi arcs.
                using var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, 8, 8, 16, 16, 220, 100);
                g.DrawArc(pen, 11, 11, 10, 10, 220, 100);
                g.FillEllipse(Brushes.White, 14, 18, 4, 4);
            }

            // Tiny label hint is skipped at 16px; glyph alone is enough for tray.
            _ = label;
        }

        // Icon.FromHandle requires the HICON to stay alive; clone via stream for a disposable Icon.
        using var ms = new MemoryStream();
        // Convert bitmap to icon manually.
        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
