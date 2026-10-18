using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Autodock;
using Xunit;

namespace Autodock.Tests;

/// The supervisor end to end, against a real git repository with a local bare "origin", the fake
/// engine in fakes/cswap.ps1 as Claude Code (synthetic accounts, no credentials) and fakes/gh.ps1 as
/// GitHub. Runs on one thread, like the tray's UI thread.
[Collection("agents end to end")]
public sealed class AgentsEndToEndTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly Loop _loop = new();
    readonly string _repo, _origin, _gh, _log;
    readonly Cli _cli;
    readonly Engine _engine;
    readonly AgentStore _store;
    readonly ConcurrentQueue<string> _notes = new();
    readonly string? _dataWas = Environment.GetEnvironmentVariable("AUTODOCK_DATA");
    Supervisor? _sup;

    static readonly DateTimeOffset Now = DateTimeOffset.Now;

    static Account A(int n, double pct, bool active = false) =>
        new(n, $"a{n}@example.com", active, "ok", new Usage(new Window(pct, Now.AddHours(2)), new Window(pct, Now.AddDays(3)), null, null));

    // #1 is the login at the keyboard and never used; #2 has the most headroom, then #3.
    static readonly IReadOnlyList<Account> Accounts = [A(1, 0, active: true), A(2, 10), A(3, 30)];

    string P(string name) => Path.Combine(_tmp.Path, name);

    public AgentsEndToEndTests()
    {
        _origin = P("origin.git");
        _repo = P("repo");
        _gh = P("gh");
        _log = P("cswap.log");
        Directory.CreateDirectory(_gh);
        Environment.SetEnvironmentVariable("FAKE_LOG", _log);
        Environment.SetEnvironmentVariable("FAKE_GH", _gh);
        Environment.SetEnvironmentVariable("AUTODOCK_DATA", P("data")); // logs stay out of the real profile
        Git(_tmp.Path, "init", "--quiet", "--bare", "-b", "main", _origin);
        Git(_tmp.Path, "clone", "--quiet", _origin, _repo);
        Git(_repo, "config", "user.email", "agents@example.com");
        Git(_repo, "config", "user.name", "autodock test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "--quiet", "-m", "init");
        Git(_repo, "push", "--quiet", "-u", "origin", "main");
        Git(_repo, "remote", "set-head", "origin", "main");
        var fakes = Path.Combine(AppContext.BaseDirectory, "fakes");
        _cli = new Cli(Engine.FindOnPath("git.exe")!, Path.Combine(fakes, "gh.cmd"));
        _engine = new Engine(Path.Combine(fakes, "cswap.cmd"));
        _store = new AgentStore(P("agents"));
    }

    static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
        return output.Trim();
    }

    static AgentsConfig Fast() => new()
    {
        Stall = TimeSpan.FromSeconds(4),
        ScanEvery = TimeSpan.FromHours(1),
        PrEvery = TimeSpan.FromSeconds(1),
        StopGrace = TimeSpan.FromSeconds(5),
        LaunchBackoff = TimeSpan.FromSeconds(1),
        Relink = TimeSpan.FromSeconds(1),
    };

    /// The tray starting, with the repository's agents terminal open (agents work only then) unless `terminal` is false.
    Task<Supervisor> StartAsync(AgentsConfig? config = null, bool terminal = true) => _loop.Run(() =>
    {
        var s = new Supervisor(_engine, _cli, _store, config ?? Fast());
        s.Authorize = (_, _, _) => Task.FromResult(true);
        s.Notify = (title, text) => _notes.Enqueue($"{title}: {text}");
        s.Start();
        if (terminal) s.Attach(_repo);
        s.UpdateAccounts(Accounts, []);
        return _sup = s;
    });

    Task Terminal(bool open) => _loop.Run(() =>
    {
        if (open) _sup!.Attach(_repo);
        else _sup!.Detach(_repo);
        return 0;
    });

    void Issues(string json) => File.WriteAllText(Path.Combine(_gh, "issues.json"), json);

    Task<string> Tool(string tool, string args) =>
        _loop.Run(() => _sup!.HandleAsync(tool, JsonDocument.Parse(args).RootElement.Clone(), _repo));

    Task<T> On<T>(Func<RepoAgents, T> read) => _loop.Run(() => read(_sup!.RepoFor(_repo)!));

    WorkItem Item(RepoAgents r, int issue) => r.Items.Single(i => i.Issue == issue);

    async Task Until(Func<RepoAgents, bool> condition, string what, int seconds = 90)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await On(r => { try { return condition(r); } catch (InvalidOperationException) { return false; } }))
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"{what}; state: {await On(r => string.Join(", ", r.Items.Select(i => $"{i.Label}={i.State} ({i.Note})")))}\nlog:\n{Read(_log)}\ngh:\n{Read(Path.Combine(_gh, "gh.log"))}\nautodock.log:\n{Read(Path.Combine(P("data"), "autodock.log"))}");
            await Task.Delay(200);
        }
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    string[] Runs => Read(_log).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("run ")).ToArray();

    [Fact]
    public async Task WorksAnIssueThroughReviewToAPullRequestAndFinishesWhenMerged()
    {
        Issues("""
        [
          {"number":1,"title":"Add greeting","body":"Say hello.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":2,"title":"Should we?","body":"","labels":[{"name":"question"}],"assignees":[],"author":{"login":"owner"}},
          {"number":3,"title":"Theirs","body":"","labels":[],"assignees":[{"login":"someone"}],"author":{"login":"owner"}},
          {"number":4,"title":"After greeting","body":"Blocked by #1.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":5,"title":"Covered","body":"","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":6,"title":"Master Roadmap (6 Phases)","body":"- [ ] #1\n- [ ] #4\n- [ ] #8","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":7,"title":"[Phase 2/6] Platform Foundation","body":"planning and tracking only","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":8,"title":"Decide the hosting model","body":"","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":9,"title":"Storage","body":"## Dependencies\n#8 hosting; coordinate #1.\n","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        File.WriteAllText(Path.Combine(_gh, "others.json"), """[{"number":3,"closingIssuesReferences":[{"number":5}]}]""");
        await StartAsync();
        var started = await Tool("agents_control", """{"action":"start","verify":"git --version"}""");
        Assert.StartsWith("Started.", started);

        await Until(r => r.Items.Any(i => i.Issue == 1 && i.State == ItemState.Open), "issue 1 reaches an open pull request");
        Assert.Equal(new[] { 1 }, await On(r => r.Items.Select(i => i.Issue!.Value).ToArray())); // 2-9 filtered out: held, theirs, blocked, covered, trackers, a decision, unmet dependency
        var item = await On(r => Item(r, 1));
        Assert.Equal(7, item.Pr);
        Assert.Equal("autodock/1-add-greeting", item.Branch);
        Assert.Contains("autodock/1-add-greeting", Git(_repo, "ls-remote", "origin"));
        var body = File.ReadAllText(Path.Combine(_gh, "pr-body-7.md"));
        Assert.Contains("For #1.", body);
        Assert.DoesNotContain("Closes", body);
        Assert.Contains("`git --version` passed", body);

        // A work run, then a fresh review run; never the signed-in account, never without isolation.
        Assert.Equal(2, Runs.Length);
        Assert.All(Runs, r => Assert.StartsWith("run 2 --require-session -- -p", r));
        Assert.DoesNotContain("Edit", Runs[0]);
        Assert.Contains("Edit Write NotebookEdit", Runs[1]);
        Assert.All(File.ReadAllLines(_log + ".env"), l => Assert.Equal("autodock-agent-has-no-github-access|file|", l));
        Assert.Contains(_notes, n => n.StartsWith("Pull request ready"));

        var status = await Tool("agents_status", "{}");
        Assert.Contains("Pull request #7 for #1 Add greeting: waiting for review", status);
        Assert.DoesNotContain("session", status); // machinery stays out of view unless asked
        Assert.Contains("session", await Tool("agents_status", """{"detail":true}"""));

        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"MERGED"}""");
        // Done first, then the worktree and local branch go.
        await Until(r => Item(r, 1).State == ItemState.Done && !Directory.Exists(item.Worktree) && Git(_repo, "branch", "--list", "autodock/*").Length == 0,
            "the merged pull request finishes the item and cleans up");
        Assert.DoesNotContain("issue close", Read(Path.Combine(_gh, "gh.log")));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AQuestionParksItsIssueWorkMovesOnAndAnAnswerOnTheIssueBringsItBack()
    {
        Issues("""
        [
          {"number":10,"title":"Greeting tone","body":"[fake:blocked] Pick a tone.","labels":[{"name":"bug"}],"assignees":[],"author":{"login":"owner"}},
          {"number":11,"title":"Add farewell","body":"Say bye.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":12,"title":"Add emoji","body":"Smile.","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        var config = Fast();
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");

        await Until(r => Item(r, 10).State == ItemState.NeedsOwner, "issue 10 asks the owner");
        Assert.Equal("Should the greeting be formal?", await On(r => Item(r, 10).Note));
        await AgentProcessTests.WaitUntil(() => File.Exists(Path.Combine(_gh, "question-10.md"))); // asked on the issue
        Assert.Contains("Should the greeting be formal?", File.ReadAllText(Path.Combine(_gh, "question-10.md")));
        await Until(r => Item(r, 11).State == ItemState.Open, "work moves on to issue 11");
        await Until(r => Item(r, 12).State == ItemState.Open, "and to issue 12");
        Assert.Contains("Needs you: #10 Greeting tone: Should the greeting be formal?", await Tool("agents_status", "{}"));

        // The owner answers on GitHub. The next scan brings #10 back, but two pull requests already
        // wait for review: nothing new opens until one of them is merged.
        Issues("""[{"number":10,"title":"Greeting tone","body":"[fake:blocked] Pick a tone.","labels":[],"assignees":[],"author":{"login":"owner"}},{"number":13,"title":"New","body":"","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        File.WriteAllText(Path.Combine(_gh, "comments-10.json"), $$"""{"comments":[{"author":{"login":"owner"},"body":"Formal, please.","createdAt":"{{DateTimeOffset.UtcNow.AddMinutes(1):O}}"}]}""");
        await _loop.Run(() => { config.ScanEvery = TimeSpan.FromSeconds(1); _sup!.UpdateAccounts(Accounts, []); return 0; });
        await Until(r => Item(r, 10).State == ItemState.Queued && Item(r, 10).Guidance != null, "the answer brings issue 10 back");
        await Task.Delay(1500);
        Assert.Equal(ItemState.Queued, await On(r => Item(r, 10).State));
        Assert.Contains("Holding new work: 2 pull requests are waiting for review.", await Tool("agents_status", "{}"));

        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"MERGED"}""");
        await Until(r => Item(r, 10).State == ItemState.Open, "after a merge, the answered issue reaches a pull request");
        Assert.Contains("owner: Formal, please.", File.ReadAllText(_log + ".prompts"));
        Assert.Contains(Runs, r => r.Contains("--resume")); // in its own conversation
        Assert.Equal(ItemState.Queued, await On(r => Item(r, 13).State)); // two waiting again
        _sup!.Dispose();
    }

    [Fact]
    public async Task ALimitMovesTheWorkToAnotherAccountWithAHandoff()
    {
        Issues("""[{"number":20,"title":"Quota","body":"[fake:quota] Do it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 20).State == ItemState.Open, "the item finishes on another account");
        Assert.StartsWith("run 2 ", Runs[0]);
        Assert.StartsWith("run 3 ", Runs[1]);
        Assert.Contains("--session-id", Runs[1]); // a new conversation...
        Assert.Contains("handoff --from claude --dir", Read(_log)); // ...that starts from the engine's hand-off notes
        Assert.Contains("hand-off notes", File.ReadAllText(_log + ".prompts"));
        Assert.Equal(0, await On(r => Item(r, 20).Failures)); // a limit is not the work's fault
        _sup!.Dispose();
    }

    [Fact]
    public async Task VerificationFailuresAndReviewFindingsGoBackToTheSameConversation()
    {
        Issues("""[{"number":30,"title":"Fragile","body":"[fake:break] [fake:review-changes] Careful.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start","verify":"if exist broken.txt (exit 1) else (exit 0)"}""");
        await Until(r => Item(r, 30).State == ItemState.Open, "the item passes verification and review");
        var prompts = File.ReadAllText(_log + ".prompts");
        Assert.Contains("and it failed", prompts);
        Assert.Contains("A fresh review of your change found these problems", prompts);
        Assert.Contains("the greeting has no test", prompts);
        // work, (verify fails) resume, review → changes, resume, review → approved
        Assert.Equal(5, Runs.Length);
        Assert.Equal(3, Runs.Count(r => !r.Contains("NotebookEdit")));
        Assert.Equal(2, Runs.Count(r => r.Contains("--resume")));
        Assert.False(File.Exists(Path.Combine(await On(r => Item(r, 30).Worktree!), "broken.txt")));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AStalledRunIsResumedAndARestartReattachesInsteadOfStartingAgain()
    {
        Issues("""[{"number":40,"title":"Stuck","body":"[fake:stall]","labels":[],"assignees":[],"author":{"login":"owner"}},{"number":41,"title":"Slow","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 40).State == ItemState.Open, "the stalled run is stopped and resumed", 120);
        // The stalled work run, its resumption in the same conversation, then the review.
        Assert.Equal(3, Runs.Length);
        Assert.Contains("--resume", Runs[1]);

        await Until(r => Item(r, 41).State == ItemState.Working && Item(r, 41).Run is { Kind: "work", Pid: > 0 }, "the slow run starts");
        var pid = await On(r => Item(r, 41).Run!.Pid);
        await _loop.Run(() => { _sup!.Dispose(); return 0; }); // the tray exits; the run carries on
        Assert.False(Process.GetProcessById(pid).HasExited);
        await StartAsync();
        await Until(r => Item(r, 41).State == ItemState.Open, "the re-attached run finishes");
        Assert.Equal(5, Runs.Length); // #40's three, then #41's work and review: no second work run after the restart
        Assert.Equal(0, await On(r => Item(r, 41).Failures));
        _sup!.Dispose();
    }

    [Fact]
    public async Task PullRequestFeedbackGetsAnotherPassOnTheSameBranch()
    {
        Issues("""[{"number":50,"title":"Shiny","body":"Polish.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 50).State == ItemState.Open, "the pull request opens");
        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), $$"""
        {"state":"OPEN","mergeable":"MERGEABLE",
         "statusCheckRollup":[{"__typename":"CheckRun","name":"test","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://github.com/o/r/actions/runs/123/job/9"}],
         "comments":[{"author":{"login":"owner"},"body":"Please also update the README.","createdAt":"{{DateTimeOffset.UtcNow.AddMinutes(1):O}}"}],
         "reviews":[]}
        """);
        await Until(r => Item(r, 50).Followups == 1, "the feedback is picked up");
        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"OPEN","mergeable":"MERGEABLE","statusCheckRollup":[],"comments":[],"reviews":[]}""");
        await Until(r => Item(r, 50).State == ItemState.Open && Item(r, 50).Followup == null, "the follow-up is pushed");
        var prompts = File.ReadAllText(_log + ".prompts");
        Assert.Contains("These checks failed: test.", prompts);
        Assert.Contains("FAIL greeting.test: expected hello", prompts);
        Assert.Contains("owner: Please also update the README.", prompts);
        var gh = Read(Path.Combine(_gh, "gh.log"));
        Assert.Contains("run view 123 --log-failed", gh);
        Assert.Single(gh.Split('\n'), l => l.StartsWith("pr create")); // the same pull request, updated
        _sup!.Dispose();
    }

    [Fact]
    public async Task PauseStopsTheRunWithoutCountingItAndResumeContinuesIt()
    {
        Issues("""[{"number":60,"title":"Long","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 60).State == ItemState.Working && Item(r, 60).Run is { Pid: > 0 }, "the run starts");
        var pid = await On(r => Item(r, 60).Run!.Pid);
        Assert.StartsWith("Paused.", await Tool("agents_control", """{"action":"pause"}"""));
        await Until(r => Item(r, 60).State == ItemState.Queued && Item(r, 60).Run is { Live: false }, "the run stops");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.Equal(0, await On(r => Item(r, 60).Failures));
        Assert.Contains("agents paused", await Tool("agents_status", "{}"));
        Assert.Equal("Resumed.", await Tool("agents_control", """{"action":"resume"}"""));
        await Until(r => Item(r, 60).State == ItemState.Open, "the resumed run finishes");
        Assert.Contains(Runs, r => r.Contains("--resume"));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ClosingTheTerminalPausesTheWorkAndReopeningContinuesIt()
    {
        Issues("""[{"number":61,"title":"Long","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 61).State == ItemState.Working && Item(r, 61).Run is { Pid: > 0 }, "the run starts");
        var pid = await On(r => Item(r, 61).Run!.Pid);
        await Terminal(open: false);
        await Until(r => Item(r, 61).State == ItemState.Queued && Item(r, 61).Run is { Live: false }, "the run stops with its terminal");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.Equal(0, await On(r => Item(r, 61).Failures));
        var runs = Runs.Length;
        await Task.Delay(3000);
        Assert.Equal(runs, Runs.Length); // nothing starts in the background
        Assert.Equal(ItemState.Queued, await On(r => Item(r, 61).State));
        Assert.True(await On(r => r.Running)); // still authorized, not paused: it waits for the terminal

        await Terminal(open: true);
        await Until(r => Item(r, 61).State == ItemState.Open, "the work continues once the terminal is open again");
        Assert.Contains("--resume", Runs[runs]);
        _sup!.Dispose();
    }

    [Fact]
    public async Task ClosingTheTerminalDuringTheBaselineCheckRunsItAgainLater()
    {
        Issues("""[{"number":63,"title":"Small","body":"Polish.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var ready = P("ready");
        await StartAsync();
        await Tool("agents_control", JsonSerializer.Serialize(new { action = "start", verify = $"if exist \"{ready}\" (exit 0) else (ping -n 120 127.0.0.1 >nul)" }));
        await Task.Delay(3000); // the baseline check is under way (or about to be)
        await Terminal(open: false);
        await Task.Delay(3000);
        Assert.False(await On(r => r.Paused)); // stopped, not failed: no "fails on main"
        Assert.Null(await On(r => r.Note));
        File.WriteAllText(ready, "");
        await Terminal(open: true);
        await Until(r => Item(r, 63).State == ItemState.Open, "the baseline runs again and the work goes on"); // a baseline left running would hold it for 2 minutes
        _sup!.Dispose();
    }

    [Fact]
    public async Task ARestartWithoutItsTerminalStopsTheRunUntilItOpens()
    {
        Issues("""[{"number":62,"title":"Long","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 62).State == ItemState.Working && Item(r, 62).Run is { Pid: > 0 }, "the run starts");
        var pid = await On(r => Item(r, 62).Run!.Pid);
        await _loop.Run(() => { _sup!.Dispose(); return 0; }); // the tray exits; the terminal is closed meanwhile
        await StartAsync(terminal: false);
        await Until(r => Item(r, 62).State == ItemState.Queued && Item(r, 62).Run is { Live: false }, "the re-attached run stops");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.Equal(0, await On(r => Item(r, 62).Failures));
        await Terminal(open: true);
        await Until(r => Item(r, 62).State == ItemState.Open, "the work continues once the terminal opens");
        _sup!.Dispose();
    }

    [Fact]
    public async Task SkippingOrClosingAnIssueMidRunEndsItsWorkForGood()
    {
        Issues("""
            [{"number":70,"title":"Skip me","body":"[fake:long]","labels":[],"assignees":[],"author":{"login":"owner"}},
             {"number":71,"title":"Close me","body":"[fake:long]","labels":[],"assignees":[],"author":{"login":"owner"}}]
            """);
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 70).Run is { Pid: > 0 }, "the first run starts");
        var first = await On(r => Item(r, 70).Run!.Pid);
        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"#70"}"""));
        await Until(r => r.Items.All(i => i.Issue != 70) && Item(r, 71).Run is { Pid: > 0 }, "work moves on to the next issue");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(first));

        var second = await On(r => Item(r, 71).Run!.Pid);
        Issues("[]"); // the owner closes #71 on GitHub
        await _loop.Run(() => { _sup!.RepoFor(_repo)!.LastScan = null; _sup.UpdateAccounts(Accounts, []); return 0; });
        await Until(r => r.Items.Count == 0, "the closed issue's work ends");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(second));
        await Task.Delay(1500);
        Assert.InRange(Runs.Length, 1, 2); // at most one each (a run killed early may not have logged), and neither came back
        Assert.Empty(Git(_repo, "branch", "--list", "autodock/*"));
        Assert.DoesNotContain("pr create", Read(Path.Combine(_gh, "gh.log")));
        _sup!.Dispose();
    }

    public void Dispose()
    {
        if (_sup != null) _loop.Run(() => { _sup.Dispose(); return 0; }).Wait(TimeSpan.FromSeconds(5));
        _loop.Dispose();
        // Runs outlive the supervisor by design; a test's must not outlive the test.
        foreach (var run in _store.Load().Repos.SelectMany(r => r.Items).Select(i => i.Run).OfType<Run>().Where(r => r.Live))
            using (var p = AgentProcess.Attach(run.Job, run.Pid, run.Created))
                p?.StopAsync(TimeSpan.Zero).Wait(TimeSpan.FromSeconds(10));
        Environment.SetEnvironmentVariable("AUTODOCK_DATA", _dataWas);
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(_tmp.Path, recursive: true); break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                foreach (var f in Directory.EnumerateFiles(_tmp.Path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Thread.Sleep(300);
            }
        }
    }
}

/// A single-threaded SynchronizationContext: what the tray's UI thread is to the supervisor.
sealed class Loop : SynchronizationContext, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = [];

    public Loop()
    {
        new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "agents test loop" }.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { } // disposed
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public Task<T> Run<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { tcs.SetResult(await work()); }
            catch (Exception e) { tcs.SetException(e); }
        }, null);
        return tcs.Task;
    }

    public Task<T> Run<T>(Func<T> work) => Run(() => Task.FromResult(work()));

    public void Dispose() => _queue.CompleteAdding();
}
