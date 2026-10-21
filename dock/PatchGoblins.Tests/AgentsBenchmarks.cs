using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PatchGoblins;
using Xunit;
using Xunit.Abstractions;

namespace PatchGoblins.Tests;

/// Resource measurements, opt-in: PATCH_GOBLINS_BENCH=<results.md> (and PATCH_GOBLINS_BENCH_APP=<built patch-goblins.exe>
/// for the whole-app idle numbers). Each prints a markdown table to the test output and the file.
[Collection("agents end to end")]
public sealed class AgentsBenchmarks(ITestOutputHelper output) : IDisposable
{
    readonly string? _results = Environment.GetEnvironmentVariable("PATCH_GOBLINS_BENCH");
    readonly TempDir _tmp = new();

    void Report(string title, IEnumerable<(string, string)> rows)
    {
        var sb = new StringBuilder($"\n### {title}\n\n| measure | value |\n|---|---|\n");
        foreach (var (k, v) in rows) sb.Append($"| {k} | {v} |\n");
        output.WriteLine(sb.ToString());
        File.AppendAllText(_results!, sb.ToString());
    }

    static double CpuMs(Process p) { p.Refresh(); return p.TotalProcessorTime.TotalMilliseconds; }

    [Fact]
    public async Task SupervisorCostIdleAndWithAWorker()
    {
        if (_results == null) return;
        var fakes = Path.Combine(AppContext.BaseDirectory, "fakes");
        var repo = Path.Combine(_tmp.Path, "repo");
        var gh = Path.Combine(_tmp.Path, "gh");
        Directory.CreateDirectory(gh);
        Directory.CreateDirectory(repo);
        foreach (var args in new[] { "init --quiet -b main", "config user.email b@example.com", "config user.name bench", "commit --quiet --allow-empty -m init" })
            Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = repo, UseShellExecute = false })!.WaitForExit();
        Environment.SetEnvironmentVariable("FAKE_GH", gh);
        Environment.SetEnvironmentVariable("FAKE_LOG", Path.Combine(_tmp.Path, "cswap.log"));
        File.WriteAllText(Path.Combine(gh, "issues.json"), "[]");
        using var loop = new Loop();
        var pumps = 0;
        var sup = await loop.Run(() =>
        {
            var s = new Supervisor(new Engine(Path.Combine(fakes, "cswap.cmd")), new Cli(Engine.FindOnPath("git.exe")!, Path.Combine(fakes, "gh.cmd")),
                new AgentStore(Path.Combine(_tmp.Path, "agents")), new AgentsConfig());
            s.Authorize = (_, _, _) => Task.FromResult(true);
            s.Changed += () => pumps++;
            s.Start();
            s.UpdateAccounts([new Account(2, "b@example.com", false, "ok", null)], []);
            return s;
        });
        await loop.Run(() => sup.HandleAsync("agents_control", JsonDocument.Parse("""{"action":"start"}""").RootElement, repo));
        await Task.Delay(5000); // first scan done

        var me = Process.GetCurrentProcess();
        (double Cpu, long Alloc, int Pumps) Sample() => (CpuMs(me), GC.GetTotalAllocatedBytes(), pumps);
        var idleSeconds = 60;
        var a = Sample();
        await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
        var b = Sample();

        File.WriteAllText(Path.Combine(gh, "issues.json"), """[{"number":1,"title":"Long","body":"[fake:long]","labels":[],"assignees":[]}]""");
        await loop.Run(() => { sup.RepoFor(repo)!.LastScan = null; sup.UpdateAccounts([new Account(2, "b@example.com", false, "ok", null)], []); return 0; });
        await AgentProcessTests.WaitUntil(() => loop.Run(() => sup.Working).Result == 1, 60);
        var c = Sample();
        await Task.Delay(TimeSpan.FromSeconds(45));
        var d = Sample();
        await loop.Run(() => { sup.Dispose(); return 0; });
        foreach (var run in new AgentStore(Path.Combine(_tmp.Path, "agents")).Load().Repos.SelectMany(r => r.Items).Select(i => i.Run).OfType<Run>().Where(r => r.Live))
            using (var p = AgentProcess.Attach(run.Job, run.Pid, run.Created)) p?.StopAsync(TimeSpan.Zero).Wait();

        Report("Supervisor, in process (test host; includes its own noise)", [
            ("authorized, nothing to do: CPU", $"{(b.Cpu - a.Cpu) / idleSeconds * 60:0} ms per minute"),
            ("authorized, nothing to do: wake-ups", $"{b.Pumps - a.Pumps} in {idleSeconds} s"),
            ("authorized, nothing to do: allocated", $"{(b.Alloc - a.Alloc) / 1024.0:0} KB in {idleSeconds} s"),
            ("one worker streaming 1 event/s: CPU", $"{(d.Cpu - c.Cpu) / 45 * 60:0} ms per minute"),
            ("one worker streaming 1 event/s: wake-ups", $"{d.Pumps - c.Pumps} in 45 s"),
            ("one worker streaming 1 event/s: allocated", $"{(d.Alloc - c.Alloc) / 1024.0:0} KB in 45 s"),
        ]);
    }

    [Fact]
    public void StatusLineRender()
    {
        if (_results == null) return;
        var bash = @"C:\Program Files\Git\bin\bash.exe";
        if (!File.Exists(bash)) return;
        string F(string n) => Path.Combine(_tmp.Path, n).Replace('\\', '/');
        File.WriteAllText(F("state"), "on\n");
        File.WriteAllText(F("t.jsonl"), "{}");
        var json = $$"""{"session_id":"x","transcript_path":"{{F("t.jsonl").Replace("/", "\\\\")}}"}""";
        var rows = new List<(string, string)>();
        foreach (var (name, user) in new[] { ("label only", (string?)null), ("label + cached owner line", "printf 'mine'") })
        {
            var script = Path.Combine(_tmp.Path, $"s{rows.Count}.sh");
            File.WriteAllText(script, AgentsTerminal.StatusScript(user, F("state"), F($"cache{rows.Count}")).ReplaceLineEndings("\n"));
            double wall = 0, cpu = 0;
            const int n = 20;
            for (var i = 0; i < n; i++)
            {
                var sw = Stopwatch.StartNew();
                var psi = new ProcessStartInfo(bash, $"--noprofile --norc -c \". '{script.Replace('\\', '/')}'\"") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                p.StandardInput.Write(json);
                p.StandardInput.Close();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                wall += sw.Elapsed.TotalMilliseconds;
                cpu += p.TotalProcessorTime.TotalMilliseconds;
            }
            rows.Add(($"{name}: per refresh", $"{wall / n:0} ms wall, {cpu / n:0} ms CPU"));
            rows.Add(($"{name}: every {AgentsTerminal.RefreshSeconds} s while the terminal is open", $"{cpu / n / (AgentsTerminal.RefreshSeconds * 10.0):0.00}% of one core"));
        }
        Report("Status line (Git Bash, as Claude Code runs it)", rows);
    }

    [Fact]
    public async Task McpBridgeAndWholeAppIdle()
    {
        if (_results == null || Environment.GetEnvironmentVariable("PATCH_GOBLINS_BENCH_APP") is not { Length: > 0 } app) return;
        var rows = new List<(string, string)>();

        // The bridge Claude Code starts for the agents terminal.
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(app, "agents-mcp --repo C:\\x") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using (var mcp = Process.Start(psi)!)
        {
            mcp.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
            var reply = await mcp.StandardOutput.ReadLineAsync();
            rows.Add(("MCP bridge: start to initialize reply", $"{sw.ElapsedMilliseconds} ms"));
            Assert.Contains("Patch Goblins", reply);
            await Task.Delay(1000);
            mcp.Refresh();
            rows.Add(("MCP bridge: memory while the agents terminal is open", $"{mcp.WorkingSet64 / 1048576.0:0} MB working set, {mcp.PrivateMemorySize64 / 1048576.0:0} MB private"));
            var cpu0 = CpuMs(mcp);
            await Task.Delay(10_000);
            rows.Add(("MCP bridge: CPU while idle", $"{CpuMs(mcp) - cpu0:0} ms in 10 s"));
            mcp.StandardInput.Close();
            mcp.WaitForExit(5000);
        }

        // The whole tray, isolated (its own data folder, a fake engine), agents never used vs a repository
        // authorized with nothing to do.
        var fakes = Path.Combine(AppContext.BaseDirectory, "fakes");
        foreach (var authorized in new[] { false, true })
        {
            var data = Path.Combine(_tmp.Path, authorized ? "data-on" : "data-off");
            Directory.CreateDirectory(Path.Combine(data, "agents"));
            if (authorized)
            {
                var repo = Path.Combine(_tmp.Path, "repo-app");
                Directory.CreateDirectory(repo);
                Process.Start(new ProcessStartInfo("git", "init --quiet") { WorkingDirectory = repo, UseShellExecute = false })!.WaitForExit();
                new AgentStore(Path.Combine(data, "agents")).Save(new AgentsState { Repos = [new RepoAgents { Path = repo, Authorized = true, LastScan = DateTimeOffset.Now }] });
            }
            var start = new ProcessStartInfo(app, "--minimized") { UseShellExecute = false };
            start.Environment["PATCH_GOBLINS_DATA"] = data;
            start.Environment["PATCH_GOBLINS_ENGINE"] = Path.Combine(fakes, "cswap.cmd");
            using var tray = Process.Start(start)!;
            try
            {
                await Task.Delay(15_000); // settle: first refresh, JIT
                var c0 = CpuMs(tray);
                await Task.Delay(60_000);
                tray.Refresh();
                var label = authorized ? "repository authorized, nothing to do" : "agents never used";
                rows.Add(($"whole tray, {label}: CPU", $"{CpuMs(tray) - c0:0} ms in 60 s"));
                rows.Add(($"whole tray, {label}: memory", $"{tray.WorkingSet64 / 1048576.0:0} MB working set, {tray.PrivateMemorySize64 / 1048576.0:0} MB private, {tray.Threads.Count} threads"));
            }
            finally { tray.Kill(entireProcessTree: true); }
        }
        Report("Bridge and whole tray (built single-file exe)", rows);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp.Path, true); } catch { }
    }
}
