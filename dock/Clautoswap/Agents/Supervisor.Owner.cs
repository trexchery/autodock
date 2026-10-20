using System.Text;
using System.Text.Json;

namespace Clautoswap;

// What the agents terminal asks for (through the clautoswap MCP tools), the answers, and the prompts.
public sealed partial class Supervisor
{
    /// One tool call from the agents terminal, for the repository the terminal was opened in.
    public async Task<string> HandleAsync(string tool, JsonElement args, string repoPath)
    {
        var repo = RepoFor(repoPath);
        string Str(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        switch (tool)
        {
            case "agents_status":
                return Status(repo, repoPath, args.ValueKind == JsonValueKind.Object && args.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.True);

            case "agents_control":
                var action = Str("action");
                if (action == "start" || (action == "resume" && repo is not { Authorized: true }))
                    return await StartRepoAsync(repoPath, Str("verify"), Str("base"));
                if (repo == null) return "Agents were never started for this repository.";
                switch (action)
                {
                    case "pause":
                        var n = _workers.Keys.Count(repo.Items.Contains);
                        await PauseAsync(repo);
                        return n > 0 ? "Paused. The item in progress stopped where it was and continues from there on resume." : "Paused.";
                    case "resume":
                        Resume(repo);
                        return "Resumed.";
                    case "stop":
                        await StopAsync(repo);
                        return "Stopped continuous work here. Open pull requests stay open; nothing was merged or deleted.";
                }
                return "Unknown action; use start, pause, resume or stop.";

            case "agents_queue":
                if (repo == null) return "Agents were never started for this repository.";
                return await QueueCommandAsync(repo, Str("action"), args, Str("item"), Str("text"), Str("title"));

            case "agents_memory":
                var shared = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("global", out var g) && g.ValueKind == JsonValueKind.True;
                var notes = shared ? _state.Memory : repo?.Memory;
                var what = Str("text");
                switch (Str("action"))
                {
                    case "list" or "":
                        return Memory.List(_state.Memory, repo?.Memory ?? []);
                    case "add" or "forget" when notes == null:
                        return "Agents were never started for this repository; use global to note something for every repository.";
                    case "add":
                        if (what.Length == 0) return "Say what to remember.";
                        Memory.Add(notes!, what, "owner", true, DateTimeOffset.Now);
                        Save();
                        return shared ? "Noted for every repository." : "Noted for this repository.";
                    case "forget":
                        // List prints shared notes as g1, g2 …
                        what = what.Trim();
                        if (System.Text.RegularExpressions.Regex.IsMatch(what, @"^g\d+\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) (notes, what) = (_state.Memory, what[1..].TrimEnd('.'));
                        var gone = Memory.Forget(notes!, what);
                        Save();
                        return gone == 0 ? "No such note; the numbers are those agents_memory list prints." : $"Forgot {gone} note{(gone == 1 ? "" : "s")}.";
                }
                return "Unknown action; use list, add or forget.";

            case "agents_details":
                if (repo == null) return "Agents were never started for this repository.";
                return Find(repo, Str("item")) is { } item ? Details(repo, item) : NoItem;
        }
        return "Unknown tool.";
    }

    async Task<string> StartRepoAsync(string path, string verify, string baseBranch)
    {
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
            return $"{path} is not the root of a git repository.";
        var repo = RepoFor(path);
        var fresh = repo == null;
        repo ??= new RepoAgents { Path = Path.GetFullPath(path).TrimEnd('\\', '/') };
        var newVerify = verify.Length > 0 ? verify : repo.Verify;
        var newBase = baseBranch.Length > 0 ? baseBranch : fresh || !repo.Authorized ? await DefaultBranchAsync(repo.Path) : repo.Base;
        // The command clautoswap will run unattended, and where pushes go, are the owner's to see and confirm,
        // first time and whenever they change.
        if (!repo.Authorized || newVerify != repo.Verify || newBase != repo.Base)
        {
            if (Authorize == null || !await Authorize(repo, newVerify, newBase)) return "The owner did not confirm in the clautoswap dialog, so nothing changed.";
            repo.AuthorizedAt = DateTimeOffset.Now;
        }
        if (fresh) _state.Repos.Add(repo);
        repo.Authorized = true;
        repo.Paused = false;
        repo.Note = null;
        (repo.Verify, repo.Base) = (newVerify, newBase);
        repo.LastScan = null;
        Save();
        Fire(() => VerifyBaselineAsync(repo));
        Pump();
        return $"Started. Agents now work through the actionable open issues of {repo.Leaf}, one at a time against {repo.Base}" +
               (repo.Verify is { } v ? $", gated by `{v}`" : " (no verification command, so pull requests rely on CI)") +
               $", with at most {Config.MaxOpenPrs} pull requests waiting for review. They work while this terminal is open: closing it pauses them, and reopening it continues.";
    }

    async Task<string> DefaultBranchAsync(string path)
    {
        var head = await GitAsync(path, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        if (head.Ok && head.Out.Trim() is { Length: > 7 } h && h.StartsWith("origin/")) return h[7..];
        var current = await GitAsync(path, "rev-parse", "--abbrev-ref", "HEAD");
        return current.Ok && current.Out.Trim() is { Length: > 0 } c && c != "HEAD" ? c : "main";
    }

    async Task<string> QueueCommandAsync(RepoAgents repo, string action, JsonElement args, string which, string text, string title)
    {
        switch (action)
        {
            case "prioritize":
                var numbers = args.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : [];
                if (numbers.Count == 0) return "Name the issues in the order they should run, e.g. [12, 7].";
                var top = Math.Max(repo.Items.Select(i => i.Rank).DefaultIfEmpty(0).Max(), OwnerRank);
                var missing = new List<int>();
                for (var k = 0; k < numbers.Count; k++)
                {
                    repo.Skipped.Remove(numbers[k]);
                    var item = repo.Items.FirstOrDefault(i => i.Issue == numbers[k]) ?? await AddIssueAsync(repo, numbers[k]);
                    if (item == null) { missing.Add(numbers[k]); continue; }
                    item.Rank = top + (numbers.Count - k) * 10;
                }
                Save();
                Pump();
                return "Order updated." + (missing.Count > 0 ? $" Not found or not open: {string.Join(", ", missing.Select(m => "#" + m))}." : "");

            case "skip":
                if (Find(repo, which) is not { } skip) return NoItem;
                if (skip.Issue is { } si && !repo.Skipped.Contains(si)) repo.Skipped.Add(si);
                await StopWorkerAsync(skip, "skipped");
                await FinishAsync(repo, skip, "skipped by the owner");
                if (skip.Pr == null) repo.Items.Remove(skip);
                Save();
                Pump();
                return $"{skip.Label} is skipped" + (skip.Pr is { } p ? $"; pull request #{p} is left as it is." : ".");

            case "restore":
                if (!int.TryParse(which.TrimStart('#'), out var rn) || !repo.Skipped.Remove(rn)) return "That issue was not skipped.";
                repo.LastScan = null;
                Save();
                Pump();
                return $"#{rn} can be picked up again.";

            case "add":
                if (title.Length == 0 && text.Length == 0) return "Describe the work (title and details).";
                var request = new WorkItem
                {
                    Id = "w" + repo.NextLocal++,
                    Title = title.Length > 0 ? title : text.Split('\n')[0],
                    Body = text,
                    Rank = Math.Max(repo.Items.Select(i => i.Rank).DefaultIfEmpty(0).Max(), OwnerRank) + 10,
                };
                repo.Items.Add(request);
                Save();
                Pump();
                return $"Queued as {request.Id}" + (repo.Running ? "." : "; it starts once agents are running here.");

            case "answer" or "retry":
                if (Find(repo, which) is not { } it) return NoItem;
                if (it.State is not (ItemState.NeedsOwner or ItemState.Done)) return $"{it.Label} is {Describe(it)}; nothing to answer.";
                if (it.HoldFor != null && text.Length == 0 && it.Worktree != null)
                {
                    // The owner approves the push as it is.
                    (it.State, it.PushApproved, it.HoldFor, it.Note) = (ItemState.Checking, true, null, null);
                    Save();
                    Fire(() => CheckAsync(repo, it));
                    return $"{it.Label}: pushing it as it is.";
                }
                if (text.Length > 0)
                {
                    it.Guidance = text;
                    Memory.Add(repo.Memory, $"{it.Label}: {text}", "answer", true, DateTimeOffset.Now);
                }
                (it.State, it.Failures, it.Note, it.AskedAt, it.HoldFor, it.Reviews) = (ItemState.Queued, 0, null, null, null, 0);
                if (it.Followups > MaxFollowups) it.Followups = 0;
                Save();
                Pump();
                return $"{it.Label} is queued again" + (text.Length > 0 ? " with your answer." : ".");
        }
        return "Unknown action; use prioritize, skip, restore, add, answer or retry.";
    }

    const string NoItem = "No such item; name it as #12 (issue) or w3 (request).";

    static WorkItem? Find(RepoAgents repo, string which)
    {
        which = which.Trim().TrimStart('#');
        return repo.Items.FirstOrDefault(i => i.Id.Equals(which, StringComparison.OrdinalIgnoreCase));
    }

    // -- answers ------------------------------------------------------------

    /// What happened since `since`, for the morning notification; null when nothing did.
    public string? Summary(DateTimeOffset since)
    {
        var items = Repos.SelectMany(r => r.Items).ToList();
        var opened = items.Where(i => i.Pr != null && i.PrSeen >= since).ToList();
        var asks = items.Count(i => i.State == ItemState.NeedsOwner && !Problem(i));
        var stuck = items.Count(Problem);
        if (opened.Count + asks + stuck == 0) return null;
        var parts = new List<string>();
        if (opened.Count > 0) parts.Add($"{opened.Count} pull request{(opened.Count == 1 ? "" : "s")} opened ({string.Join(", ", opened.Take(5).Select(i => "#" + i.Pr))}{(opened.Count > 5 ? ", …" : "")})");
        if (asks > 0) parts.Add($"{asks} waiting on you");
        if (stuck > 0) parts.Add($"{stuck} stuck");
        return string.Join(" · ", parts);
    }

    /// Outcomes first. `detail` adds the machinery: accounts, attempts, sessions, worktrees.
    public string Status(RepoAgents? repo, string path, bool detail)
    {
        if (repo == null || (!repo.Authorized && repo.Items.Count == 0))
            return $"Agents are not running for {Path.GetFileName(path.TrimEnd('\\', '/'))}. Ask to start them if they should: clautoswap asks the owner to confirm in a native dialog first.";
        var sb = new StringBuilder();
        var actives = repo.Items.Where(i => i.State is ItemState.Working or ItemState.Checking).ToList();
        var prs = repo.Items.Where(i => i.Pr != null && i.State != ItemState.Done).ToList();
        var needs = repo.Items.Where(i => i.State == ItemState.NeedsOwner).ToList();
        var queued = repo.Items.Where(i => i.State == ItemState.Queued).OrderByDescending(i => i.Rank).ThenBy(i => i.Issue ?? int.MaxValue).ToList();
        var mode = !repo.Authorized ? "stopped" : repo.Paused ? "paused" : _baseline.Contains(repo) ? "checking the verification command" : "running";
        sb.AppendLine($"{repo.Leaf}: agents {mode}.");
        if (repo.Note != null) sb.AppendLine(repo.Note);
        foreach (var active in actives) sb.AppendLine($"Working on {active.Label} ({Describe(active)}).{(detail ? Machinery(repo, active) : "")}");
        foreach (var i in prs) sb.AppendLine($"Pull request #{i.Pr} for {i.Label}: {(i.State == ItemState.Queued ? "being updated after feedback" : "waiting for review")}{(i.PrUrl is { } u ? " · " + u : "")}{(detail ? Machinery(repo, i) : "")}");
        foreach (var i in needs) sb.AppendLine($"Needs you: {i.Label}: {i.Note}{(i.AskedAt != null ? " (also asked on the issue)" : "")}{(detail ? Machinery(repo, i) : "")}");
        if (queued.Count > 0)
            sb.AppendLine("Up next: " + string.Join(", ", queued.Take(detail ? 50 : 5).Select(i => i.Label)) + (queued.Count > 5 && !detail ? $" (+{queued.Count - 5} more)" : ""));
        if (repo.Running && actives.Count == 0 && queued.Count > 0)
            sb.AppendLine(prs.Count >= Config.MaxOpenPrs && Next(repo, Config.MaxOpenPrs) == null
                ? $"Holding new work: {prs.Count} pull requests are waiting for review."
                : Waiting != null ? $"Waiting: {Waiting}." : "");
        if (repo.Running && actives.Count == 0 && queued.Count == 0)
            sb.AppendLine($"Nothing else is eligible right now; clautoswap checks the issues again every {Config.ScanEvery.TotalMinutes:0} minutes.");
        if (detail)
        {
            sb.AppendLine($"Base {repo.Base}; verification {(repo.Verify is { } v ? $"`{v}`" : "none")}; permission mode {PermissionMode}.");
            foreach (var (k, until) in _cooled.Where(c => c.Value > DateTimeOffset.Now)) sb.AppendLine($"Resting {k.Split('|')[0]} until {until.ToLocalTime():HH:mm}.");
            if (repo.Memory.Count + _state.Memory.Count > 0) sb.AppendLine($"Memory: {repo.Memory.Count} notes here, {_state.Memory.Count} shared across repositories.");
            if (repo.Skipped.Count > 0) sb.AppendLine("Skipped: " + string.Join(", ", repo.Skipped.Select(s => "#" + s)));
            var done = repo.Items.Where(i => i.State == ItemState.Done).ToList();
            if (done.Count > 0) sb.AppendLine("Done: " + string.Join("; ", done.Select(i => $"{i.Label} ({i.Note})")));
        }
        return Engine.Redact(string.Join('\n', sb.ToString().Split('\n').Where(l => l.Trim().Length > 0)));
    }

    string Describe(WorkItem i) => i.State switch
    {
        ItemState.Working when i.Run is { Kind: "review" } => "reviewing the change",
        ItemState.Working when i.Run is { } r => $"started {Format.Age(Math.Max(60, (DateTimeOffset.Now - r.Started).TotalSeconds))}" + (i.Followup != null ? ", addressing feedback" : ""),
        ItemState.Checking => "verifying and preparing the pull request",
        ItemState.Open => $"pull request #{i.Pr} open",
        ItemState.NeedsOwner => "waiting for you",
        ItemState.Done => i.Note ?? "done",
        _ => "queued",
    };

    string Machinery(RepoAgents repo, WorkItem i)
    {
        var parts = new List<string>();
        if (i.Run is { } r) parts.Add($"{r.Kind} run {r.Attempt} on {r.Email}, session {r.SessionId}");
        if (i.Failures > 0) parts.Add($"{i.Failures} failed attempt(s)");
        if (i.Reviews > 0) parts.Add($"{i.Reviews} review round(s)");
        if (i.Branch != null) parts.Add($"branch {i.Branch}");
        if (i.Worktree != null) parts.Add($"worktree {i.Worktree}");
        if (_workers.TryGetValue(i, out var w)) parts.Add($"{w.Proc.ProcessIds().Length} process(es), {w.Proc.CpuTime.TotalSeconds:0}s CPU");
        return parts.Count == 0 ? "" : "\n  " + string.Join("; ", parts);
    }

    /// What the item's latest run did, newest last.
    public string Details(RepoAgents repo, WorkItem item)
    {
        var sb = new StringBuilder($"{item.Label}: {Describe(item)}.");
        if (item.Note != null) sb.Append(' ').Append(item.Note);
        sb.AppendLine(Machinery(repo, item));
        if (item.Followup != null) sb.AppendLine("Next pass will address: " + OneLine(item.Followup));
        if (item.Run is { } r)
        {
            var lines = StreamTail.ReadAll(_store.RunFile(repo, item, r.Attempt, "out"))
                .Where(e => e is not TurnEvent).Select(StreamTail.Describe).Where(l => l.Length > 0).TakeLast(30).ToList();
            if (lines.Count > 0) sb.AppendLine("Recent activity:").AppendJoin('\n', lines.Select(l => "- " + (l.Length > 240 ? l[..240] + "…" : l)));
        }
        return Engine.Redact(sb.ToString().TrimEnd());
    }

    // -- prompts ------------------------------------------------------------

    string WorkPrompt(RepoAgents repo, WorkItem item, string? notes, string? discussion)
    {
        var sb = new StringBuilder();
        sb.AppendLine(item.Issue is { } n
            ? $"You are an clautoswap agent. Resolve GitHub issue #{n} of this repository: {item.Title}"
            : $"You are an clautoswap agent. The repository owner asked for: {item.Title}");
        if (item.Body.Trim().Length > 0) sb.AppendLine().AppendLine(Clip(item.Body, 8000));
        if (discussion != null) sb.AppendLine().AppendLine("Discussion on the issue, oldest first (decisions the owner documented here stand):").AppendLine(discussion);
        if (item.Guidance != null) sb.AppendLine().AppendLine("The owner's answer to your earlier question:").AppendLine(item.Guidance);
        if (item.Followup != null) sb.AppendLine().AppendLine("This pass must address:").AppendLine(item.Followup);
        if (notes != null) sb.AppendLine().AppendLine($"An earlier session worked on this and stopped. Read its hand-off notes in {notes} first, check the current state of this worktree, and continue from there; do not redo finished work.");
        if (Memory.Prompt(_state.Memory, repo.Memory) is { } memory) sb.AppendLine().AppendLine(memory);
        sb.AppendLine().AppendLine(Rules(repo, item));
        return sb.ToString();
    }

    string ResumePrompt(RepoAgents repo, WorkItem item)
    {
        var sb = new StringBuilder("Continue this task where you left off; your session was interrupted, and the worktree is as you left it.");
        if (item.Guidance != null) sb.AppendLine().AppendLine().AppendLine("The owner's answer to your question:").AppendLine(item.Guidance);
        if (item.Followup != null) sb.AppendLine().AppendLine().AppendLine("Before finishing, address:").AppendLine(item.Followup);
        if (Memory.Prompt(_state.Memory, repo.Memory) is { } memory) sb.AppendLine().AppendLine().AppendLine(memory);
        sb.AppendLine().AppendLine().Append(Rules(repo, item));
        return sb.ToString();
    }

    string Rules(RepoAgents repo, WorkItem item) =>
        $"""
        How to work:
        - This folder is a dedicated git worktree on branch {item.Branch}, based on {repo.Base}. Stay within this item.
        - Understand the relevant code, the repository's conventions (CLAUDE.md, AGENTS.md, README, existing code and tests) and its CI before changing anything; then make the change completely. The owner's notes in this issue and in the issues it links to (a roadmap's routing or dependencies) are binding.
        - Run the relevant tests.{(repo.Verify is { } v ? $" clautoswap runs `{v}` before the pull request opens; make sure it passes." : "")}
        - Commit your work with clear messages. Do not push, open pull requests or use gh: clautoswap does that once the work checks out.
        - Never merge, close issues, deploy, publish packages or releases, spend money, or change billing, secrets or repository settings.
        - Optionally, before your final line, print up to 3 lines `CLAUTOSWAP: LEARNED <durable fact about this repository>`: only what would save the next agent real time (a build quirk, a convention, where something lives); never secrets.
        - If this needs a decision only the owner can make (product direction, trade-offs the issue leaves open, anything irreversible), is unclear, or cannot be done in code, do not guess. End with one line:
          CLAUTOSWAP: BLOCKED <your question for the owner, self-contained>
        - Otherwise end with one line:
          CLAUTOSWAP: DONE <one-line summary for the pull request>
        """;

    string ReviewPrompt(RepoAgents repo, WorkItem item) =>
        $"""
        You are reviewing a change an clautoswap agent made in this worktree for {(item.Issue is { } n ? $"GitHub issue #{n}" : "the owner's request")}: {item.Title}

        {Clip(item.Body, 6000)}

        The change is `git diff origin/{repo.Base}...HEAD` (branch {item.Branch}). Read it and the code around it, and run tests if that helps. Do not modify any file and do not commit.
        Report only real problems: bugs, parts of the issue left undone, missing or broken tests, security issues, unrelated changes. Ignore style preferences.{(Memory.Prompt(_state.Memory, repo.Memory.Where(n => n.Owner || n.Source != Memory.NormalizeSource(item.Label)).ToList()) is { } memory ? "\n\n" + memory + "\n" : "")}
        End with one line:
        CLAUTOSWAP: APPROVED <one-line verdict>
        or, if something must change:
        CLAUTOSWAP: CHANGES <the problems, concrete and concise>
        """;

    string PullRequestBody(RepoAgents repo, WorkItem item, string summary) =>
        $"""
        {summary}

        {(item.Issue is { } n ? $"For #{n}." : "Requested in the clautoswap agents terminal.")}
        {(item.ReviewNotes is { } notes ? $"\nThe agent review still raised, for you to weigh:\n{notes}\n" : "")}
        ---
        Prepared by clautoswap agents: reviewed{(repo.Verify is { } v ? $" and `{v}` passed" : "")} before it opened. It is not merged automatically, and merging it does not close the issue.
        """;

    static string Clip(string s, int max) => s.Length > max ? s[..max] + "\n[…]" : s;
}
