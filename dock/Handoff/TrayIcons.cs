using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Handoff;

public static class TrayIcons
{
    public static readonly Icon App = LoadApp();

    static Icon LoadApp()
    {
        using var s = typeof(TrayIcons).Assembly.GetManifestResourceStream("dock.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    /// App glyph with a usage dot (green / amber / red; grey when unknown).
    public static Icon WithDot(double? peakPct, bool auto)
    {
        var size = SystemInformation.SmallIconSize.Width * 2 > 32 ? 32 : SystemInformation.SmallIconSize.Width;
        size = Math.Max(16, size);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var baseIcon = new Icon(App, size, size)) g.DrawIcon(baseIcon, new Rectangle(0, 0, size, size));
            var d = size * 7 / 16;
            var r = new Rectangle(size - d, size - d, d - 1, d - 1);
            var c = peakPct is { } p ? Palette.ForPct(p) : Color.Gray;
            using var ring = new Pen(Color.FromArgb(32, 32, 32), Math.Max(1, size / 16f));
            using var fill = new SolidBrush(c);
            g.FillEllipse(fill, r);
            g.DrawEllipse(ring, r);
            if (auto)
            {
                using var w = new SolidBrush(Color.White);
                var inner = r;
                inner.Inflate(-d / 3, -d / 3);
                g.FillEllipse(w, inner);
            }
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);
}
