using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Autodock;

public sealed record Palette(
    bool IsDark, Color Back, Color Card, Color Text, Color Muted, Color Track,
    Color Accent, Color OnAccent, Color Border, Color Hover, Color Pressed)
{
    public static Palette Current => IsLight() ? Light : Dark;

    static readonly Palette Light = new(false,
        Color.FromArgb(243, 243, 243), Color.White, Color.FromArgb(27, 27, 27), Color.FromArgb(104, 104, 104),
        Color.FromArgb(230, 230, 230), Color.FromArgb(13, 128, 117), Color.White, Color.FromArgb(229, 229, 229),
        Color.FromArgb(0, 0, 0, 0x0A), Color.FromArgb(0, 0, 0, 0x14));

    static readonly Palette Dark = new(true,
        Color.FromArgb(28, 28, 28), Color.FromArgb(41, 41, 41), Color.FromArgb(242, 242, 242), Color.FromArgb(160, 160, 160),
        Color.FromArgb(58, 58, 58), Color.FromArgb(52, 190, 172), Color.FromArgb(8, 32, 29), Color.FromArgb(52, 52, 52),
        Color.FromArgb(0x10, 255, 255, 255), Color.FromArgb(0x1C, 255, 255, 255));

    static bool IsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    public static Color ForPct(double pct) =>
        pct >= 90 ? Color.FromArgb(226, 72, 60) : pct >= 70 ? Color.FromArgb(232, 163, 42) : Color.FromArgb(44, 168, 96);
}

/// Fonts, DPI scaling, glyphs and Win32 cosmetics shared by every surface.
public static class Ui
{
    // ponytail: one scale from the primary monitor at start-up; per-monitor
    // re-layout on WM_DPICHANGED if people drag the flyout across monitors.
    public static readonly float Scale = GetScale();

    static float GetScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    public static int S(int px) => (int)Math.Round(px * Scale);

    static readonly string TextFamily = Installed("Segoe UI Variable Text") ?? "Segoe UI";
    static readonly string DisplayFamily = Installed("Segoe UI Variable Display") ?? "Segoe UI";
    static readonly string IconFamily = Installed("Segoe Fluent Icons") ?? "Segoe MDL2 Assets";

    static string? Installed(string name) =>
        FontFamily.Families.Any(f => f.Name == name) ? name : null;

    public static Font Font(float pt, bool semibold = false) =>
        new(semibold ? (TextFamily == "Segoe UI" ? "Segoe UI Semibold" : TextFamily) : TextFamily, pt,
            semibold && TextFamily != "Segoe UI" ? FontStyle.Bold : FontStyle.Regular);

    public static Font Display(float pt) => new(DisplayFamily, pt, FontStyle.Bold);
    public static Font Icons(float pt) => new(IconFamily, pt);

    public const string Refresh = "\uE72C", Settings = "\uE713", More = "\uE712", Terminal = "\uE756",
        Add = "\uE710", Check = "\uE73E", Pin = "\uE718", Unpin = "\uE77A", Close = "\uE711";

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// Dark scrollbars and title bars where Windows supports them; no-ops elsewhere.
    public static void ThemeWindow(Control c, Palette pal)
    {
        _ = SetWindowTheme(c.Handle, pal.IsDark ? "DarkMode_Explorer" : "Explorer", null);
        int dark = pal.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(c.Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    /// Recolours a plain dialog's standard controls to the palette.
    public static void ThemeDialog(Control root, Palette pal)
    {
        root.BackColor = pal.Back;
        root.ForeColor = pal.Text;
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox or NumericUpDown or ComboBox:
                    c.BackColor = pal.Card;
                    c.ForeColor = pal.Text;
                    if (c is TextBox t) t.BorderStyle = BorderStyle.FixedSingle;
                    if (c is NumericUpDown n) n.BorderStyle = BorderStyle.FixedSingle;
                    if (c is ComboBox cb) cb.FlatStyle = FlatStyle.Flat;
                    break;
                case Label l when l.Tag as string == "muted":
                    l.ForeColor = pal.Muted;
                    break;
                case Pill or Toggle:
                    break;
                default:
                    ThemeDialog(c, pal);
                    break;
            }
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// Label + percentage on one line, a slim meter underneath.
public sealed class UsageBar : Control
{
    public string Label { get; set; } = "";
    public double Pct { get; set; }
    public string Detail { get; set; } = "";
    public bool Stale { get; set; }

    public UsageBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = Ui.S(32);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var label = Ui.Font(8.5f);
        using var strong = Ui.Font(8.5f, semibold: true);

        var pctText = $"{Pct:0}%";
        var pctSize = TextRenderer.MeasureText(g, pctText, strong, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, pctText, strong, new Point(Width - pctSize.Width, 0), pal.Text, TextFormatFlags.NoPadding);
        var labelSize = TextRenderer.MeasureText(g, Label, label, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Label, label, new Point(0, 0), pal.Text, TextFormatFlags.NoPadding);
        if (Detail.Length > 0)
        {
            var x = labelSize.Width + Ui.S(8);
            TextRenderer.DrawText(g, Detail, label, new Rectangle(x, 0, Width - x - pctSize.Width - Ui.S(8), labelSize.Height),
                pal.Muted, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        float h = Ui.S(4), y = Height - h - Ui.S(4);
        using (var track = new SolidBrush(pal.Track))
        using (var path = Ui.Round(new RectangleF(0, y, Width - 1, h), h / 2)) g.FillPath(track, path);
        var w = (float)((Width - 1) * Math.Clamp(Pct, 0, 100) / 100.0);
        if (w > 0)
        {
            var c = Palette.ForPct(Pct);
            using var fill = new SolidBrush(Stale ? Color.FromArgb(110, c) : c);
            using var path = Ui.Round(new RectangleF(0, y, Math.Max(w, h), h), h / 2);
            g.FillPath(fill, path);
        }
    }
}

public enum PillStyle { Primary, Subtle, Icon }

/// Rounded flat button with hover/press states; text, a Fluent glyph, or both.
public sealed class Pill : Control, IButtonControl
{
    public PillStyle Style { get; set; }
    public string Glyph { get; set; } = "";
    public DialogResult DialogResult { get; set; }
    bool _hover, _down;

    public Pill(string text, PillStyle style = PillStyle.Subtle, string glyph = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Text = text;
        Style = style;
        Glyph = glyph;
        Cursor = Cursors.Hand;
        Font = Ui.Font(9f, semibold: style == PillStyle.Primary);
        Height = Ui.S(32);
        Width = style == PillStyle.Icon ? Ui.S(32) : MeasureWidth();
        BackColor = Color.Transparent;
    }

    int MeasureWidth()
    {
        var w = TextRenderer.MeasureText(Text, Font).Width + Ui.S(24);
        return Glyph.Length > 0 ? w + Ui.S(20) : w;
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnClick(EventArgs e)
    {
        if (DialogResult != DialogResult.None && FindForm() is { } f) f.DialogResult = DialogResult;
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) PerformClick();
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Ui.Round(r, Ui.S(6));

        Color fg;
        if (Style == PillStyle.Primary)
        {
            var bg = Enabled ? pal.Accent : pal.Track;
            if (_down) bg = ControlPaint.Dark(bg, 0.05f);
            else if (_hover) bg = pal.IsDark ? ControlPaint.Light(bg, 0.15f) : ControlPaint.Dark(bg, 0.02f);
            using var b = new SolidBrush(bg);
            g.FillPath(b, path);
            fg = Enabled ? pal.OnAccent : pal.Muted;
        }
        else
        {
            if (Style == PillStyle.Subtle)
            {
                using var b = new SolidBrush(pal.Hover);
                g.FillPath(b, path);
            }
            if (_hover || _down)
            {
                using var b = new SolidBrush(_down ? pal.Pressed : pal.Hover);
                g.FillPath(b, path);
            }
            fg = Enabled ? pal.Text : pal.Muted;
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(pal.Text, Ui.S(1));
            g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        if (Style == PillStyle.Icon || Text.Length == 0)
        {
            using var icon = Ui.Icons(10.5f);
            TextRenderer.DrawText(g, Glyph, icon, ClientRectangle, fg, flags | TextFormatFlags.HorizontalCenter);
            return;
        }
        if (Glyph.Length > 0)
        {
            using var icon = Ui.Icons(9f);
            var textW = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var x = (Width - textW - Ui.S(18)) / 2;
            TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x, 0, Ui.S(14), Height), fg, flags);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + Ui.S(18), 0, textW + Ui.S(2), Height), fg, flags);
            return;
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, flags | TextFormatFlags.HorizontalCenter);
    }
}

/// Windows 11–style on/off switch with an optional caption to its right.
public sealed class Toggle : Control
{
    bool _checked;
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public Toggle(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Text = text;
        Font = Ui.Font(9f);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Height = Ui.S(24);
        Width = Ui.S(52) + TextRenderer.MeasureText(text, Font).Width;
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) Checked = !Checked;
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Ui.S(40), h = Ui.S(20), y = (Height - h) / 2f;
        using var track = Ui.Round(new RectangleF(1, y, w, h), h / 2);
        if (Checked)
        {
            using var b = new SolidBrush(pal.Accent);
            g.FillPath(b, track);
        }
        else
        {
            using var pen = new Pen(pal.Muted, Math.Max(1, Ui.S(1)));
            g.DrawPath(pen, track);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(pal.Text, Math.Max(1, Ui.S(1))) { DashStyle = DashStyle.Dot };
            g.DrawPath(focus, track);
        }
        float k = Checked ? h - Ui.S(8) : h - Ui.S(10);
        var kx = Checked ? 1 + w - k - Ui.S(4) : 1 + Ui.S(5);
        using (var knob = new SolidBrush(Checked ? pal.OnAccent : pal.Muted))
            g.FillEllipse(knob, kx, y + (h - k) / 2, k, k);
        TextRenderer.DrawText(g, Text, Font, new Point((int)w + Ui.S(10), (Height - Font.Height) / 2), pal.Text, TextFormatFlags.NoPadding);
    }
}

/// Initials in a soft coloured circle, stable per email.
public sealed class Avatar : Control
{
    static readonly Color[] Hues =
    [
        Color.FromArgb(45, 140, 128), Color.FromArgb(90, 110, 200), Color.FromArgb(176, 92, 160),
        Color.FromArgb(200, 120, 60), Color.FromArgb(70, 150, 80), Color.FromArgb(180, 80, 80),
    ];

    readonly string _initials;
    readonly Color _color;

    public Avatar(string name, string key)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(Ui.S(36), Ui.S(36));
        var letters = name.Split(['@', '.', ' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        _initials = (letters.Length switch
        {
            0 => "?",
            1 => letters[0][..Math.Min(2, letters[0].Length)],
            _ => $"{letters[0][0]}{letters[1][0]}",
        }).ToUpperInvariant();
        // string.GetHashCode is randomised per process; a stable hash keeps colours fixed.
        _color = Hues[key.ToLowerInvariant().Aggregate(0u, (h, c) => h * 31 + c) % (uint)Hues.Length];
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_color);
        g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        using var f = Ui.Font(9.5f, semibold: true);
        TextRenderer.DrawText(g, _initials, f, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// Rounded surface; an optional accent outline marks the active account.
public class Surface : Panel
{
    public Color Fill { get; set; }
    public Color? Outline { get; set; }

    public Surface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var stroke = Outline.HasValue ? Ui.S(2) : 1;
        var r = new RectangleF(stroke / 2f, stroke / 2f, Width - stroke - 0.5f, Height - stroke - 0.5f);
        using var path = Ui.Round(r, Ui.S(8));
        using var b = new SolidBrush(Fill);
        g.FillPath(b, path);
        using var pen = new Pen(Outline ?? Palette.Current.Border, stroke);
        g.DrawPath(pen, path);
    }
}

/// Palette-aware context menus (the default renderer is always light).
public sealed class MenuRenderer(Palette pal) : ToolStripProfessionalRenderer(new MenuColors(pal))
{
    public static void Apply(ToolStrip menu)
    {
        var pal = Palette.Current;
        menu.Renderer = new MenuRenderer(pal);
        menu.Font = Ui.Font(9f);
        menu.ForeColor = pal.Text;
        menu.Padding = new Padding(Ui.S(4));
        foreach (ToolStripItem item in menu.Items)
        {
            item.Padding = new Padding(Ui.S(4), Ui.S(4), Ui.S(4), Ui.S(4));
            if (item is ToolStripMenuItem { HasDropDownItems: true } m) Apply(m.DropDown);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? pal.Text : pal.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = pal.Muted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        using var f = Ui.Icons(9f);
        TextRenderer.DrawText(e.Graphics, Ui.Check, f, e.ImageRectangle, pal.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    sealed class MenuColors(Palette p) : ProfessionalColorTable
    {
        Color Popup => p.IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        Color Sel => p.IsDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(234, 234, 234);
        public override Color ToolStripDropDownBackground => Popup;
        public override Color ImageMarginGradientBegin => Popup;
        public override Color ImageMarginGradientMiddle => Popup;
        public override Color ImageMarginGradientEnd => Popup;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => Sel;
        public override Color MenuItemSelected => Sel;
        public override Color MenuItemSelectedGradientBegin => Sel;
        public override Color MenuItemSelectedGradientEnd => Sel;
        public override Color MenuItemPressedGradientBegin => Sel;
        public override Color MenuItemPressedGradientEnd => Sel;
        public override Color CheckBackground => Popup;
        public override Color CheckSelectedBackground => Sel;
        public override Color CheckPressedBackground => Sel;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.Border;
    }
}
