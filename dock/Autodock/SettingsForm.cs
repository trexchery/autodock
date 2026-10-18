namespace Autodock;

/// Dock preferences plus the engine's autoswitch.* keys (written via `cswap config set`).
public sealed class SettingsForm : Form
{
    public readonly NumericUpDown Threshold = new() { Minimum = 50, Maximum = 99, Value = 90 };
    public readonly NumericUpDown RefreshInterval = new() { Minimum = DockSettings.MinRefreshSeconds, Maximum = 3600, Increment = 30, Value = 180 };
    public readonly TextBox Models = new() { PlaceholderText = "none" };
    public readonly ComboBox Strategy = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public readonly Toggle Startup = new("");
    public readonly Toggle PreserveRc = new(""), AutoProject = new(""), QuotaNotify = new("");

    readonly Palette _pal = Palette.Current;
    int _y;
    readonly int _w = Ui.S(400), _pad = Ui.S(22);

    public SettingsForm()
    {
        Text = "autodock settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Font(9f);
        Icon = TrayIcons.App;
        Strategy.Items.AddRange(["Most quota left", "Soonest weekly reset"]);
        Strategy.SelectedIndex = 0;
        _y = Ui.S(20);

        Section("Auto-switch");
        Controls.Add(new Label { Text = "Claude accounts only; Codex and Grok switch manually.", Tag = "muted", Font = Ui.Font(8f), AutoSize = true, Location = new Point(_pad, _y - Ui.S(4)) });
        _y += Ui.S(20);
        Row("Switch at", "of the 5-hour or weekly limit", Threshold, Ui.S(72), "%");
        Row("Next account", null, Strategy, Ui.S(170));
        Row("Also watch models", "e.g. Opus, or 'all'", Models, Ui.S(170));
        _y += Ui.S(6);
        Section("General");
        Row("Start with Windows", null, Startup, Ui.S(34));
        Row("Refresh every", null, RefreshInterval, Ui.S(72), "s");
        Row("Preserve Remote Control", "New terminals turn Remote Control back on only if their last session used it", PreserveRc, Ui.S(34));
        Row("Use project accounts automatically", "Terminals and Continue in use the account assigned to the folder", AutoProject, Ui.S(34));
        Row("Quota notifications", "When a limit is near, reached or reset", QuotaNotify, Ui.S(34));

        _y += Ui.S(8);
        var about = new Label
        {
            Text = "autodock is an independent open-source project, not affiliated with or endorsed by Anthropic. " +
                   "Built on claude-swap by Onur Cetinkol (MIT).",
            Tag = "muted",
            Font = Ui.Font(7.5f),
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
        Controls.Add(new Label { Text = title.ToUpperInvariant(), Tag = "muted", Font = Ui.Font(7.5f, semibold: true), AutoSize = true, Location = new Point(_pad, _y) });
        _y += Ui.S(24);
    }

    void Row(string label, string? hint, Control input, int inputWidth, string? suffix = null)
    {
        var textWidth = _w - 2 * _pad - inputWidth - Ui.S(40);
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(_pad, _y) });
        var hintHeight = 0;
        if (hint != null)
        {
            var h = new Label { Text = hint, Tag = "muted", Font = Ui.Font(8f), AutoSize = true, MaximumSize = new Size(textWidth, 0), Location = new Point(_pad, _y + Ui.S(19)) };
            Controls.Add(h);
            hintHeight = Ui.S(19) + h.PreferredHeight;
        }
        var right = _w - _pad - (suffix != null ? Ui.S(20) : 0);
        input.Width = inputWidth;
        input.Location = new Point(right - inputWidth, _y);
        Controls.Add(input);
        if (suffix != null)
            Controls.Add(new Label { Text = suffix, Tag = "muted", AutoSize = true, Location = new Point(right + Ui.S(6), _y + Ui.S(3)) });
        _y += Math.Max(Math.Max(hintHeight, Ui.S(20)), input.Height) + Ui.S(14);
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
