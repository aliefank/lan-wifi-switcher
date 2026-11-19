using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LanWifiSwitcher;

public static class IconFactory
{
    public const int WifiLoopFrameCount = 24;
    public const int LanLoopFrameCount = 24;
    private const int CanvasSize = 32;
    private const uint GetClientAreaAnimation = 0x1042;

    internal static Icon AppIcon { get; } = LoadAppIcon();

    internal static bool AnimationsEnabled =>
        !SystemParametersInfo(GetClientAreaAnimation, 0, out var enabled, 0) || enabled;

    public static Icon CreateForMode(ConnectionMode mode) =>
        Render(AccentFor(mode), mode);

    /// <summary>The caller owns the returned icons.</summary>
    public static IReadOnlyList<Icon> CreateToggleAnimation(ConnectionMode from, ConnectionMode to)
    {
        const int count = 16;
        var frames = new List<Icon>(count);
        for (var i = 0; i < count; i++)
        {
            var t = i / (float)(count - 1);
            var target = t >= 0.5f;
            var visibility = Ease(MathF.Abs(2f * t - 1f));
            var accent = Blend(AccentFor(from), AccentFor(to), Ease(t));
            frames.Add(Render(accent, target ? to : from, opacity: visibility, scale: 0.92f + visibility * 0.08f));
        }

        return frames;
    }

    /// <summary>The caller owns the returned icons.</summary>
    public static IReadOnlyList<Icon> CreateContinuousLoop(ConnectionMode mode)
    {
        if (!SupportsContinuousLoop(mode))
        {
            return Array.Empty<Icon>();
        }

        var count = mode == ConnectionMode.Wifi ? WifiLoopFrameCount : LanLoopFrameCount;
        var frames = new List<Icon>(count);
        for (var i = 0; i < count; i++)
        {
            frames.Add(Render(AccentFor(mode), mode, phase: i / (float)count));
        }

        return frames;
    }

    public static bool SupportsContinuousLoop(ConnectionMode mode) =>
        mode is ConnectionMode.Lan or ConnectionMode.Wifi or ConnectionMode.Both;

    private static Icon Render(Color accent, ConnectionMode mode, float? phase = null, float opacity = 1f, float scale = 1f)
    {
        // Draw at the system's small-icon size to keep the silhouette crisp at taskbar scale.
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(size.Width / (float)CanvasSize, size.Height / (float)CanvasSize);

            var bounds = new RectangleF(1.4f, 1.4f, 29.2f, 29.2f);
            using var tile = RoundedRectangle(bounds, 7.4f);
            using var gradient = new LinearGradientBrush(bounds,
                Blend(Color.FromArgb(24, 38, 58), accent, 0.12f),
                Color.FromArgb(12, 20, 34), LinearGradientMode.ForwardDiagonal);
            g.FillPath(gradient, tile);
            using var rim = new Pen(Blend(Color.FromArgb(55, 76, 104), accent, 0.60f), 1.45f);
            g.DrawPath(rim, tile);

            g.TranslateTransform(16f, 16f);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-16f, -16f);
            DrawGlyph(g, mode, accent, phase, opacity);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawGlyph(Graphics g, ConnectionMode mode, Color accent, float? phase, float opacity)
    {
        var foreground = Blend(accent, Color.White, 0.28f);
        using var pen = Stroke(foreground, 2.65f, opacity);
        using var ink = new SolidBrush(Alpha(foreground, opacity));

        switch (mode)
        {
            case ConnectionMode.Lan:
                g.DrawLines(pen, [
                    new PointF(7.7f, 9f), new PointF(24.3f, 9f),
                    new PointF(24.3f, 20.2f), new PointF(20.8f, 20.2f),
                    new PointF(20.8f, 24f), new PointF(11.2f, 24f),
                    new PointF(11.2f, 20.2f), new PointF(7.7f, 20.2f), new PointF(7.7f, 9f)
                ]);
                for (var i = 0; i < 3; i++)
                {
                    var x = 12f + 4f * i;
                    using var pin = Stroke(foreground, 2f, opacity * Breathe(phase, i * 0.19f));
                    g.DrawLine(pin, x, 10f, x, 14.3f);
                }
                break;

            case ConnectionMode.Wifi:
                for (var i = 0; i < 3; i++)
                {
                    var diameter = 10f + 7f * i;
                    using var arc = Stroke(foreground, 2.45f, opacity * Breathe(phase, i * 0.15f));
                    g.DrawArc(arc, 16f - diameter / 2f, 23.8f - diameter / 2f, diameter, diameter, 226f, 88f);
                }
                g.FillEllipse(ink, 14.1f, 21.9f, 3.8f, 3.8f);
                break;

            case ConnectionMode.Both:
                using (var lan = Stroke(AccentFor(ConnectionMode.Lan), 2.45f, opacity))
                using (var wifi = Stroke(AccentFor(ConnectionMode.Wifi), 2.45f, opacity))
                {
                    g.DrawEllipse(lan, 6.3f, 10.5f, 11f, 11f);
                    g.DrawEllipse(wifi, 14.7f, 10.5f, 11f, 11f);
                }
                using (var link = new SolidBrush(Alpha(foreground, opacity * Breathe(phase, 0f))))
                {
                    g.FillEllipse(link, 14.2f, 14.2f, 3.6f, 3.6f);
                }
                break;

            case ConnectionMode.Neither:
                g.DrawEllipse(pen, 8.5f, 8.5f, 15f, 15f);
                g.DrawLine(pen, 11f, 11f, 21f, 21f);
                break;

            default:
                using (var mark = new GraphicsPath())
                {
                    mark.AddBezier(12f, 11.6f, 12.7f, 7.5f, 20f, 7.6f, 20f, 12f);
                    mark.AddBezier(20f, 12f, 20f, 15.5f, 16f, 14.9f, 16f, 18f);
                    g.DrawPath(pen, mark);
                }
                g.FillEllipse(ink, 14.6f, 21.2f, 2.8f, 2.8f);
                break;
        }
    }

    private static Pen Stroke(Color color, float width, float opacity) => new(Alpha(color, opacity), width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };

    private static float Breathe(float? phase, float offset) => phase is float t
        ? 0.61f + 0.39f * (0.5f + 0.5f * MathF.Cos((t - offset) * MathF.Tau))
        : 1f;

    private static float Ease(float t) => t * t * (3f - 2f * t);

    private static Color Alpha(Color color, float opacity) => Color.FromArgb((int)(255f * opacity), color);

    private static Color Blend(Color from, Color to, float t) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * t),
        (int)(from.G + (to.G - from.G) * t),
        (int)(from.B + (to.B - from.B) * t));

    private static Color AccentFor(ConnectionMode mode) => mode switch
    {
        ConnectionMode.Lan => Color.FromArgb(117, 190, 255),
        ConnectionMode.Wifi => Color.FromArgb(104, 233, 191),
        ConnectionMode.Both => Color.FromArgb(160, 210, 249),
        _ => Color.FromArgb(177, 193, 216)
    };

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = 2f * radius;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(IconFactory).Assembly.GetManifestResourceStream("LanWifiSwitcher.App.ico")
            ?? throw new InvalidOperationException("The application icon resource was not found.");
        return new Icon(stream);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
