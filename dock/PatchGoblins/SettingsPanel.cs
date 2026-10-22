namespace PatchGoblins;

/// Dock preferences plus the engine's autoswitch.* keys (written via `cswap config set`), shown inside the flyout
/// in place of the account list. The search box keeps only the rows whose name, hint or section match every word.
public sealed class SettingsPanel : UserControl
{
    public readonly NumericUpDown Threshold = new() { Minimum = 50, Maximum = 99, Value = 90 };
    public readonly NumericUpDown RefreshInterval = new() { Minimum = DockSettings.MinRefreshSeconds, Maximum = 3600, Increment = 30, Value = 180 };
    public readonly TextBox Models = new() { PlaceholderText = "none" };
    public readonly TextBox TerminalFolder = new() { PlaceholderText = "your user folder" };
    public readonly ComboBox Strategy = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public readonly Toggle Startup = new("");
    public readonly Toggle AutoSwitch = new(""), PreserveRc = new(""), AutoProject = new(""), QuotaNotify = new(""), AutoPrime = new(""), DraftPrs = new(""), Ultracode = new(""), AskApproval = new("");
    public readonly NumericUpDown AgentIssues = new() { Minimum = 0, Maximum = 10, Value = 1 }, AgentOpenPrs = new() { Minimum = 1, Maximum = 50, Value = 10 };
    public readonly TextBox Search = new() { PlaceholderText = "Search settings" };

    public event Action? SaveClicked, CancelClicked;

    readonly Palette _pal = Palette.Current;
    readonly FlowLayoutPanel _rows;
    readonly Label _none;
    readonly List<(Control Row, string Text, Label Section)> _all = [];
    Label? _section;
    public const int BaseWidth = 400;
    readonly int _w = Ui.S(BaseWidth), _pad = Ui.S(22);

    public SettingsPanel()
    {
        Font = Ui.Font(9f);
        Strategy.Items.AddRange(["Most quota left", "Soonest weekly reset"]);
        Strategy.SelectedIndex = 0;

        Search.Location = new Point(_pad, Ui.S(10));
        Search.Width = _w - 2 * _pad;
        var top = new Panel { Dock = DockStyle.Top, Height = Search.PreferredHeight + Ui.S(18) };
        top.Controls.Add(Search);

        _rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        _none = new Label { Text = "No setting matches.", Tag = "muted", AutoSize = true, Visible = false, Margin = new Padding(_pad, Ui.S(8), 0, 0) };

        Section("Team");
        Row("Start with Windows", null, Startup, Ui.S(34));
        Row("Refresh every", null, RefreshInterval, Ui.S(72), "s");
        Row("Terminal folder", "Where Open terminal starts browsing", TerminalFolderPicker(), Ui.S(200));
        Row("Use project accounts automatically", "Terminals and Continue in use the account assigned to the folder", AutoProject, Ui.S(34));
        Row("Preserve Remote Control", "New terminals turn Remote Control back on only if their last session used it", PreserveRc, Ui.S(34));
        Row("Quota notifications", "When a limit is near, reached or reset", QuotaNotify, Ui.S(34));

        Section("Work");
        Row("Issues at once", "0 = automatic: one per account with quota to spare, started a few minutes apart", AgentIssues, Ui.S(72));
        Row("Open pull requests", "New work holds while this many wait for review", AgentOpenPrs, Ui.S(72));
        Row("Draft pull requests", "Agents open pull requests as drafts", DraftPrs, Ui.S(34));
        Row("Ultracode", "Agents may fan a task out to parallel subagents when it benefits and an account has ample room; uses quota faster", Ultracode, Ui.S(34));
        Row("Ask for approval", "Off: agents start, run goal checks and push without asking; only files the repository's policy protects (workflows, secrets) still wait for you. You hear from Patch Goblins only when something is blocked or has a question", AskApproval, Ui.S(34));

        Section("Upkeep");
        Row("Auto-switch near limit", "Claude accounts only; Codex and Grok visitors switch by hand", AutoSwitch, Ui.S(34));
        Row("Switch at", "of the 5-hour or weekly limit", Threshold, Ui.S(72), "%");
        Row("Next account", null, Strategy, Ui.S(170));
        Row("Also watch models", "e.g. Opus, or 'all'", Models, Ui.S(170));
        Row("Auto-Prime", "Track 5-hour windows; prime fresh ones early from the tray menu, and automatically while agents run", AutoPrime, Ui.S(34));

        var about = new Label
        {
            Text = "Patch Goblins is an independent open-source project, not affiliated with or endorsed by Anthropic. " +
                   "Built on claude-swap by Onur Cetinkol (MIT).",
            Tag = "muted",
            Font = Ui.Font(7.5f),
            MaximumSize = new Size(_w - 2 * _pad, 0),
            AutoSize = true,
            Margin = new Padding(_pad, Ui.S(8), 0, Ui.S(12)),
        };
        _rows.Controls.AddRange([_none, about]);

        // Inline completion (no dropdown: a separate window would take focus from the flyout and close it).
        Search.AutoCompleteMode = AutoCompleteMode.Append;
        Search.AutoCompleteSource = AutoCompleteSource.CustomSource;
        Search.AutoCompleteCustomSource.AddRange(_all.Select(r => r.Row.Controls[0].Text).ToArray());
        Search.TextChanged += (_, _) => Filter(Search.Text);
        Search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && Search.Text.Length > 0) { Search.Clear(); e.SuppressKeyPress = e.Handled = true; } };

        var cancel = new Pill("Cancel");
        var save = new Pill("Save", PillStyle.Primary) { Width = Math.Max(cancel.Width, Ui.S(88)) };
        cancel.Width = save.Width;
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = save.Height + Ui.S(20) };
        save.Location = new Point(_w - _pad - save.Width, Ui.S(10));
        cancel.Location = new Point(save.Left - Ui.S(8) - cancel.Width, Ui.S(10));
        save.Click += (_, _) => SaveClicked?.Invoke();
        cancel.Click += (_, _) => CancelClicked?.Invoke();
        bottom.Paint += (_, e) => { using var pen = new Pen(_pal.Border); e.Graphics.DrawLine(pen, Ui.S(20), 0, bottom.Width - Ui.S(20), 0); };
        bottom.Controls.AddRange([cancel, save]);

        Controls.Add(_rows);
        Controls.Add(top);
        Controls.Add(bottom);
        Width = _w;
        Ui.ThemeDialog(this, _pal);
    }

    /// The height every row needs, unfiltered: the flyout grows to it (up to its screen cap).
    public int ContentHeight => Controls.OfType<Panel>().Sum(p => p.Height) + _rows.Controls.Cast<Control>().Where(c => c.Visible).Sum(c => c.Height + c.Margin.Vertical);

    internal void Filter(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _rows.SuspendLayout();
        foreach (var (row, text, _) in _all) row.Visible = words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        foreach (var section in _all.Select(r => r.Section).Distinct()) section.Visible = _all.Any(r => r.Section == section && r.Row.Visible);
        _none.Visible = !_all.Any(r => r.Row.Visible);
        _rows.ResumeLayout();
    }

    internal IEnumerable<string> VisibleRows => _all.Where(r => r.Row.Visible).Select(r => r.Row.Controls[0].Text);

    void Section(string title)
    {
        _section = new Label
        {
            Text = title.ToUpperInvariant(), Tag = "muted", Font = Ui.Font(7.5f, semibold: true), AutoSize = true,
            Margin = new Padding(_pad, _rows.Controls.Count == 0 ? Ui.S(4) : Ui.S(12), 0, Ui.S(8)),
        };
        _rows.Controls.Add(_section);
    }

    void Row(string label, string? hint, Control input, int inputWidth, string? suffix = null)
    {
        var row = new Panel { Width = _w - SystemInformation.VerticalScrollBarWidth - Ui.S(2), Margin = Padding.Empty }; // room for the scrollbar: no sideways scrolling
        var textWidth = _w - 2 * _pad - inputWidth - Ui.S(40);
        row.Controls.Add(new Label { Text = label, AutoSize = true, MaximumSize = new Size(textWidth + Ui.S(20), 0), Location = new Point(_pad, 0) });
        var hintHeight = 0;
        if (hint != null)
        {
            var h = new Label { Text = hint, Tag = "muted", Font = Ui.Font(8f), AutoSize = true, MaximumSize = new Size(textWidth, 0), Location = new Point(_pad, Ui.S(19)) };
            row.Controls.Add(h);
            hintHeight = Ui.S(19) + h.PreferredHeight;
        }
        var right = row.Width - Ui.S(10) - (suffix != null ? Ui.S(20) : 0);
        input.Width = inputWidth;
        input.Location = new Point(right - inputWidth, 0);
        row.Controls.Add(input);
        if (suffix != null)
            row.Controls.Add(new Label { Text = suffix, Tag = "muted", AutoSize = true, Location = new Point(right + Ui.S(6), Ui.S(3)) });
        row.Height = Math.Max(Math.Max(hintHeight, Ui.S(20)), input.Height) + Ui.S(14);
        _rows.Controls.Add(row);
        _all.Add((row, $"{label} {hint} {_section!.Text}", _section));
    }

    /// The terminal folder box with its Browse button; an empty box means the user folder.
    Control TerminalFolderPicker()
    {
        var browse = new Pill("…") { Width = Ui.S(32) };
        var panel = new Panel { Height = Math.Max(TerminalFolder.PreferredHeight, browse.Height) };
        panel.Layout += (_, _) =>
        {
            browse.Location = new Point(panel.Width - browse.Width, 0);
            TerminalFolder.Width = panel.Width - browse.Width - Ui.S(6);
        };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Open terminal starts browsing in…", UseDescriptionForTitle = true };
            if (Directory.Exists(TerminalFolder.Text)) d.InitialDirectory = TerminalFolder.Text;
            var dash = FindForm() as DashboardForm;
            if (dash != null) dash.HoldOpen++; // the dialog takes focus: the flyout must not close under it
            try { if (d.ShowDialog(FindForm()) == DialogResult.OK) TerminalFolder.Text = d.SelectedPath; }
            finally { if (dash != null) dash.HoldOpen--; }
        };
        panel.Controls.AddRange([TerminalFolder, browse]);
        return panel;
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
        Ui.ThemeWindow(_rows, _pal);
    }
}
