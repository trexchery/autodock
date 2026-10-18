using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Autodock;

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
        var env = Environment.GetEnvironmentVariable("AUTODOCK_ENGINE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return new Engine(env);
        var bundled = Path.Combine(AppContext.BaseDirectory, "engine", "cswap.exe");
        if (File.Exists(bundled)) return new Engine(bundled);
        var onPath = FindOnPath("cswap.exe");
        if (onPath != null) return new Engine(onPath);
        throw new EngineException(
            "Engine not found. Expected engine\\cswap.exe next to autodock.exe " +
            "(or set AUTODOCK_ENGINE).");
    }

    internal static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), file))
            .FirstOrDefault(File.Exists);

    public async Task<EngineResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, string? input = null, CancellationToken ct = default)
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
            psi.WorkingDirectory = StoreSecurity.UserProfile;

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
            : new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = false };
        if (wt != null)
        {
            // One argv item per token: wt re-joins them for cmd.exe, quoting only
            // what needs it. wt may hand the tab to an already-running Terminal process, which
            // ignores our WorkingDirectory: pass it explicitly.
            foreach (var a in new[] { "-w", "new", "-d", workDir ?? StoreSecurity.UserProfile, "--title", title, "--suppressApplicationTitle", "cmd.exe", "/k", Exe }.Concat(args))
                psi.ArgumentList.Add(a);
        }
        else
        {
            var safeTitle = new string(title.Where(c => "\"&|<>^%".IndexOf(c) < 0).ToArray());
            psi.Arguments = $"/k \"title {safeTitle} && {cmdline}\"";
        }
        psi.WorkingDirectory = workDir ?? StoreSecurity.UserProfile;
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        Process.Start(psi)?.Dispose();
    }

    internal static string QuoteArg(string a) =>
        a.Length > 0 && a.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    static string? LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    // Defense in depth: the engine never prints tokens, but anything we surface
    // (balloons, dialogs, the dock log) goes through this first.
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{6,}|(?i:bearer)\s+[A-Za-z0-9._\-]{12,}|(?i:""(access|refresh)_?token""\s*:\s*""[^""]*"")")]
    private static partial Regex SecretPattern();

    public static string Redact(string s) => SecretPattern().Replace(s, "[redacted]");
}
