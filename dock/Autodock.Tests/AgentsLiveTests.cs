using System.Diagnostics;
using Autodock;
using Xunit;
using Xunit.Abstractions;

namespace Autodock.Tests;

/// Opt-in, against the real engine and Claude Code (it uses a little of a real account's quota):
///   AUTODOCK_LIVE_ENGINE=<path to cswap.exe>  AUTODOCK_LIVE_ACCOUNT=<number, not the signed-in one>
/// Skipped (passes trivially) otherwise, like the engine integration test.
public class AgentsLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ARealRunIsIsolatedResumableAndSealedAfterwards()
    {
        var engine = Environment.GetEnvironmentVariable("AUTODOCK_LIVE_ENGINE");
        var account = Environment.GetEnvironmentVariable("AUTODOCK_LIVE_ACCOUNT");
        if (engine is not { Length: > 0 } || account is not { Length: > 0 }) return;
        using var tmp = new TempDir();
        string F(string n) => Path.Combine(tmp.Path, n);
        var work = F("work");
        Directory.CreateDirectory(work);
        Process.Start(new ProcessStartInfo("git", "init --quiet") { WorkingDirectory = work, UseShellExecute = false })!.WaitForExit();

        var session = Guid.NewGuid().ToString();
        var first = await RunAsync(engine, account, session, resume: false, work, F, 1,
            "Reply with exactly this one line and nothing else:\nAUTODOCK: DONE smoke test");
        Assert.Contains(first, e => e is InitEvent i && i.SessionId == session);
        Assert.Equal("smoke test", Supervisor.Marker(first.OfType<ResultEvent>().Last().Text, "DONE"));

        var second = await RunAsync(engine, account, session, resume: true, work, F, 2,
            "What did you reply last time? Answer with exactly one line: AUTODOCK: DONE <your previous line's last two words>");
        Assert.Equal("smoke test", Supervisor.Marker(second.OfType<ResultEvent>().Last().Text, "DONE"));

        // The wrapper sealed the account's profile again: no plaintext login left behind.
        var sessions = Path.Combine(StoreSecurity.BackupRoot, "sessions");
        foreach (var profile in Directory.GetDirectories(sessions, account + "-*"))
        {
            output.WriteLine($"profile {Path.GetFileName(profile)}: records {Directory.GetFiles(Path.Combine(profile, "sessions"), "*.json").Length}");
            Assert.False(File.Exists(Path.Combine(profile, ".credentials.json")), "profile left unsealed");
        }
    }

    async Task<List<StreamEvent>> RunAsync(string engine, string account, string session, bool resume, string work, Func<string, string> f, int n, string prompt)
    {
        File.WriteAllText(f($"prompt-{n}.md"), prompt);
        var started = Stopwatch.StartNew();
        using var p = AgentProcess.Start(engine, Supervisor.WorkerArgs(int.Parse(account), session, resume, review: false), work,
            $@"Local\autodock-live-{Guid.NewGuid():N}", f($"prompt-{n}.md"), f($"out-{n}.jsonl"), f($"err-{n}.log"), Supervisor.WorkerEnvironment(f("no-gh")));
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
        var events = StreamTail.ReadAll(f($"out-{n}.jsonl"));
        var result = events.OfType<ResultEvent>().LastOrDefault();
        output.WriteLine($"run {n}: exit {p.ExitCode} in {started.Elapsed.TotalSeconds:0.0}s, {events.Count} events, cost {result?.CostUsd}, result: {result?.Text}");
        output.WriteLine("stderr: " + Supervisor.TailOf(f($"err-{n}.log"), 500));
        Assert.NotNull(result);
        Assert.False(result!.IsError, result.Text);
        Assert.Equal(0, p.ExitCode);
        return events;
    }
}
