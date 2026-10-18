using System.Runtime.InteropServices;

namespace Autodock;

/// Tray flyout. Pinned, it becomes a movable window that stays on the desktop.
public sealed class DashboardForm : Form
{
    readonly FlowLayoutPanel _list;
    readonly Label _title, _status;
    readonly Toggle _auto;
    readonly Panel _header, _footer;
    readonly Pill _pin, _close;
    readonly ToolTip _tips = new();
    bool _syncing;
    string? _expandedEmail;

    public bool Pinned { get; private set; }

    public event Action<Account>? SwitchRequested;
    public event Action<Account>? TerminalRequested;
    public event Action<Account, string>? AccountAction;
    public event Action? RefreshRequested, SettingsRequested, AddRequested, Moved;
    public event Action<bool>? AutoToggled, PinToggled;

    public DashboardForm()
    {
        Text = "autodock";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Width = Ui.S(AccountRow.BaseWidth + 16);
        Icon = TrayIcons.App;
        Padding = new Padding(1);

        _header = new Panel { Dock = DockStyle.Top, Height = Ui.S(44) };
        _title = new Label { Text = "autodock", Font = Ui.Font(9.5f, semibold: true), AutoSize = true, Location = new Point(Ui.S(20), Ui.S(14)) };
        _status = new Label { Font = Ui.Font(8f), AutoSize = true };
        var settings = HeaderIcon(Ui.Settings, "Settings", 0);
        var add = HeaderIcon(Ui.Add, "Add account", 1);
        _pin = HeaderIcon(Ui.Pin, "Pin to desktop", 2);
        _close = HeaderIcon(Ui.Close, "Hide", 3);
        _close.Visible = false;
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        add.Click += (_, _) => AddRequested?.Invoke();
        _pin.Click += (_, _) => { SetPinned(!Pinned); PinToggled?.Invoke(Pinned); };
        _close.Click += (_, _) => Hide();
        _header.Controls.AddRange([_title, _status, settings, add, _pin, _close]);
        foreach (var c in new Control[] { _header, _title, _status }) c.MouseDown += DragWindow;

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(Ui.S(8), 0, 0, Ui.S(4)),
        };

        _footer = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(42) };
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Current.Border);
            e.Graphics.DrawLine(pen, Ui.S(20), 0, _footer.Width - Ui.S(20), 0);
        };
        _auto = new Toggle("Auto-switch near limit") { Location = new Point(Ui.S(20), Ui.S(10)) };
        _auto.CheckedChanged += (_, _) => { if (!_syncing) AutoToggled?.Invoke(_auto.Checked); };
        var refresh = new Pill("", PillStyle.Icon, Ui.Refresh) { TabStop = false };
        refresh.Location = new Point(Width - Ui.S(14) - refresh.Width, Ui.S(7));
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _tips.SetToolTip(refresh, "Refresh now");
        _footer.Controls.AddRange([_auto, refresh]);

        Controls.Add(_list);
        Controls.Add(_header);
        Controls.Add(_footer);
        ApplyTheme();
        Deactivate += (_, _) => { if (!Pinned) Hide(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        ResizeEnd += (_, _) => { if (Pinned) Moved?.Invoke(); };
    }

    Pill HeaderIcon(string glyph, string tip, int slot)
    {
        var b = new Pill("", PillStyle.Icon, glyph) { TabStop = false };
        b.Location = new Point(Width - Ui.S(14) - (slot + 1) * Ui.S(30), Ui.S(8));
        _tips.SetToolTip(b, tip);
        return b;
    }

    void DragWindow(object? sender, MouseEventArgs e)
    {
        if (!Pinned || e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
        Moved?.Invoke();
    }

    public void SetPinned(bool pinned)
    {
        Pinned = pinned;
        TopMost = !pinned; // a pinned window behaves like a normal desktop window
        _pin.Glyph = pinned ? Ui.Unpin : Ui.Pin;
        _tips.SetToolTip(_pin, pinned ? "Unpin" : "Pin to desktop");
        _close.Visible = pinned;
        _pin.Invalidate();
        _header.Cursor = pinned ? Cursors.SizeAll : Cursors.Default;
    }

    void ApplyTheme()
    {
        var pal = Palette.Current;
        BackColor = _list.BackColor = _header.BackColor = _footer.BackColor = pal.Back;
        _title.ForeColor = pal.Text;
        _status.ForeColor = pal.Faint;
        if (IsHandleCreated) Ui.ThemeWindow(_list, pal);
        Invalidate(true);
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        _status.MaximumSize = new Size(Math.Max(0, Width - Ui.S(14) - 4 * Ui.S(30) - _title.Right - Ui.S(8)), 0);
        _status.Location = new Point(_title.Right + Ui.S(4), _title.Top + (_title.Height - _status.Height) / 2 + Ui.S(1));
    }

    public void SetAuto(bool on)
    {
        _syncing = true;
        _auto.Checked = on;
        _syncing = false;
    }

    /// `others`: Codex/Grok snapshots (or their error), drawn below Claude's rows.
    public void Render(Snapshot? snap, string? error, IReadOnlyList<(string Provider, Snapshot? Snap, string? Error)>? others = null)
    {
        ApplyTheme();
        _list.SuspendLayout();
        foreach (Control c in _list.Controls) c.Dispose();
        _list.Controls.Clear();
        var sections = new List<(string Provider, Snapshot? Snap, string? Error)> { ("claude", snap, error) };
        sections.AddRange((others ?? []).Where(o => o.Snap is { Accounts.Count: > 0 } || o.Error != null));
        var labelled = sections.Count(s => s.Snap is { Accounts.Count: > 0 } || (s.Provider != "claude" && s.Error != null)) > 1;
        var now = DateTimeOffset.Now;
        foreach (var (provider, s, err) in sections)
        {
            if (labelled && (s is { Accounts.Count: > 0 } || err != null)) _list.Controls.Add(SectionLabel(ProviderCli.Title(provider)));
            if (err != null) _list.Controls.Add(Message("Couldn't refresh · " + err));
            foreach (var a in (s?.Accounts ?? []).OrderByDescending(a => a.Active).ThenBy(a => a.Number))
            {
                var row = new AccountRow(a, now);
                row.SwitchClicked += x => SwitchRequested?.Invoke(x);
                row.TerminalClicked += x => TerminalRequested?.Invoke(x);
                row.MenuAction += (x, act) => AccountAction?.Invoke(x, act);
                row.ExpandRequested += ToggleRow;
                row.SizeChanged += (_, _) => FitHeight();
                if (Key(a) == _expandedEmail) row.SetExpanded(true, animate: false);
                _list.Controls.Add(row);
            }
        }
        var any = sections.Any(x => x.Snap is { Accounts.Count: > 0 });
        if (!any && snap == null && error == null)
            _list.Controls.Add(Message("Loading…"));
        else if (!any && error == null)
        {
            _list.Controls.Add(Message("No accounts yet. Add the account you use with Claude Code."));
            var add = new Pill("Add account", PillStyle.Primary, Ui.Add) { Margin = new Padding(Ui.S(12), Ui.S(2), 0, Ui.S(12)) };
            add.Click += (_, _) => AddRequested?.Invoke();
            _list.Controls.Add(add);
        }
        _list.ResumeLayout();
        FitHeight();
    }

    void ToggleRow(AccountRow row)
    {
        var open = !row.Expanded;
        foreach (var r in _list.Controls.OfType<AccountRow>()) if (r != row && r.Expanded) r.SetExpanded(false);
        row.SetExpanded(open);
        _expandedEmail = open ? Key(row.Account) : null;
    }

    static string Key(Account a) => a.Provider + ":" + a.Email;

    /// Small muted caps above a provider's rows; only drawn when several providers have accounts.
    static Label SectionLabel(string title) => new()
    {
        Text = title.ToUpperInvariant(),
        Tag = "section",
        ForeColor = Palette.Current.Faint,
        Font = Ui.Font(7f, semibold: true),
        AutoSize = true,
        Margin = new Padding(Ui.S(12), Ui.S(8), 0, Ui.S(2)),
    };

    Label Message(string text) => new()
    {
        Text = text,
        ForeColor = Palette.Current.Muted,
        Font = Ui.Font(8.5f),
        AutoSize = true,
        MaximumSize = new Size(Ui.S(AccountRow.BaseWidth - 24), 0),
        Margin = new Padding(Ui.S(12), Ui.S(6), 0, Ui.S(12)),
    };

    /// Grows/shrinks with content, keeping the edge nearest the taskbar fixed.
    void FitHeight()
    {
        var content = _list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + _list.Padding.Vertical;
        var screen = Screen.FromPoint(Visible ? Location : Cursor.Position).WorkingArea;
        var h = Math.Min(_header.Height + _footer.Height + content + Padding.Vertical, (int)(screen.Height * 0.8));
        if (h == Height) return;
        var bottom = Bottom;
        Height = h;
        if (Visible && !Pinned) Top = bottom - h;
    }

    public void ShowNearTray()
    {
        FitHeight();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - Ui.S(12), wa.Bottom - Height - Ui.S(12));
        Show();
        Activate();
    }

    /// Shows the pinned window at a saved spot, pulled back on-screen if a monitor went away.
    public void ShowPinnedAt(int? x, int? y)
    {
        FitHeight();
        var wa = Screen.FromPoint(new Point(x ?? 0, y ?? 0)).WorkingArea;
        var px = Math.Clamp(x ?? wa.Right - Width - Ui.S(12), wa.Left, Math.Max(wa.Left, wa.Right - Width));
        var py = Math.Clamp(y ?? wa.Top + Ui.S(12), wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        Location = new Point(px, py);
        Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Palette.Current.IsDark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(220, 220, 220));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.RoundCorners(Handle);
        Ui.ThemeWindow(_list, Palette.Current);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
