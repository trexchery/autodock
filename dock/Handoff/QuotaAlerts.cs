namespace Handoff;

public enum QuotaState { Normal, Near, Exhausted }

/// `State` is what the window just became: Near, Exhausted, or Normal for "reset".
public sealed record QuotaAlert(string Who, string Window, QuotaState State, double Pct)
{
    public string Line => State switch
    {
        QuotaState.Near => $"{Who} · {Window} at {(int)Pct}%",
        QuotaState.Exhausted => $"{Who} · {Window} limit reached",
        _ => $"{Who} · {Window} limit reset",
    };
}

/// Turns successive refresh results into threshold-crossing alerts, one per (provider,
/// account, window). The first reading of a window is a silent baseline. Only live readings
/// count: errors, stale or last-known data and unknown status keep the previous state, and
/// are never read as 0 %.
public sealed class QuotaAlerts
{
    readonly Dictionary<string, QuotaState> _state = [];

    public List<QuotaAlert> Observe(string provider, IEnumerable<Account> accounts)
    {
        var alerts = new List<QuotaAlert>();
        foreach (var a in accounts)
        {
            if (a.UsageStatus != "ok" || a.Usage is not { } u) continue;
            var who = (provider == "claude" ? "" : ProviderCli.Title(provider) + " ") + a.ShortName;
            foreach (var (window, w) in Windows(u))
            {
                var key = $"{provider}|{a.Email}|{a.OrganizationUuid}|{window}";
                var now = w.Pct >= 100 ? QuotaState.Exhausted : w.Pct >= Palette.WarnPct ? QuotaState.Near : QuotaState.Normal;
                if (_state.TryGetValue(key, out var was) && Notable(was, now)) alerts.Add(new(who, window, now, w.Pct));
                _state[key] = now;
            }
        }
        return alerts;
    }

    /// Normal to Near, anything to Exhausted, Exhausted to Normal (reset). Easing back down is silent.
    static bool Notable(QuotaState was, QuotaState now) =>
        was != now && (now == QuotaState.Exhausted || (was, now) is (QuotaState.Normal, QuotaState.Near) or (QuotaState.Exhausted, QuotaState.Normal));

    static IEnumerable<(string, Window)> Windows(Usage u)
    {
        if (u.FiveHour is { } f) yield return ("5-hour", f);
        if (u.SevenDay is { } s) yield return ("weekly", s);
        foreach (var m in u.Scoped ?? []) yield return (m.Name ?? "limit", m);
    }

    /// One balloon for everything a refresh found; null when there is nothing to say.
    public static (string Title, string Text)? Balloon(IReadOnlyList<QuotaAlert> alerts)
    {
        if (alerts.Count == 0) return null;
        var lines = alerts.Take(3).Select(a => a.Line).ToList();
        if (alerts.Count > 3) lines.Add($"+{alerts.Count - 3} more");
        return ("Quota update", string.Join("\n", lines));
    }
}
