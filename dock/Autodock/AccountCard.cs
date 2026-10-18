namespace Autodock;

public sealed class AccountCard : Panel
{
    public event Action<Account>? SwitchClicked;
    public event Action<Account>? TerminalClicked;
    public event Action<Account, string>? MenuAction;

    public AccountCard(Account a, Palette pal, DateTimeOffset now)
    {
        BackColor = pal.Card;
        Padding = new Padding(14, 10, 14, 10);
        Margin = new Padding(0, 0, 0, 8);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
            BackColor = pal.Card,
        };
        Controls.Add(stack);
        const int W = 330;

        var header = new Panel { Width = W, Height = 22, BackColor = pal.Card, Margin = Padding.Empty };
        var name = new Label
        {
            Text = $"#{a.Number}  {a.DisplayName}",
            AutoEllipsis = true,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = pal.Text,
            Location = new Point(0, 0),
            Size = new Size(W - 78, 22),
        };
        header.Controls.Add(name);
        var badgeText = a.Active ? "ACTIVE" : a.Disabled ? "EXCLUDED" : "";
        if (badgeText.Length > 0)
        {
            header.Controls.Add(new Label
            {
                Text = badgeText,
                Font = new Font("Segoe UI Semibold", 7.5f),
                ForeColor = a.Active ? Color.White : pal.Muted,
                BackColor = a.Active ? pal.Accent : pal.Track,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(64, 18),
                Location = new Point(W - 64, 2),
            });
        }
        stack.Controls.Add(header);

        var notes = new List<string>();
        if (a.StatusNote is { } sn) notes.Add(sn);
        if (a.Usage == null && a.LastGoodUsage != null) notes.Add("showing last known usage");
        if (a.LoginExpiresAt is { } exp && exp - now < TimeSpan.FromDays(5))
            notes.Add($"login expires {Format.When(exp, now)} — re-enroll soon");
        if (notes.Count > 0)
            stack.Controls.Add(new Label
            {
                Text = string.Join(" · ", notes),
                ForeColor = pal.Muted,
                Font = new Font("Segoe UI", 8.25f),
                AutoSize = true,
                MaximumSize = new Size(W, 0),
                Margin = new Padding(0, 2, 0, 2),
            });

        var u = a.ShownUsage;
        var stale = a.Usage == null;
        if (u?.FiveHour is { } fh) stack.Controls.Add(Bar("5-hour", fh, stale, now, W));
        if (u?.SevenDay is { } sd) stack.Controls.Add(Bar("Weekly", sd, stale, now, W));
        foreach (var m in u?.Scoped ?? []) stack.Controls.Add(Bar($"{m.Name} weekly", m, stale, now, W));
        if (u?.Spend is { } sp)
            stack.Controls.Add(Bar("Extra usage", new Window(sp.Pct, sp.ResetsAt), stale, now, W,
                $"{sp.Used:0.00}/{sp.Limit:0.00} {sp.Currency}"));

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0), BackColor = pal.Card, WrapContents = false };
        var sw = MakeButton(a.Active ? "Active" : "Switch", pal, primary: !a.Active);
        sw.Enabled = !a.Active;
        sw.Click += (_, _) => SwitchClicked?.Invoke(a);
        var term = MakeButton("Terminal", pal);
        term.Click += (_, _) => TerminalClicked?.Invoke(a);
        var more = MakeButton("•••", pal);
        more.Width = 44;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open terminal in folder…", null, (_, _) => MenuAction?.Invoke(a, "terminal-folder"));
        menu.Items.Add("Pin a folder to this account…", null, (_, _) => MenuAction?.Invoke(a, "map"));
        menu.Items.Add(a.Disabled ? "Include in auto-switch" : "Exclude from auto-switch", null,
            (_, _) => MenuAction?.Invoke(a, a.Disabled ? "enable" : "disable"));
        menu.Items.Add("Set alias…", null, (_, _) => MenuAction?.Invoke(a, "alias"));
        menu.Items.Add("Re-enroll (log in again)…", null, (_, _) => MenuAction?.Invoke(a, "reenroll"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove account…", null, (_, _) => MenuAction?.Invoke(a, "remove"));
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height));
        buttons.Controls.AddRange([sw, term, more]);
        stack.Controls.Add(buttons);
    }

    static UsageBar Bar(string label, Window w, bool stale, DateTimeOffset now, int width, string? extra = null)
    {
        var reset = w.ResetsAt is { } r ? "resets " + Format.When(r, now) : "";
        if (w.AheadOfPace == true) reset = "ahead of pace · " + reset;
        if (extra != null) reset = extra + (reset.Length > 0 ? " · " + reset : "");
        return new UsageBar { Label = label, Pct = w.Pct, Detail = reset, Stale = stale, Width = width, Margin = new Padding(0, 4, 0, 0) };
    }

    static Button MakeButton(string text, Palette pal, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Height = 28,
            Width = 86,
            Font = new Font("Segoe UI", 9f),
            BackColor = primary ? pal.Accent : pal.Track,
            ForeColor = primary ? Color.White : pal.Text,
            Margin = new Padding(0, 0, 6, 0),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
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
