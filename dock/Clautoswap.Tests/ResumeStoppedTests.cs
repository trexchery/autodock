using Clautoswap;
using Xunit;

namespace Clautoswap.Tests;

public class ResumeStoppedTests
{
    const string Limit = """{"type":"assistant","isApiErrorMessage":true,"message":{"role":"assistant","content":[{"type":"text","text":"You've hit your session limit · resets 1:30am (America/Chicago)"}]}}""";
    const string Done = """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"All done."}]}}""";
    const string Typed = """{"type":"user","message":{"role":"user","content":"continue"}}""";
    const string Meta = """{"type":"system","subtype":"stop_hook_summary"}""";

    [Fact]
    public void LimitErrorAsLastMessageIsStopped() =>
        Assert.True(ResumeStopped.EndsAtLimit([Done, Typed, Limit, Meta, "{\"type\":\"api-req"]));

    [Fact]
    public void FinishedOrAnsweredSessionsAreNotStopped()
    {
        Assert.False(ResumeStopped.EndsAtLimit([Limit, Typed])); // someone typed after the limit
        Assert.False(ResumeStopped.EndsAtLimit([Limit, Done]));
        Assert.False(ResumeStopped.EndsAtLimit([Meta]));
        Assert.False(ResumeStopped.EndsAtLimit([
            """{"type":"assistant","isApiErrorMessage":true,"message":{"content":[{"type":"text","text":"API Error: 500 overloaded"}]}}"""]));
    }

    static Account A(int n, double five, bool active = false, bool disabled = false, string status = "ok") =>
        new(n, $"{n}@example.com", active, status, new Usage(new Window(five, null), new Window(10, null), null, null), Disabled: disabled);

    static StoppedSession On(int? account) => new(1, "id", @"C:\w", @"C:\cfg", account, @"C:\cfg\projects\w\id.jsonl");

    [Fact]
    public void OwnAccountWinsOnceItHasRoom() =>
        Assert.Equal(3, ResumeStopped.Target(On(3), [A(1, 0), A(3, 40, active: true)], _ => true));

    [Fact]
    public void ExhaustedAccountMovesToTheOneWithMostRoom() =>
        Assert.Equal(2, ResumeStopped.Target(On(3), [A(1, 50), A(2, 5), A(3, 100, active: true)], _ => true));

    [Fact]
    public void ExcludedUnreadableAndProfilelessAccountsAreSkipped()
    {
        Assert.Equal(4, ResumeStopped.Target(On(3),
            [A(1, 0, disabled: true), A(2, 0, status: "token_expired"), A(3, 100, active: true), A(4, 60), A(5, 1)], n => n != 5));
        Assert.Null(ResumeStopped.Target(On(3), [A(1, 100), A(3, 100, active: true)], _ => true)); // nowhere has room: wait
    }

    [Fact]
    public void ActiveAccountNeedsNoProfile() =>
        Assert.Equal(1, ResumeStopped.Target(On(3), [A(1, 10, active: true), A(3, 100)], _ => false));

    [Fact]
    public void TranscriptAndItsFolderAreCopiedNextToTheTarget()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var src = Path.Combine(root, "a", "projects", "C--w");
            Directory.CreateDirectory(Path.Combine(src, "sid", "subagents"));
            File.WriteAllText(Path.Combine(src, "sid.jsonl"), Limit);
            File.WriteAllText(Path.Combine(src, "sid", "subagents", "x.jsonl"), "{}");
            var s = new StoppedSession(1, "sid", @"C:\w", Path.Combine(root, "a"), 3, Path.Combine(src, "sid.jsonl"));
            ResumeStopped.CopyTranscript(s, Path.Combine(root, "b"));
            Assert.Equal(Limit, File.ReadAllText(Path.Combine(root, "b", "projects", "C--w", "sid.jsonl")));
            Assert.True(File.Exists(Path.Combine(root, "b", "projects", "C--w", "sid", "subagents", "x.jsonl")));
            ResumeStopped.CopyTranscript(s, Path.Combine(root, "a")); // same profile: nothing to do
        }
        finally { Directory.Delete(root, true); }
    }
}

/// Read-only scan of this machine's real Claude windows; set CLAUTOSWAP_LIVE_SCAN=1. Passes trivially otherwise.
public class ResumeStoppedLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void ScansRealWindows()
    {
        if (Environment.GetEnvironmentVariable("CLAUTOSWAP_LIVE_SCAN") != "1") return;
        var dirs = ResumeStopped.ConfigDirs(null);
        foreach (var d in dirs) output.WriteLine($"dir {d.Account} {d.Dir}");
        foreach (var s in ResumeStopped.Scan(dirs, DateTime.UtcNow)) output.WriteLine($"stopped {s.Pid} {s.SessionId} {s.Cwd} #{s.Account}");
        foreach (var f in (Environment.GetEnvironmentVariable("CLAUTOSWAP_LIVE_TRANSCRIPTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            output.WriteLine($"atLimit={ResumeStopped.EndsAtLimit(File.ReadLines(f))} {Path.GetFileName(f)}");
    }
}
