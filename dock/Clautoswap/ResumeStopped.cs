using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clautoswap;

/// A Claude Code window waiting for someone to type: stopped on a usage-limit error (`AtLimit`), or done
/// with its turn. `Record` and `Seen` (the transcript's write time) let the move check nothing changed since.
/// `Limit` is the error as recorded; `OwnRefused`: the API refused the window's own account (not one it was
/// only labelled with after a switch).
public sealed record StoppedSession(int Pid, string SessionId, string Cwd, string ConfigDir, int? Account, string Transcript,
    bool AtLimit = true, string? Record = null, DateTime Seen = default, Limit? Limit = null, bool OwnRefused = false);

/// A usage-limit stop as Claude Code recorded it: the window that ran out ("five_hour", "seven_day", ...), when it
/// resets, and when the stop happened. Default fields: an older error without the details.
public sealed record Limit(string? Window, DateTimeOffset? ResetsAt, DateTimeOffset At);

/// With auto-switch on: windows stopped at a limit are reopened (`claude --resume`) on an account with
/// room, the default login's own when it has some again, so their work carries on unattended. A `cswap run`
/// window whose account is at the switch threshold is moved the same way once its turn is done, before the
/// limit stops it. (The default login needs no move: the engine switches its account in place.)
/// Only an idle session, quiet a while, is touched: nothing that is working, or that someone has typed
/// into since, is ever stopped.
public static class ResumeStopped
{
    /// A reading at or above this is no room: the engine's own at-limit line.
    public const double Full = 99;
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(20);
    /// A stop at a limit is final once written; this only rides out the lines Claude Code writes with it.
    static readonly TimeSpan LimitQuiet = TimeSpan.FromSeconds(5);
    /// What a window already sent to an account this pass costs that account's room, so one pass spreads stopped windows.
    public const double PerWindow = 10;
    public static string Prompt(bool moved) =>
        "This session stopped at a usage limit and clautoswap reopened it" + (moved ? " on another account" : " now that the limit has reset") +
        ". Continue exactly where you left off.";

    /// Claude args that reopen `s` on another account: a stopped turn is told to carry on, a finished one
    /// just waits for its user again.
    public static string[] ResumeArgs(StoppedSession s, bool moved) =>
        s.AtLimit ? ["--resume", s.SessionId, Prompt(moved)] : ["--resume", s.SessionId];

    /// Whether the reopened window is on another account than the one that refused it. A default-login stop from
    /// before a switch is: its label already names the new default, but the account that said no was the old one.
    public static bool Moved(StoppedSession s, int target) => target != s.Account || s.AtLimit && !s.OwnRefused;

    public static string DefaultDir => Path.Combine(StoreSecurity.UserProfile, ".claude");
    static string ProfilesDir => Path.Combine(StoreSecurity.BackupRoot, "sessions");

    /// Every Claude config dir with its account: the default login, then each `cswap run` profile (`N-email`).
    public static List<(string Dir, int? Account)> ConfigDirs(int? active)
    {
        var dirs = new List<(string, int?)> { (DefaultDir, active) };
        try
        {
            foreach (var d in Directory.EnumerateDirectories(ProfilesDir))
                if (int.TryParse(Path.GetFileName(d).Split('-')[0], out var n)) dirs.Add((d, n));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return dirs;
    }

    /// The default login's account and when the engine last wrote `sequence.json` (it does on every switch). Read
    /// errors: `(null, MaxValue)`, so nothing is attributed.
    public static (int? Account, DateTime ChangedAt) DefaultLogin()
    {
        try
        {
            var path = Path.Combine(StoreSecurity.BackupRoot, "sequence.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var n = doc.RootElement.TryGetProperty("activeAccountNumber", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var v) ? v : (int?)null; // null until the first switch
            return (n, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return (null, DateTime.MaxValue); }
    }

    /// A stop in a profile is always that profile's account; one in `~/.claude` belongs to today's default login
    /// only if it landed well after the last switch: a refusal just after one can still be the old login's (a
    /// request in flight, or one sent before Claude Code reread the login). Subtraction, as `defaultSince` can be MaxValue.
    public static bool OwnStop(bool defaultDir, DateTimeOffset at, DateTime defaultSince) => !defaultDir || at.UtcDateTime - defaultSince >= TimeSpan.FromSeconds(30);

    public static List<StoppedSession> Scan(IEnumerable<(string Dir, int? Account)> dirs, DateTime nowUtc, DateTime? defaultLoginChanged = null)
    {
        var found = new List<StoppedSession>();
        var defaultSince = defaultLoginChanged ?? DefaultLogin().ChangedAt;
        foreach (var (dir, account) in dirs)
        {
            string[] records;
            try { records = Directory.GetFiles(Path.Combine(dir, "sessions"), "*.json"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var rec in records)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rec));
                    var r = doc.RootElement;
                    // Only idle: "shell" (idle with a background command running) would orphan that command if moved.
                    if (Str(r, "status") != "idle" || Str(r, "kind") is { } k && k != "interactive") continue;
                    if (Str(r, "sessionId") is not { } id || Str(r, "cwd") is not { } cwd || !r.TryGetProperty("pid", out var pidEl)) continue;
                    var pid = pidEl.GetInt32();
                    var stamp = Str(r, "procStart");
                    if (!IsLiveClaude(pid, stamp)) continue;
                    var started = long.TryParse(stamp, out var ft) ? DateTime.FromFileTimeUtc(ft) : DateTime.MinValue;
                    if (TranscriptOf(dir, id, cwd) is not { } t) continue;
                    var seen = File.GetLastWriteTimeUtc(t);
                    var tail = Tail(t).ToList();
                    if (LimitOf(tail) is { } limit)
                    {
                        // Quiet from the error itself: notices written after it (Remote Control dropping on a switch) don't restart it.
                        // This process's own stop only: a resumed (or tray-copied) transcript still ends at the error that stopped the window before it.
                        var at = limit.At == default ? seen : limit.At.UtcDateTime;
                        if (at > started && nowUtc - at >= LimitQuiet)
                            found.Add(new StoppedSession(pid, id, cwd, dir, account, t, true, rec, seen, limit, OwnStop(dir == DefaultDir, limit.At, defaultSince)));
                    }
                    else if (nowUtc - seen >= Quiet && EndsTurn(tail))
                        found.Add(new StoppedSession(pid, id, cwd, dir, account, t, false, rec, seen));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
            }
        }
        return found;
    }

    /// Whether the conversation's last message is Claude Code's own usage-limit error.
    public static bool EndsAtLimit(IEnumerable<string> lines) => LimitOf(lines) != null;

    /// The usage-limit error the conversation ends with, or null. A look at the limit (/usage, /status) typed after it doesn't hide it; any other command does.
    public static Limit? LimitOf(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (type is not ("assistant" or "user") || type == "user" && IsLook(r)) continue;
                if (type == "user" || !r.TryGetProperty("isApiErrorMessage", out var err) || err.ValueKind != JsonValueKind.True) return null;
                JsonElement info = default;
                var refused = r.TryGetProperty("apiErrorParams", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("rate_limit_info", out info) && info.ValueKind == JsonValueKind.Object;
                var text = r.TryGetProperty("message", out var m) ? m.ToString() : "";
                if (Str(r, "apiError") != "usage_limit_reached" && !(refused && Str(info, "status") == "rejected")
                    && !(text.Contains("limit", StringComparison.OrdinalIgnoreCase)
                        && (text.Contains("hit your", StringComparison.OrdinalIgnoreCase) || text.Contains("usage", StringComparison.OrdinalIgnoreCase)))) return null;
                var resets = refused && info.TryGetProperty("resetsAt", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix) : (DateTimeOffset?)null;
                var at = r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && ts.TryGetDateTimeOffset(out var t) ? t : default;
                return new Limit(refused ? Str(info, "rateLimitType") : null, resets, at);
            }
            catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException) { } // a line cut by the tail window, or one being written
        }
        return null;
    }

    /// Whether the conversation's last message is Claude's own finished reply: no tool call left open, no
    /// error, nobody's message after it. A turn still running, or one cut off, never matches.
    public static bool EndsTurn(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (type is not ("assistant" or "user") || type == "user" && IsLocal(r)) continue;
                if (type == "user" || r.TryGetProperty("isApiErrorMessage", out var err) && err.ValueKind == JsonValueKind.True) return false;
                if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return false;
                if (Str(m, "stop_reason") is "tool_use" or "max_tokens" or "pause_turn") return false;
                return !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array
                    || !c.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") is "tool_use" or "server_tool_use");
            }
            catch (JsonException) { } // a line cut by the tail window, or one being written
        }
        return false;
    }

    /// A user line Claude never answers: a local slash command (/usage, /model…), its output, or a /compact summary.
    static bool IsLocal(JsonElement r) =>
        r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
        || r.TryGetProperty("isCompactSummary", out var cs) && cs.ValueKind == JsonValueKind.True
        || r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "content") is { } text
           && (text.StartsWith("<command-name>") || text.StartsWith("<local-command-"));

    /// A user line that only looks at the limit: a /usage or /status command, or local command output. Nothing else (/model, /compact) is.
    static bool IsLook(JsonElement r) =>
        r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
        || r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "content") is { } text
           && (text.StartsWith("<command-name>/usage<") || text.StartsWith("<command-name>/status<") || text.StartsWith("<local-command-"));

    /// How long since the last keyboard or mouse input anywhere in this session. A finished turn is only
    /// moved after a quiet spell, so a prompt someone is still typing is never closed under them.
    public static TimeSpan UserIdle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }

    /// The account to reopen on. Stopped at a limit: the session's own once it has room again, else the one
    /// with the most room (not excluded, usage readable) whose profile exists, or the default login.
    /// Done with a turn: only when its own account reads at or above `threshold`, onto one below it by at
    /// least `hysteresis` points, so two accounts near the line never trade a window back and forth.
    /// Null: stay put (nowhere has room yet, or no move is due).
    /// An account the API refused the window on stays out until its reset, whatever a stale reading says; `load`
    /// (windows already sent to each account this pass) lowers an account's room, so one pass spreads them.
    public static int? Target(StoppedSession s, IReadOnlyList<Account> accounts, Func<int, bool> hasProfile,
        double threshold = Full, double hysteresis = 0, DateTimeOffset? now = null, IReadOnlyDictionary<int, int>? load = null)
    {
        double Weight(Account a) => Format.Peak(a) + PerWindow * (load?.GetValueOrDefault(a.Number) ?? 0);
        bool Read(Account a) => !a.Disabled && a.UsageStatus == "ok" && a.Usage != null;
        bool Room(Account a) => Read(a) && Format.Peak(a) < Full;
        var own = accounts.FirstOrDefault(a => a.Number == s.Account);
        var refused = s.OwnRefused && s.Limit?.ResetsAt > (now ?? DateTimeOffset.UtcNow);
        if (s.AtLimit && own != null && Room(own) && !refused) return own.Number;
        if (!s.AtLimit && (own == null || !Read(own) || Format.Peak(own) < threshold)) return null;
        return accounts.Where(a => a.Number != s.Account && Room(a) && (a.Active || hasProfile(a.Number)))
            .Where(a => s.AtLimit || Format.Peak(a) < threshold && Format.Peak(own!) - Format.Peak(a) >= hysteresis)
            .OrderBy(Weight).ThenByDescending(a => a.Active)
            .Select(a => (int?)a.Number).FirstOrDefault();
    }

    /// Whether `s` is exactly as the scan saw it: still idle, transcript untouched. Checked right before the
    /// window is closed, so a turn started (or a message sent) since the scan is never cut off, nor a recycled pid hit. A stop at a limit
    /// holds while the error is still the last message: notices Claude Code writes after it don't cancel the move.
    public static bool Unchanged(StoppedSession s)
    {
        try
        {
            if (s.AtLimit && s.Limit is { } limit ? LimitOf(Tail(s.Transcript))?.At != limit.At : File.GetLastWriteTimeUtc(s.Transcript) != s.Seen) return false;
            if (s.Record == null) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(s.Record));
            var r = doc.RootElement;
            return Str(r, "status") == "idle" && Str(r, "sessionId") == s.SessionId && IsLiveClaude(s.Pid, Str(r, "procStart"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// The `cswap import-usage -` document that hands the engine the limits the API just gave these windows (one
    /// row per account, 100% on the window that ran out), or null. `done` holds `account:reset` keys already sent.
    /// Only a refused own account with a known future reset on the 5-hour or 7-day window counts. The engine keeps
    /// a newer real reading over this one; `spend` is not carried (display only, back with the next real fetch).
    public static string? LimitReport(IEnumerable<StoppedSession> stops, IReadOnlyList<Account> accounts, DateTimeOffset now, ISet<string> done)
    {
        var rows = new List<object>();
        foreach (var s in stops)
        {
            if (!s.OwnRefused || s.Limit is not { Window: "five_hour" or "seven_day", ResetsAt: { } reset } l || reset <= now) continue;
            if (accounts.FirstOrDefault(a => a.Number == s.Account) is not { } a || !done.Add($"{a.Number}:{reset.ToUnixTimeSeconds()}")) continue;
            var u = a.ShownUsage;
            var full = new Window(100, reset);
            rows.Add(new
            {
                email = a.Email,
                organizationUuid = a.OrganizationUuid ?? "",
                usageStatus = "ok",
                usageAgeSeconds = Math.Max(0, (now - l.At).TotalSeconds),
                usage = new { fiveHour = l.Window == "five_hour" ? full : u?.FiveHour, sevenDay = l.Window == "seven_day" ? full : u?.SevenDay, scoped = u?.Scoped },
            });
        }
        return rows.Count == 0 ? null
            : JsonSerializer.Serialize(new { schemaVersion = 1, accounts = rows }, LimitJson);
    }

    static readonly JsonSerializerOptions LimitJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, // usage_from_json rejects a null window
    };

    /// When each session was last reopened, kept across tray restarts so a restart never re-arms a move.
    public static Dictionary<string, DateTime> LoadMoves(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// Saves `moves`, dropping entries a day old: by then no guard needs them.
    public static void SaveMoves(string path, Dictionary<string, DateTime> moves, DateTime nowUtc)
    {
        foreach (var k in moves.Where(m => nowUtc - m.Value > TimeSpan.FromDays(1)).Select(m => m.Key).ToList()) moves.Remove(k);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(moves));
    }

    public static string? ProfileDir(int account)
    {
        try { return Directory.EnumerateDirectories(ProfilesDir, $"{account}-*").FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Copies the transcript (and its subagents/tool-results folder), file checkpoints and todos into
    /// `targetDir`, where `--resume` finds them.
    public static void CopyTranscript(StoppedSession s, string targetDir)
    {
        if (Path.GetFullPath(targetDir).TrimEnd('\\').Equals(Path.GetFullPath(s.ConfigDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        var project = Path.GetFileName(Path.GetDirectoryName(s.Transcript)!);
        var dest = Path.Combine(targetDir, "projects", project);
        Directory.CreateDirectory(dest);
        File.Copy(s.Transcript, Path.Combine(dest, Path.GetFileName(s.Transcript)), overwrite: true);
        var extra = Path.Combine(Path.GetDirectoryName(s.Transcript)!, s.SessionId);
        if (Directory.Exists(extra)) CopyDir(extra, Path.Combine(dest, s.SessionId));
        var history = Path.Combine(s.ConfigDir, "file-history", s.SessionId);
        if (Directory.Exists(history)) CopyDir(history, Path.Combine(targetDir, "file-history", s.SessionId));
        var todos = Path.Combine(s.ConfigDir, "todos");
        if (Directory.Exists(todos))
            foreach (var f in Directory.GetFiles(todos, s.SessionId + "*.json"))
            {
                Directory.CreateDirectory(Path.Combine(targetDir, "todos"));
                File.Copy(f, Path.Combine(targetDir, "todos", Path.GetFileName(f)), overwrite: true);
            }
    }

    /// Ends the stopped claude and returns; a `cswap run` wrapper around it is then left to exit on its own (it
    /// seals the profile's login) and the shell that held it is closed, so the window it sat in goes away. The
    /// new `cswap run` need not wait for that: it registers its run marker first, which a peer's seal respects.
    public static async Task CloseAsync(int pid)
    {
        var chain = new List<(int Pid, string Name)>();
        for (var (p, guard) = (pid, 0); guard < 4 && ParentOf(p) is { } parent; p = parent, guard++)
        {
            var name = NameOf(parent);
            if (name is not ("cswap" or "cmd" or "powershell" or "pwsh")) break;
            chain.Add((parent, name));
        }
        using (var claude = Process.GetProcessById(pid))
        {
            claude.Kill();
            try { await claude.WaitForExitAsync(new CancellationTokenSource(10_000).Token); }
            catch (OperationCanceledException) { } // slow to go: the window is still reopened
        }
        _ = CloseShellsAsync(chain).ContinueWith(t => Program.Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
    }

    static async Task CloseShellsAsync(List<(int Pid, string Name)> chain)
    {
        foreach (var (p, name) in chain)
        {
            try
            {
                using var proc = Process.GetProcessById(p);
                if (name == "cswap") { try { await proc.WaitForExitAsync(new CancellationTokenSource(30_000).Token); } catch (OperationCanceledException) { } }
                else proc.Kill();
            }
            catch (ArgumentException) { } // already gone
            catch (InvalidOperationException) { }
        }
    }

    internal static bool IsLiveClaude(int pid, string? procStart)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // The record's start stamp names the process that wrote it, so a recycled pid never matches. The image name
            // is only a fallback for unstamped records: Claude Code's updater renames a running claude.exe to
            // claude.exe.old.<stamp>.<pid>, which would hide every window older than the update.
            if (long.TryParse(procStart, out var ft))
                return Math.Abs(p.StartTime.ToUniversalTime().ToFileTimeUtc() - ft) < TimeSpan.TicksPerSecond * 2;
            return NameOf(p.Id) == "claude";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static string? TranscriptOf(string dir, string sessionId, string cwd)
    {
        // Where Claude Code files it: the cwd with every other character as "-". The search covers long (hashed)
        // paths, and a session that outlived a `cd`.
        var direct = Path.Combine(dir, "projects", Regex.Replace(cwd, "[^A-Za-z0-9]", "-"), sessionId + ".jsonl");
        if (File.Exists(direct)) return direct;
        try { return Directory.EnumerateFiles(Path.Combine(dir, "projects"), sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The last lines of a transcript; they can be megabytes long, so only the end is read.
    static IEnumerable<string> Tail(string path, int bytes = 256 * 1024)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, f.Length - bytes);
        f.Seek(start, SeekOrigin.Begin);
        var buf = new byte[f.Length - start];
        f.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return start > 0 ? lines.Skip(1) : lines;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static string? NameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName.Split('.')[0].ToLowerInvariant(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
    }

    static int? ParentOf(int pid)
    {
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == -1) return null;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                if (e.th32ProcessID == pid) return (int)e.th32ParentProcessID;
            return null;
        }
        finally { CloseHandle(snap); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
