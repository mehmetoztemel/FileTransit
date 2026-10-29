namespace FileTransit;

// Shared palette and control styling for the main window and dialogs.
internal static class Theme
{
    public static readonly Color Ink = Color.FromArgb(24, 39, 58), Muted = Color.FromArgb(104, 119, 137), Line = Color.FromArgb(221, 228, 235),
        Canvas = Color.FromArgb(238, 242, 246), Surface = Color.White, Stripe = Color.FromArgb(249, 251, 252), HeaderCell = Color.FromArgb(244, 247, 249),
        Accent = Color.FromArgb(0, 128, 122), AccentDark = Color.FromArgb(0, 102, 97), AccentSoft = Color.FromArgb(219, 240, 238), Hover = Color.FromArgb(235, 245, 244),
        Upload = Color.FromArgb(37, 99, 200), Success = Color.FromArgb(28, 135, 80), Warning = Color.FromArgb(190, 120, 0), Danger = Color.FromArgb(192, 48, 48),
        AccentSoftHover = Color.FromArgb(198, 231, 227), DangerSoft = Color.FromArgb(252, 233, 233), DangerHover = Color.FromArgb(248, 214, 214), DangerBorder = Color.FromArgb(238, 196, 196),
        HeaderMuted = Color.FromArgb(150, 170, 190), HeaderChip = Color.FromArgb(36, 56, 80), HeaderHover = Color.FromArgb(44, 66, 92), Online = Color.FromArgb(132, 219, 202);

    // Pastel role colours: light fill, same-hue dark text. Buttons signal their role by fill colour, not text colour alone.
    //   Green: start/confirm · Teal: transfer · Blue: navigation/helpers · Amber: needs attention · Red: close/delete/cancel · Violet: settings/cleanup · Neutral: plain.
    public enum Tone { Neutral, Blue, Green, Teal, Amber, Red, Violet }
    public readonly record struct ToneColors(Color Back, Color Hover, Color Pressed, Color Text);
    public static ToneColors Colors(Tone tone) => tone switch
    {
        Tone.Blue => new(Color.FromArgb(224, 236, 252), Color.FromArgb(207, 224, 249), Color.FromArgb(188, 211, 245), Color.FromArgb(29, 78, 160)),
        Tone.Green => new(Color.FromArgb(220, 243, 228), Color.FromArgb(200, 235, 212), Color.FromArgb(178, 225, 194), Color.FromArgb(22, 110, 62)),
        Tone.Teal => new(Color.FromArgb(214, 240, 237), Color.FromArgb(193, 231, 227), Color.FromArgb(170, 220, 215), Color.FromArgb(0, 100, 95)),
        Tone.Amber => new(Color.FromArgb(253, 242, 214), Color.FromArgb(250, 231, 186), Color.FromArgb(246, 219, 156), Color.FromArgb(140, 86, 0)),
        Tone.Red => new(Color.FromArgb(252, 231, 231), Color.FromArgb(248, 213, 213), Color.FromArgb(242, 193, 193), Color.FromArgb(178, 40, 40)),
        Tone.Violet => new(Color.FromArgb(237, 230, 252), Color.FromArgb(224, 213, 249), Color.FromArgb(209, 194, 245), Color.FromArgb(88, 56, 168)),
        _ => new(Color.FromArgb(232, 237, 242), Color.FromArgb(219, 226, 234), Color.FromArgb(205, 214, 224), Ink)
    };
    public static void Style(Button button, Tone tone)
    {
        var colors = Colors(tone);
        button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand; button.UseVisualStyleBackColor = false; button.FlatAppearance.BorderSize = 0;
        button.BackColor = colors.Back; button.ForeColor = colors.Text; button.FlatAppearance.MouseOverBackColor = colors.Hover; button.FlatAppearance.MouseDownBackColor = colors.Pressed;
    }

    // Primary: solid teal for the one confirming action of a dialog. Soft/Danger are the Neutral/Red tones.
    public enum ButtonKind { Secondary, Primary, Soft, Danger, Header }

    public static void Style(Button button, ButtonKind kind = ButtonKind.Secondary)
    {
        if (kind == ButtonKind.Soft) { Style(button, Tone.Neutral); return; }
        if (kind == ButtonKind.Danger) { Style(button, Tone.Red); return; }
        button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand; button.UseVisualStyleBackColor = false;
        var look = button.FlatAppearance;
        switch (kind)
        {
            case ButtonKind.Primary:
                look.BorderSize = 0; button.BackColor = Accent; button.ForeColor = Color.White; look.MouseOverBackColor = AccentDark; look.MouseDownBackColor = AccentDark; break;
            case ButtonKind.Header:
                // Same chip colour as the connection badge so header controls read as one group on the dark bar.
                look.BorderSize = 0; button.BackColor = HeaderChip; button.ForeColor = Color.White; look.MouseOverBackColor = HeaderHover; look.MouseDownBackColor = Color.FromArgb(54, 80, 108); break;
            default:
                look.BorderSize = 1; look.BorderColor = Line; button.BackColor = Surface; button.ForeColor = Ink; look.MouseOverBackColor = Hover; look.MouseDownBackColor = AccentSoft; break;
        }
    }

    // Window icon taken from the executable (ApplicationIcon); null when it cannot be read.
    public static Icon? AppIcon { get; } = LoadAppIcon();
    private static Icon? LoadAppIcon() { try { return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; } }

    // White panel with a one-pixel outline; used for the connection card and file panes.
    public static void Card(Control control)
    {
        control.BackColor = Surface;
        control.Paint += (_, e) => { using var pen = new Pen(Line); e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1); };
        control.Resize += (_, _) => control.Invalidate();
    }

    // Windows icon font glyphs (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10).
    public static class Glyph
    {
        public const char Servers = '', Save = '', Settings = '', Up = '', Download = '', Upload = '', Refresh = '',
            NewFolder = '', Rename = '', Delete = '', Play = '', Pause = '', Cancel = '', ChevronUp = '', ChevronDown = '';
    }
    private static readonly string? iconFont = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }.FirstOrDefault(name => { using var font = new Font(name, 10); return font.Name == name; });
    private static readonly Dictionary<(char, int, int), Bitmap> glyphs = [];
    // Returns null when no icon font is installed; callers then show text only.
    public static Image? Icon(char glyph, Color color, int size = 16)
    {
        if (iconFont == null) return null;
        if (glyphs.TryGetValue((glyph, color.ToArgb(), size), out var cached)) return cached;
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap)) using (var font = new Font(iconFont, size * .8f, GraphicsUnit.Pixel)) using (var brush = new SolidBrush(color)) using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
        }
        return glyphs[(glyph, color.ToArgb(), size)] = bitmap;
    }

    // Applies a tone to a toolbar button: pastel fill via Renderer, matching text and icon colour.
    public static T Tint<T>(T item, Tone tone, char? glyph = null) where T : ToolStripItem
    {
        var colors = Colors(tone); item.Tag = tone; item.ForeColor = colors.Text; item.Margin = new Padding(0, 1, 4, 2); item.Padding = new Padding(8, 0, 8, 0);
        if (glyph is { } g && Icon(g, colors.Text) is { } image) item.Image = image;
        return item;
    }

    public static ToolStrip Strip() => new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 2), BackColor = Surface, Renderer = Renderer };

    public static readonly ToolStripRenderer Renderer = new FlatRenderer();
    private sealed class FlatRenderer : ToolStripProfessionalRenderer
    {
        public FlatRenderer() : base(new Palette()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e); }
        // Toolbar buttons tagged with a Tone get that tone's pastel fill; disabled ones fall back to a flat grey.
        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Tag is not Tone tone) { base.OnRenderButtonBackground(e); return; }
            var colors = Colors(tone);
            var fill = !e.Item.Enabled ? Stripe : e.Item.Pressed ? colors.Pressed : e.Item.Selected ? colors.Hover : colors.Back;
            using var brush = new SolidBrush(fill); e.Graphics.FillRectangle(brush, new Rectangle(1, 1, e.Item.Width - 2, e.Item.Height - 2));
        }
    }
    private sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Surface; public override Color ToolStripGradientMiddle => Surface; public override Color ToolStripGradientEnd => Surface;
        public override Color ToolStripBorder => Surface; public override Color ToolStripDropDownBackground => Surface;
        public override Color StatusStripGradientBegin => Surface; public override Color StatusStripGradientEnd => Surface;
        public override Color ButtonSelectedHighlight => Hover; public override Color ButtonSelectedHighlightBorder => Line; public override Color ButtonSelectedBorder => Line;
        public override Color ButtonSelectedGradientBegin => Hover; public override Color ButtonSelectedGradientMiddle => Hover; public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonPressedGradientBegin => AccentSoft; public override Color ButtonPressedGradientMiddle => AccentSoft; public override Color ButtonPressedGradientEnd => AccentSoft; public override Color ButtonPressedBorder => Line;
        public override Color ButtonCheckedGradientBegin => AccentSoft; public override Color ButtonCheckedGradientMiddle => AccentSoft; public override Color ButtonCheckedGradientEnd => AccentSoft;
        public override Color MenuItemSelected => Hover; public override Color MenuItemBorder => Line; public override Color MenuBorder => Line;
        public override Color MenuItemSelectedGradientBegin => Hover; public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => AccentSoft; public override Color MenuItemPressedGradientEnd => AccentSoft;
        public override Color ImageMarginGradientBegin => Surface; public override Color ImageMarginGradientMiddle => Surface; public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Line; public override Color SeparatorLight => Surface;
    }
}
