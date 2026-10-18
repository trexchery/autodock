using System.Runtime.InteropServices;

namespace Autodock;

/// Flyout anchored above the tray: every account, its windows, and actions.
public sealed class DashboardForm : Form
{
    readonly FlowLayoutPanel _list;
    readonly Label _status;
    readonly CheckBox _auto;
    readonly Panel _header, _footer;
    bool _syncing;

    public event Action<Account>? SwitchRequested;
    public event Action<Account>? TerminalRequested;
    public event Action<Account, string>? AccountAction;
    public event Action? RefreshRequested, SettingsRequested, AddRequested;
    public event Action<bool>? AutoToggled;

    public DashboardForm()
    {
        Text = "autodock";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        Width = 390;
        Font = new Font("Segoe UI", 9f);
        Icon = TrayIcons.App;

        _header = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(16, 0, 10, 0) };
        var title = new Label { Text = "autodock", Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(16, 13) };
        var settings = IconButton("⚙", "Settings");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var refresh = IconButton("⟳", "Refresh now");
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        settings.Location = new Point(Width - 46, 9);
        refresh.Location = new Point(Width - 84, 9);
        _header.Controls.AddRange([title, refresh, settings]);

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12, 0, 0, 4),
        };

        _footer = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(16, 6, 16, 6) };
        _auto = new CheckBox { Text = "Auto-switch near limit", AutoSize = true, Location = new Point(16, 8) };
        _auto.CheckedChanged += (_, _) => { if (!_syncing) AutoToggled?.Invoke(_auto.Checked); };
        var add = new Button { Text = "+ Add account", FlatStyle = FlatStyle.Flat, Size = new Size(116, 28), Location = new Point(Width - 132, 5), Cursor = Cursors.Hand };
        add.FlatAppearance.BorderSize = 0;
        add.Click += (_, _) => AddRequested?.Invoke();
        _status = new Label { AutoSize = false, Size = new Size(Width - 32, 18), Location = new Point(16, 38), Font = new Font("Segoe UI", 8f), AutoEllipsis = true };
        _footer.Controls.AddRange([_auto, add, _status]);

        Controls.Add(_list);
        Controls.Add(_header);
        Controls.Add(_footer);
        ApplyTheme();
        Deactivate += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    Button IconButton(string glyph, string tip)
    {
        var b = new Button { Text = glyph, FlatStyle = FlatStyle.Flat, Size = new Size(34, 30), Font = new Font("Segoe UI Symbol", 11f), Cursor = Cursors.Hand, TabStop = false };
        b.FlatAppearance.BorderSize = 0;
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    void ApplyTheme()
    {
        var pal = Palette.Current;
        BackColor = _list.BackColor = pal.Back;
        foreach (Control c in new Control[] { _header, _footer })
        {
            c.BackColor = pal.Back;
            foreach (Control child in c.Controls)
            {
                child.ForeColor = pal.Text;
                if (child is Button b) b.BackColor = b.Text.StartsWith('+') ? pal.Accent : pal.Back;
                if (child is Button { Text: ['+', ..] } addBtn) addBtn.ForeColor = Color.White;
            }
        }
        _status.ForeColor = pal.Muted;
    }

    public void SetStatus(string text) => _status.Text = text;

    public void SetAuto(bool on)
    {
        _syncing = true;
        _auto.Checked = on;
        _syncing = false;
    }

    public void Render(Snapshot? snap, string? error)
    {
        var pal = Palette.Current;
        ApplyTheme();
        _list.SuspendLayout();
        foreach (Control c in _list.Controls) c.Dispose();
        _list.Controls.Clear();
        if (snap == null || snap.Accounts.Count == 0)
        {
            _list.Controls.Add(new Label
            {
                Text = error ?? "No accounts yet.\n\nClick “+ Add account” to sign in with Claude Code. " +
                       "Your current login is added too, so nothing is lost.",
                ForeColor = pal.Muted,
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                Margin = new Padding(4, 16, 0, 16),
            });
        }
        else
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
        _list.ResumeLayout();
        Resize();
    }

    new void Resize()
    {
        var content = _list.Controls.Cast<Control>().Sum(c => c.PreferredSize.Height + c.Margin.Vertical) + _list.Padding.Vertical;
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Height = Math.Min(_header.Height + _footer.Height + content + 4, (int)(screen.Height * 0.85));
    }

    public void ShowNearTray()
    {
        Resize();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        Show();
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounded corners + dark title-less frame; harmless no-op on Windows 10.
        int round = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
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

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
