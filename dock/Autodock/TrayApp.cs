namespace Autodock;

public sealed partial class TrayApp : ApplicationContext
{
    readonly Engine _engine;
    readonly DockSettings _settings = DockSettings.Load();
    readonly NotifyIcon _tray;
    readonly DashboardForm _dash = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly ContextMenuStrip _menu = new();
    Snapshot? _snap;
    string? _error;
    // Codex / Grok: polled on the same tick, each failure kept to its own section.
    readonly Dictionary<string, (Snapshot? Snap, string? Error)> _others = [];
    bool _busy;
    bool? _pending; // a request made during a busy pass: null none, else whether it forces a refresh
    DateTime _dashHiddenAt;
    DateTimeOffset? _lastExhaustedNotice;
    QuotaAlerts _quota = new();

    public TrayApp(Engine engine, bool minimized)
    {
        _engine = engine;
        _tray = new NotifyIcon { Icon = TrayIcons.App, Text = "autodock", Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleDashboard(); };
        _menu.Opening += (_, _) => BuildMenu();

        _dash.SwitchRequested += a => _ = SwitchAsync(a);
        _dash.TerminalRequested += a => OpenTerminal(a, null);
        _dash.AccountAction += (a, act) => _ = AccountActionAsync(a, act);
        _dash.RefreshRequested += () => _ = TickAsync(runAuto: false, refresh: true);
        _dash.SettingsRequested += () => _ = ShowSettingsAsync();
        _dash.AddRequested += PickProviderToAdd;
        _dash.AutoToggled += on => SetAutoSwitch(on);
        _dash.VisibleChanged += (_, _) => { if (!_dash.Visible) _dashHiddenAt = DateTime.UtcNow; };
        _dash.SetAuto(_settings.AutoSwitch);
        _dash.PinToggled += pinned => { _settings.Pinned = pinned; SavePinnedPosition(); };
        _dash.Moved += SavePinnedPosition;
        _dash.AgentsRequested += (anchor, menu) => { if (menu) ShowAgentsMenu(anchor); else LaunchAgents(); };

        HardenStores();
        if (AgentStore.HasWork(AgentStore.DefaultRoot)) EnsureAgents(); // authorized earlier: carry on
        _timer.Interval = _settings.RefreshSeconds * 1000;
        _timer.Tick += (_, _) => _ = TickAsync(runAuto: true);
        _timer.Start();
        _ = TickAsync(runAuto: true);
        if (_settings.Pinned)
        {
            _dash.SetPinned(true);
            RenderDash();
            _dash.ShowPinnedAt(_settings.PinnedX, _settings.PinnedY);
        }
        else if (!minimized) _dash.ShowNearTray();
    }

    void HardenStores()
    {
        foreach (var dir in new[] { StoreSecurity.BackupRoot, DockSettings.Dir })
        {
            try { if (!Directory.Exists(dir) || !StoreSecurity.IsHardened(dir)) StoreSecurity.Harden(dir); }
            catch (Exception e) { Notify("Could not restrict folder permissions", $"{dir}: {e.Message}", ToolTipIcon.Warning); }
        }
        ClaudeCli.PurgeEnrollLeftovers(ClaudeCli.EnrollRoot);
    }

    void ToggleDashboard()
    {
        if (_dash.Visible && _dash.Pinned) { _dash.Activate(); return; }
        if (_dash.Visible) { _dash.Hide(); return; }
        // A click on the tray first deactivates (hides) the flyout; don't reopen it.
        if ((DateTime.UtcNow - _dashHiddenAt).TotalMilliseconds < 300) return;
        ShowDashboard();
    }

    void ShowDashboard()
    {
        _ = TickAsync(runAuto: false, refresh: true); // opening it is when freshness matters
        RenderDash();
        if (_dash.Pinned) { _dash.ShowPinnedAt(_settings.PinnedX, _settings.PinnedY); _dash.Activate(); }
        else _dash.ShowNearTray();
    }

    void SavePinnedPosition()
    {
        if (_dash.Pinned) (_settings.PinnedX, _settings.PinnedY) = (_dash.Left, _dash.Top);
        _settings.Save();
    }

    async Task TickAsync(bool runAuto, bool refresh = false)
    {
        if (_busy)
        {
            _pending = refresh || _pending == true; // one re-run right after the current pass
            return;
        }
        _busy = true;
        _dash.SetStatus("updating…");
        var fresh = new List<(string Provider, Snapshot Snap)>(); // this pass's readings only; a failed poll never feeds the quota alerts
        try
        {
            if (runAuto && _settings.AutoSwitch) await AutoTickAsync();
            var reread = _settings.AutoPrime && Prime.WantsReread(DateTimeOffset.Now);
            _snap = await _engine.ListAsync(refresh || reread);
            if (reread) Prime.Reread(DateTimeOffset.Now); // spent only on a reading that came back
            if (_settings.AutoPrime && Prime.Observe(_snap.Accounts, DateTimeOffset.Now)) Prime.Save();
            if (_settings.AutoPrime && Prime.Problem != _primeProblem && (_primeProblem = Prime.Problem) != null)
                Notify("Auto-Prime paused", _primeProblem, ToolTipIcon.Warning);
            fresh.Add(("claude", _snap));
            _agents?.UpdateAccounts(_snap.Accounts, ProjectMap.Load());
            _error = null;
            _dash.SetStatus($"{DateTime.Now:HH:mm}");
        }
        catch (Exception e)
        {
            _error = Engine.Redact(e.Message);
            _dash.SetStatus("offline");
        }
        try { await PollProvidersAsync(refresh, fresh); }
        finally
        {
            _busy = false;
        }
        if (_settings.QuotaNotifications
            && QuotaAlerts.Balloon(fresh.SelectMany(f => _quota.Observe(f.Provider, f.Snap.Accounts)).ToList()) is { } balloon)
            Notify(balloon.Title, balloon.Text);
        // Tray dot and tooltip follow Claude, unless Claude is unused and exactly one other provider is.
        var withAccounts = _others.Values.Where(o => o.Snap is { Accounts.Count: > 0 }).ToList();
        var basis = _snap is not { Accounts.Count: > 0 } && withAccounts.Count == 1 ? withAccounts[0].Snap : _snap;
        var active = basis?.Accounts.FirstOrDefault(a => a.Active);
        var old = _tray.Icon;
        _tray.Icon = TrayIcons.WithDot(active?.ShownUsage == null ? null : Format.Peak(active), _settings.AutoSwitch);
        if (old != TrayIcons.App) old?.Dispose();
        _tray.Text = Format.Tooltip(basis, _settings.AutoSwitch);
        if (_dash.Visible) RenderDash();
        if (_pending is { } again)
        {
            _pending = null;
            _ = TickAsync(runAuto: false, refresh: again);
        }
    }

    async Task PollProvidersAsync(bool refresh, List<(string Provider, Snapshot Snap)> fresh)
    {
        foreach (var p in ProviderCli.All)
        {
            if (!ProviderCli.Present(p)) { _others.Remove(p); continue; }
            try
            {
                var s = await _engine.ListAsync(refresh, p);
                _others[p] = (s, null);
                fresh.Add((p, s));
            }
            catch (Exception e) { _others[p] = (_others.GetValueOrDefault(p).Snap, Engine.Redact(e.Message)); }
        }
    }

    void RenderDash() =>
        _dash.Render(_snap, _error, ProviderCli.All.Where(_others.ContainsKey).Select(p => (p, _others[p].Snap, _others[p].Error)).ToList(), ProjectMap.Load());

    /// "+" with several CLIs installed: a small themed menu picks which provider to add.
    void PickProviderToAdd()
    {
        var installed = ProviderCli.All.Where(p => ProviderCli.Find(p) != null).ToList();
        if (installed.Count == 0) { _ = EnrollAsync(null); return; }
        var menu = new ContextMenuStrip();
        foreach (var p in installed.Prepend("claude"))
            menu.Items.Add(ProviderCli.Title(p) + " account…", null, (_, _) => _ = EnrollAsync(null, p));
        MenuRenderer.Apply(menu);
        MenuRenderer.DisposeOnClose(menu);
        menu.Show(Cursor.Position);
    }

    async Task AutoTickAsync()
    {
        var (_, events) = await _engine.AutoOnceAsync();
        foreach (var e in events)
        {
            switch (e.Event)
            {
                case "switch" when !e.DryRun:
                    Notify("", Format.SwitchNotice(e.From, e.To), ToolTipIcon.None);
                    break;
                case "account-quarantined":
                    Notify("Account needs re-enrollment", $"{e.Email}: {e.Reason}", ToolTipIcon.Warning);
                    break;
                case "all-exhausted" when e.EarliestResetAt != _lastExhaustedNotice:
                    _lastExhaustedNotice = e.EarliestResetAt;
                    Notify("All accounts near their limits",
                        e.EarliestResetAt is { } t ? $"Earliest reset {Format.When(t, DateTimeOffset.Now)}." : "No reset time known.",
                        ToolTipIcon.Warning);
                    break;
            }
        }
    }

    static string Name(AccountRef? r) => r == null ? "(none)" : $"#{r.Number} {r.Email}";

    void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) =>
        _tray.ShowBalloonTip(5000, title, Engine.Redact(text), icon);

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        var sw = new ToolStripMenuItem("Switch to");
        var term = new ToolStripMenuItem("Open terminal as");
        foreach (var a in _snap?.Accounts ?? [])
        {
            var label = $"#{a.Number} {(string.IsNullOrEmpty(a.Alias) ? a.Email : a.Alias)}";
            sw.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => _ = SwitchAsync(a)) { Checked = a.Active, Enabled = !a.Active });
            term.DropDownItems.Add(label, null, (_, _) => OpenTerminal(a, null));
        }
        sw.Enabled = term.Enabled = sw.DropDownItems.Count > 0;
        _menu.Items.Add(sw);
        _menu.Items.Add(term);
        var proj = new ToolStripMenuItem("Project terminals");
        foreach (var m in ProjectMap.Load())
            proj.DropDownItems.Add(new ToolStripMenuItem($"{m.Leaf} · {m.Email}", null, (_, _) => OpenProjectTerminal(m)) { ToolTipText = m.Folder });
        if (proj.HasDropDownItems) _menu.Items.Add(proj);
        var cont = new ToolStripMenuItem("Continue interrupted work");
        foreach (var src in ProviderCli.Every)
            foreach (var t in ProviderCli.HandoffTargets(src))
                cont.DropDownItems.Add($"{ProviderCli.Title(src)} → {ProviderCli.Title(t)}…", null, (_, _) => _ = ContinueAsync(src, t));
        cont.Enabled = cont.HasDropDownItems;
        _menu.Items.Add(cont);
        if (_agents?.Repos.Count > 0)
        {
            var agents = new ToolStripMenuItem("Agents");
            AddAgentItems(agents.DropDownItems);
            _menu.Items.Add(agents);
        }
        _menu.Items.Add(new ToolStripMenuItem("Auto-switch near limit", null, (_, _) => SetAutoSwitch(!_settings.AutoSwitch)) { Checked = _settings.AutoSwitch });
        if (_settings.AutoPrime && _snap != null) AddPrimeItems(_snap.Accounts);
        _menu.Items.Add("Add account…", null, (_, _) => _ = EnrollAsync(null));
        _menu.Items.Add("Refresh now", null, (_, _) => _ = TickAsync(runAuto: false, refresh: true));
        _menu.Items.Add("Settings…", null, (_, _) => _ = ShowSettingsAsync());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => _ = ExitAsync());
        MenuRenderer.Apply(_menu);
    }

    Primer? _primer;
    Primer Prime => _primer ??= new(Primer.DefaultPath);
    bool _priming;
    string? _primeProblem;

    /// One click primes every fresh window; the status lines say what happened and what's next.
    void AddPrimeItems(IReadOnlyList<Account> accounts)
    {
        var now = DateTimeOffset.Now;
        var ready = Prime.Ready(accounts, now);
        if (ready.Count > 0)
            _menu.Items.Add(new ToolStripMenuItem($"Prime {ready.Count} fresh 5-hour window{(ready.Count == 1 ? "" : "s")}", null, (_, _) => _ = PrimeAsync(ready))
            {
                Enabled = !_priming,
                ToolTipText = string.Join(", ", ready.Select(a => $"#{a.Number} {a.ShortName}")),
            });
        var status = new ToolStripMenuItem("Auto-Prime");
        foreach (var line in Prime.Status(accounts, now).DefaultIfEmpty("No fresh 5-hour windows right now"))
            status.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
        _menu.Items.Add(status);
    }

    /// Silent: no window, no transcript, no notification unless something needs the owner.
    async Task PrimeAsync(List<Account> shown)
    {
        if (_priming) return;
        _priming = true;
        var failed = new List<string>();
        try
        {
            // Eligibility from a reading taken now, not the menu's: a window may have started or a limit moved since.
            var snap = await _engine.ListAsync(refresh: true);
            var now = DateTimeOffset.Now;
            if (Prime.Observe(snap.Accounts, now)) Prime.Save();
            foreach (var a in Prime.Ready(snap.Accounts, now, Primer.SendReadingAge).Where(a => shown.Any(s => Primer.Same(s, a))))
            {
                if (!Prime.Begin(a, DateTimeOffset.Now)) break; // unrecorded, a send could prime this window twice
                var (result, detail) = await _engine.PrimeAsync(a);
                Prime.Finish(a, result, detail);
                if (result == PrimeResult.Refused) failed.Add($"#{a.Number} {a.ShortName}: {detail}");
                if (result == PrimeResult.Unknown) failed.Add($"#{a.Number} {a.ShortName}: {detail} (it may have gone out, so not again for this window)");
            }
            if (Prime.Problem != null) failed.Add(Prime.Problem);
        }
        catch (Exception e) { failed.Add(e.Message); } // e.g. no fresh reading: nothing sent
        finally { _priming = false; }
        if (failed.Count > 0) Notify("Auto-Prime", "Could not prime " + string.Join("; ", failed), ToolTipIcon.Warning);
        _ = TickAsync(runAuto: false);
    }

    async Task SwitchAsync(Account a)
    {
        if (a.Provider != "claude")
        {
            // Only the login file is rewritten atomically; running sessions are never touched.
            try
            {
                var pr = await _engine.SwitchAsync(a.Number, a.Provider);
                Notify(pr.Switched ? $"Switched {ProviderCli.Title(a.Provider)} account" : "No switch",
                    pr.Switched ? $"Now using {Name(pr.To)}. {pr.Note}" : pr.Reason ?? "Already active");
            }
            catch (Exception e) { Error("Switch failed", e); }
            await TickAsync(runAuto: false);
            return;
        }
        var running = ClaudeCli.RunningCount();
        if (running > 0 && MessageBox.Show(
                $"{running} Claude Code process(es) are running.\n\n" +
                "Switching never stops them. A response already in progress finishes on the current account; " +
                "sessions on the default login use the new account from their next message (the first one may " +
                "use extra quota while its cache rebuilds). Terminals opened for another account keep it; " +
                "one opened for the active account follows switches.\n\n" +
                $"Switch to #{a.Number} {a.Email}?",
                "autodock", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            return;
        try
        {
            var r = await _engine.SwitchAsync(a.Number);
            if (r.Switched) Notify("", Format.SwitchNotice(r.From, r.To), ToolTipIcon.None);
            else Notify("No switch", r.Reason ?? "Already active");
        }
        catch (Exception e) { Error("Switch failed", e); }
        await TickAsync(runAuto: false);
    }

    void OpenTerminal(Account a, string? folder)
    {
        try
        {
            if (a.Provider != "claude")
            {
                var title = $"{ProviderCli.Title(a.Provider)} · {a.Email}";
                // The live login runs the plain CLI: `run` refuses it, since two copies would share a single-use refresh token.
                if (a.Active && ProviderCli.Find(a.Provider) is { } cli) Engine.LaunchConsoleWith(cli, title, folder);
                else _engine.LaunchConsole(title, folder, a.Provider, "run", a.Number.ToString());
                return;
            }
            // `cswap run N` pins this console to account N via an isolated profile; global switches
            // and auto-switch never touch it. When N is the active login, the engine runs plain `claude`
            // instead (no second copy of the refresh token), which follows later switches.
            _engine.LaunchConsole($"Claude · {a.ShortName}", folder, Launch.Run(a.Number, _settings.PreserveRemoteControl));
        }
        catch (Exception e) { Error("Could not open terminal", e); }
    }

    /// With "Use project accounts automatically", the account assigned to `folder` (else null).
    Account? MappedAccount(string folder) =>
        _settings.AutoProjectAccount ? ProjectMap.AccountFor(folder, _snap?.Accounts ?? [], ProjectMap.Load()) : null;

    /// `cswap run` with no account in the folder: the engine resolves the assignment itself.
    void OpenProjectTerminal(ProjectMapping m)
    {
        if (!Directory.Exists(m.Folder)) { Notify("Folder not found", m.Folder, ToolTipIcon.Warning); return; }
        try { _engine.LaunchConsole($"Claude · {m.Leaf} · {m.Email}", m.Folder, Launch.Run(null, _settings.PreserveRemoteControl)); }
        catch (Exception e) { Error("Could not open terminal", e); }
    }

    /// Starts a new session in `target` with notes from the interrupted `source` session.
    /// Not a resume: the notes are built locally and autodock sends nothing; the
    /// target agent reads them like any prompt.
    async Task ContinueAsync(string source, string target)
    {
        if (PickFolder($"Folder of the interrupted {ProviderCli.Title(source)} work") is not { } folder) return;
        try
        {
            var exe = ProviderCli.FindAny(target) ?? throw new EngineException($"{ProviderCli.Title(target)} CLI not found.");
            var h = await _engine.HandoffAsync(source, folder);
            var prompt = $"Read the hand-off notes in {h.Path} and continue the interrupted task.";
            var mapped = target == "claude" ? MappedAccount(folder) : null;
            var who = mapped ?? (target == "claude" ? _snap : _others.GetValueOrDefault(target).Snap)?.Accounts.FirstOrDefault(a => a.Active);
            var title = $"{ProviderCli.Title(target)} · continue" + (who == null ? "" : " · " + who.ShortName);
            if (target == "grok")
            {
                // ponytail: no known initial-prompt argument for grok; pass it as an arg once one exists.
                Clipboard.SetText(prompt);
                Engine.LaunchConsoleWith(exe, title, folder);
                Notify("Hand-off ready", "Paste the copied line into Grok to start with the notes.");
                return;
            }
            // Claude goes through `cswap run` so Remote Control can be restored. In a folder assigned to an
            // account the engine picks it itself; else the active account, which runs plain claude. No known account: direct.
            if (target == "claude" && Launch.Continue(mapped, who, _settings.PreserveRemoteControl, prompt) is { } run)
                _engine.LaunchConsole(title, folder, run);
            else Engine.LaunchConsoleWith(exe, title, folder, prompt);
            Notify("Hand-off ready", $"New {ProviderCli.Title(target)} session started with notes from the interrupted one.");
        }
        catch (Exception e) { Error("Could not continue", e); }
    }

    async Task AccountActionAsync(Account a, string action)
    {
        try
        {
            switch (action)
            {
                case not null when action.StartsWith("continue:"):
                    await ContinueAsync(a.Provider, action["continue:".Length..]);
                    return;
                case "terminal-folder":
                    if (PickFolder($"Open a terminal as #{a.Number} in…") is { } f)
                        OpenTerminal(a.Provider == "claude" ? MappedAccount(f) ?? a : a, f);
                    return;
                case "map":
                    if (PickFolder($"Project folder for #{a.Number} (`cswap run` there uses this account)") is not { } m) return;
                    Notify("Project folder assigned", await _engine.CommandAsync("map", a.Number.ToString(), m));
                    break;
                case not null when action.StartsWith("project-open:"):
                    var dir = action["project-open:".Length..];
                    if (Directory.Exists(dir)) OpenTerminal(a, dir);
                    else Notify("Folder not found", dir, ToolTipIcon.Warning);
                    return;
                case not null when action.StartsWith("project-remove:"):
                    Notify("Project folder removed", await _engine.CommandAsync("unmap", action["project-remove:".Length..]));
                    break;
                case "enable" or "disable":
                    await _engine.CommandAsync(action, a.Number.ToString());
                    break;
                case "alias":
                    var alias = Prompt.Show("Set alias", $"Short name for {a.Email} (blank removes it):", a.Alias ?? "");
                    if (alias == null) return;
                    await _engine.CommandAsync(alias.Length == 0
                        ? ["alias", a.Number.ToString(), "--unset"]
                        : ["alias", a.Number.ToString(), alias]);
                    break;
                case "reenroll":
                    await EnrollAsync(a, a.Provider);
                    return;
                case "remove" when a.Provider != "claude":
                    if (MessageBox.Show($"Remove {ProviderCli.Title(a.Provider)} #{a.Number} {a.Email} from autodock?\n\n" +
                                        "Its stored login is deleted. The current login is not logged out.",
                            "autodock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    await _engine.CommandAsync(a.Provider, "remove", a.Number.ToString(), "--json");
                    break;
                case "remove":
                    if (MessageBox.Show($"Remove #{a.Number} {a.Email} from autodock?\n\nIts stored backup is deleted. " +
                                        "Your current Claude Code login is not logged out.",
                            "autodock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    await _engine.CommandWithInputAsync("y\n", "remove", a.Number.ToString());
                    break;
            }
        }
        catch (Exception e) { Error("Action failed", e); }
        await TickAsync(runAuto: false);
    }

    async Task EnrollAsync(Account? existing, string provider = "claude")
    {
        if (provider != "claude")
        {
            var title = ProviderCli.Title(provider);
            if (MessageBox.Show(
                    (existing == null ? $"Add a {title} account.\n\n" : $"Log in again as {existing.Email}.\n\n") +
                    $"A console opens with the official login (`{provider} login`). Sign in in the browser " +
                    "(a private window avoids approving the account you're already signed in with).\n\n" +
                    "This uses a separate, temporary profile: your current login and running sessions are not affected.",
                    "autodock", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            try
            {
                // First account: keep the login already on this machine too (errors when there is none).
                if (_others.GetValueOrDefault(provider).Snap is not { Accounts.Count: > 0 })
                    try { await _engine.CommandAsync(provider, "add-current", "--json"); } catch (EngineException) { }
                await ProviderCli.EnrollAsync(_engine, provider);
                Notify("Account enrolled", $"{title} account added.");
            }
            catch (Exception e) { Error("Enrollment failed", e); }
            await TickAsync(runAuto: false);
            return;
        }
        if (MessageBox.Show(
                (existing == null ? "Add a Claude account.\n\n" : $"Log in again as {existing.Email}.\n\n") +
                "A console opens with the official Claude Code login (`claude auth login`).\n\n" +
                "1. Open the login link in a private browser window, so you don't approve the account you're already signed in with.\n" +
                "2. Sign in and authorize.\n" +
                "3. If a code is shown, paste it into the console and press Enter. Nothing appears while you paste; that is normal.\n\n" +
                "The console closes when done. Your password is only ever entered in the browser, never in the console.\n\n" +
                "This uses a separate, temporary profile: your current login and running sessions are not affected.",
                "autodock", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        try
        {
            // First run: keep the login already on this machine as an account too.
            if (_snap is { Accounts.Count: 0 })
            {
                var status = await _engine.RunAsync(["status", "--json"]);
                using var doc = System.Text.Json.JsonDocument.Parse(status.Stdout);
                if (doc.RootElement.TryGetProperty("active", out var act) && act.ValueKind == System.Text.Json.JsonValueKind.Object
                    && act.TryGetProperty("managed", out var managed) && managed.ValueKind == System.Text.Json.JsonValueKind.False)
                    await _engine.CommandAsync("add");
            }
            var msg = await ClaudeCli.EnrollAsync(_engine);
            Notify("Account enrolled", msg);
        }
        catch (Exception e) { Error("Enrollment failed", e); }
        await TickAsync(runAuto: false);
    }

    void SetAutoSwitch(bool on)
    {
        if (on && !_settings.AutoSwitchPolicyAccepted)
        {
            var ok = MessageBox.Show(
                "Auto-switch moves Claude Code to another of your accounts when the active one nears its limit.\n\n" +
                "• You are responsible for following Anthropic's Consumer Terms and Usage Policy (anthropic.com/legal), " +
                "including any rules on multiple accounts and usage limits. Use only accounts that are yours.\n" +
                "• It never bypasses a limit: an exhausted account stays exhausted until it resets.\n" +
                "• Excluded accounts, accounts whose login has expired, and API-key accounts are never chosen.\n" +
                "• Sessions on the default login move to the new account from their next message; a response in " +
                "progress is not interrupted. Terminals opened for another account keep it; one opened for the " +
                "active account follows switches.\n" +
                "• Polling is conservative: the engine spaces out usage checks per account.\n\n" +
                "autodock is an independent project, not affiliated with or endorsed by Anthropic.\n\nEnable auto-switch?",
                "autodock — auto-switch", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
            if (!ok) { _dash.SetAuto(false); return; }
            _settings.AutoSwitchPolicyAccepted = true;
        }
        _settings.AutoSwitch = on;
        _settings.Save();
        _dash.SetAuto(on);
        _ = TickAsync(runAuto: true);
    }

    async Task ShowSettingsAsync()
    {
        using var f = new SettingsForm();
        try
        {
            var cfg = await _engine.ConfigAsync();
            if (double.TryParse(cfg.GetValueOrDefault("autoswitch.threshold"), System.Globalization.CultureInfo.InvariantCulture, out var t))
                f.Threshold.Value = Math.Clamp((decimal)t, f.Threshold.Minimum, f.Threshold.Maximum);
            f.StrategyValue = cfg.GetValueOrDefault("autoswitch.strategy") ?? "best";
            f.Models.Text = cfg.GetValueOrDefault("autoswitch.model") ?? "";
        }
        catch (Exception e) { Error("Could not read engine settings", e); return; }
        f.RefreshInterval.Value = _settings.RefreshSeconds;
        f.Startup.Checked = Startup.IsEnabled();
        f.PreserveRc.Checked = _settings.PreserveRemoteControl;
        f.AutoProject.Checked = _settings.AutoProjectAccount;
        f.QuotaNotify.Checked = _settings.QuotaNotifications;
        f.AutoPrime.Checked = _settings.AutoPrime;
        if (f.ShowDialog() != DialogResult.OK) return;
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            await _engine.CommandAsync("config", "set", "autoswitch.threshold", f.Threshold.Value.ToString(inv));
            await _engine.CommandAsync("config", "set", "autoswitch.strategy", f.StrategyValue);
            var models = f.Models.Text.Trim();
            await _engine.CommandAsync(models.Length == 0
                ? ["config", "unset", "autoswitch.model"]
                : ["config", "set", "autoswitch.model", models]);
            Startup.Set(f.Startup.Checked);
            _settings.RefreshSeconds = (int)f.RefreshInterval.Value;
            _settings.PreserveRemoteControl = f.PreserveRc.Checked;
            _settings.AutoProjectAccount = f.AutoProject.Checked;
            if (f.QuotaNotify.Checked != _settings.QuotaNotifications) _quota = new(); // switching on starts from a fresh, silent baseline
            _settings.QuotaNotifications = f.QuotaNotify.Checked;
            _settings.AutoPrime = f.AutoPrime.Checked;
            _settings.Save();
            _ = ReadEngineThresholdAsync(); // agents follow the engine's threshold
            _timer.Interval = _settings.RefreshSeconds * 1000;
        }
        catch (Exception e) { Error("Could not save settings", e); }
    }

    static string? PickFolder(string description)
    {
        using var d = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
    }

    static void Error(string title, Exception e) =>
        MessageBox.Show(Engine.Redact(e.Message), "autodock — " + title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _link?.Dispose();
        _agents?.Dispose(); // workers keep running; the next start re-attaches
        _tray.Visible = false;
        _tray.Dispose();
        _dash.Dispose();
        base.ExitThreadCore();
    }
}

static class Prompt
{
    public static string? Show(string title, string text, string value)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(340, 110), Font = new Font("Segoe UI", 9f) };
        var label = new Label { Text = text, Location = new Point(12, 10), Size = new Size(316, 32) };
        var box = new TextBox { Text = value, Location = new Point(12, 44), Width = 316 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(172, 76) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(253, 76) };
        f.Controls.AddRange([label, box, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }
}
