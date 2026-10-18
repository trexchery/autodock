namespace Autodock;

/// Dock preferences plus the engine's autoswitch.* keys (written via `cswap config set`).
public sealed class SettingsForm : Form
{
    public readonly NumericUpDown Threshold = new() { Minimum = 50, Maximum = 99, Value = 90 };
    public readonly NumericUpDown RefreshInterval = new() { Minimum = DockSettings.MinRefreshSeconds, Maximum = 3600, Increment = 30, Value = 180 };
    public readonly TextBox Models = new() { PlaceholderText = "none" };
    public readonly ComboBox Strategy = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public readonly Toggle Startup = new("");

    readonly Palette _pal = Palette.Current;
    int _y;
    readonly int _w = Ui.S(460), _pad = Ui.S(24);

    public SettingsForm()
    {
        Text = "autodock settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Font(9f);
        Icon = TrayIcons.App;
        Strategy.Items.AddRange(["Most quota left", "Use soonest reset first"]);
        Strategy.SelectedIndex = 0;
        _y = Ui.S(20);

        Section("Auto-switch");
        Row("Switch at", "When the active account's 5-hour or weekly usage reaches this percentage.", Threshold, Ui.S(80), "%");
        Row("Pick next account by", "Most quota left, or the account whose weekly quota resets soonest.", Strategy, Ui.S(190));
        Row("Also watch models", "Per-model weekly limits to include, e.g. Opus. Comma-separated, or 'all'.", Models, Ui.S(190));
        _y += Ui.S(12);
        Section("General");
        Row("Start with Windows", "Launch autodock in the tray when you sign in.", Startup, Ui.S(44));
        Row("Refresh every", "Seconds between dashboard updates. The engine still spaces out real API calls.", RefreshInterval, Ui.S(80), "s");

        _y += Ui.S(8);
        var about = new Label
        {
            Text = "autodock is an independent open-source project, not affiliated with or endorsed by Anthropic. " +
                   "Built on claude-swap by Onur Cetinkol (MIT).",
            Tag = "muted",
            Font = Ui.Font(8f),
            Location = new Point(_pad, _y),
            MaximumSize = new Size(_w - 2 * _pad, 0),
            AutoSize = true,
        };
        Controls.Add(about);
        _y += about.PreferredHeight + Ui.S(20);

        var cancel = new Pill("Cancel") { DialogResult = DialogResult.Cancel };
        var save = new Pill("Save", PillStyle.Primary) { DialogResult = DialogResult.OK, Width = Math.Max(cancel.Width, Ui.S(88)) };
        cancel.Width = save.Width;
        save.Location = new Point(_w - _pad - save.Width, _y);
        cancel.Location = new Point(save.Left - Ui.S(8) - cancel.Width, _y);
        Controls.AddRange([cancel, save]);
        AcceptButton = save;
        CancelButton = cancel;
        ClientSize = new Size(_w, _y + save.Height + Ui.S(20));
        Ui.ThemeDialog(this, _pal);
    }

    void Section(string title)
    {
        Controls.Add(new Label { Text = title, Font = Ui.Font(10.5f, semibold: true), AutoSize = true, Location = new Point(_pad, _y) });
        _y += Ui.S(32);
    }

    void Row(string label, string hint, Control input, int inputWidth, string? suffix = null)
    {
        var textWidth = _w - 2 * _pad - inputWidth - Ui.S(40);
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(_pad, _y) });
        var h = new Label { Text = hint, Tag = "muted", Font = Ui.Font(8f), AutoSize = true, MaximumSize = new Size(textWidth, 0), Location = new Point(_pad, _y + Ui.S(20)) };
        Controls.Add(h);
        var right = _w - _pad - (suffix != null ? Ui.S(20) : 0);
        input.Width = inputWidth;
        input.Location = new Point(right - inputWidth, _y);
        Controls.Add(input);
        if (suffix != null)
            Controls.Add(new Label { Text = suffix, Tag = "muted", AutoSize = true, Location = new Point(right + Ui.S(6), _y + Ui.S(3)) });
        _y += Math.Max(Ui.S(20) + h.PreferredHeight, input.Height) + Ui.S(16);
    }

    /// Engine values for the strategy picker.
    public string StrategyValue
    {
        get => Strategy.SelectedIndex == 1 ? "consume-first" : "best";
        set => Strategy.SelectedIndex = value == "consume-first" ? 1 : 0;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.ThemeWindow(this, _pal);
    }
}
