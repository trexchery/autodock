using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchGoblins;

// Mirrors the schema-v1 payloads of `cswap list --json` / `switch --json`
// (src/claude_swap/json_output.py). Unknown fields are ignored: the contract is additive.

public sealed record Window(
    double Pct,
    DateTimeOffset? ResetsAt,
    string? Name = null,
    bool? AheadOfPace = null);

public sealed record Spend(double Used, double Limit, double Pct, string Currency, DateTimeOffset? ResetsAt);

public sealed record Usage(Window? FiveHour, Window? SevenDay, List<Window>? Scoped, Spend? Spend);

public sealed record Account(
    int Number,
    string Email,
    bool Active,
    string UsageStatus,
    Usage? Usage,
    Usage? LastGoodUsage = null,
    double? UsageAgeSeconds = null,
    double? LastGoodAgeSeconds = null,
    string? Alias = null,
    bool Disabled = false,
    DateTimeOffset? LoginExpiresAt = null,
    string? UsageError = null,
    string? UsageNote = null,
    string? OrganizationUuid = null)
{
    /// "claude", "codex" or "grok"; set by Engine.ListAsync, not part of the payload.
    [JsonIgnore] public string Provider { get; init; } = "claude";

    /// When the live usage reading was taken (absent when there is none).
    public DateTimeOffset? UsageFetchedAt { get; init; }

    public string DisplayName => string.IsNullOrEmpty(Alias) ? Email : $"{Alias}  ·  {Email}";

    /// Alias or email: how titles and balloons name the account.
    public string ShortName => string.IsNullOrEmpty(Alias) ? Email : Alias;

    /// Usage to draw: live if trusted, else last-known (shown as stale).
    public Usage? ShownUsage => Usage ?? LastGoodUsage;

    public string? StatusNote => UsageStatus switch
    {
        "ok" => null,
        _ when UsageNote != null => UsageNote, // provider engines explain their own states
        "token_expired" => "token expired — refreshes on next Claude Code use",
        "relogin_required" => "login dead — re-enroll this account",
        "api_key" => "API key (no subscription quota)",
        "no_credentials" => "no stored credentials",
        "foreign_credential" => "credential mismatch — switch to repair",
        "unavailable" => UsageError is null ? "usage unavailable" : $"usage unavailable ({UsageError})",
        _ => UsageStatus,
    };
}

public sealed record Snapshot(int SchemaVersion, int? ActiveAccountNumber, List<Account> Accounts);

public sealed record AccountRef(int? Number, string Email);

public sealed record SwitchResult(bool Switched, AccountRef? From, AccountRef? To, string? Reason, string? Note = null);

public sealed record HandoffResult(string Path, string Source, int Chars);

public sealed record EngineError(string? Type, string? Message);

// `cswap auto --once --json` event lines (autoswitch.py AutoSwitchEvent subclasses).
public sealed record AutoEvent(
    string Event,
    AccountRef? From = null,
    AccountRef? To = null,
    string? Trigger = null,
    string? Reason = null,
    string? Detail = null,
    string? Message = null,
    string? Email = null,
    bool DryRun = false,
    DateTimeOffset? EarliestResetAt = null);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("empty payload");

    /// `{"schemaVersion":1,"error":{...}}` on handled CLI errors.
    public static EngineError? TryError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var e)
                ? e.Deserialize<EngineError>(Options)
                : null;
        }
        catch (JsonException) { return null; }
    }
}
