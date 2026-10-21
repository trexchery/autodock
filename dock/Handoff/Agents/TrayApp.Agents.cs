using System.Globalization;

namespace Handoff;

// The tray's side of handoff agents: one launcher in the flyout footer, a few menu items, the owner's
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
        _agents = new Supervisor(_engine, cli, new AgentStore(AgentStore.DefaultRoot),
            new AgentsConfig { Threshold = Threshold, MaxConcurrent = _settings.AgentIssues, MaxOpenPrs = _settings.AgentOpenPrs, DraftPrs = _settings.AgentDraftPrs });
        _agents.Authorize = AuthorizeAgents;
        _agents.ConfirmChecks = ConfirmAgentChecks;
        _agents.Notify = (title, text) => Notify(title, text);
        _agents.Changed += OnAgentsChanged;
        _agents.RefreshWanted += () => _ = TickAsync(runAuto: false, refresh: true);
        _link = new AgentsLink.Server(_agents.HandleAsync, presence: (repo, open) => { if (open) _agents.Attach(repo); else _agents.Detach(repo); });
        _agents.Start();
        _agentsThreshold = ReadEngineThresholdAsync();
        _ = UpdateAgentAccountsAsync();
        return _agents;
    }

    Task _agentsThreshold = Task.CompletedTask;

    /// Agents get accounts only once the engine's threshold is read, so the first dispatch judges headroom at its line, not 97.
    async Task UpdateAgentAccountsAsync()
    {
        await _agentsThreshold;
        if (_snap != null) _agents?.UpdateAccounts(_snap.Accounts, ProjectMap.Load(), LiveOrLaunching());
        MorningSummary(); // every tick too: with no agents terminal open, nothing else wakes it
    }

    void ApplyAgentSettings()
    {
        if (_agents == null) return;
        _agents.Config.MaxConcurrent = _settings.AgentIssues;
        _agents.Config.MaxOpenPrs = _settings.AgentOpenPrs;
        _agents.Config.DraftPrs = _settings.AgentDraftPrs;
        _agents.Poke();
    }

    /// Runs and window moves use the engine's own auto-switch threshold and strategy, so all agree on "near the limit".
    async Task ReadEngineThresholdAsync()
    {
        try
        {
            var cfg = await _engine.ConfigAsync();
            if (double.TryParse(cfg.GetValueOrDefault("autoswitch.threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                _engineThreshold = t;
                if (_agents != null) _agents.Config.Threshold = t;
            }
            if (double.TryParse(cfg.GetValueOrDefault("autoswitch.hysteresisPct"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                _engineHysteresis = h;
            ResumeStopped.Models = (cfg.GetValueOrDefault("autoswitch.model") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_agents != null) _agents.Config.ConsumeFirst = cfg.GetValueOrDefault("autoswitch.strategy") == "consume-first";
        }
        catch (Exception e) when (e is EngineException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            or System.ComponentModel.Win32Exception or IOException) { Program.Log(e); } // never faulted: every tick awaits it
    }

    DateTimeOffset _lastAutoPrime;

    /// With Auto-Prime on, fresh 5-hour windows are started early while agents run, so they also reset early.
    /// The ledger stops a window being primed twice; this only paces the attempts.
    void AutoPrimeForAgents()
    {
        if (!_settings.AutoPrime || _snap == null || _agents is not { IsRunning: true } || _priming || DateTimeOffset.Now - _lastAutoPrime < TimeSpan.FromMinutes(30)) return;
        // The default login near its line: a prime would make the account it switches to (a fresh one) a live profile meanwhile.
        if (_snap.Accounts.FirstOrDefault(a => a.Active) is { } login && ResumeStopped.Peak(login) >= Threshold - (_engineHysteresis ?? 10)) return;
        if (Prime.Ready(_snap.Accounts, DateTimeOffset.Now) is not { Count: > 0 } ready) return;
        _lastAutoPrime = DateTimeOffset.Now;
        _ = PrimeAsync(ready);
    }

    /// Once a day, from 8:00 on: what the agents did meanwhile. ponytail: fixed hour, a setting if it needs to move.
    void MorningSummary()
    {
        var now = DateTimeOffset.Now;
        if (_agents == null || now.Hour < 8 || _settings.AgentSummaryAt?.Date == now.Date) return;
        var since = _settings.AgentSummaryAt ?? now.AddDays(-1);
        _settings.AgentSummaryAt = now;
        _settings.Save();
        if (_agents.Summary(since) is { } text) Notify("Agents since last time", text);
    }

    void OnAgentsChanged()
    {
        if (_agents == null) return;
        MorningSummary();
        AutoPrimeForAgents();
        foreach (var repo in _agents.Repos)
            if (AgentsTerminal.StateOf(repo) is var state && (!_labels.TryGetValue(repo.Path, out var was) || was != state))
            {
                AgentsTerminal.SetState(AgentStore.DefaultRoot, repo.Path, state);
                _labels[repo.Path] = state;
            }
        // The dot only for a real problem: questions and approvals wait in the agents terminal.
        var problem = _agents.HasProblem;
        var working = _agents.Working;
        _dash.SetAgents(problem ? Palette.Warn : working > 0 ? Palette.Current.Live : null,
            problem ? "Agents terminal · something needs fixing" : working > 0 ? "Agents terminal · working" : "Agents terminal");
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
            var open = _agents!.TerminalOpen(repo);
            var item = new ToolStripMenuItem($"{repo.Leaf} · {Glance(repo, open)}") { ToolTipText = repo.Path };
            item.DropDownItems.Add("Open agents terminal", null, (_, _) => OpenAgentsTerminal(repo.Path));
            item.DropDownItems.Add(repo.Paused ? "Resume" : "Pause", null, (_, _) =>
            {
                if (!repo.Paused) { _ = _agents!.PauseAsync(repo); return; }
                _agents!.Resume(repo);
                if (!_agents.TerminalOpen(repo)) OpenAgentsTerminal(repo.Path); // agents work only while it is open
            });
            item.DropDownItems.Add("Stop", null, (_, _) => _ = _agents!.StopAsync(repo));
            items.Add(item);
        }
        items.Add("Agents terminal in another folder…", null, (_, _) => PickAgentsFolder());
        if (_agents?.Repos.Any(r => r.Running) == true) items.Add("Pause all agents", null, (_, _) => _ = _agents.PauseAsync());
        else if (_agents?.Repos.Any(r => r.Authorized && r.Paused) == true) items.Add("Resume all agents", null, (_, _) => _agents.Resume());
    }

    static string Glance(RepoAgents repo, bool open)
    {
        if (repo.Paused) return "paused";
        if (!open) return "waits for its terminal";
        var parts = new List<string>();
        if (repo.Items.Any(i => i.State is ItemState.Working or ItemState.Checking) || repo.Goals.Any(g => g.Run is { Live: true })) parts.Add("working");
        if (repo.Goals.Count(g => g.State == GoalState.Active) is > 0 and var goals) parts.Add($"{goals} goal{(goals == 1 ? "" : "s")}");
        if (repo.Items.Count(i => i.State == ItemState.NeedsOwner) + repo.Goals.Count(g => g.State == GoalState.NeedsOwner) is > 0 and var needs) parts.Add($"{needs} need{(needs == 1 ? "s" : "")} you");
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

    /// The owner's own decision, asked by handoff itself rather than by any model, once per repository.
    Task<bool> AuthorizeAgents(RepoAgents repo, string? verify, string baseBranch)
    {
        // A tray app has no window of its own in front; a topmost owner keeps the question visible
        // above the terminal that asked for it.
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        owner.Show();
        var ok = MessageBox.Show(owner,
            $"Let handoff agents work on {repo.Leaf} while its agents terminal is open?\n\n" +
            "• They take on its actionable open GitHub issues, " + (_settings.AgentIssues > 0 ? $"up to {_settings.AgentIssues} at a time" : "as many at a time as there are accounts with quota to spare") + " (and requests you make in the agents terminal), " +
            "each in its own git worktree, using your other Claude accounts: never the account you're signed in with, " +
            "which is never switched.\n" +
            "• Each change is reviewed and checked, then handoff pushes a branch and opens a pull request, with at most " + _settings.AgentOpenPrs + " " +
            "waiting for your review. Nothing is merged, no issue is closed, and branch protections are never bypassed.\n" +
            "• When an issue needs your decision, the question is posted on the issue and work moves to the next one.\n" +
            "• They work only while this repository's agents terminal is open: closing it pauses them, and reopening it " +
            "continues where they were. Pause or stop them in the agents terminal, or by right-clicking the agents button " +
            "in the handoff flyout.\n" +
            "• You're responsible for following Anthropic's terms (anthropic.com/legal) for the accounts you use.\n\n" +
            $"Pull requests target {baseBranch}. " +
            (verify is { Length: > 0 } v ? $"Before each one, handoff runs this command in the change's worktree:\n    {v}\n\n" : "No verification command: pull requests rely on CI.\n\n") +
            $"{(repo.Authorized ? "Apply these settings to" : "Start agents for")} {repo.Path}?",
            "handoff — agents", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        return Task.FromResult(ok);
    }

    /// The owner's confirmation of the exact commands a goal's checks will run, asked by handoff itself.
    Task<bool> ConfirmAgentChecks(RepoAgents repo, IReadOnlyList<string> checks)
    {
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        owner.Show();
        return Task.FromResult(MessageBox.Show(owner,
            $"Let handoff run these commands unattended, as you, in a fresh checkout of {repo.Base} to decide whether a goal in {repo.Leaf} is achieved?\n\n" +
            string.Join("\n", checks.Select(c => "    " + c)) + "\n\nNothing else asks them to run.",
            "handoff — goal checks", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
    }

    async Task ExitAsync()
    {
        if (_agents is { Working: > 0 } agents)
        {
            if (MessageBox.Show(
                    "An agent is working. Exiting stops it; it continues from where it was the next time its agents terminal is open.",
                    "handoff", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await agents.StopAllAsync();
        }
        ExitThread();
    }
}
