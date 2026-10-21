using Handoff;
using Xunit;

namespace Handoff.Tests;

// Model, effort and ultracode routing: the director advises, the supervisor validates, gates and escalates. Synthetic accounts only.

public class RoutingTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Now;

    static Account A(double pct, double? opus = null) =>
        new(2, "a2@example.com", false, "ok", new Usage(new Window(pct, Now.AddHours(2)), new Window(pct, Now.AddDays(3)),
            opus is { } o ? [new Window(o, Now.AddDays(3), "Opus")] : null, null));

    [Fact]
    public void TheDirectorsWordsAreValidatedAndTheThreeChoicesStayIndependent()
    {
        // Haiku only for trivial work; whatever the model, effort is the director's own choice.
        Assert.Equal(("standard", "sonnet", "max"), Shape(Routing.Validate("standard", "haiku", "max", null, null)));
        Assert.Equal(("trivial", "haiku", "max"), Shape(Routing.Validate("trivial", "haiku", "max", null, null)));
        Assert.Equal(("hard", "opus", "low"), Shape(Routing.Validate("hard", "opus", "low", null, null)));
        Assert.True(Routing.Validate("trivial", "haiku", "low", true, null).Ultracode); // never tied to Opus or a deep tier
        // Unknown or missing values fall back by difficulty; no other model gets through.
        Assert.Equal(("standard", "sonnet", "medium"), Shape(Routing.Validate("whatever", "fable", "ultracode", null, null)));
        Assert.Equal(("hard", "opus", "high"), Shape(Routing.Validate("Critical", null, null, null, null)));
        Assert.Equal(("trivial", "haiku", "low"), Shape(Routing.Validate("TRIVIAL", null, null, null, null)));
        Assert.Equal(("standard", "sonnet", "medium", false, "default"), (Routing.Default.Difficulty, Routing.Default.Model, Routing.Default.Effort, Routing.Default.Ultracode, Routing.Default.Source));
        // A reviewer is never Haiku and never fans out.
        Assert.Equal(new RunSettings("sonnet", "low", false), Routing.Review(Routing.Validate("trivial", "haiku", "max", true, null)));
        Assert.Equal(new RunSettings("opus", "high", false), Routing.Review(Routing.Validate("hard", "sonnet", "low", true, null)));
    }

    static (string, string, string) Shape(Route r) => (r.Difficulty, r.Model, r.Effort);

    [Fact]
    public void FailuresRaiseOneLeverChosenByTheirKind()
    {
        var r = Routing.Validate("standard", "sonnet", "medium", false, null);
        var review = Routing.Escalate(r, "review", ultracode: true)!;
        Assert.Equal(("opus", "medium", false, 1, "escalated"), (review.Model, review.Effort, review.Ultracode, review.Escalations, review.Source));
        var verify = Routing.Escalate(r, "verify", ultracode: true)!;
        Assert.Equal(("sonnet", "high", false), (verify.Model, verify.Effort, verify.Ultracode));
        Assert.True(verify.VerifyFailed);

        var top = Routing.Validate("hard", "opus", "max", false, null);
        Assert.True(Routing.Escalate(top, "review", ultracode: true)!.Ultracode); // model and effort at the top: ultracode
        Assert.Null(Routing.Escalate(top, "review", ultracode: false)); // not allowed: nothing left to raise
        Assert.Null(Routing.Escalate(Routing.Escalate(top, "verify", ultracode: true)!, "verify", ultracode: true));
        // Only a raised model waits for that model: an Opus route whose effort rose may still fall back to Sonnet.
        Assert.True(review.ModelRaised);
        Assert.False(Routing.Escalate(Routing.Validate("hard", "opus", "high", false, null), "verify", ultracode: true)!.ModelRaised);
        Assert.True(Routing.Escalate(review, "verify", ultracode: true)!.ModelRaised); // carried forward
    }

    [Fact]
    public void UltracodeRunsOnlyWhenAllowedRecommendedAloneAndWithRoom()
    {
        var on = Routing.Validate("trivial", "haiku", "low", true, null);
        Assert.True(Routing.UltracodeOn(on, allowed: true, A(10), othersOnAccount: 0, threshold: 90)); // any model, any effort
        Assert.False(Routing.UltracodeOn(on, allowed: false, A(10), 0, 90)); // the owner's toggle, off by default
        Assert.False(Routing.UltracodeOn(Routing.Validate("hard", "opus", "max", false, null), true, A(10), 0, 90)); // not recommended
        Assert.False(Routing.UltracodeOn(on, true, A(10), othersOnAccount: 1, threshold: 90)); // the account already runs something
        Assert.False(Routing.UltracodeOn(on, true, A(61), 0, 90)); // less than UltracodeRoom below the threshold
        Assert.False(Routing.UltracodeOn(on, true, new Account(2, "a2@example.com", false, "ok", null), 0, 90)); // unknown usage
        var opus = Routing.Validate("hard", "opus", "high", true, null);
        Assert.False(Routing.UltracodeOn(opus, true, A(10, opus: 70), 0, 90)); // the model's own weekly window counts
        Assert.True(Routing.UltracodeOn(opus, true, A(10, opus: 20), 0, 90));
        // An organization plan's live reading: a 5-hour window and per-model weekly ones, no overall weekly window.
        Account Org(double fiveHour) => new(3, "a3@example.com", false, "ok", new Usage(new Window(fiveHour, Now.AddHours(2)), null, [new Window(75, Now.AddDays(2), "Fable")], null));
        Assert.True(Routing.UltracodeOn(on, true, Org(7), 0, 90));
        Assert.False(Routing.UltracodeOn(on, true, Org(64), 0, 90));
        Assert.Equal(7, Routing.Peak(Routing.Snap(Org(7), "opus")));
        Assert.Null(Routing.Peak(new QuotaSnap(null, null, 20))); // only a model's window known: unknown
    }

    [Fact]
    public void ArgsStateModelEffortAndUltracodeAndTheModelAllowlist()
    {
        var args = Routing.Args(new RunSettings("sonnet", "xhigh", false));
        Assert.Equal(["--model", "sonnet", "--effort", "xhigh", "--settings"], args[..5]);
        Assert.Equal("""{"ultracode":false,"availableModels":["haiku","sonnet","opus"]}""", args[5]);
    }

    [Fact]
    public void ResultTokensComeFromEveryModelAndDurationIsKept()
    {
        var events = new List<StreamEvent>();
        StreamTail.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"ok","duration_ms":4290,"usage":{"input_tokens":2,"output_tokens":121},"modelUsage":{"claude-sonnet-5-5":{"inputTokens":2,"outputTokens":121,"cacheReadInputTokens":100,"cacheCreationInputTokens":50},"claude-haiku-5-5":{"inputTokens":3,"outputTokens":4,"cacheReadInputTokens":0,"cacheCreationInputTokens":0}}}"""u8, events);
        var r = Assert.IsType<ResultEvent>(Assert.Single(events));
        Assert.Equal(new Tokens(5, 125, 100, 50), r.Tokens);
        Assert.Equal(4290, r.DurationMs);
        events.Clear();
        StreamTail.Parse("""{"type":"result","is_error":false,"result":"ok","usage":{"input_tokens":2,"output_tokens":7,"cache_read_input_tokens":3,"cache_creation_input_tokens":1}}"""u8, events);
        Assert.Equal(new Tokens(2, 7, 3, 1), ((ResultEvent)events[0]).Tokens);
    }
}

public class DirectorRoutingTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    static WorkItem I(int n, int rank = 0) => new() { Id = n.ToString(), Issue = n, Title = "issue " + n, Rank = rank };

    static RepoAgents Repo(params WorkItem[] items) => new() { Path = @"C:\r", Authorized = true, Items = [.. items], Triage = new Goal { Id = "triage", Title = "t" } };

    static PlanOutput Plan(string json) => Supervisor.ParsePlan("HANDOFF: PLAN\n" + json)!;

    [Fact]
    public void ATriageRoutesOrdersAndSequencesTheBacklogAndPlansNothing()
    {
        var (a, b, c, owners) = (I(1, 100), I(2), I(3), I(4, 10_010));
        var started = I(5);
        started.Worktree = @"C:\wt\5";
        var r = Repo(a, b, c, owners, started);
        Supervisor.Ingest([r], r, r.Triage!, Plan("""
            {"status":"working","summary":"s","items":[{"title":"invented"}],"drop":["#1"],
             "route":[
               {"item":"#3","difficulty":"trivial","model":"haiku","effort":"low","ultracode":"yes","why":"typo"},
               {"item":"#2","difficulty":"hard","model":"opus","effort":"xhigh","after":["#3","#2","#9"]},
               {"item":"#1","difficulty":"standard","after":["#2"]},
               {"item":"#4","model":"opus"},
               {"item":"#5","model":"haiku"}
             ]}
            """), new Dictionary<int, IssueInfo>(), [], Now);

        Assert.Equal(5, r.Items.Count); // nothing added, nothing dropped
        Assert.Equal(("haiku", "low", true, "director"), (c.Route!.Model, c.Route.Effort, c.Route.Ultracode, c.Route.Source));
        Assert.Equal(("opus", "xhigh"), (b.Route!.Model, b.Route.Effort));
        Assert.Equal(("standard", "opus"), (owners.Route!.Difficulty, owners.Route.Model)); // the owner's item is routed (Opus for standard work is the director's call) …
        Assert.Equal(10_010, owners.Rank); // … but keeps the owner's order
        Assert.Null(started.Route); // work under way keeps its own
        // The scan's finds take the director's order and its "after" (itself and unknown items aside).
        Assert.True(c.Rank > b.Rank && b.Rank > a.Rank && a.Rank < 10_000);
        Assert.Equal([Supervisor.Key(r, c)], b.After);
        Assert.Equal([Supervisor.Key(r, b)], a.After);
        Assert.False(Supervisor.Ready([r], a)); // #1 waits for #2 to land
        Assert.Equal((1, 0), (r.Triage!.Plans, r.Triage.Failures));
    }

    [Fact]
    public void AnAfterThatWouldCloseACycleIsIgnoredAndWhatWasShownButLeftOutGetsTheDefault()
    {
        var (a, b, c) = (I(1), I(2), I(3));
        var r = Repo(a, b, c);
        a.After = [Supervisor.Key(r, b)];
        Supervisor.Ingest([r], r, r.Triage!, Plan("""{"status":"working","route":[{"item":"#2","after":["#1"]}]}"""), new Dictionary<int, IssueInfo>(), [], Now);
        Assert.Empty(b.After); // #1 already waits on #2
        Assert.Contains(r.Triage!.Log, l => l.Contains("already waits on it"));
        Assert.Equal("default", c.Route!.Source); // shown, not routed: not shown again
        Assert.Equal("default", a.Route!.Source);
    }

    [Fact]
    public void AGoalPlanRoutesItsStepsAndTheQueuedWork()
    {
        var other = I(7);
        var r = Repo(other);
        var g = new Goal { Id = "g1", Title = "Ship" };
        r.Goals.Add(g);
        Supervisor.Ingest([r], r, g, Plan("""
            {"status":"working","items":[{"key":"a","title":"Design the schema","difficulty":"hard","model":"opus","effort":"max","ultracode":true},
                                         {"key":"b","title":"Wire it","after":["a"]}],
             "route":[{"item":"#7","difficulty":"trivial","model":"haiku","effort":"medium"}]}
            """), new Dictionary<int, IssueInfo>(), [], Now);
        var design = r.Items.Single(i => i.Title == "Design the schema");
        Assert.Equal(("opus", "max", true), (design.Route!.Model, design.Route.Effort, design.Route.Ultracode));
        Assert.Equal("default", r.Items.Single(i => i.Title == "Wire it").Route!.Source); // a step without routing
        Assert.Equal(("haiku", "medium"), (other.Route!.Model, other.Route.Effort));
    }

    [Fact]
    public void AnIssueTheDirectorSequencedAfterOneThatLeavesUnlandedWaitsForTheOwner()
    {
        var (a, b) = (I(1), I(2));
        var r = Repo(a, b);
        b.After = [Supervisor.Key(r, a)];
        r.Items.Remove(a); // the scan pruned it: no longer wanted, never landed
        Supervisor.HoldDependents([r], r, a, "which agents may no longer take");
        Assert.Equal(ItemState.NeedsOwner, b.State); // not silently unlocked by its predecessor's absence
        Assert.StartsWith("it was planned after #1", b.Note);
        Assert.Empty(b.After); // an answer runs it without waiting

        // One already under way never waits again: the edge just goes.
        var (c, d) = (I(3), I(4));
        d.Worktree = @"C:\wt\4";
        var r2 = Repo(c, d);
        d.After = [Supervisor.Key(r2, c)];
        Supervisor.HoldDependents([r2], r2, c);
        Assert.Equal(ItemState.Queued, d.State);
    }

    [Fact]
    public void TheDirectorSeesTheQueueInTheOrderItWouldRun()
    {
        var (low, high, started, answered) = (I(1, 0), I(2, 300), I(3, 900), I(4, 0));
        started.Worktree = @"C:\wt\3";
        answered.Guidance = "go";
        var r = Repo(low, high, started, answered);
        Assert.Equal([answered, high, low], Supervisor.Candidates(r));

        // A routed backlog does not hide a new issue behind it: unrouted work further back is shown too.
        var backlog = Enumerable.Range(100, 30).Select(n => { var i = I(n, 5_000 - n); i.Route = Routing.Default; return i; }).ToList();
        var urgent = I(7, 300);
        var big = Repo([.. backlog, urgent]);
        var shown = Supervisor.Candidates(big);
        Assert.Equal(11, shown.Count); // the front ten, and the one with no route
        Assert.Contains(urgent, shown);
        Assert.Equal(backlog[0], shown[0]);
    }

    [Fact]
    public void WorkThatArrivedWhileTheTriageRanIsLeftForTheNext()
    {
        var (seen, late) = (I(1), I(2));
        var r = Repo(seen, late);
        r.Triage!.Run = new Run { Kind = "plan", Started = Now };
        (seen.Created, late.Created) = (Now.AddMinutes(-5), Now.AddMinutes(2));
        Supervisor.Ingest([r], r, r.Triage, Plan("""{"status":"working","route":[]}"""), new Dictionary<int, IssueInfo>(), [], Now.AddMinutes(4));
        Assert.Equal("default", seen.Route!.Source);
        Assert.Null(late.Route);
    }
}
