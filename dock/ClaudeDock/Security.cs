using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeDock;

public static class StoreSecurity
{
    // Same resolution as the engine's Path.home() (USERPROFILE first).
    public static string UserProfile =>
        Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } p ? p : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// The engine's store on Windows (paths.get_backup_root).
    public static string BackupRoot => Path.Combine(UserProfile, ".claude-swap-backup");

    /// Replace the directory's DACL with "current user + SYSTEM only", no
    /// inherited entries, propagated to everything below it. Backups and
    /// session profiles inside then never inherit broader profile ACLs.
    public static void Harden(string dir)
    {
        Directory.CreateDirectory(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(sec);
        // Files created by tools that set explicit ACLs keep them; reset children to inherit.
        foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)) continue;
            if (Directory.Exists(f))
            {
                var ds = new DirectorySecurity();
                ds.SetAccessRuleProtection(false, false);
                new DirectoryInfo(f).SetAccessControl(ds);
            }
            else
            {
                var fs = new FileSecurity();
                fs.SetAccessRuleProtection(false, false);
                new FileInfo(f).SetAccessControl(fs);
            }
        }
    }

    /// True when only the current user and SYSTEM hold access entries.
    public static bool IsHardened(string dir)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new DirectoryInfo(dir).GetAccessControl();
        return acl.AreAccessRulesProtected && acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(r => r.IdentityReference.Equals(me) || r.IdentityReference.Equals(system));
    }
}

public static class ClaudeCli
{
    /// Claude Code's executable (native installer, winget, or npm shim).
    public static string? Find() =>
        Engine.FindOnPath("claude.exe")
        ?? new[] { Path.Combine(StoreSecurity.UserProfile, ".local", "bin", "claude.exe") }.FirstOrDefault(File.Exists)
        ?? Engine.FindOnPath("claude.cmd");

    /// Number of running Claude Code processes (default-profile and pinned).
    public static int RunningCount()
    {
        var procs = Process.GetProcessesByName("claude");
        foreach (var p in procs) p.Dispose();
        return procs.Length;
    }

    /// Official `claude auth login` against a throwaway CLAUDE_CONFIG_DIR, then
    /// `cswap enroll` imports it. The default login is never touched.
    public static async Task<string> EnrollAsync(Engine engine)
    {
        var claude = Find() ?? throw new EngineException("Claude Code (claude.exe) not found on PATH.");
        var root = Path.Combine(DockSettings.Dir, "enroll");
        StoreSecurity.Harden(root);
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var psi = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", claude, "auth", "login" } }
                : new ProcessStartInfo(claude) { ArgumentList = { "auth", "login" } };
            psi.UseShellExecute = false;
            psi.CreateNoWindow = false; // visible console: the user completes the official login there
            psi.WorkingDirectory = dir;
            psi.Environment["CLAUDE_CONFIG_DIR"] = dir;
            using (var p = Process.Start(psi) ?? throw new EngineException("could not start Claude Code"))
                await p.WaitForExitAsync();
            if (!File.Exists(Path.Combine(dir, ".credentials.json")))
                throw new EngineException("Login was not completed.");
            var output = await engine.CommandAsync("enroll", dir);
            return output.Trim();
        }
        finally
        {
            // The directory holds a live credential; never leave it behind.
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
