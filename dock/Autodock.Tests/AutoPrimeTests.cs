using System.Text.Json.Nodes;
using Autodock;
using Xunit;

namespace Autodock.Tests;

// All accounts and credentials here are synthetic.
public class AutoPrimeTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    static Account A(int n, double five = 0, DateTimeOffset? resets = null, double weekly = 10, string status = "ok", bool disabled = false,
        bool active = false, Spend? spend = null, DateTimeOffset? fetched = null, List<Window>? scoped = null, string provider = "claude") =>
        new Account(n, $"a{n}@example.com", active, status,
            status == "ok" ? new Usage(new Window(five, resets), new Window(weekly, null), scoped, spend) : null,
            LastGoodUsage: status == "ok" ? null : new Usage(new Window(0, null), new Window(1, null), null, null),
            Disabled: disabled, OrganizationUuid: "org")
        { UsageFetchedAt = fetched ?? Now - TimeSpan.FromMinutes(1), Provider = provider };

    [Fact]
    public void ReadyMeansAFreshUnstartedWindowThatCannotCostAnything()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        var accounts = new[]
        {
            A(1),
            A(2, five: 3), // in use
            A(3, resets: Now.AddHours(2)), // started
            A(4, disabled: true), // excluded from auto-switch
            A(5, status: "api_key"), // no subscription: would bill
            A(6, status: "token_expired"), // only a last-known reading
            A(7, weekly: 96), // near the weekly limit: past it, extra usage could bill
            A(8, scoped: [new Window(100, null, "Opus")]),
            A(9, spend: new Spend(4, 50, 8, "USD", null)), // paid extra usage on
            A(10, fetched: Now.AddMinutes(-40)), // someone may have started it since
            A(11, provider: "codex"),
        };
        Assert.Equal([1], p.Ready(accounts, Now).Select(a => a.Number));
    }

    [Fact]
    public void EachWindowIsPrimedOnceAcrossRestartsAndTheNextOneAgain()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "prime.json");
        var p = new Primer(path);
        Assert.True(p.Observe([A(1, five: 40, resets: Now.AddMinutes(-2), fetched: Now.AddMinutes(-10))], Now)); // learns the window that just ended
        var fresh = A(1, fetched: Now);
        Assert.Single(p.Ready([fresh], Now));
        p.Begin(fresh, Now); // written before the request: a crash right after never sends twice
        Assert.Empty(new Primer(path).Ready([fresh], Now.AddMinutes(1)));
        p.Finish(fresh, ok: true, "sent");
        Assert.Empty(p.Ready([fresh], Now.AddMinutes(2)));

        // Nobody saw the primed window (the laptop slept through it): once it's surely over, fresh again.
        Assert.Single(new Primer(path).Ready([A(1, fetched: Now.AddHours(5).AddMinutes(1))], Now.AddHours(5).AddMinutes(2)));

        // The new window shows up, ends, and the one after it is fresh again.
        p.Observe([A(1, five: 1, resets: Now.AddHours(5), fetched: Now.AddMinutes(4))], Now.AddMinutes(4));
        Assert.Empty(p.Ready([A(1, fetched: Now.AddHours(4))], Now.AddHours(4).AddMinutes(1))); // read before that window ended
        Assert.Single(p.Ready([A(1, fetched: Now.AddHours(5).AddMinutes(2))], Now.AddHours(5).AddMinutes(3)));
    }

    [Fact]
    public void SuccessNeedsEvidenceThatTheWindowStarted()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        var (one, two, three) = (A(1), A(2), A(3));
        foreach (var a in new[] { one, two, three })
        {
            p.Begin(a, Now);
            p.Finish(a, ok: true, "sent");
        }
        Assert.Equal("sent", p.Records["a1@example.com|org"].Outcome);

        // A reading from before the request proves nothing.
        p.Observe([A(1, five: 2, resets: Now.AddHours(5), fetched: Now.AddMinutes(-1))], Now.AddMinutes(1));
        Assert.Equal("sent", p.Records["a1@example.com|org"].Outcome);
        Assert.False(p.WantsReread(Now.AddMinutes(1)));
        Assert.True(p.WantsReread(Now.AddMinutes(4))); // once the engine's cached reading can show it
        Assert.False(p.WantsReread(Now.AddMinutes(5)));

        p.Observe([
            A(1, five: 2, resets: Now.AddHours(5), fetched: Now.AddMinutes(4)), // rounded to the hour or not: started by the prime
            A(2, five: 30, resets: Now.AddHours(1), fetched: Now.AddMinutes(4)), // someone was already using it
        ], Now.AddMinutes(4));
        Assert.Equal("verified", p.Records["a1@example.com|org"].Outcome);
        Assert.Equal("in use", p.Records["a2@example.com|org"].Outcome);
        p.Observe([], Now.AddMinutes(31)); // never showed the new window
        Assert.Equal("unverified", p.Records["a3@example.com|org"].Outcome);
        Assert.Contains("#3 unverified: no reading showed the new window", p.Status([one, two, three], Now.AddMinutes(31)));
        Assert.Contains(p.Status([one, two, three], Now.AddMinutes(31)), l => l.StartsWith("Last primed"));
    }

    [Fact]
    public void AFailedPrimeIsReportedAndCanBeTriedAgain()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        var a = A(4);
        p.Begin(a, Now);
        p.Finish(a, ok: false, "login expired: run cswap login 4");
        Assert.Contains("#4 failed: login expired: run cswap login 4", p.Status([a], Now));
        Assert.Empty(p.Ready([a], Now.AddMinutes(1))); // the reading from before the attempt may be stale now
        Assert.Single(p.Ready([A(4, fetched: Now.AddMinutes(2))], Now.AddMinutes(3)));
        Assert.Null(p.LastPrime); // nothing went out
    }

    [Fact]
    public void APrimeIsOneQuietTurnOnTheSubscriptionLoginOnly()
    {
        string[] tail = ["-p", "Hi", "--model", "haiku", "--effort", "low", "--output-format", "json",
            "--no-session-persistence", "--safe-mode", "--strict-mcp-config", "--disable-slash-commands", "--setting-sources", "project", "--tools", ""];
        // The signed-in account: its own login, no copy and no switch. Any other: its sealed profile only.
        Assert.Equal(["run", "1", "--", .. tail], Engine.PrimeArgs(A(1, active: true)));
        Assert.Equal(["run", "2", "--require-session", "--", .. tail], Engine.PrimeArgs(A(2)));
        Assert.DoesNotContain("--bare", Engine.PrimeArgs(A(2))); // API-key auth only: would bill
        Assert.DoesNotContain("--no-share", Engine.PrimeArgs(A(2)));

        var unset = Engine.PrimeUnset(["ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK",
            "CLAUDE_CONFIG_DIR", "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_GIT_BASH_PATH", "PATH"]);
        Assert.Equal(["ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_OAUTH_TOKEN",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CONFIG_DIR"], unset.Order(StringComparer.Ordinal)); // Git Bash's location and PATH stay
    }

    [Fact]
    public void TheUsageReadingsTimeIsRead()
    {
        var snap = Json.Parse<Snapshot>("""{"schemaVersion":1,"accounts":[{"number":1,"email":"a@example.com","active":true,"usageStatus":"ok","usageFetchedAt":"2026-10-18T15:00:00+00:00"}]}""");
        Assert.Equal(Now, snap.Accounts[0].UsageFetchedAt);
    }
}

[Collection("agents end to end")] // changes process environment variables
public sealed class AutoPrimeEngineTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly Dictionary<string, string?> _was = [];

    void Set(string name, string? value)
    {
        if (!_was.ContainsKey(name)) _was[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public async Task PrimeSendsOnceSilentlyWithoutTheTraysCredentialsOrSession()
    {
        var log = Path.Combine(_tmp.Path, "cswap.log");
        Set("FAKE_LOG", log);
        Set("AUTODOCK_DATA", _tmp.Path);
        Set("ANTHROPIC_API_KEY", "sk-ant-api03-SYNTHETIC"); // must never reach the request
        Set("CLAUDECODE", "1"); // (CLAUDE_CONFIG_DIR: covered by PrimeUnset's test; the engine tests own that variable)
        var engine = new Engine(Path.Combine(AppContext.BaseDirectory, "fakes", "cswap.cmd"));
        var account = new Account(2, "b@example.com", false, "ok", null);

        Assert.Equal((true, "sent"), await engine.PrimeAsync(account));
        Assert.Contains("run 2 --require-session -- -p Hi --model haiku", File.ReadAllText(log));
        var seen = File.ReadAllText(log + ".prime").Trim().Split('|');
        Assert.Equal(["False", "False", "False"], seen[..3]); // no API key, config folder or parent session reached it
        Assert.Equal(Path.Combine(_tmp.Path, "prime"), seen[3].TrimEnd('\\')); // an empty folder, not the user's home

        Set("FAKE_PRIME_FAIL", "Account-2 is the active default login");
        var (ok, detail) = await engine.PrimeAsync(account);
        Assert.False(ok);
        Assert.Contains("active default login", detail);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _was) Environment.SetEnvironmentVariable(k, v);
        _tmp.Dispose();
    }
}

public class TerminalEnvironmentTests
{
    [Fact]
    public void ANewTerminalNeverInheritsTheSessionAutodockWasStartedFrom()
    {
        static Dictionary<string, string?> Env(params (string, string)[] vars) => vars.ToDictionary(v => v.Item1, v => (string?)v.Item2, StringComparer.OrdinalIgnoreCase);
        // What the owner's Windows gives a new program: their own settings, Claude Code's among them.
        IReadOnlyDictionary<string, string> windows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows", ["USERPROFILE"] = @"C:\Users\me", ["CLAUDE_EFFORT"] = "high", ["CLAUDE_CODE_GIT_BASH_PATH"] = @"C:\Git\bin\bash.exe",
        };
        // autodock started from inside a session: everything that session set up for its tools goes.
        var fromSession = Env(("CLAUDECODE", "1"), ("CLAUDE_CODE_CHILD_SESSION", "1"), ("CLAUDE_CODE_MESSAGING_TOKEN", "x"), ("CLAUDE_PID", "1"), ("AUTODOCK_DATA", @"C:\dev"),
            ("AI_AGENT", "claude-code_agent"), ("NO_COLOR", "1"), ("GIT_EDITOR", "true"), ("GIT_TERMINAL_PROMPT", "0"), ("PROMPT", "$P$G"),
            ("PATH", @"C:\Windows;C:\session-only"), ("USERPROFILE", @"C:\Users\me"));
        Engine.TopLevel(fromSession, () => windows);
        Assert.Equal(windows.Append(KeyValuePair.Create("AUTODOCK_DATA", @"C:\dev")).OrderBy(v => v.Key), // autodock's own instance stays
            fromSession.OrderBy(v => v.Key).Select(v => KeyValuePair.Create(v.Key, v.Value!)));

        // Started normally: the environment is the owner's own; only stray session markers go.
        var own = Env(("NO_COLOR", "1"), ("AI_AGENT", "x"), ("CLAUDE_CODE_CHILD_SESSION", "1"), ("CLAUDE_EFFORT", "high"),
            ("CLAUDE_CODE_SESSION_LOG", "1"), ("PATH", @"C:\x"));
        Engine.TopLevel(own, () => throw new InvalidOperationException("not needed"));
        Assert.Equal(["AI_AGENT", "CLAUDE_CODE_SESSION_LOG", "CLAUDE_EFFORT", "NO_COLOR", "PATH"], own.Keys.Order(StringComparer.Ordinal)); // the owner's diagnostics stay
    }

    [Fact]
    public void WindowsGivesAFreshEnvironmentWithoutAnySession()
    {
        var fresh = Engine.FreshEnvironment();
        Assert.NotNull(fresh);
        Assert.True(fresh!.ContainsKey("PATH") && fresh.ContainsKey("USERPROFILE"));
        Assert.False(fresh.ContainsKey("CLAUDECODE")); // even though these tests may run inside one
    }

    [Fact]
    public void TheAgentsTerminalThemeOnlyMakesTheMascotWhite()
    {
        using var tmp = new TempDir();
        static JsonNode Theme(string plugin) => JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "themes", "autodock.json")))!;
        var plugin = AgentsTerminal.WriteTheme(tmp.Path, "dark-daltonized")!;
        Assert.Equal("autodock-agents", JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json")))!["name"]!.GetValue<string>());
        var theme = Theme(plugin);
        Assert.Equal("dark-daltonized", theme["base"]!.GetValue<string>()); // the owner's own theme underneath
        Assert.Equal("""{"clawd_body":"#FFFFFF"}""", theme["overrides"]!.ToJsonString()); // the mascot and nothing else
        // Light, automatic, custom or no theme: the owner's look is left alone (a white mascot wouldn't read).
        foreach (var other in new[] { "light", "light-daltonized", "auto", "custom:mine", null })
            Assert.Null(AgentsTerminal.WriteTheme(Path.Combine(tmp.Path, other ?? "none"), other));
    }
}
