namespace Autodock;

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
    public static double? Peak(Account a) =>
        a.ShownUsage is { } u && (u.FiveHour != null || u.SevenDay != null)
            ? Math.Max(u.FiveHour?.Pct ?? 0, u.SevenDay?.Pct ?? 0)
            : null;

    /// The account for the next worker, or null with the reason nothing fits.
    /// `busy`: workers running per email+org. `prefer`: the account a resumable session lives on.
    /// Why nothing can ever start until the owner acts (not a limit or busy accounts, which pass).
    internal const string OnlyYours = "agents need a Claude account besides the one you are signed in with (they never run on, or switch, your own login)",
        NoneUsable = "no usable Claude account";

    public static (Account? Pick, string? Why) Pick(IReadOnlyList<Account> accounts, double threshold,
        IReadOnlyDictionary<string, int> busy, IReadOnlyDictionary<string, DateTimeOffset> cooled, DateTimeOffset now,
        int perAccount = 2, string? prefer = null, bool consumeFirst = false)
    {
        var usable = accounts.Where(Usable).ToList();
        if (usable.Count == 0) return (null, accounts.Any(a => a.Active) ? OnlyYours : NoneUsable);
        var open = usable.Where(a => !(cooled.TryGetValue(Key(a), out var until) && until > now)).ToList();
        var headroom = open.Where(a => Peak(a) is not { } p || p < threshold).ToList();
        var free = headroom.Where(a => busy.GetValueOrDefault(Key(a)) < perAccount).ToList();
        if (free.Count == 0)
        {
            if (headroom.Count > 0) return (null, "every account is already running its share of agents");
            var reset = usable.Select(a => NextReset(a, cooled, now)).Where(t => t != null).Min();
            return (null, "all accounts are near their limits" + (reset is { } r ? $"; earliest reset {Format.When(r, now)}" : ""));
        }
        if (prefer != null && free.FirstOrDefault(a => Key(a) == prefer) is { } same) return (same, null);
        // Most headroom first, and agents spread out over accounts.
        var ranked = free.OrderByDescending(a => 100 - (Peak(a) ?? 50) - 15 * busy.GetValueOrDefault(Key(a)));
        if (consumeFirst) // the engine's consume-first strategy: spend the quota that resets soonest
            ranked = free.OrderBy(a => a.ShownUsage?.SevenDay?.ResetsAt ?? DateTimeOffset.MaxValue).ThenBy(a => busy.GetValueOrDefault(Key(a)));
        return (ranked.First(), null);
    }

    public static string Key(Account a) => a.Email + "|" + (a.OrganizationUuid ?? "");

    /// When an exhausted (or cooled) account is expected back.
    public static DateTimeOffset? NextReset(Account a, IReadOnlyDictionary<string, DateTimeOffset>? cooled, DateTimeOffset now)
    {
        var times = new List<DateTimeOffset>();
        if (cooled != null && cooled.TryGetValue(Key(a), out var c) && c > now) times.Add(c);
        if (a.ShownUsage is { } u)
            foreach (var w in new[] { u.FiveHour, u.SevenDay })
                if (w is { ResetsAt: { } t } && t > now) times.Add(t);
        return times.Count == 0 ? null : times.Max();
    }
}
