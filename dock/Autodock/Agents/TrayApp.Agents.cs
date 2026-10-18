using System.Globalization;

namespace Autodock;

// The tray's side of autodock agents: one launcher in the flyout footer, a few menu items, the owner's
// confirmation dialog, and the supervisor's lifetime. Nothing here exists until agents are first used.
public sealed partial class TrayApp
{
    Supervisor? _agents;
    AgentsLink.Server? _link;
    readonly Dictionary<string, string> _labels = [];

    /// Created on first use, or at start when a repository was left running; never before.
    Supervisor? EnsureAgents()
    {
        if (_agents != null) return _agents;
        if (Cli.Locate() is not { } cli)
        {
            Notify("Agents need Git", "Install Git for Windows (and the GitHub CLI for pull requests), then try again.", ToolTipIcon.Warning);
            return null;
        }
        _agents = new Supervisor(_engine, cli, new AgentStore(AgentStore.DefaultRoot), new AgentsConfig());
        _agents.Authorize = AuthorizeAgents;
        _agents.Notify = (title, text) => Notify(title, text);
        _agents.Changed += OnAgentsChanged;
        _link = new AgentsLink.Server(_agents.HandleAsync);
        _agents.Start();
        _ = ReadEngineThresholdAsync();
        if (_snap != null) _agents.UpdateAccounts(_snap.Accounts, ProjectMap.Load());
        return _agents;
    }

    /// Runs use the engine's own auto-switch threshold and strategy, so both agree on "near the limit".
    async Task ReadEngineThresholdAsync()
    {
        if (_agents == null) return;
        try
        {
            var cfg = await _engine.ConfigAsync();
            if (double.TryParse(cfg.GetValueOrDefault("autoswitch.threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                _agents.Config.Threshold = t;
            _agents.Config.ConsumeFirst = cfg.GetValueOrDefault("autoswitch.strategy") == "consume-first";
        }
        catch (Exception e) when (e is EngineException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    void OnAgentsChanged()
    {
        if (_agents == null) return;
        foreach (var repo in _agents.Repos)
            if (AgentsTerminal.StateOf(repo) is var state && (!_labels.TryGetValue(repo.Path, out var was) || was != state))
            {
                AgentsTerminal.SetState(AgentStore.DefaultRoot, repo.Path, state);
                _labels[repo.Path] = state;
            }
        var needs = _agents.NeedsOwner;
        var working = _agents.Working;
        _dash.SetAgents(needs ? Palette.Warn : working > 0 ? Palette.Current.Live : null,
            needs ? "Agents terminal · something needs you" : working > 0 ? "Agents terminal · working" : "Agents terminal");
    }

    /// The launcher: the agents terminal for the last repository (a folder picker the first time).
    void LaunchAgents()
    {
        if (_settings.AgentLastRepo is { } last && Directory.Exists(last)) OpenAgentsTerminal(last);
        else PickAgentsFolder();
    }

    void PickAgentsFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Repository for the agents terminal", UseDescriptionForTitle = true, InitialDirectory = _settings.AgentLastRepo ?? "" };
        if (d.ShowDialog() == DialogResult.OK) OpenAgentsTerminal(d.SelectedPath);
    }

    void ShowAgentsMenu(Control anchor)
    {
        var menu = new ContextMenuStrip();
        AddAgentItems(menu.Items);
        MenuRenderer.Apply(menu);
        MenuRenderer.DisposeOnClose(menu);
        menu.Show(anchor, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
    }

    /// Per authorized repository: a one-line glance, then its terminal, pause/resume and stop.
    void AddAgentItems(ToolStripItemCollection items)
    {
        foreach (var repo in _agents?.Repos.Where(r => r.Authorized) ?? [])
        {
            var item = new ToolStripMenuItem($"{repo.Leaf} · {Glance(repo)}") { ToolTipText = repo.Path };
            item.DropDownItems.Add("Open agents terminal", null, (_, _) => OpenAgentsTerminal(repo.Path));
            item.DropDownItems.Add(repo.Paused ? "Resume" : "Pause", null, (_, _) =>
            {
                if (repo.Paused) _agents!.Resume(repo);
                else _ = _agents!.PauseAsync(repo);
            });
            item.DropDownItems.Add("Stop", null, (_, _) => _ = _agents!.StopAsync(repo));
            items.Add(item);
        }
        items.Add("Agents terminal in another folder…", null, (_, _) => PickAgentsFolder());
        if (_agents?.Repos.Any(r => r.Running) == true) items.Add("Pause all agents", null, (_, _) => _ = _agents.PauseAsync());
        else if (_agents?.Repos.Any(r => r.Authorized && r.Paused) == true) items.Add("Resume all agents", null, (_, _) => _agents.Resume());
    }

    static string Glance(RepoAgents repo)
    {
        if (repo.Paused) return "paused";
        var parts = new List<string>();
        if (repo.Items.Any(i => i.State is ItemState.Working or ItemState.Checking)) parts.Add("working");
        if (repo.Items.Count(i => i.State == ItemState.NeedsOwner) is > 0 and var needs) parts.Add($"{needs} need{(needs == 1 ? "s" : "")} you");
        if (repo.Items.Count(i => i.Pr != null && i.State != ItemState.Done) is > 0 and var prs) parts.Add($"{prs} PR{(prs == 1 ? "" : "s")} open");
        return parts.Count > 0 ? string.Join(", ", parts) : "idle";
    }

    void OpenAgentsTerminal(string folder)
    {
        var root = GitRoot(folder);
        if (root == null) { Notify("Not a git repository", folder, ToolTipIcon.Warning); return; }
        if (EnsureAgents() is not { } agents) return;
        _settings.AgentLastRepo = root;
        _settings.Save();
        var state = AgentsTerminal.StateOf(agents.RepoFor(root));
        try { AgentsTerminal.Open(_engine, AgentStore.DefaultRoot, root, _settings.PreserveRemoteControl, state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Error("Could not open the agents terminal", e); }
        _labels[root] = state;
    }

    internal static string? GitRoot(string folder)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(folder)); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                return d.FullName.TrimEnd('\\', '/');
        return null;
    }

    /// The owner's own decision, asked by autodock itself rather than by any model, once per repository.
    Task<bool> AuthorizeAgents(RepoAgents repo, string? verify, string baseBranch)
    {
        // A tray app has no window of its own in front; a topmost owner keeps the question visible
        // above the terminal that asked for it.
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        owner.Show();
        var ok = MessageBox.Show(owner,
            $"Let autodock agents work on {repo.Leaf} continuously?\n\n" +
            "• They take on its actionable open GitHub issues one at a time (and requests you make in the agents terminal), " +
            "each in its own git worktree, using your other Claude accounts: never the account you're signed in with, " +
            "which is never switched.\n" +
            "• Each change is reviewed and checked, then autodock pushes a branch and opens a pull request, with at most two " +
            "waiting for your review. Nothing is merged, no issue is closed, and branch protections are never bypassed.\n" +
            "• When an issue needs your decision, the question is posted on the issue and work moves to the next one.\n" +
            "• They keep going after restarts until you pause or stop them (in the agents terminal, or by right-clicking " +
            "the agents button in the autodock flyout).\n" +
            "• You're responsible for following Anthropic's terms (anthropic.com/legal) for the accounts you use.\n\n" +
            $"Pull requests target {baseBranch}. " +
            (verify is { Length: > 0 } v ? $"Before each one, autodock runs this command in the change's worktree:\n    {v}\n\n" : "No verification command: pull requests rely on CI.\n\n") +
            $"{(repo.Authorized ? "Apply these settings to" : "Start agents for")} {repo.Path}?",
            "autodock — agents", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        return Task.FromResult(ok);
    }

    async Task ExitAsync()
    {
        if (_agents is { Working: > 0 } agents)
        {
            var answer = MessageBox.Show(
                "An agent is working.\n\n" +
                "Yes: stop it now. It continues from where it was the next time autodock starts.\n" +
                "No: leave it running. autodock picks it up again when it starts.",
                "autodock", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes) await agents.StopAllAsync();
        }
        ExitThread();
    }
}
