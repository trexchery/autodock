using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clautoswap;

// Git and GitHub: worktrees, the review and verification gates, pushing, pull requests, issues.
public sealed partial class Supervisor
{
    async Task<CliResult> GitAsync(string cwd, params string[] args)
    {
        // One git command at a time: worktree, fetch and ref updates share the repository's locks.
        await _gitGate.WaitAsync();
        // The supervisor's git runs with the owner's credentials: never with hooks a worker could have
        // written into the repository.
        try { return await _cli.GitAsync(cwd, ["-c", "core.hooksPath=" + NoHooks, "-c", "core.fsmonitor=false", .. args]); }
        finally { _gitGate.Release(); }
    }

    string NoHooks
    {
        get
        {
            var dir = Path.Combine(_store.Root, "no-hooks");
            Directory.CreateDirectory(dir);
            return dir.Replace('\\', '/');
        }
    }

    async Task<bool> HasOriginAsync(RepoAgents repo) => (await GitAsync(repo.Path, "remote", "get-url", "origin")).Ok;

    /// A fetch that failed for want of a network; a deleted base or revoked credentials are the owner's to fix.
    internal static readonly Regex Offline = new(@"resolve host|timed out|timeout|refused|unreachable|connection reset|failed to connect|no route", RegexOptions.IgnoreCase);

    async Task<string> StartPointAsync(RepoAgents repo, bool strict = false)
    {
        if (!await HasOriginAsync(repo)) return repo.Base;
        if (Due(_fetched.GetValueOrDefault(repo), TimeSpan.FromMinutes(2)))
        {
            var fetch = await GitAsync(repo.Path, "fetch", "--quiet", "origin", repo.Base);
            if (strict && !fetch.Ok) throw new AgentsException($"could not fetch origin/{repo.Base}: {fetch.Why}", transient: Offline.IsMatch(fetch.Err + fetch.Out)); // a stale base proves nothing
            _fetched[repo] = DateTimeOffset.Now;
        }
        return "origin/" + repo.Base;
    }

    /// The item's own worktree on its own branch, created on first use and kept until the work is done.
    async Task<string> PrepareWorktreeAsync(RepoAgents repo, WorkItem item)
    {
        if (!Directory.Exists(repo.Path)) throw new AgentsException($"{repo.Path} no longer exists");
        var start = await StartPointAsync(repo);
        var wt = item.Worktree ?? _store.Worktree(repo, item);
        if (!File.Exists(Path.Combine(wt, ".git")))
        {
            await GitAsync(repo.Path, "worktree", "prune");
            item.Branch ??= $"clautoswap/{item.Id}-{Slug(item.Title)}";
            var exists = (await GitAsync(repo.Path, "rev-parse", "--verify", "--quiet", "refs/heads/" + item.Branch)).Ok;
            var r = exists
                ? await GitAsync(repo.Path, "worktree", "add", wt, item.Branch)
                : await GitAsync(repo.Path, "worktree", "add", "-b", item.Branch, wt, start);
            if (!r.Ok) throw new AgentsException("could not create a worktree: " + r.Why);
            item.Worktree = wt;
            Save();
        }
        else
        {
            // A run killed mid-commit leaves the index locked; nothing else uses this worktree now.
            var gitDir = (await GitAsync(wt, "rev-parse", "--absolute-git-dir")).Out.Trim();
            if (gitDir.Length > 0) File.Delete(Path.Combine(gitDir, "index.lock"));
        }
        if (item.Pr != null && start.StartsWith("origin/"))
        {
            // Pick up anything pushed to the pull request since (the owner's own commits included),
            // and the base, which a conflict fix merges.
            await GitAsync(repo.Path, "fetch", "--quiet", "origin", item.Branch!, repo.Base);
            await GitAsync(wt, "merge", "--ff-only", "--quiet", "origin/" + item.Branch);
        }
        return wt;
    }

    /// A reviewer must leave the change exactly as it found it.
    async Task RestoreAfterReviewAsync(string wt, string head)
    {
        if (head.Length == 0 || !Directory.Exists(wt)) return;
        var now = (await GitAsync(wt, "rev-parse", "HEAD")).Out.Trim();
        if (now != head || (await GitAsync(wt, "status", "--porcelain", "--untracked-files=no")).Out.Trim().Length > 0)
            await GitAsync(wt, "reset", "--hard", "--quiet", head);
    }

    internal static string Slug(string title)
    {
        var s = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "work" : s.Length > 40 ? s[..40].TrimEnd('-') : s;
    }

    /// After a run says it is done: commit what it left modified, verify, have it reviewed, then push
    /// and open or update the pull request. Every step is idempotent, so a restart in the middle simply
    /// runs it again.
    async Task CheckAsync(RepoAgents repo, WorkItem item)
    {
        // Paused or stopped: the check waits (in Checking) and runs again on resume.
        if (_disposed || item.State != ItemState.Checking || !Active(repo) || !_checks.Add(item)) return;
        var summary = item.Summary ?? item.Title;
        try
        {
            var wt = item.Worktree is { } w && Directory.Exists(w) ? w : throw new AgentsException("its worktree is gone");
            if ((await GitAsync(wt, "status", "--porcelain", "--untracked-files=no")).Out.Trim().Length > 0)
            {
                await GitAsync(wt, "add", "--update");
                var c = await GitAsync(wt, "commit", "--quiet", "-m", item.Label);
                if (!c.Ok) throw new AgentsException("could not commit: " + c.Why);
            }
            var start = await StartPointAsync(repo);
            if ((await GitAsync(wt, "rev-list", "--count", start + "..HEAD")).Out.Trim() == "0")
            {
                Park(repo, item, NoChange + ": " + summary);
                return;
            }
            if (!Active(repo)) return; // closed meanwhile: checked again on the next open
            var verified = repo.Verify is { Length: > 0 } verify ? await VerifyAsync(repo, item, wt, verify) : null;
            if (Dropped(repo, item)) return; // skipped during verification
            if (_interrupted.Remove(item)) return; // its terminal closed: verified again on the next open
            if (verified is { } failure)
            {
                item.Followup = $"clautoswap ran `{repo.Verify}` in this worktree and it failed:\n{failure}";
                Charge(repo, item, $"`{repo.Verify}` failed");
                return;
            }
            var head = (await GitAsync(wt, "rev-parse", "HEAD")).Out.Trim();
            if (item.ReviewedHead != head && !item.PushApproved)
            {
                // A fresh, read-only session reviews the change before anyone else sees it.
                item.ReviewNext = true;
                item.State = ItemState.Queued;
                return;
            }
            if (!item.PushApproved)
            {
                if (item.ReviewNotes is { } notes)
                {
                    Hold(repo, item, "review", "two review passes still found problems; answer to push it as is, or say what to change:\n" + notes);
                    return;
                }
                // A push runs the branch's own workflows with the repository's secrets, before anyone reviews it.
                var ci = (await GitAsync(wt, "diff", "--name-only", start + "..HEAD")).Out
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(f => f.StartsWith(".github/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (ci.Count > 0)
                {
                    Hold(repo, item, "workflow", $"the change touches {string.Join(", ", ci.Take(5))}, which runs with the repository's secrets once pushed; answer to push it, or say what to change");
                    return;
                }
            }
            if (!start.StartsWith("origin/"))
            {
                item.State = ItemState.Done;
                item.Note = $"ready on branch {item.Branch} (no remote to push to)";
                return;
            }
            if (!Active(repo) || Dropped(repo, item)) return; // paused, stopped, closed or skipped during verification
            var push = await GitAsync(wt, "push", "--quiet", "-u", "origin", item.Branch!);
            if (!push.Ok)
            {
                if (push.Err.Contains("non-fast-forward") || push.Err.Contains("fetch first"))
                {
                    item.Followup = $"The branch on GitHub has commits this worktree lacks. clautoswap fetched them: merge origin/{item.Branch} and resolve any conflicts.";
                    item.State = ItemState.Queued;
                    return;
                }
                throw new AgentsException("could not push: " + push.Why);
            }
            // Pushing is idempotent: a pause now leaves it in Checking, and resuming opens the pull request.
            if (!Active(repo) || Dropped(repo, item)) return;
            if (item.Pr == null) await OpenPullRequestAsync(repo, item, wt, summary);
            item.State = ItemState.Open;
            item.Followup = null;
            item.Reviews = 0;
            item.Failures = 0;
            (item.PushApproved, item.ReviewNotes, item.HoldFor) = (false, null, null);
            item.Note = summary;
        }
        catch (Exception e)
        {
            // Parked, whatever it was: a check that throws must not be retried in a loop.
            if (e is not AgentsException) Program.Log(e);
            Park(repo, item, e is AgentsException ? e.Message : "the check could not run: " + e.Message);
        }
        finally
        {
            _checks.Remove(item);
            Save();
            Changed?.Invoke();
            Pump();
        }
    }

    /// Runs the repository's verification command in the worktree; null when it passes, else its tail.
    async Task<string?> VerifyAsync(RepoAgents repo, WorkItem item, string wt, string verify)
    {
        var dir = _store.RunDir(repo, item);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, $"verify-{item.Run?.Attempt ?? 0}.log");
        var empty = Path.Combine(dir, "empty");
        File.WriteAllText(empty, "");
        File.WriteAllText(log, "");
        using var proc = AgentProcess.StartShell(verify, wt, $@"Local\clautoswap-verify-{repo.Key}-{item.Id}-{Guid.NewGuid().ToString("N")[..6]}", empty, log,
            new Dictionary<string, string?>(WorkerEnvironment(Path.Combine(_store.Root, "no-gh"))) { ["CI"] = "1" });
        _verifying[item] = (repo, proc);
        try { await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(Config.VerifyTimeout)); }
        finally { _verifying.Remove(item); }
        if (!proc.HasExited)
        {
            await proc.StopAsync(TimeSpan.Zero);
            return $"(timed out after {Config.VerifyTimeout.TotalMinutes:0} minutes)\n" + TailOf(log, 3000);
        }
        await proc.ReapAsync();
        return proc.ExitCode == 0 ? null : TailOf(log, 3000);
    }

    async Task OpenPullRequestAsync(RepoAgents repo, WorkItem item, string wt, string summary)
    {
        var existing = await _cli.GhAsync(wt, "pr", "list", "--head", item.Branch!, "--state", "open", "--json", "number,url");
        if (existing.Ok && ParsePrList(existing.Out) is { } found)
        {
            (item.Pr, item.PrUrl) = found;
            item.PrSeen ??= DateTimeOffset.Now;
            return;
        }
        var body = Path.Combine(_store.RunDir(repo, item), "pr-body.md");
        File.WriteAllText(body, PullRequestBody(repo, item, summary));
        string[] create = ["pr", "create", "--base", repo.Base, "--head", item.Branch!, "--title", item.Title, "--body-file", body];
        var r = await _cli.GhAsync(wt, Config.DraftPrs ? [.. create, "--draft"] : create);
        if (!r.Ok || Regex.Match(r.Out, @"https://\S+/pull/(\d+)") is not { Success: true } m)
            throw new AgentsException("could not open a pull request: " + r.Why);
        item.Pr = int.Parse(m.Groups[1].Value);
        item.PrUrl = m.Value;
        item.PrSeen = DateTimeOffset.Now;
        Notify?.Invoke("Pull request ready", $"{repo.Leaf} #{item.Pr} · {item.Title}");
    }

    internal static (int, string)? ParsePrList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var pr in doc.RootElement.EnumerateArray())
                return (pr.GetProperty("number").GetInt32(), pr.GetProperty("url").GetString() ?? "");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return null;
    }

    /// Parks an item the owner has to clear before clautoswap pushes it.
    void Hold(RepoAgents repo, WorkItem item, string kind, string why)
    {
        Park(repo, item, why);
        item.HoldFor = kind;
    }

    /// A finished run that changed nothing: the owner's call (close the issue or retry), not a failure.
    const string NoChange = "finished without changing anything";

    void Park(RepoAgents repo, WorkItem item, string why)
    {
        if (Dropped(repo, item)) return;
        item.State = ItemState.NeedsOwner;
        item.Note = Engine.Redact(why);
        Notify?.Invoke("Agents need you", $"{repo.Leaf} · {item.Label}: {item.Note}");
    }

    const string QuestionMark = "<!-- clautoswap-agents -->", OldQuestionMark = "<!-- autodock-agents -->"; // asked before the rename

    /// A decision only the owner can make: park the item, ask where the owner already works (the issue),
    /// and move on. An answer there, or in the agents terminal, brings it back.
    async Task AskOwnerAsync(RepoAgents repo, WorkItem item, string question)
    {
        item.State = ItemState.NeedsOwner;
        item.Note = question;
        item.AskedAt = DateTimeOffset.Now;
        Notify?.Invoke("Agents need you", $"{repo.Leaf} · {item.Label}: {question}");
        if (item.Issue is not { } n) return;
        var body = Path.Combine(_store.RunDir(repo, item), "question.md");
        File.WriteAllText(body,
            "clautoswap agents set this issue aside for a decision:\n\n" +
            string.Join("\n", question.Split('\n').Select(l => "> " + l)) +
            "\n\nReply here and the work picks up from where it stopped.\n" + QuestionMark + "\n");
        await _cli.GhAsync(repo.Path, "issue", "comment", n.ToString(), "--body-file", body);
    }

    /// The issue's discussion (the owner's documented decisions live there), oldest first.
    async Task<string?> IssueDiscussionAsync(RepoAgents repo, int issue)
    {
        var r = await _cli.GhAsync(repo.Path, "issue", "view", issue.ToString(), "--json", "comments");
        if (!r.Ok) return null;
        var lines = IssueInfo.Comments(r.Out, DateTimeOffset.MinValue, _me).TakeLast(15).ToList();
        return lines.Count == 0 ? null : Clip(string.Join("\n\n", lines), 8000);
    }

    /// Pull requests nobody is working on right now, whatever the item waits for.
    static bool Watched(WorkItem i) => i.Pr != null && i.State is ItemState.Open or ItemState.NeedsOwner or ItemState.Queued;

    /// Owner-visible results: merged or closed pull requests finish their items (parked ones too);
    /// failing checks, conflicts and new review comments send an open one back for another pass.
    async Task WatchPullRequestsAsync(RepoAgents repo)
    {
        _prPolled[repo] = DateTimeOffset.Now;
        foreach (var item in repo.Items.Where(Watched).ToList())
        {
            var seen = DateTimeOffset.Now;
            var r = await _cli.GhAsync(repo.Path, "pr", "view", item.Pr!.Value.ToString(), "--json", "state,mergeable,statusCheckRollup,comments,reviews");
            if (!r.Ok || PullRequestState.Parse(r.Out, item.PrSeen ?? DateTimeOffset.MinValue, _me) is not { } pr) continue;
            if (pr.State is "MERGED" or "CLOSED")
            {
                var merged = pr.State == "MERGED";
                item.Merged = merged;
                if (!merged && GoalOf(item.Goal) is { } g) g.Claim = null; // the claim may have counted on this change
                Memory.Add(repo.Memory, merged
                    ? $"Merged #{item.Pr} ({item.Label}){(item.Summary is { } s ? ": " + s : "")}"
                    : $"Closed without merging: #{item.Pr} ({item.Label}). The owner did not want this change as made{(item.Summary is { } c ? ": " + c : "")}",
                    "pull request", false, DateTimeOffset.Now);
                await FinishAsync(repo, item, pr.State == "MERGED" ? $"pull request #{item.Pr} merged" : $"pull request #{item.Pr} closed without merging");
                continue;
            }
            if (item.State != ItemState.Open || pr.ChecksPending) continue; // act on a complete picture, once
            var asks = new List<string>();
            if (pr.FailedChecks.Count > 0)
            {
                var logs = "";
                if (pr.FailedRunId is { } run && await _cli.GhAsync(repo.Path, "run", "view", run, "--log-failed") is var l)
                {
                    if (l.Ok) logs = "\n" + Engine.Redact(Tail(l.Out, 4000));
                    else Program.Log($"gh run view {run} --log-failed: exit {l.Code}: {l.Why}"); // the follow-up goes without the log
                }
                asks.Add($"These checks failed: {string.Join(", ", pr.FailedChecks)}.{logs}");
            }
            if (pr.Conflicting) asks.Add($"The pull request conflicts with {repo.Base}. clautoswap fetched it: merge origin/{repo.Base} into this branch and resolve the conflicts.");
            var inline = await _cli.GhAsync(repo.Path, "api", $"repos/{{owner}}/{{repo}}/pulls/{item.Pr}/comments");
            var feedback = pr.Feedback.Concat(inline.Ok ? PullRequestState.InlineComments(inline.Out, item.PrSeen ?? DateTimeOffset.MinValue, _me) : []).ToList();
            if (feedback.Count > 0) asks.Add("Review feedback to address:\n" + Engine.Redact(string.Join("\n\n", feedback)));
            if (asks.Count == 0) continue;
            item.PrSeen = seen;
            item.Followups++;
            if (item.Followups > MaxFollowups)
            {
                Park(repo, item, $"pull request #{item.Pr} still needs work after {MaxFollowups} passes");
                continue;
            }
            item.Followup = string.Join("\n\n", asks);
            item.State = ItemState.Queued;
        }
        Save();
    }

    /// Done for good: the worktree and local branch go; the pull request and remote branch stay.
    async Task FinishAsync(RepoAgents repo, WorkItem item, string how)
    {
        item.State = ItemState.Done;
        item.Note = how;
        item.Followup = null;
        // A verification still running there would keep the worktree from going, and run on for nothing.
        if (_verifying.Remove(item, out var v)) await v.Proc.StopAsync(TimeSpan.FromSeconds(5));
        if (item.Worktree is { } wt)
        {
            await GitAsync(repo.Path, "worktree", "remove", "--force", wt);
            item.Worktree = null;
        }
        if (item.Branch is { } b) await GitAsync(repo.Path, "branch", "-D", b);
        try { Directory.Delete(_store.RunDir(repo, item), recursive: true); }
        catch (IOException) { }
        Save();
    }

    static string Tail(string s, int chars) => s.Length > chars ? s[^chars..] : s;

    // -- issue discovery ----------------------------------------------------

    string? _me;

    /// Whether agents may take the issue: actionable, no open pull request has it, not skipped or routed elsewhere, not a goal.
    static bool WantedBy(RepoAgents repo, IssueInfo i, string? me, HashSet<int> open, HashSet<int> covered, HashSet<int> skip, HashSet<int>? only) =>
        i.Actionable(me, open) && !covered.Contains(i.Number) && !repo.Skipped.Contains(i.Number)
        && !skip.Contains(i.Number) && (only == null || only.Contains(i.Number))
        && repo.Goals.All(g => g.Issue != i.Number || g.State == GoalState.Dropped); // a goal is planned, not worked

    /// Picks up new actionable issues, drops queued ones that closed, and brings back items whose
    /// question was answered on the issue.
    async Task ScanAsync(RepoAgents repo)
    {
        repo.LastScan = DateTimeOffset.Now;
        if (await MeAsync(repo) is not { } me) { repo.Note = "could not read issues: gh is not signed in (run `gh auth login`)"; Save(); return; }
        var r = await _cli.GhAsync(repo.Path, "issue", "list", "--state", "open", "--limit", "100", "--json", "number,title,body,labels,assignees,author,blockedBy");
        if (!r.Ok) r = await _cli.GhAsync(repo.Path, "issue", "list", "--state", "open", "--limit", "100", "--json", "number,title,body,labels,assignees,author");
        if (!r.Ok) { repo.Note = "could not read issues: " + r.Why; Save(); return; }
        if (repo.Note?.StartsWith("could not") == true) repo.Note = null; // issues read, or a one-off baseline hiccup
        if (IssueInfo.ParseList(r.Out) is not { } issues) { repo.Note = "could not read issues: gh printed something unexpected"; Save(); return; }
        var open = issues.Select(i => i.Number).ToHashSet();
        // Issues an open pull request already resolves are someone's work in progress.
        var covered = new HashSet<int>();
        if (await _cli.GhAsync(repo.Path, "pr", "list", "--state", "open", "--limit", "100", "--json", "number,closingIssuesReferences") is { Ok: true } prs)
            covered = IssueInfo.ClosedByOpenPullRequests(prs.Out);
        // The owner's own routing (an "clautoswap" section in an issue they wrote) narrows it further.
        var (skip, only) = IssueInfo.Routing(issues, me);
        bool Wanted(IssueInfo i) => WantedBy(repo, i, _me, open, covered, skip, only);
        // Unstarted work found by scanning that is no longer wanted (now blocked, a tracker, routed elsewhere,
        // or someone else's pull request covers it) leaves the queue; work the owner named stays.
        var byNumber = issues.ToDictionary(i => i.Number);
        repo.Items.RemoveAll(i => i is { State: ItemState.Queued, Worktree: null, Pr: null, Guidance: null } && i.Rank < OwnerRank && i.Goal == null
                                  && i.Issue is { } n && byNumber.TryGetValue(n, out var issue) && !Wanted(issue));

        // Closed issues: unstarted and parked work goes, and so does work under way that has no pull
        // request yet. A full page may not list every open issue, so only a complete list counts.
        bool Gone(WorkItem i) => i.Issue is { } n && !open.Contains(n) && i.State != ItemState.Checking
                                 && (i.State == ItemState.Done ? i.Worktree == null : i.Pr == null)
                                 && !(i.State == ItemState.Done && i.Goal != null); // a goal's finished work is its evidence
        if (issues.Count < 100)
        {
            foreach (var gone in repo.Items.Where(Gone).ToList())
            {
                if (!Gone(gone)) continue; // its run ended while an earlier one was stopping, and its check took over
                if (gone.State != ItemState.Done)
                {
                    await StopWorkerAsync(gone, "skipped");
                    await FinishAsync(repo, gone, "its issue was closed");
                }
                repo.Items.Remove(gone);
            }
            foreach (var g in repo.Goals.Where(g => g is { State: GoalState.Active or GoalState.NeedsOwner, Issue: { } n } && !open.Contains(n)).ToList())
                await EndGoalAsync(repo, g, GoalState.Dropped, $"its issue #{g.Issue} was closed");
        }
        foreach (var issue in issues)
        {
            var dependents = issues.Count(o => o.BlockedBy(open).Contains(issue.Number));
            if (repo.Items.FirstOrDefault(i => i.Issue == issue.Number) is { } known)
            {
                if (known.State == ItemState.Queued && known.Worktree == null)
                    (known.Title, known.Body) = (issue.Title, issue.Body);
                continue;
            }
            if (!Wanted(issue)) continue;
            // Work that unblocks other issues goes first.
            repo.Items.Add(new WorkItem { Id = issue.Number.ToString(), Issue = issue.Number, Title = issue.Title, Body = issue.Body, Rank = issue.Rank + 50 * dependents });
        }

        foreach (var item in repo.Items.Where(i => i is { State: ItemState.NeedsOwner, AskedAt: not null, Issue: not null }).ToList())
        {
            var v = await _cli.GhAsync(repo.Path, "issue", "view", item.Issue!.Value.ToString(), "--json", "comments");
            if (!v.Ok) continue;
            if (item.State != ItemState.NeedsOwner || item.AskedAt is not { } asked || Dropped(repo, item)) continue; // changed during the await
            var answers = IssueInfo.Comments(v.Out, asked, _me).Where(c => !c.Contains(QuestionMark) && !c.Contains(OldQuestionMark)).ToList();
            if (answers.Count == 0) continue;
            item.Guidance = Engine.Redact(string.Join("\n\n", answers));
            Memory.Add(repo.Memory, $"#{item.Issue}: {item.Guidance}", "issue", true, DateTimeOffset.Now);
            (item.State, item.AskedAt, item.Failures, item.Note) = (ItemState.Queued, null, 0, null);
        }
        Save();
    }

    /// The owner named an issue the filters skipped: take it as is.
    async Task<WorkItem?> AddIssueAsync(RepoAgents repo, int number)
    {
        var r = await _cli.GhAsync(repo.Path, "issue", "view", number.ToString(), "--json", "number,title,body,labels,assignees,state");
        if (!r.Ok || IssueInfo.ParseList("[" + r.Out + "]") is not [var issue]) return null;
        if (repo.Items.FirstOrDefault(i => i.Issue == number) is { } scanned) return scanned; // a scan added it meanwhile
        var item = new WorkItem { Id = number.ToString(), Issue = number, Title = issue.Title, Body = issue.Body };
        repo.Items.Add(item);
        return item;
    }

    /// The owner's GitHub login, read once (a restart need not wait for the next scan).
    async Task<string?> MeAsync(RepoAgents repo) =>
        _me ??= (await _cli.GhAsync(repo.Path, "api", "user", "--jq", ".login")) is { Ok: true } me ? me.Out.Trim() : null;

    /// A clean detached checkout of the base as it is now (fetched), for a plan or a completion check, whatever a crash left there.
    async Task<string> ProbeWorktreeAsync(RepoAgents repo, WorkItem probe, bool strict = false)
    {
        if (!Directory.Exists(repo.Path)) throw new AgentsException($"{repo.Path} no longer exists");
        _fetched.Remove(repo);
        var start = await StartPointAsync(repo, strict);
        var wt = _store.Worktree(repo, probe);
        await GitAsync(repo.Path, "worktree", "remove", "--force", wt);
        try { if (Directory.Exists(wt)) Directory.Delete(wt, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        await GitAsync(repo.Path, "worktree", "prune");
        var add = await GitAsync(repo.Path, "worktree", "add", "--detach", "--force", wt, start);
        return add.Ok ? wt : throw new AgentsException($"could not check out {start}: {add.Why}");
    }

    Task RemoveProbeAsync(RepoAgents repo, WorkItem probe) => GitAsync(repo.Path, "worktree", "remove", "--force", _store.Worktree(repo, probe));

    /// Before the first run of a newly started repository: the gate must pass on the base branch, or
    /// every item would fail it for reasons that are not its own.
    async Task VerifyBaselineAsync(RepoAgents repo)
    {
        if (repo.Verify is not { Length: > 0 } verify) return;
        if (!Active(repo)) { _rebaseline.Add(repo); return; } // its terminal closed first: on the next open
        _baseline.Add(repo);
        var probe = new WorkItem { Id = "baseline", Title = "baseline" };
        var wt = _store.Worktree(repo, probe);
        var again = false; // its terminal closed: on the next open, once this one has cleaned up after itself
        try
        {
            var start = await StartPointAsync(repo);
            await GitAsync(repo.Path, "worktree", "prune");
            var add = await GitAsync(repo.Path, "worktree", "add", "--detach", "--force", wt, start);
            if (!add.Ok) { repo.Note = "could not check the verification command: " + add.Why; return; }
            if (!Active(repo)) { again = true; return; } // closed meanwhile
            var result = await VerifyAsync(repo, probe, wt, verify);
            if (_interrupted.Remove(probe)) again = true;
            else if (result is { } failure)
            {
                repo.Paused = true;
                repo.Note = $"`{verify}` fails on {repo.Base} before any change, so agents are paused. Fix it or change the command, then resume.\n{failure}";
                Notify?.Invoke("Agents paused", $"{repo.Leaf}: `{verify}` fails on {repo.Base}");
            }
        }
        finally
        {
            await GitAsync(repo.Path, "worktree", "remove", "--force", wt);
            try { Directory.Delete(_store.RunDir(repo, probe), recursive: true); } catch (IOException) { }
            _baseline.Remove(repo);
            if (again) _rebaseline.Add(repo);
            Save();
            Pump();
        }
    }
}

/// The parts of `gh issue list --json …` the supervisor decides on.
public sealed record IssueInfo(int Number, string Title, string Body, List<string> Labels, List<string> Assignees, List<int> Blockers, string Author = "")
{
    // Labels that say "not for an agent yet". Substring match, case-insensitive.
    static readonly string[] Hold = ["question", "discussion", "wontfix", "won't fix", "invalid", "duplicate", "blocked", "on hold", "needs decision", "needs design", "needs-design", "epic"];

    // Trackers organise work; they are not work: by title, by their own words, or a checklist of issues.
    static readonly Regex TrackerTitle = new(@"\b(roadmap|tracker|tracking issue|epic|umbrella|meta issue)\b|^\s*\[?\s*phase\s*\d+\s*(/\s*\d+)?\s*\]", RegexOptions.IgnoreCase);
    static readonly Regex TrackerBody = new(@"\b(tracking only|planning and tracking|tracking issue|this is (the|a|an) [\w\s-]{0,40}tracker)\b", RegexOptions.IgnoreCase);
    static readonly Regex Checklist = new(@"^\s*[-*]\s*\[[ xX]\].*#\d+", RegexOptions.Multiline);
    // A decision to make, not code to write.
    static readonly Regex DecisionTitle = new(@"^\s*(decide|decision|choose)\b", RegexOptions.IgnoreCase);
    // "## Dependencies" (or Depends on, Prerequisites, Blocked by; a heading or a bold label) up to the next heading.
    static readonly Regex DependencySection = new(@"^(?:#{1,6}\s*|\*\*)(?:dependencies|depends on|prerequisites|blocked by)\b.*(?:\n(?!#{1,6}\s).*)*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    // What follows these words on a dependency line is context, not a prerequisite: "#2 architecture; coordinate #7".
    static readonly Regex NotPrerequisite = new(@"\b(coordinat\w*|related|see also)\b", RegexOptions.IgnoreCase);
    // An issue reference or range ("#12", "#22–#27"), not a pull request's ("PR #12").
    static readonly Regex IssueRef = new(@"(?<!\b(?:PRs?|pull requests?)\s?)(?<![\w&])#(\d+)\b(?:\s*[–—-]\s*#?(\d+)\b)?", RegexOptions.IgnoreCase);
    // The owner's routing for clautoswap: a section whose heading names clautoswap, with Skip: and Eligible: lines.
    static readonly Regex RoutingSection = new(@"^#{1,6}[^\n]*clautoswap.*(?:\n(?!#{1,6}\s).*)*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    static readonly Regex RoutingLine = new(@"^[\s>*_-]*(skip|eligible)\b[^:\n]*:", RegexOptions.IgnoreCase);

    public bool Tracker => TrackerTitle.IsMatch(Title) || TrackerBody.IsMatch(Body) || Checklist.Matches(Body).Count >= 3;
    public bool Decision => DecisionTitle.IsMatch(Title);

    /// Open issues this one waits for: GitHub's own dependencies, "blocked by / depends on #N", and the
    /// issues a Dependencies section names.
    public List<int> BlockedBy(IReadOnlySet<int> open) =>
        Blockers.Concat(Regex.Matches(Body, @"(?:blocked by|depends on)\s+#(\d+)", RegexOptions.IgnoreCase).Select(m => int.Parse(m.Groups[1].Value)))
            .Concat(DependencySection.Matches(Body).SelectMany(s => s.Value.Split('\n'))
                .SelectMany(l => Refs(NotPrerequisite.Match(l) is { Success: true } m ? l[..m.Index] : l)))
            .Where(n => n != Number && open.Contains(n)).Distinct().ToList();

    static IEnumerable<int> Refs(string text)
    {
        foreach (Match m in IssueRef.Matches(text))
        {
            var from = int.Parse(m.Groups[1].Value);
            var to = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var t) && t > from && t - from <= 100 ? t : from;
            for (var n = from; n <= to; n++) yield return n;
        }
    }

    /// The owner's routing notes for clautoswap, from issues the owner wrote: issues to skip, and, when an
    /// eligible list exists, the only issues agents take up on their own.
    public static (HashSet<int> Skip, HashSet<int>? Only) Routing(IEnumerable<IssueInfo> issues, string me)
    {
        var skip = new HashSet<int>();
        HashSet<int>? only = null;
        foreach (var issue in issues.Where(i => i.Author.Equals(me, StringComparison.OrdinalIgnoreCase)))
            foreach (Match section in RoutingSection.Matches(issue.Body))
                foreach (var line in section.Value.Split('\n'))
                    if (RoutingLine.Match(line) is { Success: true } m)
                    {
                        var refs = Refs(line[m.Length..]);
                        if (m.Groups[1].Value.Equals("skip", StringComparison.OrdinalIgnoreCase)) skip.UnionWith(refs);
                        else (only ??= []).UnionWith(refs);
                    }
        return (skip, only);
    }

    /// The owner's own (written by or assigned to them: anyone can open an issue on a public repository,
    /// and its text steers an agent), not someone else's, not held by a label, not a tracker or a decision,
    /// not waiting on another open issue. Issues the owner names in the agents terminal are taken regardless.
    public bool Actionable(string? me, IReadOnlySet<int> open)
    {
        if (me == null) return false;
        var mine = Assignees.Contains(me, StringComparer.OrdinalIgnoreCase);
        if (Assignees.Count > 0 && !mine) return false;
        if (!mine && !Author.Equals(me, StringComparison.OrdinalIgnoreCase)) return false;
        if (Labels.Any(l => Hold.Any(h => l.Contains(h, StringComparison.OrdinalIgnoreCase)))) return false;
        if (Tracker || Decision) return false;
        return BlockedBy(open).Count == 0;
    }

    /// Urgency from labels; the owner's own ordering outranks all of it.
    public int Rank =>
        Labels.Select(l => l.ToLowerInvariant()).Sum(l =>
            l.Contains("critical") || l.Contains("urgent") || l.Contains("p0") ? 300
            : l.Contains("high") || l.Contains("p1") ? 200
            : l.Contains("bug") ? 100
            : 0);

    /// Null when the output is not the expected list: never mistake garbage for "no open issues".
    public static List<IssueInfo>? ParseList(string json)
    {
        var list = new List<IssueInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var i in doc.RootElement.EnumerateArray())
            {
                if (i.TryGetProperty("state", out var st) && st.GetString() is { } s && s != "OPEN") continue;
                var blockers = i.TryGetProperty("blockedBy", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("nodes", out var nodes)
                    ? nodes.EnumerateArray().Where(n => n.TryGetProperty("number", out _)).Select(n => n.GetProperty("number").GetInt32()).ToList()
                    : [];
                list.Add(new(
                    i.GetProperty("number").GetInt32(),
                    i.GetProperty("title").GetString() ?? "",
                    i.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    Names(i, "labels", "name"),
                    Names(i, "assignees", "login"),
                    blockers,
                    i.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object && au.TryGetProperty("login", out var al) ? al.GetString() ?? "" : ""));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
        return list;
    }

    /// Issues that open pull requests (`gh pr list --json closingIssuesReferences`) already resolve.
    public static HashSet<int> ClosedByOpenPullRequests(string json)
    {
        var set = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var pr in doc.RootElement.EnumerateArray())
                if (pr.TryGetProperty("closingIssuesReferences", out var refs) && refs.ValueKind == JsonValueKind.Array)
                    foreach (var r in refs.EnumerateArray())
                        if (r.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number) set.Add(n.GetInt32());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return set;
    }

    /// The owner's and collaborators' comments of `gh issue view --json comments` newer than `since`,
    /// as "login: body". Anyone else's are not instructions.
    public static List<string> Comments(string json, DateTimeOffset since, string? me)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in comments.EnumerateArray())
            {
                var who = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
                var body = c.TryGetProperty("body", out var b) ? b.GetString()?.Trim() ?? "" : "";
                var role = c.TryGetProperty("authorAssociation", out var r) ? r.GetString() : null;
                if (!PullRequestState.Trusted(who, role, me) || body.Length == 0) continue;
                if (!(c.TryGetProperty("createdAt", out var t) && DateTimeOffset.TryParse(t.GetString(), out var at) && at > since)) continue;
                list.Add($"{who}: {body}");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return list;
    }

    static List<string> Names(JsonElement e, string array, string field) =>
        e.TryGetProperty(array, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "").Where(x => x.Length > 0).ToList()
            : [];
}

/// What `gh pr view --json state,mergeable,statusCheckRollup,comments,reviews` says needs doing.
public sealed record PullRequestState(string State, bool Conflicting, bool ChecksPending, List<string> FailedChecks, string? FailedRunId, List<string> Feedback)
{
    public static PullRequestState? Parse(string json, DateTimeOffset since, string? me)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pending = false;
            var failed = new List<string>();
            string? runId = null;
            if (root.TryGetProperty("statusCheckRollup", out var checks) && checks.ValueKind == JsonValueKind.Array)
                foreach (var c in checks.EnumerateArray())
                {
                    var name = Str(c, "name") ?? Str(c, "context") ?? "check";
                    var status = Str(c, "status");
                    var outcome = Str(c, "conclusion") ?? Str(c, "state");
                    if (status is { } s && s != "COMPLETED" || outcome is "PENDING" or "EXPECTED") { pending = true; continue; }
                    if (outcome is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "STARTUP_FAILURE" or "ACTION_REQUIRED")
                    {
                        failed.Add(name);
                        if (runId == null && Regex.Match(Str(c, "detailsUrl") ?? "", @"/actions/runs/(\d+)") is { Success: true } m) runId = m.Groups[1].Value;
                    }
                }
            var feedback = new List<string>();
            foreach (var (array, time) in new[] { ("comments", "createdAt"), ("reviews", "submittedAt") })
                if (root.TryGetProperty(array, out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var c in items.EnumerateArray())
                    {
                        var who = c.TryGetProperty("author", out var a) ? Str(a, "login") ?? "" : "";
                        var body = Str(c, "body") ?? "";
                        if (!Trusted(who, Str(c, "authorAssociation"), me) || !(DateTimeOffset.TryParse(Str(c, time), out var at) && at > since)) continue;
                        if (body.Trim().Length > 0) feedback.Add($"{who}: {body.Trim()}");
                        else if (Str(c, "state") == "CHANGES_REQUESTED") feedback.Add($"{who} requested changes.");
                    }
            return new(Str(root, "state") ?? "OPEN", Str(root, "mergeable") == "CONFLICTING", pending, failed, runId, feedback);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// Inline review comments (`gh api repos/…/pulls/N/comments`) newer than `since`.
    public static IEnumerable<string> InlineComments(string json, DateTimeOffset since, string? me)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var who = c.TryGetProperty("user", out var u) ? Str(u, "login") ?? "" : "";
                if (!Trusted(who, Str(c, "author_association"), me) || !(DateTimeOffset.TryParse(Str(c, "created_at"), out var at) && at > since)) continue;
                var line = c.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                list.Add($"{who} on {Str(c, "path")}:{line}: {Str(c, "body")?.Trim()}");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return list;
    }

    /// Whose words count as the owner's: the owner, and people GitHub lists as owner, member or collaborator.
    internal static bool Trusted(string login, string? association, string? me) =>
        login.Length > 0 && !login.EndsWith("[bot]") &&
        (me != null && login.Equals(me, StringComparison.OrdinalIgnoreCase) || association is "OWNER" or "MEMBER" or "COLLABORATOR");

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
