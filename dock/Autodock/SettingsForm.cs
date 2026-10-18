namespace Autodock;

/// Dock preferences plus the engine's autoswitch.* keys (written via `cswap config set`).
public sealed class SettingsForm : Form
{
    public readonly NumericUpDown Threshold = new() { Minimum = 50, Maximum = 99, Value = 90, Width = 70 };
    public readonly NumericUpDown RefreshInterval = new() { Minimum = DockSettings.MinRefreshSeconds, Maximum = 3600, Increment = 30, Value = 180, Width = 70 };
    public readonly TextBox Models = new() { Width = 180, PlaceholderText = "e.g. Opus  (blank = none)" };
    public readonly ComboBox Strategy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    public readonly CheckBox Startup = new() { Text = "Start autodock when I sign in to Windows", AutoSize = true };

    public SettingsForm()
    {
        Text = "autodock settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        Icon = TrayIcons.App;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Strategy.Items.AddRange(["best", "consume-first"]);
        Strategy.SelectedIndex = 0;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c, string? hint = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) });
            grid.Controls.Add(c);
            if (hint != null)
            {
                grid.Controls.Add(new Label());
                grid.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(300, 0) });
            }
        }
        Row("Switch at (% used)", Threshold, "Auto-switch moves off the active account when its 5-hour or weekly window reaches this.");
        Row("Strategy", Strategy, "best: most quota left.  consume-first: use the soonest-resetting weekly quota first.");
        Row("Also watch models", Models, "Per-model weekly limits to include (comma-separated, or 'all').");
        Row("Refresh every (s)", RefreshInterval, "Dashboard/auto-switch tick. The engine still rate-limits real API calls per account.");
        grid.Controls.Add(Startup);
        grid.SetColumnSpan(Startup, 2);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
        Controls.Add(buttons);
        Controls.Add(new Label
        {
            Text = "autodock is an independent open-source project, not affiliated with or endorsed by Anthropic. " +
                   "Built on claude-swap by Onur Cetinkol (MIT).",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Dock = DockStyle.Bottom,
            Padding = new Padding(0, 10, 0, 0),
        });
    }
}
