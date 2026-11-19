using System.Runtime.InteropServices;

namespace LanWifiSwitcher;

internal static class UiTheme
{
    private static bool HighContrast => SystemInformation.HighContrast;
    public static Color Window => HighContrast ? SystemColors.Window : Color.FromArgb(15, 23, 37);
    public static Color Surface => HighContrast ? SystemColors.Control : Color.FromArgb(24, 36, 54);
    private static Color ListBack => HighContrast ? SystemColors.Window : Color.FromArgb(17, 27, 43);
    private static Color Border => HighContrast ? SystemColors.WindowText : Color.FromArgb(56, 75, 102);
    public static Color TextPrimary => HighContrast ? SystemColors.WindowText : Color.FromArgb(240, 246, 255);
    public static Color TextMuted => HighContrast ? SystemColors.WindowText : Color.FromArgb(174, 190, 211);
    public static Color AccentLan => HighContrast ? SystemColors.WindowText : Color.FromArgb(125, 196, 255);
    public static Color AccentWifi => HighContrast ? SystemColors.WindowText : Color.FromArgb(104, 233, 191);
    public static Color Warning => HighContrast ? SystemColors.WindowText : Color.FromArgb(255, 206, 129);

    public static Font TitleFont { get; } = new("Segoe UI", 19f, FontStyle.Bold);
    public static Font SectionFont { get; } = new("Segoe UI", 10.5f, FontStyle.Bold);
    private static Font BodyFont { get; } = new("Segoe UI", 9.5f);
    private static Font DetailFont { get; } = new("Segoe UI", 8.25f);

    public static void ApplyWindow(Form form, string title, Size size)
    {
        form.Text = title;
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = BodyFont;
        form.BackColor = Window;
        form.ForeColor = TextPrimary;
        form.Icon = IconFactory.AppIcon;
        form.ClientSize = size;
        // Leave enough room for two adapter lists and the footer on smaller displays.
        form.MinimumSize = form.SizeFromTargetClient(new Size(632, size.Height - 52));
        form.StartPosition = FormStartPosition.CenterScreen;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.HandleCreated += (_, _) =>
        {
            if (!HighContrast)
            {
                const int useImmersiveDarkMode = 20;
                var enabled = 1;
                DwmSetWindowAttribute(form.Handle, useImmersiveDarkMode, ref enabled, sizeof(int));
            }
        };
    }

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        Font = font ?? BodyFont,
        ForeColor = color ?? TextMuted,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false
    };

    public static Button Button(string text, bool primary = false, int width = 94)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(width, 36),
            Margin = new Padding(0, 0, 8, 0),
            Font = BodyFont,
            Cursor = Cursors.Hand
        };
        if (HighContrast) return button;

        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? AccentWifi : Surface;
        button.ForeColor = primary ? Window : TextPrimary;
        button.FlatAppearance.BorderColor = primary ? AccentWifi : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(151, 250, 214) : Color.FromArgb(36, 53, 76);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(76, 204, 162) : Window;
        return button;
    }

    public static void StyleTrayMenu(ContextMenuStrip menu)
    {
        if (HighContrast) return;

        menu.ForeColor = TextPrimary;
        menu.BackColor = Surface;
        menu.Renderer = new ToolStripProfessionalRenderer(new TrayMenuColors()) { RoundedEdges = false };
    }

    private sealed class TrayMenuColors : ProfessionalColorTable
    {
        private static Color Selection => Color.FromArgb(36, 57, 81);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemBorder => Border;
        public override Color CheckBackground => AccentWifi;
        public override Color CheckSelectedBackground => AccentWifi;
        public override Color CheckPressedBackground => AccentWifi;
    }

    public static void StyleAdapterList(ListBox list)
    {
        list.Font = BodyFont;
        list.BackColor = ListBack;
        list.ForeColor = TextPrimary;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.IntegralHeight = false;
        list.ItemHeight = Scale(list, 62);
        list.HandleCreated += (_, _) => list.ItemHeight = Scale(list, 62);
        list.DpiChangedAfterParent += (_, _) => list.ItemHeight = Scale(list, 62);
        list.DrawItem += (_, e) => DrawAdapterRow(list, e);
    }

    private static void DrawAdapterRow(ListBox list, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= list.Items.Count || list.Items[e.Index] is not NetworkAdapterInfo adapter)
            return;

        var selected = (e.State & DrawItemState.Selected) != 0;
        var focused = (e.State & DrawItemState.Focus) != 0;
        var bounds = e.Bounds;
        var accent = adapter.Kind switch
        {
            AdapterKind.Ethernet => AccentLan,
            AdapterKind.Wireless => AccentWifi,
            _ => TextMuted
        };
        var background = selected
            ? HighContrast ? SystemColors.Highlight : Color.FromArgb(33, 53, 75)
            : ListBack;
        var titleColor = selected && HighContrast ? SystemColors.HighlightText : TextPrimary;
        var detailColor = selected && HighContrast ? SystemColors.HighlightText : TextMuted;
        var statusColor = selected && HighContrast ? SystemColors.HighlightText : accent;

        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, bounds);
        using var divider = new Pen(Border);
        e.Graphics.DrawLine(divider, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

        var left = bounds.Left + Scale(list, 12);
        var width = Math.Max(1, bounds.Width - Scale(list, selected ? 44 : 22));
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

        TextRenderer.DrawText(e.Graphics, adapter.Name, SectionFont,
            new Rectangle(left, bounds.Top + Scale(list, 4), width, Scale(list, 23)), titleColor, flags);
        var status = $"{adapter.KindLabel} · {adapter.StatusLabel}";
        if (adapter.IsVirtualOrIgnored) status += " · virtual / ignored";
        TextRenderer.DrawText(e.Graphics, status, DetailFont,
            new Rectangle(left, bounds.Top + Scale(list, 26), width, Scale(list, 14)), statusColor, flags);
        if (!string.IsNullOrWhiteSpace(adapter.Description) &&
            !string.Equals(adapter.Name, adapter.Description, StringComparison.OrdinalIgnoreCase))
        {
            TextRenderer.DrawText(e.Graphics, adapter.Description, DetailFont,
                new Rectangle(left, bounds.Top + Scale(list, 41), width, Scale(list, 15)), detailColor, flags);
        }

        if (selected)
        {
            using var selection = new SolidBrush(statusColor);
            e.Graphics.FillRectangle(selection, bounds.Left, bounds.Top + Scale(list, 6),
                Scale(list, 3), bounds.Height - Scale(list, 12));
            TextRenderer.DrawText(e.Graphics, "✓", SectionFont,
                new Rectangle(bounds.Right - Scale(list, 28), bounds.Top, Scale(list, 20), bounds.Height),
                statusColor, flags | TextFormatFlags.HorizontalCenter);
        }

        if (focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -2, -2), titleColor, background);
        }
    }

    private static int Scale(Control control, int pixels) => (pixels * control.DeviceDpi + 48) / 96;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static Size SizeFromTargetClient(this Form form, Size clientSize) =>
        new(clientSize.Width + form.Width - form.ClientSize.Width, clientSize.Height + form.Height - form.ClientSize.Height);
}
