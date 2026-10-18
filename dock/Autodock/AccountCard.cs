namespace Autodock;

/// One account: identity row, usage meters, actions. Laid out top-down by hand.
public sealed class AccountCard : Surface
{
    public event Action<Account>? SwitchClicked;
    public event Action<Account>? TerminalClicked;
    public event Action<Account, string>? MenuAction;

    public const int BaseWidth = 344;

    public AccountCard(Account a, Palette pal, DateTimeOffset now)
    {
        Fill = pal.Card;
        Outline = a.Active ? pal.Accent : null;
        BackColor = pal.Back;
        Width = Ui.S(BaseWidth);
        Margin = new Padding(0, 0, 0, Ui.S(8));
        int pad = Ui.S(16), inner = Width - 2 * pad, y = Ui.S(14);

        var hasAlias = !string.IsNullOrEmpty(a.Alias);
        var avatar = new Avatar(hasAlias ? a.Alias! : a.Email, a.Email) { Location = new Point(pad, y) };
        Controls.Add(avatar);
        int tx = pad + avatar.Width + Ui.S(12);
        var badgeW = a.Active || a.Disabled ? Ui.S(72) : 0;
        Controls.Add(MakeLabel(hasAlias ? a.Alias! : a.Email, Ui.Font(10.5f, semibold: true), pal.Text, tx, y - Ui.S(1), inner - (tx - pad) - badgeW));
        Controls.Add(MakeLabel(hasAlias ? a.Email : $"Account {a.Number}", Ui.Font(8.5f), pal.Muted, tx, y + Ui.S(19), inner - (tx - pad) - badgeW));
        if (badgeW > 0)
            Controls.Add(new Badge(a.Active ? "Active" : "Excluded", a.Active ? pal.Accent : pal.Muted)
            { Location = new Point(Width - pad - Ui.S(68), y + Ui.S(8)), Size = new Size(Ui.S(68), Ui.S(20)) });
        y += avatar.Height + Ui.S(12);

        var notes = new List<string>();
        if (a.StatusNote is { } sn) notes.Add(char.ToUpper(sn[0]) + sn[1..]);
        if (a.Usage == null && a.LastGoodUsage != null) notes.Add("showing last known usage");
        if (a.LoginExpiresAt is { } exp && exp - now < TimeSpan.FromDays(5))
            notes.Add($"login expires {Format.When(exp, now)}, re-enroll soon");
        if (notes.Count > 0)
        {
            var note = new Label
            {
                Text = string.Join(" · ", notes),
                Font = Ui.Font(8.5f),
                ForeColor = pal.Muted,
                BackColor = pal.Card,
                Location = new Point(pad, y),
                MaximumSize = new Size(inner, 0),
                AutoSize = true,
            };
            Controls.Add(note);
            y += note.PreferredHeight + Ui.S(8);
        }

        var u = a.ShownUsage;
        var stale = a.Usage == null;
        void AddBar(string label, Window w, string? extra = null)
        {
            var detail = w.ResetsAt is { } r ? "resets " + Format.When(r, now) : "";
            if (w.AheadOfPace == true) detail = "ahead of pace · " + detail;
            if (extra != null) detail = extra + (detail.Length > 0 ? " · " + detail : "");
            Controls.Add(new UsageBar { Label = label, Pct = w.Pct, Detail = detail, Stale = stale, BackColor = pal.Card, Location = new Point(pad, y), Width = inner });
            y += Ui.S(38);
        }
        if (u?.FiveHour is { } fh) AddBar("5-hour", fh);
        if (u?.SevenDay is { } sd) AddBar("Weekly", sd);
        foreach (var m in u?.Scoped ?? []) AddBar($"{m.Name} weekly", m);
        if (u?.Spend is { } sp) AddBar("Extra usage", new Window(sp.Pct, sp.ResetsAt), $"{sp.Used:0.00} / {sp.Limit:0.00} {sp.Currency}");
        if (u == null && notes.Count == 0)
        {
            Controls.Add(MakeLabel("No usage reading yet", Ui.Font(8.5f), pal.Muted, pad, y, inner));
            y += Ui.S(24);
        }

        y += Ui.S(4);
        var more = new Pill("", PillStyle.Icon, Ui.More) { Location = new Point(Width - pad - Ui.S(32), y) };
        var term = new Pill("", PillStyle.Icon, Ui.Terminal) { Location = new Point(more.Left - Ui.S(36), y) };
        var tips = new ToolTip();
        tips.SetToolTip(term, "Open Claude Code as this account (pinned terminal)");
        tips.SetToolTip(more, "More");
        term.Click += (_, _) => TerminalClicked?.Invoke(a);
        Controls.AddRange([term, more]);
        if (!a.Active)
        {
            var sw = new Pill("Switch", PillStyle.Primary) { Location = new Point(pad, y) };
            sw.Click += (_, _) => SwitchClicked?.Invoke(a);
            Controls.Add(sw);
        }
        else
        {
            Controls.Add(MakeLabel("In use by Claude Code", Ui.Font(8.5f), pal.Muted, pad, y + Ui.S(8), inner - Ui.S(80)));
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open terminal in folder…", null, (_, _) => MenuAction?.Invoke(a, "terminal-folder"));
        menu.Items.Add("Pin a folder to this account…", null, (_, _) => MenuAction?.Invoke(a, "map"));
        menu.Items.Add(a.Disabled ? "Include in auto-switch" : "Exclude from auto-switch", null,
            (_, _) => MenuAction?.Invoke(a, a.Disabled ? "enable" : "disable"));
        menu.Items.Add("Rename…", null, (_, _) => MenuAction?.Invoke(a, "alias"));
        menu.Items.Add("Log in again…", null, (_, _) => MenuAction?.Invoke(a, "reenroll"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove…", null, (_, _) => MenuAction?.Invoke(a, "remove"));
        MenuRenderer.Apply(menu);
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height + Ui.S(2)));

        Height = y + Ui.S(32) + Ui.S(14);
    }

    Label MakeLabel(string text, Font font, Color color, int x, int y, int width) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        BackColor = Fill,
        AutoEllipsis = true,
        Location = new Point(x, y),
        Size = new Size(width, font.Height + Ui.S(2)),
    };

    sealed class Badge : Control
    {
        readonly Color _color;

        public Badge(string text, Color color)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Text = text;
            _color = color;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Palette.Current.Card);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f);
            using var bg = new SolidBrush(Color.FromArgb(40, _color));
            g.FillPath(bg, path);
            using var f = Ui.Font(8f, semibold: true);
            TextRenderer.DrawText(g, Text, f, ClientRectangle, _color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

public static class Format
{
    /// "in 2h 10m" within a day, "Tue 14:00" beyond.
    public static string When(DateTimeOffset t, DateTimeOffset now)
    {
        var d = t - now;
        if (d <= TimeSpan.Zero) return "now";
        if (d < TimeSpan.FromHours(1)) return $"in {Math.Max(1, (int)d.TotalMinutes)}m";
        if (d < TimeSpan.FromDays(1)) return $"in {(int)d.TotalHours}h {d.Minutes:00}m";
        return t.ToLocalTime().ToString("ddd HH:mm");
    }

    /// Compact tray tooltip (Windows caps it at 127 chars).
    public static string Tooltip(Snapshot? s, bool auto)
    {
        var active = s?.Accounts.FirstOrDefault(a => a.Active);
        var text = active == null
            ? "autodock — no managed account active"
            : $"autodock — {(string.IsNullOrEmpty(active.Alias) ? active.Email : active.Alias)}" +
              (active.ShownUsage is { } u
                  ? $"\n5h {u.FiveHour?.Pct ?? 0:0}% · week {u.SevenDay?.Pct ?? 0:0}%"
                  : "") +
              (auto ? "\nAuto-switch on" : "");
        return text.Length <= 127 ? text : text[..127];
    }

    public static double Peak(Account? a) =>
        a?.ShownUsage is { } u
            ? new[] { u.FiveHour?.Pct ?? 0, u.SevenDay?.Pct ?? 0 }.Concat((u.Scoped ?? []).Select(s => s.Pct)).Max()
            : 0;
}
