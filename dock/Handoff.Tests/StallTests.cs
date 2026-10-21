using Handoff;
using Xunit;

namespace Handoff.Tests;

// What a quiet run means: waiting on something, finished, or stalled. Only the stream and the run's own answer decide;
// silence alone never reads as done.

public class StallTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    static readonly AgentsConfig Config = new(); // Stall 10 min, PendingStall 25 min, FinishGrace 15 s

    static List<StreamEvent> Events(params string[] lines)
    {
        var events = new List<StreamEvent>();
        foreach (var l in lines) StreamTail.Parse(System.Text.Encoding.UTF8.GetBytes(l), events);
        return events;
    }

    static RunActivity Activity(params string[] lines)
    {
        var a = new RunActivity();
        a.Add(Events(lines), Now);
        return a;
    }

    const string Thinking = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}}}""";
    const string ThinkingTokens = """{"type":"system","subtype":"thinking_tokens","estimated_tokens":250,"estimated_tokens_delta":200}""";
    const string Bash = """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"npm test"}}]}}""";
    const string BashDone = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string BackgroundOn = """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b1","task_type":"local_bash"}]}""";
    const string BackgroundOff = """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""";
    const string StillRunning = """{"type":"result","subtype":"success","is_error":false,"result":"The agent is still running in the background."}""";
    const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"Did it.\nHANDOFF: DONE added it"}""";
    const string Empty = """{"type":"result","subtype":"success","is_error":false,"result":""}""";
    const string Init = """{"type":"system","subtype":"init","session_id":"s","model":"claude-haiku-5-5"}""";

    [Fact]
    public void SilentReasoningIsNeitherPendingNorDoneAndOnlyAWholeWindowOfQuietStallsIt()
    {
        var a = Activity(Init, Thinking, ThinkingTokens);
        Assert.Empty(Events(Thinking)); // deltas are skipped unparsed: only the file growing counts, as progress
        Assert.Equal((false, (DateTimeOffset?)null), (a.Pending, a.Answered));
        Assert.Null(Supervisor.Verdict(a, fanned: false, TimeSpan.FromMinutes(9.9), Now, Config));
        Assert.Equal("stalled", Supervisor.Verdict(a, fanned: false, TimeSpan.FromMinutes(10.1), Now, Config));
    }

    [Fact]
    public void APendingToolOrBackgroundTaskOrUltracodeGetsTheLongerWindow()
    {
        var tool = Activity(Init, Bash);
        Assert.True(tool.Pending);
        Assert.Null(Supervisor.Verdict(tool, false, TimeSpan.FromMinutes(20), Now, Config));
        Assert.Equal("stalled", Supervisor.Verdict(tool, false, TimeSpan.FromMinutes(26), Now, Config));
        tool.Add(Events(BashDone), Now);
        Assert.False(tool.Pending);

        var background = Activity(Init, BackgroundOn);
        Assert.Equal((true, 1), (background.Pending, background.Background));
        Assert.Null(Supervisor.Verdict(background, false, TimeSpan.FromMinutes(20), Now, Config));
        background.Add(Events(BackgroundOff), Now);
        Assert.Equal("stalled", Supervisor.Verdict(background, false, TimeSpan.FromMinutes(11), Now, Config));

        Assert.Null(Supervisor.Verdict(Activity(Init), fanned: true, TimeSpan.FromMinutes(20), Now, Config));
    }

    [Fact]
    public void OnlyAnAnswerWithItsMarkerAndNothingPendingFinishesAQuietRun()
    {
        // "Still running in the background" is no answer; the marker is, even with an empty result after it.
        var a = Activity(Init, BackgroundOn, StillRunning);
        Assert.Null(a.Answered);
        a.Add(Events(BackgroundOff, Init, Done, Empty), Now);
        Assert.Equal(Now, a.Answered);
        Assert.Null(Supervisor.Verdict(a, false, TimeSpan.Zero, Now.AddSeconds(5), Config)); // Claude gets a moment to exit
        Assert.Equal("finished", Supervisor.Verdict(a, false, TimeSpan.FromSeconds(15), Now.AddSeconds(15), Config));
        // Long after the answer, a turn that just started again (its stream grew a moment ago) is not closed.
        Assert.Null(Supervisor.Verdict(a, false, TimeSpan.FromSeconds(2), Now.AddMinutes(5), Config));

        // Background work still running after the answer: Claude may go on, so it is not over.
        var waiting = Activity(Init, BackgroundOn, Done);
        Assert.Null(Supervisor.Verdict(waiting, false, TimeSpan.FromMinutes(5), Now.AddMinutes(5), Config));
        // A new turn after the answer (a task notification woke it) clears it.
        var resumed = Activity(Init, Done, Init);
        Assert.Null(resumed.Answered);
    }

    [Fact]
    public void TheRunsAnswerIsTheResultWithItsMarker()
    {
        Assert.Equal("Did it.\nHANDOFF: DONE added it", Supervisor.FinalResult(Events(StillRunning, Done, Empty))!.Text);
        Assert.Equal("The agent is still running in the background.", Supervisor.FinalResult(Events(StillRunning, Empty))!.Text);
        Assert.Equal("", Supervisor.FinalResult(Events(Empty))!.Text);
        // A later result with only a note is not the answer.
        const string Learned = """{"type":"result","subtype":"success","is_error":false,"result":"HANDOFF: LEARNED tests live in t/"}""";
        Assert.Equal("Did it.\nHANDOFF: DONE added it", Supervisor.FinalResult(Events(Done, Learned))!.Text);
        Assert.Null(Supervisor.FinalResult(Events(Init)));
    }

    [Fact]
    public void QuotaSnapshotsAreLiveReadingsWithTheirTimeAndNextReset()
    {
        var at = Now.AddMinutes(-2);
        var usage = new Usage(new Window(40, Now.AddHours(1)), new Window(20, Now.AddDays(2)), [new Window(15, Now.AddDays(3), "Opus")], null);
        var live = new Account(2, "a2@example.com", false, "ok", usage) { UsageFetchedAt = at };
        Assert.Equal(new QuotaSnap(40, 20, 15, at, Now.AddHours(1)), Routing.Snap(live, "opus", Now));
        Assert.Equal(Now.AddMinutes(-3), Routing.Snap(new Account(2, "a2@example.com", false, "ok", usage, UsageAgeSeconds: 180), null, Now)!.At);
        // Only the last good reading: possibly hours old, so no snapshot (and no ultracode on its say-so).
        Assert.Null(Routing.Snap(new Account(2, "a2@example.com", false, "error", null, LastGoodUsage: usage), "opus", Now));
    }
}

public class RerouteTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    static WorkItem I(int n) => new() { Id = n.ToString(), Issue = n, Title = "issue " + n };

    static RepoAgents Repo(params WorkItem[] items) => new() { Path = @"C:\r", Authorized = true, Items = [.. items], Triage = new Goal { Id = "triage", Title = "t" } };

    static void Triage(RepoAgents r, string route) =>
        Supervisor.Ingest([r], r, r.Triage!, Supervisor.ParsePlan("HANDOFF: PLAN\n" + $$"""{"status":"working","route":{{route}}}""")!, new Dictionary<int, IssueInfo>(), [], Now);

    [Fact]
    public void ATriageThatLeavesOutAPrerequisiteKeepsIt()
    {
        var (a, b, c) = (I(1), I(2), I(3));
        var r = Repo(a, b, c);
        Triage(r, """[{"item":"#3","after":["#1"]}]""");
        Assert.Equal([Supervisor.Key(r, a)], c.After);
        c.Route = null; // routed again: the next triage forgets #1 and names #2
        Triage(r, """[{"item":"#3","after":["#2"]},{"item":"#1","after":["#3"]}]""");
        Assert.Equal([Supervisor.Key(r, a), Supervisor.Key(r, b)], c.After); // kept, and added to
        Assert.Empty(a.After); // #1 after #3 would close a cycle through the edge that stayed
        Assert.False(Supervisor.Ready([r], c));
    }

    [Fact]
    public void APrerequisiteThatLandedIsNoEdgeAndOneClosedUnmergedAsksTheOwner()
    {
        var (merged, closed, x, y) = (I(1), I(2), I(3), I(4));
        (merged.State, merged.Pr, merged.Merged) = (ItemState.Done, 7, true);
        (closed.State, closed.Pr) = (ItemState.Done, 8);
        var r = Repo(merged, closed, x, y);
        var z = I(5);
        r.Items.Add(z);
        Triage(r, """[{"item":"#3","after":["#1"]},{"item":"#4","after":["#2","#5"]}]""");
        Assert.Empty(x.After);
        Assert.True(Supervisor.Ready([r], x));
        Assert.Equal(ItemState.NeedsOwner, y.State); // it can never land: never silently skipped, never waited on forever
        Assert.Equal([Supervisor.Key(r, z)], y.After); // the rest of its prerequisites still hold once the owner answers
        Assert.StartsWith("it was planned after #2 issue 2, whose pull request was closed without merging", y.Note);
        Assert.False(Supervisor.Problem(y)); // a question, not a broken run
    }

    [Fact]
    public void SkippingAPrerequisiteHoldsWhatTheTriageOrderedAfterIt()
    {
        var (a, b) = (I(1), I(2));
        var r = Repo(a, b);
        Triage(r, """[{"item":"#2","after":["#1"]}]""");
        r.Items.Remove(a);
        Supervisor.HoldDependents([r], r, a);
        Assert.Equal(ItemState.NeedsOwner, b.State);
        b.Route = null;
        Triage(r, """[{"item":"#2"}]"""); // a held item is not the triage's to reroute
        Assert.Equal(ItemState.NeedsOwner, b.State);
    }
}
