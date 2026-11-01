using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LanWifiSwitcher;

/// <summary>
/// Builds crisp, high-contrast tray icons for LAN vs Wi‑Fi (readable at 16×16 / 32×32).
/// Windows 11–style flat glyphs: solid disc + simple white symbol, no tiny text.
/// </summary>
public static class IconFactory
{
    // Fluent-inspired accents that stay distinct on light and dark taskbars.
    private static readonly Color LanAccent = Color.FromArgb(255, 0, 103, 192);      // Windows blue
    private static readonly Color WifiAccent = Color.FromArgb(255, 16, 137, 62);     // green
    private static readonly Color BothAccent = Color.FromArgb(255, 196, 89, 0);      // amber
    private static readonly Color OffAccent = Color.FromArgb(255, 96, 96, 96);
    private static readonly Color UnknownAccent = Color.FromArgb(255, 110, 110, 110);

    public static Icon CreateForMode(ConnectionMode mode)
    {
        return mode switch
        {
            ConnectionMode.Lan => CreateIcon(LanAccent, DrawLanGlyph),
            ConnectionMode.Wifi => CreateIcon(WifiAccent, DrawWifiGlyph),
            ConnectionMode.Both => CreateIcon(BothAccent, DrawBothGlyph),
            ConnectionMode.Neither => CreateIcon(OffAccent, DrawOffGlyph),
            _ => CreateIcon(UnknownAccent, DrawUnknownGlyph)
        };
    }

    private static Icon CreateIcon(Color accent, Action<Graphics, int> drawGlyph)
    {
        // Draw at 32×32; NotifyIcon scales for 16×16 tray. Thick strokes keep edges clear.
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Soft dark ring so the disc reads on light taskbars; fill carries brand color.
            using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
            {
                g.FillEllipse(shadow, 1, 2, size - 3, size - 3);
            }

            using (var bg = new SolidBrush(accent))
            {
                g.FillEllipse(bg, 1, 1, size - 3, size - 3);
            }

            // Thin light rim for contrast on dark taskbars / Windows 11 acrylic.
            using (var rim = new Pen(Color.FromArgb(220, 255, 255, 255), 1.6f))
            {
                g.DrawEllipse(rim, 1.5f, 1.5f, size - 4f, size - 4f);
            }

            drawGlyph(g, size);
        }

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

    /// <summary>Ethernet: RJ45-style plug (rectangle + contact teeth).</summary>
    private static void DrawLanGlyph(Graphics g, int size)
    {
        using var pen = new Pen(Color.White, 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var fill = new SolidBrush(Color.White);

        // Body
        var body = new RectangleF(9.5f, 8f, 13f, 11f);
        g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);

        // Clip / latch notch on top
        g.FillRectangle(fill, 13.5f, 6.5f, 5f, 2.2f);

        // Contact teeth
        for (var i = 0; i < 4; i++)
        {
            var x = 11.5f + i * 2.8f;
            g.DrawLine(pen, x, 20.5f, x, 24.5f);
        }
    }

    /// <summary>Wi‑Fi: three arcs + center dot (classic signal glyph).</summary>
    private static void DrawWifiGlyph(Graphics g, int size)
    {
        using var pen = new Pen(Color.White, 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var fill = new SolidBrush(Color.White);

        // Outer → inner arcs, open downward toward the dot.
        g.DrawArc(pen, 6.5f, 7f, 19f, 19f, 220, 100);
        g.DrawArc(pen, 9.5f, 10f, 13f, 13f, 220, 100);
        g.DrawArc(pen, 12.5f, 13f, 7f, 7f, 220, 100);
        g.FillEllipse(fill, 14.5f, 20.5f, 3.5f, 3.5f);
    }

    /// <summary>Both: small LAN block + mini Wi‑Fi arc.</summary>
    private static void DrawBothGlyph(Graphics g, int size)
    {
        using var pen = new Pen(Color.White, 2.1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var fill = new SolidBrush(Color.White);

        g.DrawRectangle(pen, 7f, 11f, 8f, 7f);
        g.DrawLine(pen, 8.5f, 19f, 8.5f, 22f);
        g.DrawLine(pen, 11f, 19f, 11f, 22f);
        g.DrawLine(pen, 13.5f, 19f, 13.5f, 22f);

        g.DrawArc(pen, 16f, 8f, 10f, 10f, 220, 100);
        g.DrawArc(pen, 18.5f, 10.5f, 5f, 5f, 220, 100);
        g.FillEllipse(fill, 20f, 15.5f, 2.4f, 2.4f);
    }

    private static void DrawOffGlyph(Graphics g, int size)
    {
        using var pen = new Pen(Color.White, 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawEllipse(pen, 9f, 9f, 14f, 14f);
        g.DrawLine(pen, 12f, 12f, 20f, 20f);
    }

    private static void DrawUnknownGlyph(Graphics g, int size)
    {
        using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("?", font, brush, new RectangleF(0, 1, size, size), sf);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
