using System.Runtime.InteropServices;

namespace Autodock;

/// Tray flyout. Pinned, it becomes a movable window that stays on the desktop.
public sealed class DashboardForm : Form
{
    readonly FlowLayoutPanel _list;
    readonly Label _status, _subtitle;
    readonly Toggle _auto;
    readonly Panel _header, _footer;
    readonly Pill _pin, _close;
    bool _syncing;
    int _accounts;

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
        Width = Ui.S(AccountCard.BaseWidth + 32);
        Icon = TrayIcons.App;
        Padding = new Padding(1);

        _header = new Panel { Dock = DockStyle.Top, Height = Ui.S(64) };
        var title = new Label { Text = "autodock", Font = Ui.Display(13f), AutoSize = true, Location = new Point(Ui.S(16), Ui.S(12)) };
        _subtitle = new Label { Font = Ui.Font(8.5f), AutoSize = true, Location = new Point(Ui.S(17), Ui.S(38)) };
        var settings = HeaderIcon(Ui.Settings, "Settings", 0);
        var refresh = HeaderIcon(Ui.Refresh, "Refresh now", 1);
        _pin = HeaderIcon(Ui.Pin, "Pin to desktop", 2);
        _close = HeaderIcon(Ui.Close, "Hide", 3);
        _close.Visible = false;
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _pin.Click += (_, _) => { SetPinned(!Pinned); PinToggled?.Invoke(Pinned); };
        _close.Click += (_, _) => Hide();
        _header.Controls.AddRange([title, _subtitle, settings, refresh, _pin, _close]);
        foreach (var c in new Control[] { _header, title, _subtitle }) c.MouseDown += DragWindow;

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(Ui.S(16), Ui.S(2), 0, Ui.S(8)),
        };

        _footer = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(76) };
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Current.Border);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };
        _auto = new Toggle("Auto-switch near limit") { Location = new Point(Ui.S(16), Ui.S(14)) };
        _auto.CheckedChanged += (_, _) => { if (!_syncing) AutoToggled?.Invoke(_auto.Checked); };
        var add = new Pill("Add account", PillStyle.Subtle, Ui.Add);
        add.Location = new Point(Width - Ui.S(16) - add.Width - 2, Ui.S(10));
        add.Click += (_, _) => AddRequested?.Invoke();
        _status = new Label { AutoSize = false, Size = new Size(Width - Ui.S(32), Ui.S(18)), Location = new Point(Ui.S(16), Ui.S(50)), Font = Ui.Font(8f), AutoEllipsis = true };
        _footer.Controls.AddRange([_auto, add, _status]);

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
        b.Location = new Point(Width - Ui.S(12) - (slot + 1) * Ui.S(34), Ui.S(16));
        new ToolTip().SetToolTip(b, tip);
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
        new ToolTip().SetToolTip(_pin, pinned ? "Unpin" : "Pin to desktop");
        _close.Visible = pinned;
        _pin.Invalidate();
        _header.Cursor = pinned ? Cursors.SizeAll : Cursors.Default;
    }

    void ApplyTheme()
    {
        var pal = Palette.Current;
        BackColor = _list.BackColor = _header.BackColor = _footer.BackColor = pal.Back;
        foreach (Control c in _header.Controls) c.ForeColor = pal.Text;
        _subtitle.ForeColor = _status.ForeColor = pal.Muted;
        _auto.ForeColor = pal.Text;
        if (IsHandleCreated) Ui.ThemeWindow(_list, pal);
        Invalidate(true);
    }

    public void SetStatus(string text) => _status.Text = text;

    public void SetAuto(bool on)
    {
        _syncing = true;
        _auto.Checked = on;
        _syncing = false;
        UpdateSubtitle();
    }

    void UpdateSubtitle() =>
        _subtitle.Text = (_accounts == 1 ? "1 account" : $"{_accounts} accounts") + (_auto.Checked ? " · auto-switch on" : "");

    public void Render(Snapshot? snap, string? error)
    {
        var pal = Palette.Current;
        ApplyTheme();
        _accounts = snap?.Accounts.Count ?? 0;
        UpdateSubtitle();
        _list.SuspendLayout();
        foreach (Control c in _list.Controls) c.Dispose();
        _list.Controls.Clear();
        if (error != null)
            _list.Controls.Add(Message("Couldn't refresh: " + error, pal.Muted));
        if (snap is { Accounts.Count: > 0 })
        {
            var now = DateTimeOffset.Now;
            foreach (var a in snap.Accounts.OrderByDescending(a => a.Active).ThenBy(a => a.Number))
            {
                var card = new AccountCard(a, pal, now);
                card.SwitchClicked += x => SwitchRequested?.Invoke(x);
                card.TerminalClicked += x => TerminalRequested?.Invoke(x);
                card.MenuAction += (x, act) => AccountAction?.Invoke(x, act);
                _list.Controls.Add(card);
            }
        }
        else if (snap == null && error == null)
            _list.Controls.Add(Message("Loading accounts…", pal.Muted));
        else if (error == null)
        {
            _list.Controls.Add(Message("No accounts yet. Add the account you use with Claude Code; " +
                                       "the login already on this PC is kept too.", pal.Muted));
            var add = new Pill("Add account", PillStyle.Primary, Ui.Add) { Margin = new Padding(0, Ui.S(4), 0, Ui.S(16)) };
            add.Click += (_, _) => AddRequested?.Invoke();
            _list.Controls.Add(add);
        }
        _list.ResumeLayout();
        FitHeight();
    }

    Label Message(string text, Color color) => new()
    {
        Text = text,
        ForeColor = color,
        Font = Ui.Font(9f),
        AutoSize = true,
        MaximumSize = new Size(Ui.S(AccountCard.BaseWidth), 0),
        Margin = new Padding(0, Ui.S(8), 0, Ui.S(12)),
    };

    void FitHeight()
    {
        var content = _list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + _list.Padding.Vertical;
        var screen = Screen.FromPoint(Pinned ? Location : Cursor.Position).WorkingArea;
        Height = Math.Min(_header.Height + _footer.Height + content + Padding.Vertical, (int)(screen.Height * 0.85));
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
        using var pen = new Pen(Palette.Current.Border);
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
