using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

public class JobsTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    static Account A(int n, string email = "", bool active = false, string status = "ok", double five = 10, double week = 10, bool disabled = false) =>
        new(n, email.Length > 0 ? email : $"a{n}@example.com", active, status,
            new Usage(new Window(five, Now.AddHours(2)), new Window(week, Now.AddDays(3)), null, null), Disabled: disabled);

    static RepoAgents Repo(params WorkItem[] items) => new() { Path = @"c:\work\app", Authorized = true, Items = [.. items] };

    static WorkItem Item(string email, int issue = 42, string kind = "work", bool live = true, ItemState state = ItemState.Working) => new()
    {
        Id = $"i{issue}", Issue = issue, Title = "fix login", State = state,
        Run = new Run { Kind = kind, Email = email, Org = "", Live = live },
    };

    static Dictionary<string, Job> Run(IReadOnlyList<Account> accounts, IReadOnlyList<RepoAgents>? repos = null, int[]? live = null,
        Func<Account, DateTimeOffset?>? resting = null) =>
        Jobs.For(accounts, repos ?? [], new HashSet<int>(live ?? []), 90, Now, resting);

    static Job Of(Dictionary<string, Job> jobs, Account a) => jobs[AccountPicker.Key(a)];

    [Fact]
    public void AnAgentRunWinsAndNamesItsWork()
    {
        var (a, b) = (A(1), A(2));
        var jobs = Run([a, b], [Repo(Item(b.Email, kind: "review"))], live: [2]);
        Assert.Equal(new Job(JobKind.Working, "#42 fix login · reviewing"), Of(jobs, b));
        Assert.Equal(JobKind.Idle, Of(jobs, a).Kind);
    }

    [Fact]
    public void TwoRunsOnOneAccountSayHowManyMore()
    {
        var a = A(1);
        var jobs = Run([a], [Repo(Item(a.Email, 1), Item(a.Email, 2))]);
        Assert.Equal(new Job(JobKind.Working, "#1 fix login · working +1 more"), Of(jobs, a));
    }

    [Fact]
    public void AFinishedRunIsNoJob()
    {
        var a = A(1);
        Assert.Equal(JobKind.Idle, Of(Run([a], [Repo(Item(a.Email, live: false))]), a).Kind);
    }

    [Fact]
    public void PlannerAndTriageRunsAreJobsToo()
    {
        var (a, b) = (A(1), A(2));
        var repo = Repo();
        repo.Goals.Add(new Goal { Id = "g1", Title = "settings page", Run = new Run { Kind = "plan", Email = a.Email, Live = true } });
        repo.Triage = new Goal { Id = "t", Run = new Run { Kind = "plan", Email = b.Email, Live = true } };
        var jobs = Run([a, b], [repo]);
        Assert.Equal("settings page · planning", Of(jobs, a).Text);
        Assert.Equal("ordering issues", Of(jobs, b).Text);
    }

    [Fact]
    public void YoursIsTheDefaultLoginAndAccountsWithYourOwnTerminals()
    {
        var (login, pinned, other) = (A(1, active: true), A(2), A(3));
        var jobs = Run([login, pinned, other], live: [2]);
        Assert.Equal(new Job(JobKind.Yours, "yours · signed in"), Of(jobs, login));
        Assert.Equal(new Job(JobKind.Yours, "yours · terminal open"), Of(jobs, pinned));
        Assert.Equal(new Job(JobKind.Idle, "idle"), Of(jobs, other));
    }

    [Fact]
    public void AnAccountNearItsLineRestsUntilItsReset()
    {
        var spent = A(1, five: 95, week: 20);
        var job = Of(Run([spent]), spent);
        Assert.Equal(JobKind.Resting, job.Kind);
        Assert.Equal("resting · back in 2h 00m", job.Text);
    }

    [Fact]
    public void ARestingYieldWinsOverQuotaAndPastRestsAreIgnored()
    {
        var (a, b) = (A(1), A(2));
        var jobs = Run([a, b], resting: x => x.Number == 1 ? Now.AddMinutes(30) : Now.AddMinutes(-1));
        Assert.Equal(new Job(JobKind.Resting, "resting · back in 30m"), Of(jobs, a));
        Assert.Equal(JobKind.Idle, Of(jobs, b).Kind);
    }

    [Fact]
    public void AccountsThatCannotWorkSaySo()
    {
        var jobs = Run([A(1, disabled: true), A(2, status: "relogin_required"), A(3, status: "foreign_credential"), A(4, status: "api_key")]);
        Assert.Equal(["sitting out", "needs a login", "needs a switch", "API key · agents skip it"], jobs.Values.Select(j => j.Text));
        Assert.All(jobs.Values, j => Assert.Equal(JobKind.Idle, j.Kind));
    }

    [Fact]
    public void VisitorsHaveNoJob()
    {
        var codex = A(1) with { };
        var visitor = new Account(1, "dev@example.com", true, "ok", null) { Provider = "codex" };
        Assert.Empty(Run([visitor]));
        Assert.Single(Run([visitor, codex]));
    }

    [Fact]
    public void SummaryLeadsWithWhatNeedsYouAndIsNullWhenQuiet()
    {
        var jobs = new[] { new Job(JobKind.Working, "x"), new Job(JobKind.Working, "y"), new Job(JobKind.Resting, "z"), new Job(JobKind.Idle, "i") };
        var repo = Repo(new WorkItem { Id = "q", State = ItemState.Queued }, new WorkItem { Id = "n", State = ItemState.NeedsOwner },
            new WorkItem { Id = "w", State = ItemState.Queued, Worktree = "wt" }); // started earlier: not "queued"
        Assert.Equal("1 needs you · 2 working · 1 resting · 1 queued", Jobs.Summary(jobs, [repo]));
        repo.Paused = true; // a paused repository's queue is not counting down
        Assert.Equal("1 needs you · 2 working · 1 resting", Jobs.Summary(jobs, [repo]));
        Assert.Null(Jobs.Summary([new Job(JobKind.Idle, "idle"), new Job(JobKind.Yours, "yours")], []));
    }

    [Fact]
    public void TheTerminalThemeUsesTheTraysGreen()
    {
        Assert.Equal(Brand.Dark, Palette.ForPct(0)); // the tray dot and the terminal body are one color
        using var tmp = new TempDir();
        var theme = AgentsTerminal.WriteTheme(tmp.Path, "dark");
        Assert.Contains(Brand.DarkHex, File.ReadAllText(Path.Combine(theme, "themes", "patch-goblins.json")));
        theme = AgentsTerminal.WriteTheme(tmp.Path, "light");
        Assert.Contains(Brand.LightHex, File.ReadAllText(Path.Combine(theme, "themes", "patch-goblins.json")));
    }
}
