namespace PatchGoblins;

/// Patch Goblins' notice: a small rounded card at the bottom right of the screen, in place of Windows' balloon. It never
/// takes focus, rises in, stays while hovered, fades out after a few seconds, and opens the flyout when clicked.
/// Several stack upward; at most three at a time, the oldest going first.
public sealed class Toast : Form
{
    static readonly List<Toast> Open = [];
    const int LifeMs = 7000, FadeMs = 180, Max = 3;

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
    readonly Color _accent;
    readonly string _title, _text;
    readonly Action? _click;
    long _shownAt, _fadeFrom = -1;
    Icon? _goblin;

    public static void Show(string title, string text, Color? accent = null, Action? click = null)
    {
        while (Open.Count >= Max) Open[0].Close();
        new Toast(title, text, accent ?? Palette.Current.Live, click).Present();
    }

    internal Toast(string title, string text, Color accent, Action? click)
    {
        (_title, _text, _accent, _click) = (title, text.Length > 220 ? text[..219] + "…" : text, accent, click);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Palette.Current.Card;
        Cursor = Cursors.Hand;
        Width = Ui.S(340);
        var textHeight = TextRenderer.MeasureText(_text, Ui.Font(8.5f), new Size(Width - Ui.S(72), 0), TextFormatFlags.WordBreak).Height;
        Height = Ui.S(44) + Math.Min(textHeight, Ui.S(54));
        Opacity = 0;
        _tick.Tick += (_, _) => Step();
        Click += (_, _) => { _click?.Invoke(); Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never takes focus, never in Alt+Tab
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    void Present()
    {
        Open.Add(this);
        Restack();
        _shownAt = Environment.TickCount64;
        base.Show();
        _tick.Start();
    }

    /// Bottom right of the screen under the cursor, newest lowest.
    static void Restack()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        var y = wa.Bottom - Ui.S(12);
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            y -= t.Height;
            t.Location = new Point(wa.Right - t.Width - Ui.S(12), y);
            y -= Ui.S(8);
        }
    }

    void Step()
    {
        var now = Environment.TickCount64;
        var hovered = Bounds.Contains(Cursor.Position);
        if (hovered) { _shownAt = now; _fadeFrom = -1; }
        if (_fadeFrom < 0 && now - _shownAt > LifeMs) _fadeFrom = now;
        Opacity = _fadeFrom >= 0 ? 1 - Math.Min(1, (now - _fadeFrom) / (double)FadeMs) : Math.Min(1, (now - _shownAt) / (double)FadeMs);
        if (_fadeFrom >= 0 && Opacity <= 0) Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.RoundCorners(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bar = new SolidBrush(_accent)) g.FillRectangle(bar, 0, 0, Ui.S(4), Height);
        g.DrawIcon(_goblin ??= new Icon(TrayIcons.App, Ui.S(24), Ui.S(24)), new Rectangle(Ui.S(16), Ui.S(12), Ui.S(24), Ui.S(24)));
        using var titleFont = Ui.Font(9f, semibold: true);
        using var textFont = Ui.Font(8.5f);
        TextRenderer.DrawText(g, _title, titleFont, new Rectangle(Ui.S(52), Ui.S(10), Width - Ui.S(64), Ui.S(20)), pal.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _text, textFont, new Rectangle(Ui.S(52), Ui.S(32), Width - Ui.S(72), Height - Ui.S(40)), pal.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var pen = new Pen(pal.IsDark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(220, 220, 220));
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Open.Remove(this);
        Restack();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _goblin?.Dispose(); }
        base.Dispose(disposing);
    }
}
