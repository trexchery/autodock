namespace PatchGoblins;

public enum JobKind { Idle, Yours, Working, Resting }

/// What one Claude account is doing for the team: the line under its name in the flyout.
public readonly record struct Job(JobKind Kind, string Text);

/// The team roster's job lines and the header's one-line status, from the accounts as listed and the agents' own state.
public static class Jobs
{
    /// A job per Claude account, keyed by AccountPicker.Key. An agent run on it wins; then the owner's own use of it;
    /// then rest (near its line, or just yielded: `restingUntil`); else idle.
    public static Dictionary<string, Job> For(IReadOnlyList<Account> accounts, IReadOnlyList<RepoAgents> repos, IReadOnlySet<int> live,
        double threshold, DateTimeOffset now, Func<Account, DateTimeOffset?>? restingUntil = null)
    {
        var runs = new Dictionary<string, List<string>>();
        void Add(Run? r, string what)
        {
            if (r is not { Live: true, Email.Length: > 0 }) return;
            var key = r.Email + "|" + r.Org;
            if (!runs.TryGetValue(key, out var list)) runs[key] = list = [];
            list.Add(what);
        }
        foreach (var repo in repos)
        {
            foreach (var i in repo.Items) Add(i.Run, $"{i.Label} · {Verb(i.Run)}");
            foreach (var g in repo.Goals) Add(g.Run, $"{g.Title} · planning");
            Add(repo.Triage?.Run, "ordering issues");
        }

        var jobs = new Dictionary<string, Job>();
        foreach (var a in accounts.Where(a => a.Provider == "claude"))
        {
            var key = AccountPicker.Key(a);
            jobs[key] =
                runs.TryGetValue(key, out var mine) ? new(JobKind.Working, mine.Count == 1 ? mine[0] : $"{mine[0]} +{mine.Count - 1} more")
                : a.Active ? new(JobKind.Yours, "yours · signed in")
                : a.Disabled ? new(JobKind.Idle, "sitting out")
                : a.UsageStatus switch
                {
                    "relogin_required" or "no_credentials" => new(JobKind.Idle, "needs a login"),
                    "foreign_credential" => new(JobKind.Idle, "needs a switch"),
                    "api_key" => new(JobKind.Idle, "API key · agents skip it"),
                    _ when live.Contains(a.Number) => new(JobKind.Yours, "yours · terminal open"),
                    _ => Rest(a, restingUntil?.Invoke(a), threshold, now) ?? new(JobKind.Idle, "idle"),
                };
        }
        return jobs;
    }

    static string Verb(Run? r) => r?.Kind switch { "review" => "reviewing", "plan" => "planning", _ => "working" };

    static Job? Rest(Account a, DateTimeOffset? until, double threshold, DateTimeOffset now)
    {
        if (until is not { } t || t <= now)
        {
            if (AccountPicker.Peak(a, now) is not { } p || p < threshold) return null;
            t = AccountPicker.NextReset(a, null, now, threshold) ?? default;
        }
        return new(JobKind.Resting, t > now ? $"resting · back {Format.When(t, now)}" : "resting · near its limit");
    }

    /// "2 working · 1 resting · 3 queued", leading with what needs the owner; null when the agents have nothing to report.
    public static string? Summary(IEnumerable<Job> team, IReadOnlyList<RepoAgents> repos)
    {
        var jobs = team.ToList();
        var parts = new List<string>();
        var needs = repos.Where(r => r.Authorized).Sum(r => r.Items.Count(i => i.State == ItemState.NeedsOwner) + r.Goals.Count(g => g.State == GoalState.NeedsOwner));
        if (needs > 0) parts.Add($"{needs} need{(needs == 1 ? "s" : "")} you");
        if (jobs.Count(j => j.Kind == JobKind.Working) is > 0 and var working) parts.Add($"{working} working");
        if (jobs.Count(j => j.Kind == JobKind.Resting) is > 0 and var resting) parts.Add($"{resting} resting");
        if (repos.Where(r => r.Running).Sum(r => r.Items.Count(i => i.State == ItemState.Queued && !i.InFlight)) is > 0 and var queued) parts.Add($"{queued} queued");
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }
}
