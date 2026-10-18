using System.Text.Json;

namespace Autodock;

// Auto-Prime: start an account's fresh 5-hour window early with one tiny request, so it also resets
// early. Human-initiated by design: Anthropic's terms don't clearly allow unattended sends, so autodock
// only tracks the windows, and one click in the tray menu primes every fresh one, invisibly.

/// What autodock keeps per account: diagnostics only, never the prompt or the reply.
public sealed class PrimeRecord
{
    /// The latest 5-hour reset seen in a live reading; the window after it is identified by it.
    public DateTimeOffset? LastResetsAt { get; set; }
    /// The window last primed, recorded before the request goes out: a crash never sends twice.
    public string? Window { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    /// sent, verified, in use (something else started the window first), unverified, failed.
    public string? Outcome { get; set; }
    public string? Detail { get; set; }
    /// The one forced re-read spent on verifying this prime.
    public bool Reread { get; set; }
}

public sealed class Primer(string path)
{
    public static string DefaultPath => Path.Combine(DockSettings.Dir, "prime.json");

    /// A reading older than this may miss a window someone started meanwhile.
    static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(15);
    static readonly TimeSpan VerifyFor = TimeSpan.FromMinutes(30);
    /// The engine serves a cached reading for three minutes after it fetched one.
    static readonly TimeSpan RereadAfter = TimeSpan.FromSeconds(210);

    /// Keyed by email|organization: account numbers are reused after a remove.
    public Dictionary<string, PrimeRecord> Records { get; } = Load(path);

    public DateTimeOffset? LastPrime => Records.Values.Where(r => r.Outcome is "sent" or "verified" or "unverified").Max(r => r.SentAt);

    static Dictionary<string, PrimeRecord> Load(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, PrimeRecord>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public void Save()
    {
        try { AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Records, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Program.Log(e); }
    }

    static string Key(Account a) => a.Email + "|" + (a.OrganizationUuid ?? "");

    PrimeRecord Of(Account a) => Records.TryGetValue(Key(a), out var r) ? r : Records[Key(a)] = new();

    static string WindowId(PrimeRecord r) => r.LastResetsAt?.ToUniversalTime().ToString("O") ?? "idle";

    /// When a live reading was taken; last-known readings never count.
    static DateTimeOffset? FetchedAt(Account a, DateTimeOffset now) =>
        a is { Provider: "claude", UsageStatus: "ok", Usage: not null }
            ? a.UsageFetchedAt ?? (a.UsageAgeSeconds is { } s ? now - TimeSpan.FromSeconds(s) : null)
            : null;

    /// Never an excluded or API-key account, one with paid extra usage, or one near a weekly limit:
    /// a prime must not cost anything. The margin also covers unlimited extra usage, which the usage
    /// reading doesn't report: it only bills once a plan limit is reached.
    static bool Eligible(Account a) =>
        a is { Provider: "claude", Disabled: false, UsageStatus: "ok", Usage: { Spend: null } u }
        && (u.SevenDay?.Pct ?? 0) < 95 && (u.Scoped ?? []).All(w => w.Pct < 95);

    /// Accounts whose fresh 5-hour window hasn't started, read recently and after the previous window
    /// ended, and not primed for this window yet (a failed prime may be tried again).
    public List<Account> Ready(IEnumerable<Account> accounts, DateTimeOffset now) =>
        accounts.Where(a => Eligible(a) && a.Usage!.FiveHour is { Pct: <= 0, ResetsAt: null }
                            && FetchedAt(a, now) is { } at && now - at <= MaxReadingAge && Fresh(a, at)).ToList();

    bool Fresh(Account a, DateTimeOffset fetched)
    {
        if (!Records.TryGetValue(Key(a), out var r)) return true;
        if (r.LastResetsAt is { } last && fetched < last) return false; // read before the last window ended
        if (r.Window != WindowId(r)) return true;
        // Primed for this window: again only once that window is surely over (nobody may have seen it
        // start), or after a failure, on a reading taken since (the failed request may have gone out).
        return r.SentAt is { } sent && (fetched >= sent + TimeSpan.FromHours(5) || r.Outcome == "failed" && fetched > sent);
    }

    /// Learns each live reading's window and settles sent primes on evidence. True when anything changed.
    public bool Observe(IEnumerable<Account> accounts, DateTimeOffset now)
    {
        var changed = false;
        foreach (var a in accounts)
        {
            if (FetchedAt(a, now) is not { } at || a.Usage!.FiveHour?.ResetsAt is not { } resets) continue;
            var r = Of(a);
            if (r is { Outcome: "sent", SentAt: { } sent } && at > sent)
            {
                // Evidence: the window now ends about five hours after the request (resets may be rounded to the hour).
                var started = resets >= sent + TimeSpan.FromHours(4) && resets <= sent + TimeSpan.FromHours(5) + TimeSpan.FromMinutes(10);
                (r.Outcome, r.Detail) = started ? ("verified", $"window resets {resets.ToLocalTime():HH:mm}") : ("in use", "the window had already started");
                changed = true;
            }
            if (r.LastResetsAt != resets)
            {
                r.LastResetsAt = resets;
                changed = true;
            }
        }
        foreach (var r in Records.Values.Where(r => r is { Outcome: "sent", SentAt: { } s } && now - s > VerifyFor))
        {
            (r.Outcome, r.Detail) = ("unverified", "no reading showed the new window");
            changed = true;
        }
        return changed;
    }

    /// One forced re-read per prime, once the engine's cached reading can show it.
    public bool WantsReread(DateTimeOffset now)
    {
        var due = Records.Values.Where(r => r is { Outcome: "sent", Reread: false, SentAt: { } s } && now - s >= RereadAfter).ToList();
        foreach (var r in due) r.Reread = true;
        return due.Count > 0;
    }

    /// Recorded before the request goes out: a crash means a missed prime, never a second one.
    public void Begin(Account a, DateTimeOffset now)
    {
        var r = Of(a);
        (r.Window, r.SentAt, r.Outcome, r.Detail, r.Reread) = (WindowId(r), now, "sent", null, false);
        Save();
    }

    /// A sent request is only "sent": success needs the next reading's evidence.
    public void Finish(Account a, bool ok, string detail)
    {
        if (!ok) (Of(a).Outcome, Of(a).Detail) = ("failed", detail);
        Save();
    }

    /// The tray menu's lines: last prime, the next fresh window, failures.
    public List<string> Status(IReadOnlyList<Account> accounts, DateTimeOffset now)
    {
        var lines = new List<string>();
        if (LastPrime is { } last) lines.Add($"Last primed {last.ToLocalTime():ddd HH:mm}");
        if (accounts.Where(Eligible).Select(a => a.Usage!.FiveHour?.ResetsAt).Where(t => t > now).Min() is { } next)
            lines.Add($"Next fresh window ~{next.ToLocalTime():HH:mm}");
        foreach (var a in accounts)
            if (Records.TryGetValue(Key(a), out var r) && r.Outcome is "failed" or "unverified")
                lines.Add($"#{a.Number} {r.Outcome}: {r.Detail}");
        return lines;
    }
}

public sealed partial class Engine
{
    /// One "Hi": Haiku, low effort, one turn, no tools, MCP servers or customizations, nothing saved.
    /// The signed-in account runs on its own login (the engine's fast path: no switch, no copy); any
    /// other in its own sealed profile, refused if it became the signed-in one meanwhile.
    internal static string[] PrimeArgs(Account a) =>
    [
        "run", a.Number.ToString(), .. a.Active ? (string[])[] : ["--require-session"], "--",
        "-p", "Hi", "--model", "haiku", "--effort", "low", "--output-format", "json",
        "--no-session-persistence", "--safe-mode", "--strict-mcp-config", "--disable-slash-commands",
        "--setting-sources", "project", // not the owner's settings: no proxy, API-key helper or provider from there
        "--tools", "",
    ];

    /// The subscription login only: no API key, token, endpoint, cloud provider, config folder or parent
    /// session from the tray's environment, so a prime can't bill anything or land elsewhere. Git Bash's
    /// location stays (Claude Code needs it on Windows).
    internal static List<string> PrimeUnset(IEnumerable<string> names) =>
        names.Where(n => n.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                         || n.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) && !n.Equals("CLAUDE_CODE_GIT_BASH_PATH", StringComparison.OrdinalIgnoreCase)
                         || n.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase))
            .Concat(SessionMarkers(names)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// Sends one prime, silently: no window, no transcript. "sent" only means the request succeeded.
    public async Task<(bool Ok, string Detail)> PrimeAsync(Account a)
    {
        var dir = Path.Combine(DockSettings.Dir, "prime");
        Directory.CreateDirectory(dir); // an empty folder: no project instructions to read
        EngineResult r;
        try { r = await RunAsync(PrimeArgs(a), TimeSpan.FromSeconds(90), unset: PrimeUnset(Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList()), workDir: dir); }
        catch (EngineException e) { return (false, e.Message); }
        var json = r.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        try
        {
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                var e = doc.RootElement;
                if (e.TryGetProperty("type", out var t) && t.GetString() == "result")
                    return e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.False
                        ? (true, "sent")
                        : (false, Clip(e.TryGetProperty("result", out var why) ? why.ToString() : "Claude reported an error"));
            }
        }
        catch (JsonException) { }
        return (false, Clip(LastLine(r.Stderr) ?? $"exited {r.ExitCode}"));
    }

    static string Clip(string s)
    {
        s = Redact(s).ReplaceLineEndings(" ").Trim();
        return s.Length > 120 ? s[..120] + "…" : s;
    }
}
