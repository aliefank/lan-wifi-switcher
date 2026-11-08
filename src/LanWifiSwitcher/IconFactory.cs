using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LanWifiSwitcher;

/// <summary>
/// Windows 11–style tray icons: flat accent discs with clear LAN / Wi‑Fi glyphs.
/// Short toggle transition plus continuous LAN / Wi‑Fi loop frames for the tray.
/// </summary>
public static class IconFactory
{
    public const int WifiLoopFrameCount = 14;
    public const int LanLoopFrameCount = 12;
    // Fluent / Windows 11 accents — readable on light and dark taskbars.
    private static readonly Color LanAccent = Color.FromArgb(255, 0, 120, 212);   // Accent blue
    private static readonly Color WifiAccent = Color.FromArgb(255, 15, 148, 86);  // Signal green
    private static readonly Color BothAccent = Color.FromArgb(255, 202, 105, 0);  // Amber
    private static readonly Color OffAccent = Color.FromArgb(255, 108, 108, 108);
    private static readonly Color UnknownAccent = Color.FromArgb(255, 120, 120, 120);
    private static readonly Color TransitionAccent = Color.FromArgb(255, 72, 72, 74); // neutral pulse

    public static Icon CreateForMode(ConnectionMode mode)
    {
        return mode switch
        {
            ConnectionMode.Lan => CreateIcon(LanAccent, DrawLanGlyph, progress: 1f),
            ConnectionMode.Wifi => CreateIcon(WifiAccent, DrawWifiGlyph, progress: 1f),
            ConnectionMode.Both => CreateIcon(BothAccent, DrawBothGlyph, progress: 1f),
            ConnectionMode.Neither => CreateIcon(OffAccent, DrawOffGlyph, progress: 1f),
            _ => CreateIcon(UnknownAccent, DrawUnknownGlyph, progress: 1f)
        };
    }

    /// <summary>
    /// Brief Windows 11–style transition: pulse → morph glyph → settle on target mode.
    /// Caller owns and must dispose returned icons after the animation finishes.
    /// </summary>
    public static IReadOnlyList<Icon> CreateToggleAnimation(ConnectionMode from, ConnectionMode to)
    {
        var frames = new List<Icon>(8);
        var fromAccent = AccentFor(from);
        var toAccent = AccentFor(to);

        // 1–2: shrink / fade previous glyph
        frames.Add(CreateIcon(Blend(fromAccent, TransitionAccent, 0.35f), GlyphFor(from), progress: 0.85f, scale: 0.92f));
        frames.Add(CreateIcon(Blend(fromAccent, TransitionAccent, 0.7f), GlyphFor(from), progress: 0.45f, scale: 0.78f));

        // 3: neutral pulse disc
        frames.Add(CreateIcon(TransitionAccent, DrawPulseGlyph, progress: 1f, scale: 0.7f));

        // 4–6: grow target glyph with rising signal / plug
        frames.Add(CreateIcon(Blend(TransitionAccent, toAccent, 0.45f), GlyphFor(to), progress: 0.35f, scale: 0.82f));
        frames.Add(CreateIcon(Blend(TransitionAccent, toAccent, 0.75f), GlyphFor(to), progress: 0.7f, scale: 0.94f));
        frames.Add(CreateIcon(toAccent, GlyphFor(to), progress: 1f, scale: 1.05f));

        // 7: settle
        frames.Add(CreateIcon(toAccent, GlyphFor(to), progress: 1f, scale: 1f));

        return frames;
    }

    /// <summary>
    /// Continuous tray loop while LAN or Wi‑Fi is the active path. Caller disposes icons on mode change.
    /// </summary>
    public static IReadOnlyList<Icon> CreateContinuousLoop(ConnectionMode mode)
    {
        return mode switch
        {
            ConnectionMode.Wifi => CreateWifiLoopFrames(),
            ConnectionMode.Lan => CreateLanLoopFrames(),
            ConnectionMode.Both => CreateBothLoopFrames(),
            _ => Array.Empty<Icon>()
        };
    }

    public static bool SupportsContinuousLoop(ConnectionMode mode) =>
        mode is ConnectionMode.Lan or ConnectionMode.Wifi or ConnectionMode.Both;

    private static IReadOnlyList<Icon> CreateWifiLoopFrames()
    {
        // Signal arcs fill outward then ease back — reads at 16×16.
        var frames = new List<Icon>(WifiLoopFrameCount);
        for (var i = 0; i < WifiLoopFrameCount; i++)
        {
            var progress = TriangleWave(i / (float)WifiLoopFrameCount);
            progress = 0.12f + progress * 0.88f;
            frames.Add(CreateIcon(WifiAccent, DrawWifiGlyph, progress));
        }

        return frames;
    }

    private static IReadOnlyList<Icon> CreateLanLoopFrames()
    {
        // RJ45 plug steady; contact teeth + link pulse travel downward.
        var frames = new List<Icon>(LanLoopFrameCount);
        for (var i = 0; i < LanLoopFrameCount; i++)
        {
            var phase = i / (float)LanLoopFrameCount;
            frames.Add(CreateIcon(LanAccent, DrawLanGlyph, progress: phase));
        }

        return frames;
    }

    private static IReadOnlyList<Icon> CreateBothLoopFrames()
    {
        const int n = 10;
        var frames = new List<Icon>(n);
        for (var i = 0; i < n; i++)
        {
            var t = TriangleWave(i / (float)n);
            frames.Add(CreateIcon(BothAccent, DrawBothGlyph, progress: 0.35f + t * 0.65f));
        }

        return frames;
    }

    private static float TriangleWave(float t)
    {
        t %= 1f;
        if (t < 0) t += 1f;
        return t < 0.5f ? t * 2f : (1f - t) * 2f;
    }

    private static Color AccentFor(ConnectionMode mode) => mode switch
    {
        ConnectionMode.Lan => LanAccent,
        ConnectionMode.Wifi => WifiAccent,
        ConnectionMode.Both => BothAccent,
        ConnectionMode.Neither => OffAccent,
        _ => UnknownAccent
    };

    private static Action<Graphics, int, float> GlyphFor(ConnectionMode mode) => mode switch
    {
        ConnectionMode.Lan => DrawLanGlyph,
        ConnectionMode.Wifi => DrawWifiGlyph,
        ConnectionMode.Both => DrawBothGlyph,
        ConnectionMode.Neither => DrawOffGlyph,
        _ => DrawUnknownGlyph
    };

    private static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            255,
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    private static Icon CreateIcon(
        Color accent,
        Action<Graphics, int, float> drawGlyph,
        float progress,
        float scale = 1f)
    {
        // Draw at 32×32; NotifyIcon scales for 16×16 tray. Thick strokes keep edges clear.
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            var cx = size / 2f;
            var cy = size / 2f;
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-cx, -cy);

            // Soft shadow for light taskbars.
            using (var shadow = new SolidBrush(Color.FromArgb(36, 0, 0, 0)))
            {
                g.FillEllipse(shadow, 1.5f, 2.5f, size - 4f, size - 4f);
            }

            using (var bg = new SolidBrush(accent))
            {
                g.FillEllipse(bg, 1f, 1f, size - 3f, size - 3f);
            }

            // Thin light rim — Windows 11 acrylic contrast.
            using (var rim = new Pen(Color.FromArgb(210, 255, 255, 255), 1.5f))
            {
                g.DrawEllipse(rim, 1.8f, 1.8f, size - 4.6f, size - 4.6f);
            }

            drawGlyph(g, size, Math.Clamp(progress, 0f, 1f));
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

    /// <summary>Ethernet: RJ45 + sequenced teeth + downward link pulse (reads at 16px).</summary>
    private static void DrawLanGlyph(Graphics g, int size, float progress)
    {
        using var bodyPen = new Pen(Color.White, 2.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var fill = new SolidBrush(Color.White);

        var body = new RectangleF(9f, 8.5f, 14f, 10.5f);
        g.DrawRoundedRectangle(bodyPen, body, 2.2f);
        g.FillRectangle(fill, 13.2f, 6.8f, 5.6f, 2.3f);

        for (var i = 0; i < 4; i++)
        {
            var toothPhase = (progress + i * 0.18f) % 1f;
            var alpha = (int)(255 * (0.35f + 0.65f * Pulse(toothPhase)));
            using var toothPen = new Pen(Color.FromArgb(alpha, Color.White), 2.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var x = 11.2f + i * 3f;
            g.DrawLine(toothPen, x, 20.2f, x, 24.8f);
        }

        // Cable/link pulse below the jack.
        var linkY = 25.5f + progress * 5.5f;
        var linkAlpha = (int)(220 * (1f - Math.Abs(progress * 2f - 1f)));
        using (var linkPen = new Pen(Color.FromArgb(linkAlpha, Color.White), 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            g.DrawLine(linkPen, 16f, 19.8f, 16f, linkY);
        }

        using var dot = new SolidBrush(Color.FromArgb(Math.Clamp(linkAlpha + 35, 0, 255), Color.White));
        g.FillEllipse(dot, 14.8f, Math.Min(linkY - 1.2f, 29.5f), 2.6f, 2.6f);
    }

    private static float Pulse(float phase) =>
        0.5f + 0.5f * MathF.Sin(phase * MathF.PI * 2f);

    /// <summary>Wi‑Fi: arcs reveal outward with progress (animation + steady state).</summary>
    private static void DrawWifiGlyph(Graphics g, int size, float progress)
    {
        using var pen = new Pen(Color.White, 2.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var fill = new SolidBrush(Color.White);

        g.FillEllipse(fill, 14.4f, 21f, 3.4f, 3.4f);

        if (progress > 0.2f)
        {
            using var p = new Pen(Color.FromArgb((int)(255 * Math.Min(1f, (progress - 0.15f) / 0.35f)), Color.White), 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(p, 12.2f, 13.5f, 7.6f, 7.6f, 220, 100);
        }

        if (progress > 0.45f)
        {
            using var p = new Pen(Color.FromArgb((int)(255 * Math.Min(1f, (progress - 0.4f) / 0.35f)), Color.White), 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(p, 9.2f, 10.2f, 13.6f, 13.6f, 220, 100);
        }

        if (progress > 0.7f)
        {
            using var p = new Pen(Color.FromArgb((int)(255 * Math.Min(1f, (progress - 0.65f) / 0.35f)), Color.White), 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(p, 6.2f, 7f, 19.6f, 19.6f, 220, 100);
        }
    }

    private static void DrawBothGlyph(Graphics g, int size, float progress)
    {
        using var pen = new Pen(Color.FromArgb((int)(255 * progress), Color.White), 2.1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var fill = new SolidBrush(Color.FromArgb((int)(255 * progress), Color.White));

        g.DrawRoundedRectangle(pen, new RectangleF(6.5f, 11f, 8.5f, 7f), 1.5f);
        g.DrawLine(pen, 8.2f, 19.2f, 8.2f, 22.5f);
        g.DrawLine(pen, 10.8f, 19.2f, 10.8f, 22.5f);
        g.DrawLine(pen, 13.4f, 19.2f, 13.4f, 22.5f);

        g.DrawArc(pen, 16.2f, 8f, 10.2f, 10.2f, 220, 100);
        g.DrawArc(pen, 18.6f, 10.4f, 5.4f, 5.4f, 220, 100);
        g.FillEllipse(fill, 20.1f, 15.6f, 2.5f, 2.5f);
    }

    private static void DrawOffGlyph(Graphics g, int size, float progress)
    {
        using var pen = new Pen(Color.FromArgb((int)(255 * progress), Color.White), 2.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawEllipse(pen, 9f, 9f, 14f, 14f);
        g.DrawLine(pen, 12f, 12f, 20f, 20f);
    }

    private static void DrawUnknownGlyph(Graphics g, int size, float progress)
    {
        using var font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb((int)(255 * progress), Color.White));
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("?", font, brush, new RectangleF(0, 1, size, size), sf);
    }

    private static void DrawPulseGlyph(Graphics g, int size, float progress)
    {
        using var fill = new SolidBrush(Color.FromArgb((int)(180 * progress), Color.White));
        g.FillEllipse(fill, 13.5f, 13.5f, 5f, 5f);
    }

    private static void DrawRoundedRectangle(this Graphics g, Pen pen, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2f;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
