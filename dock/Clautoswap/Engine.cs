using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Clautoswap;

public sealed record EngineResult(int ExitCode, string Stdout, string Stderr);

public sealed class EngineException(string message) : Exception(message);

/// Thin process wrapper over the bundled claude-swap CLI. All OAuth, quota,
/// locking and switching logic stays in the Python engine; this class only
/// starts it, enforces one call at a time, and parses its --json output.
public sealed partial class Engine(string exe)
{
    public string Exe { get; } = exe;
    readonly SemaphoreSlim _gate = new(1, 1);

    public static Engine Locate()
    {
        var env = Environment.GetEnvironmentVariable("CLAUTOSWAP_ENGINE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return new Engine(env);
        var bundled = Path.Combine(AppContext.BaseDirectory, "engine", "cswap.exe");
        if (File.Exists(bundled)) return new Engine(bundled);
        // No PATH fallback: an upstream claude-swap there lacks DPAPI sealing and providers.
        throw new EngineException(
            "Engine not found. Expected engine\\cswap.exe next to clautoswap.exe; " +
            "reinstall clautoswap (developers: set CLAUTOSWAP_ENGINE).");
    }

    internal static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), file))
            .FirstOrDefault(File.Exists);

    /// Absolute, so cmd is never resolved through the current directory or PATH.
    internal static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// `unset`: variables the child must not inherit; `workDir`: instead of the user's profile folder.
    public async Task<EngineResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, string? input = null, CancellationToken ct = default,
        IEnumerable<string>? unset = null, string? workDir = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["NO_COLOR"] = "1";
            // Engine resolves `claude` via PATH; never from the working directory.
            psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
            foreach (var k in unset ?? []) psi.Environment.Remove(k);
            psi.WorkingDirectory = workDir ?? StoreSecurity.UserProfile;

            using var p = Process.Start(psi) ?? throw new EngineException("failed to start engine");
            if (input != null) await p.StandardInput.WriteAsync(input);
            p.StandardInput.Close(); // never block on an interactive prompt
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(120));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                // Only our own engine child is killed, never a Claude Code process.
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new EngineException("engine timed out");
            }
            return new EngineResult(p.ExitCode, await stdout, Redact(await stderr));
        }
        finally { _gate.Release(); }
    }

    async Task<T> RunJsonAsync<T>(params string[] args)
    {
        var r = await RunAsync(args);
        var err = Json.TryError(r.Stdout);
        if (err != null) throw new EngineException(Redact(err.Message ?? err.Type ?? "engine error"));
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout))
            throw new EngineException(LastLine(r.Stderr) ?? $"engine exited {r.ExitCode}");
        return Json.Parse<T>(r.Stdout);
    }

    /// `refresh`: someone is looking, so re-read readings older than the
    /// engine's 3-minute floor instead of waiting out the idle back-off.
    public async Task<Snapshot> ListAsync(bool refresh = false, string provider = "claude")
    {
        var s = await RunJsonAsync<Snapshot>(Args(provider, refresh ? ["list", "--json", "--refresh"] : ["list", "--json"]));
        return s with { Accounts = s.Accounts.Select(a => a with { Provider = provider }).ToList() };
    }

    public Task<SwitchResult> SwitchAsync(int number, string provider = "claude") =>
        RunJsonAsync<SwitchResult>(Args(provider, ["switch", number.ToString(), "--json"]));

    /// Writes local hand-off notes from `source`'s newest session in `folder`; returns their path.
    public Task<HandoffResult> HandoffAsync(string source, string folder) =>
        RunJsonAsync<HandoffResult>("handoff", "--from", source, "--dir", folder, "--json");

    /// `cswap codex …` / `cswap grok …` route to the provider engines; Claude is the bare CLI.
    internal static string[] Args(string provider, params string[] args) =>
        provider == "claude" ? args : [provider, .. args];

    /// One auto-switch tick. The engine owns thresholds, cooldown, hysteresis,
    /// quarantine and the disabled/API-key exclusions; exit 0=switched 2=no-op 3=blocked.
    public async Task<(int Code, List<AutoEvent> Events)> AutoOnceAsync()
    {
        var r = await RunAsync(["auto", "--once", "--json"]);
        return (r.ExitCode, ParseEvents(r.Stdout));
    }

    internal static List<AutoEvent> ParseEvents(string stdout)
    {
        var events = new List<AutoEvent>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { events.Add(Json.Parse<AutoEvent>(line)); }
            catch (System.Text.Json.JsonException) { }
        }
        return events;
    }

    public Task<string> CommandAsync(params string[] args) => CommandWithInputAsync(null, args);

    public async Task<string> CommandWithInputAsync(string? input, params string[] args)
    {
        var r = await RunAsync(args, input: input);
        if (r.ExitCode != 0) throw new EngineException(LastLine(r.Stderr) ?? LastLine(Redact(r.Stdout)) ?? $"engine exited {r.ExitCode}");
        return Redact(r.Stdout);
    }

    public async Task<Dictionary<string, string?>> ConfigAsync()
    {
        var r = await RunAsync(["config", "--json"]);
        using var doc = System.Text.Json.JsonDocument.Parse(r.Stdout);
        return doc.RootElement.GetProperty("settings").EnumerateArray().ToDictionary(
            s => s.GetProperty("key").GetString()!,
            s => s.GetProperty("value") is var v && v.ValueKind == System.Text.Json.JsonValueKind.Null ? null : v.ToString());
    }

    /// Opens a visible console running the engine with `args` (e.g. `run 2`).
    /// Windows Terminal when installed, plain conhost otherwise.
    public void LaunchConsole(string title, string? workDir, params string[] args) => LaunchConsoleWith(Exe, title, workDir, args);

    /// Same console, running `exe` (e.g. the provider's own CLI) instead of the engine.
    public static void LaunchConsoleWith(string Exe, string title, string? workDir, params string[] args)
    {
        var cmdline = string.Join(' ', new[] { Exe }.Concat(args).Select(QuoteArg));
        var wt = FindOnPath("wt.exe");
        var psi = wt != null
            ? new ProcessStartInfo(wt) { UseShellExecute = false }
            : new ProcessStartInfo(Cmd) { UseShellExecute = false, CreateNoWindow = false };
        if (wt != null)
        {
            // One argv item per token: wt re-joins them for cmd.exe, quoting only
            // what needs it. wt may hand the tab to an already-running Terminal process, which
            // ignores our WorkingDirectory: pass it explicitly.
            foreach (var a in new[] { "-w", "new", "-d", workDir ?? StoreSecurity.UserProfile, "--title", title, "--suppressApplicationTitle", Cmd, "/k", Exe }.Concat(args))
                psi.ArgumentList.Add(a.Replace(";", "\\;")); // wt splits subcommands on a bare ';'
        }
        else
        {
            var safeTitle = new string(title.Where(c => "\"&|<>^%".IndexOf(c) < 0).ToArray());
            psi.Arguments = $"/k \"title {safeTitle} && {cmdline}\"";
        }
        psi.WorkingDirectory = workDir ?? StoreSecurity.UserProfile;
        // A new terminal is a top-level session, even when clautoswap itself was started from inside one.
        TopLevel(psi.Environment, FreshEnvironment);
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        Process.Start(psi)?.Dispose();
    }

    /// What a running Claude Code session hands its own children: its id, messaging socket and token,
    /// "child session" and the like. A session that inherits them runs as that session's child (and,
    /// among other things, saves no transcript). Configuration the owner set (CLAUDE_CODE_GIT_BASH_PATH,
    /// CLAUDE_CODE_USE_BEDROCK, …) is not a marker and stays.
    internal static IEnumerable<string> SessionMarkers(IEnumerable<string> names) =>
        names.Where(n => SessionMarker().IsMatch(n));

    /// A terminal's environment. Started from inside a Claude Code session, clautoswap carries what that
    /// session set up for its own tools (NO_COLOR and AI_AGENT, so no colors; no git prompts or editor;
    /// its own prompt and encodings, …): then a terminal gets the environment Windows gives any new
    /// program of the signed-in user instead, as if opened from the Start menu. Either way, no session
    /// markers.
    internal static void TopLevel(IDictionary<string, string?> env, Func<IReadOnlyDictionary<string, string>?> fresh)
    {
        if (env.Keys.Any(k => k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)) && fresh() is { } clean)
        {
            // clautoswap's own (an isolated instance's CLAUTOSWAP_DATA) carry over: its terminals talk to it.
            var own = env.Where(v => v.Key.StartsWith("CLAUTOSWAP_", StringComparison.OrdinalIgnoreCase)).ToList();
            env.Clear();
            foreach (var (k, v) in clean.Concat(own.Select(o => KeyValuePair.Create(o.Key, o.Value ?? "")))) env[k] = v;
        }
        foreach (var k in SessionMarkers(env.Keys.ToList())) env.Remove(k);
    }

    /// Machine, user and volatile variables, as Windows builds them for a new process of this user.
    internal static IReadOnlyDictionary<string, string>? FreshEnvironment()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008 /* TOKEN_QUERY */, out var token)) return null;
        try
        {
            if (!CreateEnvironmentBlock(out var block, token, false)) return null;
            try
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var p = block; Marshal.PtrToStringUni(p) is { Length: > 0 } entry; p += (entry.Length + 1) * 2)
                    if (entry.IndexOf('=', 1) is var eq and > 0) env[entry[..eq]] = entry[(eq + 1)..]; // skips "=C:=C:\" entries
                return env;
            }
            finally { DestroyEnvironmentBlock(block); }
        }
        finally { CloseHandle(token); }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);

    [System.Runtime.InteropServices.DllImport("userenv.dll", SetLastError = true)]
    static extern bool DestroyEnvironmentBlock(IntPtr block);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [GeneratedRegex(@"^(CLAUDECODE|CLAUDE_PID|CLAUDE_CODE_(CHILD_SESSION|SESSION_(ID|ATTENDED|KIND|ACCESS_TOKEN|ORIGIN|NAME)|BRIDGE_SESSION_ID|MESSAGING_(SOCKET|TOKEN)|ENTRYPOINT|EXECPATH|SSE_PORT))$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionMarker();

    internal static string QuoteArg(string a) =>
        a.Length > 0 && a.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    static string? LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    // Defense in depth: the engine never prints tokens, but anything we surface
    // (balloons, dialogs, the dock log) goes through this first.
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{6,}"
        + @"|(?i:bearer)\s+[A-Za-z0-9._\-]{12,}"
        + @"|(?i:""(access|refresh|id)_?token""\s*:\s*""[^""]*"")"
        + @"|(?i:(access|refresh|id)_?token[""']?\s*[=:]\s*(?:""[^""]*""|'[^']*'|[^\s""',;]+))"
        + @"|(?i:(api_?key|secret|password)\w*[""']?\s*[=:]\s*(?:""[^""]*""|'[^']*'|[^\s""',;]+))"
        + @"|\b(sk|xai)-[A-Za-z0-9_\-]{16,}"
        + @"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*"
        + @"|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,}"
        + @"|\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"
        + @"|xox[abprs]-[A-Za-z0-9-]{10,}"
        + @"|AIza[0-9A-Za-z_\-]{30,}"
        + @"|\b[rs]k_(?:live|test)_[A-Za-z0-9]{16,}"
        + @"|-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|\z)")]
    private static partial Regex SecretPattern();

    public static string Redact(string s) => SecretPattern().Replace(s, "[redacted]");
}
