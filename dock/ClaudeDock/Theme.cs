using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace ClaudeDock;

public sealed record Palette(Color Back, Color Card, Color Text, Color Muted, Color Track, Color Accent, Color Border)
{
    public static Palette Current => IsLight() ? Light : Dark;

    static readonly Palette Light = new(
        Color.FromArgb(243, 243, 243), Color.White, Color.FromArgb(28, 28, 28), Color.FromArgb(96, 96, 96),
        Color.FromArgb(225, 225, 225), Color.FromArgb(201, 100, 66), Color.FromArgb(222, 222, 222));

    static readonly Palette Dark = new(
        Color.FromArgb(32, 32, 32), Color.FromArgb(43, 43, 43), Color.FromArgb(240, 240, 240), Color.FromArgb(170, 170, 170),
        Color.FromArgb(62, 62, 62), Color.FromArgb(217, 119, 87), Color.FromArgb(58, 58, 58));

    static bool IsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    public static Color ForPct(double pct) =>
        pct >= 90 ? Color.FromArgb(220, 68, 55) : pct >= 70 ? Color.FromArgb(230, 160, 40) : Color.FromArgb(46, 160, 90);
}

/// Thin rounded usage meter: "label ▓▓▓░░ 42%  · resets in 2h 10m".
public sealed class UsageBar : Control
{
    public string Label { get; set; } = "";
    public double Pct { get; set; }
    public string Detail { get; set; } = "";
    public bool Stale { get; set; }

    public UsageBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 34;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? pal.Card);

        using var font = new Font("Segoe UI", 8.5f);
        var pctText = $"{Pct:0}%";
        TextRenderer.DrawText(g, Label, font, new Point(0, 0), pal.Text);
        var pw = TextRenderer.MeasureText(pctText, font).Width;
        TextRenderer.DrawText(g, pctText, font, new Point(Width - pw, 0), pal.Text);
        if (Detail.Length > 0)
        {
            var dw = TextRenderer.MeasureText(Detail, font).Width;
            TextRenderer.DrawText(g, Detail, font, new Point(Math.Max(90, Width - pw - dw - 6), 0), pal.Muted);
        }

        var track = new Rectangle(0, 20, Width - 1, 6);
        using (var b = new SolidBrush(pal.Track)) FillRound(g, b, track);
        var w = (int)Math.Round(track.Width * Math.Clamp(Pct, 0, 100) / 100.0);
        if (w > 0)
        {
            var c = Palette.ForPct(Pct);
            if (Stale) c = Color.FromArgb(120, c);
            using var fill = new SolidBrush(c);
            FillRound(g, fill, track with { Width = Math.Max(w, 6) });
        }
    }

    static void FillRound(Graphics g, Brush b, Rectangle r)
    {
        using var path = new GraphicsPath();
        var d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(b, path);
    }
}
