namespace Clautoswap;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Started by the agents terminal's Claude Code as its clautoswap tools; talks to the running tray.
        if (args is ["agents-mcp", ..]) { Environment.ExitCode = AgentsLink.ServeMcp(args); return; }

        var isolated = Environment.GetEnvironmentVariable("CLAUTOSWAP_DATA") is { Length: > 0 } ? "-" + AgentStore.KeyFor(DockSettings.Dir) : "";
        using var mutex = new Mutex(true, @"Local\Clautoswap-" + Environment.UserName + isolated, out var first);
        if (!first) return; // already running in the tray

        if (!MigrateFromAutodock()) return;

        // A bug in a UI handler must never take the tray down silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Engine engine;
        try { engine = Engine.Locate(); }
        catch (EngineException e)
        {
            MessageBox.Show(e.Message, "clautoswap", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrayApp(engine, minimized: args.Contains("--minimized")));
    }

    // One-time carry-over from the pre-rename name (autodock): settings folder and autostart entry.
    // False when the old tray is still running: two trays must never run the same agents.
    static bool MigrateFromAutodock()
    {
        if (Environment.GetEnvironmentVariable("CLAUTOSWAP_DATA") is { Length: > 0 }) return true; // isolated instance
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "autodock");
        var oldRun = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("autodock") is string;
        if (!Directory.Exists(old) && !oldRun) return true;
        if (System.Diagnostics.Process.GetProcessesByName("autodock").Length > 0)
        {
            MessageBox.Show("The old autodock tray is still running. Quit it (tray icon, Exit), then start clautoswap again: it carries over your settings and agents.",
                "clautoswap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        // Workers outlive the tray that started them, and work inside the old folder: it cannot move under them, and
        // clautoswap must not start beside them without their state.
        if (AgentStore.Running(Path.Combine(old, "agents")))
        {
            MessageBox.Show("Agents the old autodock tray started are still working. Start clautoswap again once they finish: it then carries over your settings and agents.",
                "clautoswap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        // The agents' state moves whole or not at all, before anything else: left behind, a new empty state would hide it for good.
        var (oldAgents, agents) = (Path.Combine(old, "agents"), Path.Combine(DockSettings.Dir, "agents"));
        if (Directory.Exists(oldAgents) && !(Directory.Exists(agents) && Directory.EnumerateFileSystemEntries(agents).Any()))
        {
            try
            {
                Directory.CreateDirectory(DockSettings.Dir);
                if (Directory.Exists(agents)) Directory.Delete(agents);
                Directory.Move(oldAgents, agents);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log(e);
                MessageBox.Show("Something still has a file open in the old autodock agents folder (a worktree in an editor or a shell). Close it, then start clautoswap again: it carries over your settings and agents.",
                    "clautoswap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
        }
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(DockSettings.Dir)) Directory.Move(old, DockSettings.Dir);
            // A clautoswap folder that already exists (say, only a log from an earlier start, or what moved before an entry
            // was locked) is not a reason to leave everything behind: move the old entries in beside what is there.
            else if (Directory.Exists(old))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(old))
                {
                    var dest = Path.Combine(DockSettings.Dir, Path.GetFileName(entry));
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    try { if (Directory.Exists(entry)) Directory.Move(entry, dest); else File.Move(entry, dest); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); } // one locked entry stays; the rest move
                }
                if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); }
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("autodock") is string) { run.DeleteValue("autodock"); if (!Startup.IsEnabled()) Startup.Set(true); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); }
        return true;
    }

    static string LogPath => Path.Combine(DockSettings.Dir, "clautoswap.log");

    static void Report(Exception? e, bool fatal)
    {
        Log(e);
        MessageBox.Show($"clautoswap hit an unexpected error{(fatal ? " and has to close" : "")}.\n\n" +
                        $"{Engine.Redact(e?.Message ?? "unknown")}\n\nDetails: {LogPath}",
            "clautoswap", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// Local only, redacted, capped at ~1 MB.
    internal static void Log(Exception? e) => Log(e?.ToString() ?? "unknown");

    internal static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(DockSettings.Dir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {Engine.Redact(text)}\n\n");
        }
        catch { }
    }
}
