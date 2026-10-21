using Clautoswap;
using Xunit;

namespace Clautoswap.Tests;

/// A usage-limit error reaching the switch: seen in a file, deduped, charged to an account, the default login moved.
/// Synthetic accounts, files and engine calls only: nothing here touches a real login or process.
public class LimitWatchTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    static string LimitLine(DateTimeOffset at, DateTimeOffset resets, string window = "five_hour", string error = "usage_limit_reached") =>
        $$$"""{"type":"assistant","timestamp":"{{{at:O}}}","apiError":"{{{error}}}","isApiErrorMessage":true,"apiErrorParams":{"rate_limit_info":{"status":"rejected","resetsAt":{{{resets.ToUnixTimeSeconds()}}},"rateLimitType":"{{{window}}}"}},"message":{"role":"assistant","content":[{"type":"text","text":"You've hit your limit"}]}}""";

    static Account A(int n, double five, bool active = false, DateTimeOffset? resets = null, bool disabled = false) =>
        new(n, $"{n}@example.com", active, "ok", new Usage(new Window(five, resets), new Window(10, null), null, null), Disabled: disabled);

    static string TempRoot() => Directory.CreateTempSubdirectory().FullName;

    static string Write(string root, string rel, params string[] lines)
    {
        var path = Path.Combine(root, "projects", "C--w", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllLines(path, lines);
        return path;
    }

    // ---- LimitWatch: reading the files --------------------------------------------------------------------------

    [Fact]
    public void PollReadsOnlyWhatWasAddedAndOnlyRealFreshLimits()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            var f = Write(root, "sid.jsonl", """{"type":"user","message":{"role":"user","content":"hi"}}""");
            w.Poll([(f, root)]);
            Assert.Empty(hits);
            File.AppendAllLines(f, [LimitLine(Now, Now.AddHours(1))]);
            w.Poll([(f, root)]);
            var hit = Assert.Single(hits);
            Assert.Equal("five_hour", hit.Limit.Window);
            Assert.Equal(root, hit.ConfigDir);
            w.Poll([(f, root)]);
            Assert.Single(hits); // read once
            File.AppendAllLines(f, [LimitLine(Now.AddMinutes(-10), Now.AddHours(1)), LimitLine(Now, Now.AddMinutes(-1)),
                LimitLine(Now, Now.AddHours(1), error: "rate_limit")]);
            w.Poll([(f, root)]);
            Assert.Single(hits); // history, a reset already past, and a server throttle are not limits
            File.AppendAllLines(f, [LimitLine(Now.AddSeconds(1), Now.AddHours(2), "seven_day")]);
            w.Poll([(f, root)]);
            Assert.Equal(2, hits.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ACopiedTranscriptDoesNotCountTwice()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            var f = Write(root, "sid.jsonl", LimitLine(Now, Now.AddHours(1)));
            w.Poll([(f, root)]);
            var copy = Path.Combine(root, "other", "sid.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(f, copy);
            w.Poll([(copy, root)]); // what the tray makes when it moves a window: the same error, in another profile
            Assert.Single(hits);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFileWatcherSeesASubagentFileAndASecondErrorInIt()
    {
        var root = TempRoot();
        try
        {
            var hits = new System.Collections.Concurrent.BlockingCollection<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            Directory.CreateDirectory(Path.Combine(root, "projects", "C--w"));
            w.Sync([root]);
            Assert.True(w.Watching(root));
            var f = Write(root, Path.Combine("sid", "subagents", "workflows", "wf_1", "agent-a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            Assert.True(hits.TryTake(out var hit, 10_000), "no hit from the watcher");
            Assert.Equal(f, hit.File);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WatcherOverflowFallsBackToThePollAndIsRestarted()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            var log = new List<string>();
            using var w = new LimitWatch(hits.Add, log.Add);
            Directory.CreateDirectory(Path.Combine(root, "projects", "C--w"));
            w.Sync([root]);
            w.Fail(root, new InternalBufferOverflowException("too many changes"));
            Assert.False(w.Watching(root));
            Assert.Contains(log, l => l.Contains("poll"));
            var f = Write(root, Path.Combine("sid", "subagents", "agent-a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            w.Poll([(f, root)]);
            Assert.Single(hits); // nothing was lost
            w.Sync([root]);
            Assert.True(w.Watching(root)); // and the watcher is back
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- LimitSwitch: what an error does ------------------------------------------------------------------------

    /// A switch with fake engine calls: the snapshot, the default login and the live profiles are variables the test moves.
    sealed class Rig
    {
        public Snapshot Snap;
        public int Default = 1;
        public DateTime Changed = DateTime.UtcNow.AddHours(-1); // when the default login last changed
        public HashSet<int> Live = [];
        public List<int> Switched = [];
        public List<string> Reports = [];
        public List<string> Log = [];
        public HashSet<int> Failing = [];
        public LimitSwitch Switch;

        public Rig(params Account[] accounts)
        {
            Snap = new Snapshot(1, 1, [.. accounts]);
            Switch = new LimitSwitch(() => Snap, () => (Default, Changed), () => Live, d => { Reports.Add(d); return Task.CompletedTask; },
                m =>
                {
                    if (Failing.Contains(m)) throw new EngineException("lock timeout");
                    Switched.Add(m);
                    var from = Default;
                    (Default, Changed) = (m, DateTime.UtcNow);
                    return Task.FromResult(new SwitchResult(true, new AccountRef(from, $"{from}@example.com"), new AccountRef(m, $"{m}@example.com"), null));
                }, new HashSet<string>(), Log.Add);
        }

        public Task<LimitOutcome?> Hit(DateTimeOffset at, DateTimeOffset resets, string? dir = null, string file = @"C:\x\sid.jsonl") =>
            Switch.OnLimitAsync(new LimitHit(file, dir ?? ResumeStopped.DefaultDir, new Limit("five_hour", resets, at)));
    }

    [Fact]
    public async Task OnLimitDedupesOnAccountAndReset()
    {
        var resets = Now.AddHours(1);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20), A(3, 60));
        var first = await rig.Hit(Now, resets);
        Assert.Equal(2, first!.Switched!.To!.Number);
        Assert.Null(await rig.Hit(Now.AddSeconds(1), resets, file: @"C:\x\sid\subagents\a.jsonl")); // another window, same refusal
        Assert.Equal([2], rig.Switched);
        var doc = Assert.Single(rig.Reports); // the engine was told once, the refused account at 100%
        Assert.Contains("1@example.com", doc);
        Assert.Contains(rig.Log, l => l.StartsWith("limit seen") && l.Contains("acct 1"));
        Assert.Contains(rig.Log, l => l.StartsWith("switch 1->2 ") && l.EndsWith(" ms"));
    }

    [Fact]
    public async Task AccountWithALiveProfileWindowIsNeverTheTarget()
    {
        var rig = new Rig(A(1, 50, active: true), A(2, 5), A(3, 60)) { Live = [2] };
        var o = await rig.Hit(Now, Now.AddHours(1));
        Assert.Equal([3], rig.Switched); // 2 has the most room, but a window of `cswap run 2` holds its login
        Assert.Equal(3, o!.Switched!.To!.Number);
        Assert.Equal([3], ResumeStopped.SwitchTargets(rig.Snap.Accounts, 1, new HashSet<int> { 2 }, new Dictionary<int, DateTimeOffset>(), Now));
    }

    [Fact]
    public async Task SecondRefusalHopsOnAndNeverGoesBackToTheFirstRefuser()
    {
        var rig = new Rig(A(1, 10, active: true), A(2, 20), A(3, 60)); // 1's reading is low and stale: it just said no
        Assert.Equal(2, (await rig.Hit(Now, Now.AddHours(1)))!.Switched!.To!.Number);
        rig.Snap = rig.Snap with { ActiveAccountNumber = 2, Accounts = [.. rig.Snap.Accounts.Select(a => a with { Active = a.Number == 2 })] };
        var o = await rig.Hit(Now.AddMinutes(1), Now.AddHours(2));
        Assert.Equal(3, o!.Switched!.To!.Number); // not 1, whatever its reading says, until it resets
        Assert.Equal([2, 3], rig.Switched);
        Assert.Equal(Now.AddHours(1).ToUnixTimeSeconds(), rig.Switch.RefusedUntil[1].ToUnixTimeSeconds());
    }

    [Fact]
    public async Task NoAccountWithRoomMeansOneNoticeWithTheEarliestResetAndNoSwitch()
    {
        var soon = Now.AddMinutes(40);
        var rig = new Rig(A(1, 100, active: true), A(2, 100, resets: soon), A(3, 40, disabled: true));
        var o = await rig.Hit(Now, Now.AddHours(1));
        Assert.True(o!.NoRoom);
        Assert.Equal(soon.ToUnixTimeSeconds(), o.Earliest!.Value.ToUnixTimeSeconds());
        Assert.Empty(rig.Switched);
    }

    [Fact]
    public async Task FailedSwitchTriesTheNextAccountThenGivesUp()
    {
        var rig = new Rig(A(1, 50, active: true), A(2, 20), A(3, 30)) { Failing = [2] };
        Assert.Equal(3, (await rig.Hit(Now, Now.AddHours(1)))!.Switched!.To!.Number);
        var all = new Rig(A(1, 50, active: true), A(2, 20)) { Failing = [2] };
        var o = await all.Hit(Now, Now.AddHours(1));
        Assert.NotNull(o!.Error); // the tray then falls back to the engine's own check
        Assert.Contains(all.Log, l => l.Contains("switch 1->2 failed"));
    }

    [Fact]
    public async Task ARefusalOnAnotherLoginOrProfileDoesNotMoveTheDefaultLogin()
    {
        var resets = Now.AddHours(3);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20), A(3, 60));
        var o = await rig.Hit(Now, resets, dir: Path.Combine(Path.GetTempPath(), "3-c_example.com")); // a `cswap run 3` window
        Assert.Null(o!.Switched);
        Assert.Empty(rig.Switched);
        Assert.Contains(3, rig.Switch.RefusedUntil.Keys); // but 3 is out until its reset, for every window
        rig.Default = 2; // the engine's own check already moved the login on: nothing more to do for a refusal of 1
        var late = await rig.Hit(Now.AddMinutes(5), resets); // 1's window, as the reset time says
        Assert.Null(late!.Switched);
        Assert.Empty(rig.Switched);
    }

    [Fact]
    public void RefusalIsChargedToTheAccountThatSaidNo()
    {
        var resets = Now.AddHours(2);
        var l = new Limit("five_hour", resets, Now);
        Account[] accounts = [A(1, 30, active: true), A(2, 90, resets: resets.AddSeconds(30)), A(3, 20)];
        Assert.Equal(3, ResumeStopped.Refuser(l, 3, accounts, (1, DateTime.UtcNow.AddHours(-1)))); // a profile's own
        Assert.Equal(2, ResumeStopped.Refuser(l, null, accounts, (1, DateTime.UtcNow))); // the window that resets then, though the login just changed
        Assert.Equal(1, ResumeStopped.Refuser(l, null, [A(1, 30, active: true), A(3, 20)], (1, DateTime.UtcNow.AddHours(-1))));
        Assert.Null(ResumeStopped.Refuser(l, null, [A(1, 30, active: true), A(3, 20)], (1, DateTime.UtcNow))); // too soon after a switch: unknown
        Assert.Equal(3, ResumeStopped.ProfileAccount(@"C:\b\sessions\3-nick_example.com"));
        Assert.Null(ResumeStopped.ProfileAccount(ResumeStopped.DefaultDir));
    }

    // ---- the windows themselves ---------------------------------------------------------------------------------

    [Fact]
    public void MoveCapCountsASessionsMovesInTheLastHour()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        // One stamp per move, so the same session bouncing between two accounts still adds up (an account key is overwritten).
        var moves = new Dictionary<string, DateTime> { ["s1#2"] = now.AddMinutes(-5), ["s1#3"] = now.AddMinutes(-30),
            [ResumeStopped.MoveStamp("s1", now.AddMinutes(-5))] = now.AddMinutes(-5), [ResumeStopped.MoveStamp("s1", now.AddMinutes(-35))] = now.AddMinutes(-35),
            [ResumeStopped.MoveStamp("s1", now.AddMinutes(-59))] = now.AddMinutes(-59), [ResumeStopped.MoveStamp("s1", now.AddMinutes(-61))] = now.AddMinutes(-61),
            [ResumeStopped.MoveStamp("s2", now.AddMinutes(-1))] = now.AddMinutes(-1), [ResumeStopped.MoveStamp("s10", now.AddMinutes(-1))] = now.AddMinutes(-1) };
        Assert.Equal(ResumeStopped.MovesPerHour, ResumeStopped.MovesInLastHour(moves, "s1", now));
        Assert.Equal(1, ResumeStopped.MovesInLastHour(moves, "s2", now));
        Assert.Equal(0, ResumeStopped.MovesInLastHour(moves, "s9", now));
    }

    [Fact]
    public void ProfileWithARunMarkerOrALiveClaudeIsInUse()
    {
        var root = TempRoot();
        try
        {
            string Profile(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var marked = Profile("3-c_example.com");
            Directory.CreateDirectory(Path.Combine(marked, ".cswap-runs"));
            File.WriteAllText(Path.Combine(marked, ".cswap-runs", Environment.ProcessId.ToString()), "");
            var stale = Profile("4-d_example.com");
            Directory.CreateDirectory(Path.Combine(stale, ".cswap-runs"));
            File.WriteAllText(Path.Combine(stale, ".cswap-runs", "2147483000"), ""); // no such process
            var quiet = Profile("5-e_example.com");
            var self = System.Diagnostics.Process.GetCurrentProcess();
            var windowed = Profile("6-f_example.com");
            Directory.CreateDirectory(Path.Combine(windowed, "sessions"));
            File.WriteAllText(Path.Combine(windowed, "sessions", "1.json"),
                $$"""{"pid":{{self.Id}},"sessionId":"sid","procStart":"{{self.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""");
            var live = ResumeStopped.LiveProfiles([(marked, 3), (stale, 4), (quiet, 5), (windowed, 6), (ResumeStopped.DefaultDir, 1)]);
            Assert.Equal([3, 6], live.Order().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    static void Stage(string root, string status, params string[] lines)
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var project = Path.Combine(root, "projects", "C--w");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        File.WriteAllLines(Path.Combine(project, "sid.jsonl"), lines);
        File.WriteAllText(Path.Combine(root, "sessions", "1.json"),
            $$"""{"pid":{{self.Id}},"sessionId":"sid","cwd":"C:\\w","status":"{{status}}","kind":"interactive","procStart":"{{self.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""");
    }

    [Fact]
    public void BusyShellOrWaitingWindowAtALimitIsSeenButNeverMovable()
    {
        var root = TempRoot();
        try
        {
            var later = DateTime.UtcNow.AddSeconds(10);
            foreach (var status in new[] { "busy", "shell", "waiting" })
            {
                Stage(root, status, LimitLine(Now, Now.AddHours(1)));
                Assert.Empty(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue)); // the default scan never finds it
                var s = Assert.Single(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue, includeBusy: true));
                Assert.False(s.Idle);
                Assert.True(s.AtLimit);
                Assert.False(ResumeStopped.Unchanged(s)); // and the last check before a close refuses it too
            }
            Stage(root, "idle", LimitLine(Now, Now.AddHours(1)));
            Assert.True(Assert.Single(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue, includeBusy: true)).Idle);
            // the poll reads a live window's subagent files too
            Write(root, Path.Combine("sid", "subagents", "agent-a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            var files = ResumeStopped.LiveTranscripts([(root, 3)], DateTime.UtcNow).Select(f => Path.GetFileName(f.File)).Order().ToArray();
            Assert.Equal(["agent-a.jsonl", "sid.jsonl"], files);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnAccountRefusedUntilItsResetIsNotATargetForAnyWindow()
    {
        var s = new StoppedSession(1, "id", @"C:\w", @"C:\cfg", 3, @"C:\cfg\projects\w\id.jsonl");
        Account[] accounts = [A(1, 10), A(2, 30), A(3, 100)];
        var until = new Dictionary<int, DateTimeOffset> { [1] = Now.AddHours(1) };
        Assert.Equal(1, ResumeStopped.Target(s, accounts, _ => true, now: Now));
        Assert.Equal(2, ResumeStopped.Target(s, accounts, _ => true, now: Now, refusedUntil: until)); // 1 said no an hour ago, its reading is just old
        Assert.Equal(1, ResumeStopped.Target(s, accounts, _ => true, now: Now.AddHours(2), refusedUntil: until));
    }

    // ---- review fixes --------------------------------------------------------------------------------------------

    [Fact]
    public void ABackgroundNoticeAfterTheErrorDoesNotHideTheStop()
    {
        var notice = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>done</task-notification>"}}""";
        var typed = """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"carry on"}}""";
        var limit = LimitLine(Now, Now.AddHours(1));
        Assert.NotNull(ResumeStopped.LimitOf([limit, notice]));
        Assert.Null(ResumeStopped.LimitOf([limit, typed])); // someone typing does
        Assert.Null(ResumeStopped.LimitOf([limit, notice, typed]));
    }

    [Fact]
    public async Task ARefusalSeenAgainStillSwitchesWhileTheDefaultLoginIsBackOnTheRefuser()
    {
        var resets = Now.AddHours(1);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20), A(3, 60));
        Assert.Equal(2, (await rig.Hit(Now, resets))!.Switched!.To!.Number);
        rig.Default = 1; // the user (or the engine) moved the login back onto the account that said no
        var again = await rig.Hit(Now.AddSeconds(5), resets);
        Assert.Equal(2, again!.Switched!.To!.Number);
        Assert.Single(rig.Reports); // but it is reported once
        Assert.Single(rig.Log, l => l.StartsWith("limit seen"));
    }

    [Fact]
    public async Task AHitBeforeTheFirstListingIsNotConsumed()
    {
        var resets = Now.AddHours(1);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20));
        var snap = rig.Snap;
        rig.Snap = null!;
        Assert.Null(await rig.Hit(Now, resets));
        rig.Snap = snap;
        Assert.Equal(2, (await rig.Hit(Now, resets))!.Switched!.To!.Number); // the same refusal still counts once there is a listing
    }

    [Fact]
    public void SwitchTargetsStayBelowTheEnginesThresholdWhileAnyAre()
    {
        Account[] accounts = [A(1, 100, active: true), A(2, 98), A(3, 60)];
        var none = new Dictionary<int, DateTimeOffset>();
        Assert.Equal([3], ResumeStopped.SwitchTargets(accounts, 1, new HashSet<int>(), none, Now, threshold: 97));
        Assert.Equal([2], ResumeStopped.SwitchTargets([accounts[0], accounts[1]], 1, new HashSet<int>(), none, Now, threshold: 97)); // only above it: better than nothing
    }

    [Fact]
    public void ACopyTheTrayMadeIsNotANewRefusalButTheNextErrorThereIs()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            w.Remember(Now); // before the copy lands
            var f = Write(root, "sid.jsonl", LimitLine(Now, Now.AddHours(1)));
            w.Poll([(f, root)]);
            Assert.Empty(hits);
            File.AppendAllLines(f, [LimitLine(Now.AddSeconds(2), Now.AddHours(1))]); // the reopened window's own refusal
            w.Poll([(f, root)]);
            Assert.Single(hits);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ALimitOnlyInASubagentFileStopsItsSessionToo()
    {
        var root = TempRoot();
        try
        {
            var agent = Write(root, Path.Combine("sid", "subagents", "a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            Assert.Equal("sid", ResumeStopped.AgentSession(root, agent));
            Assert.Null(ResumeStopped.AgentSession(root, Path.Combine(root, "projects", "C--w", "sid.jsonl")));
            Stage(root, "idle", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"waiting on the workflow"}],"stop_reason":"tool_use"}}""");
            var later = DateTime.UtcNow.AddSeconds(10);
            Assert.Empty(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue));
            var s = Assert.Single(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue, agentLimits: new Dictionary<string, Limit> { ["sid"] = new Limit("five_hour", Now.AddHours(1), Now) }));
            Assert.True(s.AtLimit);
            Assert.True(ResumeStopped.Unchanged(s)); // the main transcript is as quiet as at the scan
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AReplayedRefusalIsChargedToTheAccountAlreadyRefusedForThatReset()
    {
        var resets = Now.AddHours(2);
        Account[] both = [A(1, 100, active: true, resets: resets), A(2, 100, resets: resets.AddSeconds(30))]; // two accounts reset together
        var l = new Limit("five_hour", resets, Now.AddMinutes(5)); // a stuck window's new line, the old reset
        var after = (2, DateTime.UtcNow.AddMinutes(-5)); // the login is on 2 now, long enough ago to be trusted
        Assert.Equal(2, ResumeStopped.Refuser(l, null, both, after)); // without a record: the login
        Assert.Equal(1, ResumeStopped.Refuser(l, null, both, after, new Dictionary<int, DateTimeOffset> { [1] = resets }));
    }

    [Fact]
    public void ATypedPromptAfterTheErrorKeepsTheWindowAtTheLimitUntilAReplyOrTheReset()
    {
        var root = TempRoot();
        try
        {
            var typed = """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"continue"}}""";
            var reply = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}}""";
            var at = DateTimeOffset.UtcNow;
            Stage(root, "idle", LimitLine(at, at.AddHours(1)), typed);
            Assert.Single(ResumeStopped.Scan([(root, 3)], DateTime.UtcNow.AddSeconds(10), DateTime.MinValue));
            Assert.Empty(ResumeStopped.Scan([(root, 3)], DateTime.UtcNow.AddHours(2), DateTime.MinValue)); // the reset has passed
            Stage(root, "idle", LimitLine(at, at.AddHours(1)), typed, reply);
            Assert.Empty(ResumeStopped.Scan([(root, 3)], DateTime.UtcNow.AddSeconds(10), DateTime.MinValue)); // it answered: running again
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnOldAgentLimitDoesNotHideAReopenedSessionsFinishedTurn()
    {
        var root = TempRoot();
        try
        {
            Stage(root, "idle", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}}""");
            var old = new Limit("five_hour", Now.AddHours(1), Now.AddDays(-1)); // from before this process started
            var s = Assert.Single(ResumeStopped.Scan([(root, 3)], DateTime.UtcNow.AddSeconds(30), DateTime.MinValue, agentLimits: new Dictionary<string, Limit> { ["sid"] = old }));
            Assert.False(s.AtLimit); // a finished turn: the threshold move still sees it
        }
        finally { Directory.Delete(root, true); }
    }
}
