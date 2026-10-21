namespace Handoff;

/// Chooses the Claude account a new worker runs as, from the engine's own snapshot and thresholds.
/// It never switches anything: the worker starts in that account's isolated profile (`cswap run N`),
/// so the default login and every running session stay as they are.
public static class AccountPicker
{
    /// Same exclusions as the engine's auto-switch (excluded accounts, dead or missing logins, API keys),
    /// plus the login in use at the keyboard: a worker on it could have its account switched mid-run.
    /// An account with extra usage turned on qualifies only with a live reading: a guess could cost money.
    public static bool Usable(Account a) =>
        a.Provider == "claude" && !a.Disabled && !a.Active && a.UsageStatus is "ok" or "token_expired" or "unavailable"
        && !(SpendsMoney(a) && a.Usage == null);

    /// Extra usage (paid overage) is set up on the account.
    public static bool SpendsMoney(Account a) => (a.Usage ?? a.LastGoodUsage)?.Spend != null;

    /// Highest of the 5-hour and weekly windows; unknown usage counts as unknown, not as empty.
    /// A window whose reset time has passed counts as empty: the reading is older than the reset, and a
    /// 5-hour window only starts ticking again at the next message.
    public static double? Peak(Account a, DateTimeOffset? now = null) =>
        a.ShownUsage is { } u && (u.FiveHour != null || u.SevenDay != null)
            ? Math.Max(Live(u.FiveHour, now ?? DateTimeOffset.Now), Live(u.SevenDay, now ?? DateTimeOffset.Now))
            : null;

    static double Live(Window? w, DateTimeOffset now) => w is null || w.ResetsAt is { } t && t <= now ? 0 : w.Pct;

    /// The account for the next worker, or null with the reason nothing fits.
    /// `busy`: workers running per email+org. `prefer`: the account a resumable session lives on.
    /// Why nothing can ever start until the owner acts (not a limit or busy accounts, which pass).
    internal const string OnlyYours = "agents need a Claude account besides the one you are signed in with (they never run on, or switch, your own login)",
        NoneUsable = "no usable Claude account", AllNearLimits = "all accounts are near their limits";

    public static (Account? Pick, string? Why) Pick(IReadOnlyList<Account> accounts, double threshold,
        IReadOnlyDictionary<string, int> busy, IReadOnlyDictionary<string, DateTimeOffset> cooled, DateTimeOffset now,
        int perAccount = 2, string? prefer = null, bool consumeFirst = false, IReadOnlySet<int>? live = null)
    {
        var usable = accounts.Where(Usable).ToList();
        if (usable.Count == 0) return (null, accounts.Any(a => a.Active) ? OnlyYours : NoneUsable);
        var open = usable.Where(a => !(cooled.TryGetValue(Key(a), out var until) && until > now)).ToList();
        var headroom = open.Where(a => Peak(a, now) is not { } p || p < threshold).ToList();
        var free = headroom.Where(a => busy.GetValueOrDefault(Key(a)) < perAccount).ToList();
        // The default login near its line can only switch to an account no `cswap run` holds (`live`: the owner's pinned windows too): keep the roomiest one free of agents.
        // Runs already holding every such account yield one once the default login reaches its line (TrayApp.FreePinnedAsync, Supervisor.YieldAsync).
        var kept = accounts.FirstOrDefault(a => a.Active) is { } own && Peak(own, now) is { } p0 && p0 >= threshold - MinRoom
            ? headroom.Where(a => busy.GetValueOrDefault(Key(a)) == 0 && live?.Contains(a.Number) != true).OrderBy(a => Peak(a, now) ?? threshold).FirstOrDefault() : null;
        if (kept != null) free.Remove(kept);
        if (free.Count == 0 && kept != null) return (null, $"#{kept.Number} is kept free for your own login, which is near its limit");
        if (free.Count == 0)
        {
            if (headroom.Count > 0) return (null, "every account is already running its share of agents");
            var reset = usable.Select(a => NextReset(a, cooled, now, threshold)).Where(t => t != null).Min();
            return (null, AllNearLimits + (reset is { } r ? $"; earliest reset {Format.When(r, now)}" : ""));
        }
        if (prefer != null && free.FirstOrDefault(a => Key(a) == prefer) is { } same) return (same, null);
        var ranked = free.OrderByDescending(a => Score(a, threshold, busy.GetValueOrDefault(Key(a)), now));
        if (consumeFirst) // the engine's consume-first strategy: spend the quota that resets soonest
            ranked = free.OrderBy(a => a.ShownUsage?.SevenDay?.ResetsAt ?? DateTimeOffset.MaxValue).ThenBy(a => busy.GetValueOrDefault(Key(a)));
        return (ranked.First(), null);
    }

    /// Quota points an account needs below the threshold for a new run to be worth starting on it.
    const double MinRoom = 10;

    /// Higher is better. For 5-hour windows: spend what expires soonest. An open window's unused room
    /// is lost at its reset, so the room left per hour until reset ranks first; accounts without an
    /// open window (idle, or only a weekly reading) come next by headroom and load, and accounts with
    /// an open window but too little room last. ponytail: a flat MinRoom, a learned per-run cost if
    /// runs still hit the limit mid-way.
    internal static double Score(Account a, double threshold, int busy, DateTimeOffset now)
    {
        var peak = Peak(a, now);
        var fallback = 100 - (peak ?? 50) - 15 * busy;
        if (a.ShownUsage?.FiveHour is not { ResetsAt: { } t } || t <= now || peak is not { } p) return fallback;
        var room = threshold - p - 15 * busy;
        return room >= MinRoom ? 1000 + room / Math.Max(0.25, (t - now).TotalHours) : fallback - 1000;
    }

    /// When the first account that is out of usage comes back, if any reading says.
    public static DateTimeOffset? EarliestReturn(IEnumerable<Account> accounts, IReadOnlyDictionary<string, DateTimeOffset> cooled, DateTimeOffset now, double threshold = ResumeStopped.Full) =>
        accounts.Where(Usable).Select(a => NextReset(a, cooled, now, threshold)).Where(t => t != null).Min();

    public static string Key(Account a) => a.Email + "|" + (a.OrganizationUuid ?? "");

    /// When an exhausted (or cooled) account is expected back: the latest reset among the windows at or above `threshold`
    /// (a weekly window with room never benches it until its own reset), and the cooling. Neither: the soonest reset ahead.
    public static DateTimeOffset? NextReset(Account a, IReadOnlyDictionary<string, DateTimeOffset>? cooled, DateTimeOffset now, double threshold = ResumeStopped.Full)
    {
        var times = new List<DateTimeOffset>();
        if (cooled != null && cooled.TryGetValue(Key(a), out var c) && c > now) times.Add(c);
        var ahead = a.ShownUsage is { } u ? new[] { u.FiveHour, u.SevenDay }.Where(w => w is { ResetsAt: { } t } && t > now).ToList() : [];
        times.AddRange(ahead.Where(w => w!.Pct >= threshold).Select(w => w!.ResetsAt!.Value));
        if (times.Count == 0 && ahead.Count > 0) return ahead.Min(w => w!.ResetsAt!.Value); // a stale reading: the soonest is the likeliest
        return times.Count == 0 ? null : times.Max();
    }
}
