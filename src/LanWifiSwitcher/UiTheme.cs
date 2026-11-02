namespace LanWifiSwitcher;

/// <summary>
/// Shared modern palette for Settings / Choose adapters (wallet-app inspired:
/// dark charcoal surface, mint + sky accents, calm typography — no purple glow).
/// </summary>
internal static class UiTheme
{
    public static readonly Color WindowBg = Color.FromArgb(18, 20, 26);
    public static readonly Color CardBg = Color.FromArgb(28, 31, 42);
    public static readonly Color CardBorder = Color.FromArgb(48, 52, 68);
    public static readonly Color TextPrimary = Color.FromArgb(242, 244, 248);
    public static readonly Color TextMuted = Color.FromArgb(156, 163, 180);
    public static readonly Color AccentLan = Color.FromArgb(64, 148, 255);
    public static readonly Color AccentWifi = Color.FromArgb(46, 204, 140);
    public static readonly Color AccentDanger = Color.FromArgb(232, 93, 93);
    public static readonly Color ButtonBg = Color.FromArgb(40, 44, 58);
    public static readonly Color ButtonPrimary = Color.FromArgb(64, 148, 255);
    public static readonly Color ListSelected = Color.FromArgb(42, 58, 88);
    public static readonly Color ListHover = Color.FromArgb(36, 40, 54);

    public static Font TitleFont { get; } = new("Segoe UI Semibold", 16f, FontStyle.Bold);
    public static Font SectionFont { get; } = new("Segoe UI Semibold", 10.5f, FontStyle.Bold);
    public static Font BodyFont { get; } = new("Segoe UI", 9.25f, FontStyle.Regular);
    public static Font SmallFont { get; } = new("Segoe UI", 8.25f, FontStyle.Regular);
    public static Font ButtonFont { get; } = new("Segoe UI Semibold", 9f, FontStyle.Bold);

    public static void ApplyWindow(Form form)
    {
        form.BackColor = WindowBg;
        form.ForeColor = TextPrimary;
        form.Font = BodyFont;
        form.ShowIcon = false;
    }

    public static Label MakeMutedLabel(string text, Point location, Size size) => new()
    {
        Text = text,
        Location = location,
        Size = size,
        ForeColor = TextMuted,
        BackColor = Color.Transparent,
        Font = SmallFont
    };

    public static Button MakePrimaryButton(string text, Point location, Size size)
    {
        var btn = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = ButtonPrimary,
            ForeColor = Color.White,
            Font = ButtonFont,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(86, 162, 255);
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(48, 120, 220);
        return btn;
    }

    public static Button MakeGhostButton(string text, Point location, Size size)
    {
        var btn = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = ButtonBg,
            ForeColor = TextPrimary,
            Font = ButtonFont,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = CardBorder;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = ListHover;
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(32, 36, 48);
        return btn;
    }

    public static Panel MakeCard(Point location, Size size)
    {
        var panel = new Panel
        {
            Location = location,
            Size = size,
            BackColor = CardBg,
            Padding = new Padding(14)
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(CardBorder);
            var r = panel.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        };
        return panel;
    }

    public static void StyleOwnerDrawList(ListBox list)
    {
        list.BackColor = Color.FromArgb(24, 26, 34);
        list.ForeColor = TextPrimary;
        list.BorderStyle = BorderStyle.None;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = 52;
        list.IntegralHeight = false;
        list.DrawItem += (_, e) =>
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= list.Items.Count)
            {
                return;
            }

            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var bounds = e.Bounds;
            using (var bg = new SolidBrush(selected ? ListSelected : Color.FromArgb(24, 26, 34)))
            {
                e.Graphics.FillRectangle(bg, bounds);
            }

            if (list.Items[e.Index] is not NetworkAdapterInfo adapter)
            {
                return;
            }

            var accent = adapter.Kind switch
            {
                AdapterKind.Ethernet => AccentLan,
                AdapterKind.Wireless => AccentWifi,
                _ => TextMuted
            };

            using (var bar = new SolidBrush(accent))
            {
                e.Graphics.FillRectangle(bar, bounds.X, bounds.Y + 8, 3, bounds.Height - 16);
            }

            var titleRect = new Rectangle(bounds.X + 14, bounds.Y + 8, bounds.Width - 22, 22);
            var subRect = new Rectangle(bounds.X + 14, bounds.Y + 28, bounds.Width - 22, 18);
            TextRenderer.DrawText(
                e.Graphics,
                adapter.Name,
                SectionFont,
                titleRect,
                TextPrimary,
                TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(
                e.Graphics,
                adapter.Subtitle,
                SmallFont,
                subRect,
                TextMuted,
                TextFormatFlags.EndEllipsis);

            if (selected)
            {
                using var pen = new Pen(accent, 1);
                e.Graphics.DrawRectangle(pen, bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
            }
        };
    }
}
