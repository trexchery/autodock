using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Clautoswap;

/// A Claude Code window that stopped on a usage-limit error and is waiting for someone to type.
public sealed record StoppedSession(int Pid, string SessionId, string Cwd, string ConfigDir, int? Account, string Transcript);

/// With auto-switch on: windows stopped at a limit are reopened (`claude --resume`) on an account with
/// room, the default login's own when it has some again, so their work carries on unattended.
/// Only an idle session whose transcript ends on the limit error and has been quiet a while is touched:
/// nothing that is working, or that someone has typed into since, is ever stopped.
public static class ResumeStopped
{
    /// A reading at or above this is no room: the engine's own at-limit line.
    public const double Full = 99;
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(20);
    public static string Prompt(bool moved) =>
        "This session stopped at a usage limit and clautoswap reopened it" + (moved ? " on another account" : " now that the limit has reset") +
        ". Continue exactly where you left off.";

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

    public static List<StoppedSession> Scan(IEnumerable<(string Dir, int? Account)> dirs, DateTime nowUtc)
    {
        var found = new List<StoppedSession>();
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
                    if (Str(r, "status") != "idle" || Str(r, "kind") is { } k && k != "interactive") continue;
                    if (Str(r, "sessionId") is not { } id || Str(r, "cwd") is not { } cwd || !r.TryGetProperty("pid", out var pidEl)) continue;
                    var pid = pidEl.GetInt32();
                    if (!IsLiveClaude(pid, Str(r, "procStart"))) continue;
                    if (TranscriptOf(dir, id) is not { } t) continue;
                    if (nowUtc - File.GetLastWriteTimeUtc(t) < Quiet) continue;
                    if (EndsAtLimit(Tail(t))) found.Add(new StoppedSession(pid, id, cwd, dir, account, t));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
            }
        }
        return found;
    }

    /// Whether the conversation's last message is Claude Code's own usage-limit error.
    public static bool EndsAtLimit(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (type is not ("assistant" or "user")) continue;
                if (type == "user" || !r.TryGetProperty("isApiErrorMessage", out var err) || err.ValueKind != JsonValueKind.True) return false;
                var text = r.TryGetProperty("message", out var m) ? m.ToString() : "";
                return text.Contains("limit", StringComparison.OrdinalIgnoreCase)
                    && (text.Contains("hit your", StringComparison.OrdinalIgnoreCase) || text.Contains("usage", StringComparison.OrdinalIgnoreCase));
            }
            catch (JsonException) { } // a line cut by the tail window, or one being written
        }
        return false;
    }

    /// The account to reopen on: the session's own once it has room again, else the one with the most room
    /// (not excluded, usage readable) whose profile exists, or the default login. Null: nowhere has room yet.
    public static int? Target(StoppedSession s, IReadOnlyList<Account> accounts, Func<int, bool> hasProfile)
    {
        bool Room(Account a) => !a.Disabled && a.UsageStatus == "ok" && a.Usage != null && Format.Peak(a) < Full;
        if (accounts.FirstOrDefault(a => a.Number == s.Account) is { } own && Room(own)) return own.Number;
        return accounts.Where(a => a.Number != s.Account && Room(a) && (a.Active || hasProfile(a.Number)))
            .OrderBy(Format.Peak).ThenByDescending(a => a.Active)
            .Select(a => (int?)a.Number).FirstOrDefault();
    }

    public static string? ProfileDir(int account)
    {
        try { return Directory.EnumerateDirectories(ProfilesDir, $"{account}-*").FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Copies the transcript (and its subagents/tool-results folder) into `targetDir`, where `--resume` finds it.
    public static void CopyTranscript(StoppedSession s, string targetDir)
    {
        if (Path.GetFullPath(targetDir).TrimEnd('\\').Equals(Path.GetFullPath(s.ConfigDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        var project = Path.GetFileName(Path.GetDirectoryName(s.Transcript)!);
        var dest = Path.Combine(targetDir, "projects", project);
        Directory.CreateDirectory(dest);
        File.Copy(s.Transcript, Path.Combine(dest, Path.GetFileName(s.Transcript)), overwrite: true);
        var extra = Path.Combine(Path.GetDirectoryName(s.Transcript)!, s.SessionId);
        if (Directory.Exists(extra)) CopyDir(extra, Path.Combine(dest, s.SessionId));
    }

    /// Ends the stopped claude, lets a `cswap run` wrapper around it exit on its own (it seals the profile's
    /// login), then closes the shell that held it, so the window it sat in goes away.
    public static async Task CloseAsync(int pid)
    {
        var chain = new List<(int Pid, string Name)>();
        for (var (p, guard) = (pid, 0); guard < 4 && ParentOf(p) is { } parent; p = parent, guard++)
        {
            var name = NameOf(parent);
            if (name is not ("cswap" or "cmd" or "powershell" or "pwsh")) break;
            chain.Add((parent, name));
        }
        using (var claude = Process.GetProcessById(pid)) { claude.Kill(); await claude.WaitForExitAsync(new CancellationTokenSource(10_000).Token); }
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

    static bool IsLiveClaude(int pid, string? procStart)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)) return false;
            // A recycled pid: the record names the process it was written by.
            return procStart == null || !long.TryParse(procStart, out var ft)
                || Math.Abs(p.StartTime.ToUniversalTime().ToFileTimeUtc() - ft) < TimeSpan.TicksPerSecond * 2;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    static string? TranscriptOf(string dir, string sessionId)
    {
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
        try { using var p = Process.GetProcessById(pid); return p.ProcessName.ToLowerInvariant(); }
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
}
