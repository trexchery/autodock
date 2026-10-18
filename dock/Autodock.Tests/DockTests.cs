using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Autodock;
using Xunit;

namespace Autodock.Tests;

// All credentials in this file are synthetic.

public class ModelTests
{
    const string ListJson = """
    {
      "schemaVersion": 1,
      "activeAccountNumber": 2,
      "accounts": [
        { "number": 1, "email": "a@example.com", "organizationName": "", "active": false,
          "usageStatus": "token_expired", "usage": null, "disabled": true,
          "lastGoodUsage": { "fiveHour": { "pct": 12.0, "resetsAt": "2026-06-22T23:29:59Z" } },
          "loginExpiresAt": "2026-07-01T00:00:00Z" },
        { "number": 2, "email": "b@example.com", "alias": "work", "active": true, "usageStatus": "ok",
          "usage": {
            "fiveHour": { "pct": 25.0, "resetsAt": "2026-06-22T23:29:59Z", "countdown": "2h", "clock": "23:29" },
            "sevenDay": { "pct": 91.5, "resetsAt": "2026-06-26T17:59:59Z", "expectedPct": 40.0, "aheadOfPace": true },
            "scoped": [ { "name": "Fable", "pct": 99.0, "resetsAt": "2026-06-26T17:59:59Z" } ],
            "spend": { "used": 1.5, "limit": 10, "pct": 15, "currency": "USD" }
          },
          "usageAgeSeconds": 42.5, "someFutureField": { "x": 1 } },
        { "number": 3, "email": "c@example.com", "active": false, "usageStatus": "unavailable",
          "usage": null, "usageError": "http-429" }
      ],
      "unclaimedCredentials": []
    }
    """;

    [Fact]
    public void ParsesListPayloadIncludingAdditiveFields()
    {
        var s = Json.Parse<Snapshot>(ListJson);
        Assert.Equal(2, s.ActiveAccountNumber);
        Assert.Equal(3, s.Accounts.Count);

        var b = s.Accounts[1];
        Assert.True(b.Active);
        Assert.Equal("work  ·  b@example.com", b.DisplayName);
        Assert.Equal(91.5, b.Usage!.SevenDay!.Pct);
        Assert.True(b.Usage.SevenDay.AheadOfPace);
        Assert.Equal("Fable", b.Usage.Scoped![0].Name);
        Assert.Equal(15, b.Usage.Spend!.Pct);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 23, 29, 59, TimeSpan.Zero), b.Usage.FiveHour!.ResetsAt);
        Assert.Null(b.StatusNote);
        Assert.Equal(99.0, Format.Peak(b));

        var a = s.Accounts[0];
        Assert.True(a.Disabled);
        Assert.Null(a.Usage);
        Assert.Equal(12.0, a.ShownUsage!.FiveHour!.Pct); // last-known shown when live is untrusted
        Assert.Contains("token expired", a.StatusNote);

        Assert.Equal("usage unavailable (http-429)", s.Accounts[2].StatusNote);
    }

    [Fact]
    public void DetectsEngineErrorPayload()
    {
        var err = Json.TryError("""{"schemaVersion":1,"error":{"type":"AccountNotFoundError","message":"no such account"}}""");
        Assert.Equal("no such account", err!.Message);
        Assert.Null(Json.TryError(ListJson));
        Assert.Null(Json.TryError("not json"));
    }

    [Fact]
    public void ParsesAutoEventStreamAndSkipsNoise()
    {
        var stdout = """
            {"schemaVersion":1,"event":"poll","ts":"2026-06-22T10:00:00Z","active":{"number":1,"email":"a@example.com"},"headroomPct":{"1":5.0},"threshold":90}
            not json at all
            {"schemaVersion":1,"event":"switch","ts":"2026-06-22T10:00:01Z","trigger":"proactive","from":{"number":1,"email":"a@example.com"},"to":{"number":2,"email":"b@example.com"},"warnings":[],"dryRun":false}
            {"schemaVersion":1,"event":"account-quarantined","number":"3","email":"c@example.com","reason":"invalid_grant"}
            {"schemaVersion":1,"event":"all-exhausted","earliestResetAt":"2026-06-22T12:00:00Z"}
            """;
        var events = Engine.ParseEvents(stdout);
        Assert.Equal(["poll", "switch", "account-quarantined", "all-exhausted"], events.Select(e => e.Event));
        Assert.Equal(2, events[1].To!.Number);
        Assert.Equal("proactive", events[1].Trigger);
        Assert.Equal("c@example.com", events[2].Email);
        Assert.NotNull(events[3].EarliestResetAt);
    }
}

public class ProviderTests
{
    // Shape of `cswap codex list --json` / `cswap grok list --json` (synthetic).
    const string ProviderJson = """
    {
      "schemaVersion": 1, "activeAccountNumber": 1,
      "accounts": [
        { "number": 1, "email": "dev@example.com", "plan": "plus", "active": true, "usageStatus": "ok",
          "usage": { "fiveHour": { "pct": 40, "resetsAt": "2026-06-22T23:00:00Z" },
                     "sevenDay": { "pct": 10, "resetsAt": "2026-06-26T00:00:00Z" } }, "usageAgeSeconds": 3 },
        { "number": 2, "email": "x@example.com", "active": false, "usageStatus": "token_expired", "usage": null,
          "usageNote": "login expired; codex renews it after a switch, else re-enroll" },
        { "number": 3, "email": "g@example.com", "active": false, "usageStatus": "ok",
          "usage": { "scoped": [ { "name": "Monthly limit", "pct": 63.5, "resetsAt": "2026-07-01T00:00:00Z" } ] } }
      ]
    }
    """;

    [Fact]
    public void ParsesProviderSnapshotAndPrefersEngineNote()
    {
        var s = Json.Parse<Snapshot>(ProviderJson);
        Assert.Equal(3, s.Accounts.Count);
        Assert.Equal("claude", s.Accounts[0].Provider); // tagged by Engine.ListAsync, never by the payload
        Assert.Equal(40, s.Accounts[0].Usage!.FiveHour!.Pct);
        Assert.Equal("login expired; codex renews it after a switch, else re-enroll", s.Accounts[1].StatusNote);
        Assert.Equal(63.5, Format.Peak(s.Accounts[2]));
        Assert.Equal("Now using", Json.Parse<SwitchResult>("""{"switched":true,"note":"Now using"}""").Note);
    }

    [Fact]
    public void RoutesProviderArguments()
    {
        Assert.Equal(["list", "--json"], Engine.Args("claude", "list", "--json"));
        Assert.Equal(["codex", "switch", "2", "--json"], Engine.Args("codex", "switch", "2", "--json"));
    }
}

public class RedactionTests
{
    [Theory]
    [InlineData("token sk-ant-oat01-AAAAAAAAAAAAAAAAAAAA leaked", "token [redacted] leaked")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", "Authorization: [redacted]")]
    [InlineData("{\"refreshToken\": \"sk-ant-ort01-zzz\"}", "{[redacted]}")]
    [InlineData("{\"access_token\":\"xyz\"}", "{[redacted]}")]
    [InlineData("Switched to #2 b@example.com", "Switched to #2 b@example.com")]
    public void RedactsSecrets(string input, string expected) => Assert.Equal(expected, Engine.Redact(input));
}

public class FormatTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FormatsResetTimes()
    {
        Assert.Equal("now", Format.When(Now.AddMinutes(-1), Now));
        Assert.Equal("in 1m", Format.When(Now.AddSeconds(20), Now));
        Assert.Equal("in 45m", Format.When(Now.AddMinutes(45), Now));
        Assert.Equal("in 2h 05m", Format.When(Now.AddMinutes(125), Now));
        Assert.Equal("7m ago", Format.Age(420));
        Assert.Equal("2h ago", Format.Age(7300));
        Assert.Equal(Now.AddDays(3).ToLocalTime().ToString("ddd HH:mm"), Format.When(Now.AddDays(3), Now));
    }

    [Fact]
    public void TooltipStaysUnderWindowsLimit()
    {
        var acct = new Account(1, new string('x', 200) + "@example.com", true, "ok",
            new Usage(new Window(10, null), new Window(20, null), null, null));
        var tip = Format.Tooltip(new Snapshot(1, 1, [acct]), auto: true);
        Assert.True(tip.Length <= 127);
        Assert.Equal("autodock — no managed account active", Format.Tooltip(null, false));
    }

    [Fact]
    public void QuotesArgumentsForConsole()
    {
        Assert.Equal("run", Engine.QuoteArg("run"));
        Assert.Equal("\"C:\\Program Files\\x\\cswap.exe\"", Engine.QuoteArg(@"C:\Program Files\x\cswap.exe"));
        Assert.Equal("\"a&b\"", Engine.QuoteArg("a&b"));
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dock-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class StorageTests
{
    [Fact]
    public void SettingsRoundTripAtomicallyAndClampRefresh()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "sub", "settings.json");
        new DockSettings { AutoSwitch = true, RefreshSeconds = 5 }.Save(file);
        var loaded = DockSettings.Load(file);
        Assert.True(loaded.AutoSwitch);
        Assert.Equal(DockSettings.MinRefreshSeconds, loaded.RefreshSeconds);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!)); // no temp files left behind
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(file, "{ not json");
        Assert.False(DockSettings.Load(file).AutoSwitch);
    }

    [Fact]
    public void HardenRestrictsDirectoryAndChildrenToCurrentUser()
    {
        using var t = new TempDir();
        var store = Path.Combine(t.Path, "store");
        Directory.CreateDirectory(Path.Combine(store, "credentials"));
        File.WriteAllText(Path.Combine(store, "credentials", "x.enc"), "dpapi1:synthetic");

        StoreSecurity.Harden(store);

        Assert.True(StoreSecurity.IsHardened(store));
        var me = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var fileRules = new FileInfo(Path.Combine(store, "credentials", "x.enc")).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(fileRules);
        Assert.All(fileRules, r => Assert.True(r.IdentityReference.Equals(me) || r.IdentityReference.Equals(system)));
        // A file created afterwards inherits the same restriction.
        var later = Path.Combine(store, "later.json");
        File.WriteAllText(later, "{}");
        Assert.All(new FileInfo(later).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => Assert.True(r.IdentityReference.Equals(me) || r.IdentityReference.Equals(system)));
    }
}

/// Drives the real engine end to end against a synthetic home directory.
/// Set AUTODOCK_TEST_ENGINE to a cswap.exe (build.ps1 does); skipped otherwise.
[Collection("engine")]
public class EngineIntegrationTests
{
    [Fact]
    public async Task EnrollListSealRemove()
    {
        var exe = Environment.GetEnvironmentVariable("AUTODOCK_TEST_ENGINE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return; // no engine build available

        using var home = new TempDir();
        var saved = new Dictionary<string, string?>();
        void Set(string k, string? v) { saved.TryAdd(k, Environment.GetEnvironmentVariable(k)); Environment.SetEnvironmentVariable(k, v); }
        Set("USERPROFILE", home.Path);
        Set("HOME", home.Path);
        Set("CLAUDE_CONFIG_DIR", null);
        // Usage fetches must not leave the machine: route them into a dead proxy.
        Set("HTTPS_PROXY", "http://127.0.0.1:9");
        Set("HTTP_PROXY", "http://127.0.0.1:9");
        try
        {
            var login = Path.Combine(home.Path, "login");
            Directory.CreateDirectory(login);
            File.WriteAllText(Path.Combine(login, ".credentials.json"), JsonSerializer.Serialize(new
            {
                claudeAiOauth = new { accessToken = "sk-ant-oat01-SYNTHETIC", refreshToken = "sk-ant-ort01-SYNTHETIC", expiresAt = 4102444800000, scopes = new[] { "user:inference" } },
            }));
            File.WriteAllText(Path.Combine(login, ".claude.json"), JsonSerializer.Serialize(new
            {
                oauthAccount = new { emailAddress = "synthetic@example.com", accountUuid = "acc-1", organizationUuid = "org-1", organizationName = "" },
            }));

            var engine = new Engine(exe);
            var enrolled = await engine.CommandAsync("enroll", login);
            Assert.Contains("synthetic@example.com", enrolled);
            Assert.DoesNotContain("SYNTHETIC", enrolled);

            var snap = await engine.ListAsync();
            var acct = Assert.Single(snap.Accounts);
            Assert.Equal("synthetic@example.com", acct.Email);
            Assert.False(acct.Active); // enrollment never touches the default login

            var raw = await engine.RunAsync(["list", "--json"]);
            Assert.DoesNotContain("SYNTHETIC", raw.Stdout + raw.Stderr);

            var enc = Directory.GetFiles(Path.Combine(home.Path, ".claude-swap-backup", "credentials"), "*.enc");
            Assert.NotEmpty(enc);
            Assert.All(enc, f =>
            {
                var text = File.ReadAllText(f);
                Assert.StartsWith("dpapi1:", text);
                Assert.DoesNotContain("SYNTHETIC", System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(text["dpapi1:".Length..])));
            });

            await engine.CommandWithInputAsync("y\n", "remove", "1");
            Assert.Empty((await engine.ListAsync()).Accounts);
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }
    }
}

public class RenderTests
{
    static List<string> SectionLabels(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => x is Label { Tag: "section" } l ? [l.Text] : SectionLabels(x)).ToList();

    static IEnumerable<AccountRow> FindRows(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => x is AccountRow r ? [r] : FindRows(x));

    /// Smoke test: the dashboard lays out a realistic snapshot without throwing.
    /// Set DOCK_RENDER_OUT=<file.png> to also save what it drew.
    [Fact]
    public void DashboardRendersAccounts()
    {
        var now = DateTimeOffset.UtcNow;
        Window W(double p, double hours, string? name = null) => new(p, now.AddHours(hours), name);
        var snap = new Snapshot(1, 2,
        [
            new Account(1, "personal@example.com", false, "ok", new Usage(W(34, 2.2), W(58, 50), null, null)),
            new Account(2, "work@example.com", true, "ok", new Usage(W(92, 1.1), W(71, 30), [W(88, 70, "Opus")], null), Alias: "work"),
            new Account(3, "side@example.com", false, "token_expired", null,
                LastGoodUsage: new Usage(W(5, 4), W(12, 90), null, null), Disabled: true),
        ]);
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new DashboardForm();
                form.Render(snap, null);
                form.SetStatus("12:00");
                form.Location = new Point(-10000, -10000); // off-screen: never steals the user's desktop
                form.Show();
                Application.DoEvents();
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                Assert.InRange(form.Height, Ui.S(200), Ui.S(320)); // three slim rows, not cards
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_OUT") is { Length: > 0 } outPath) bmp.Save(outPath);
                Assert.Empty(SectionLabels(form)); // one provider: no section labels

                var codex = new Snapshot(1, 1, [new Account(1, "dev@example.com", true, "ok", new Usage(W(40, 3), W(10, 90), null, null)) { Provider = "codex" }]);
                form.Render(snap, null, [("codex", codex, null), ("grok", null, "offline")]);
                Application.DoEvents();
                Assert.Equal(["CLAUDE", "CODEX", "GROK"], SectionLabels(form));
                Assert.InRange(form.Height, Ui.S(260), Ui.S(460));
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_PROVIDERS_OUT") is { Length: > 0 } pOut)
                {
                    using var pb = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(pb, new Rectangle(0, 0, form.Width, form.Height));
                    pb.Save(pOut);
                }
                form.Render(snap, null);
                Application.DoEvents();

                // Expanded row (accordion) must also lay out.
                var row = FindRows(form).First();
                row.SetExpanded(true, animate: false);
                Application.DoEvents();
                Assert.True(row.Height > Ui.S(100));
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_EXPANDED_OUT") is { Length: > 0 } exOut)
                {
                    using var eb = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(eb, new Rectangle(0, 0, form.Width, form.Height));
                    eb.Save(exOut);
                }

                using var settings = new SettingsForm { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) };
                settings.Show();
                Application.DoEvents();
                Assert.True(settings.Width > 300 && settings.Height > 200);
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_SETTINGS_OUT") is { Length: > 0 } sOut)
                {
                    using var sb = new Bitmap(settings.Width, settings.Height);
                    settings.DrawToBitmap(sb, new Rectangle(0, 0, settings.Width, settings.Height));
                    sb.Save(sOut);
                }
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw failure;
    }
}
