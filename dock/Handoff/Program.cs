namespace Handoff;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Started by the agents terminal's Claude Code as its handoff tools; talks to the running tray.
        if (args is ["agents-mcp", ..]) { Environment.ExitCode = AgentsLink.ServeMcp(args); return; }

        var isolated = Environment.GetEnvironmentVariable("HANDOFF_DATA") is { Length: > 0 } ? "-" + AgentStore.KeyFor(DockSettings.Dir) : "";
        using var mutex = new Mutex(true, @"Local\Handoff-" + Environment.UserName + isolated, out var first);
        if (!first) return; // already running in the tray

        if (!MigrateFromOldNames()) return;

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
            MessageBox.Show(e.Message, "handoff", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrayApp(engine, minimized: args.Contains("--minimized")));
    }

    // One-time carry-over from the pre-rename names (clautoswap, then autodock): settings folder and autostart entry.
    // False when an old tray is still running: two trays must never run the same agents.
    static bool MigrateFromOldNames()
    {
        if (Environment.GetEnvironmentVariable("HANDOFF_DATA") is { Length: > 0 }) return true; // isolated instance
        foreach (var name in new[] { "clautoswap", "autodock" })
            if (!MigrateFrom(name)) return false;
        return true;
    }

    static bool MigrateFrom(string name)
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
        var oldRun = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue(name) is string;
        if (!Directory.Exists(old) && !oldRun) return true;
        if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
        {
            MessageBox.Show($"The old {name} tray is still running. Quit it (tray icon, Exit), then start handoff again: it carries over your settings and agents.",
                "handoff", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(DockSettings.Dir)) Directory.Move(old, DockSettings.Dir);
            // A handoff folder that holds no settings yet (say, only a log from an earlier start) is not a
            // reason to leave everything behind: move the old entries in beside what is there.
            else if (Directory.Exists(old) && !File.Exists(Path.Combine(DockSettings.Dir, "settings.json")))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(old))
                {
                    var dest = Path.Combine(DockSettings.Dir, Path.GetFileName(entry));
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    if (Directory.Exists(entry)) Directory.Move(entry, dest); else File.Move(entry, dest);
                }
                if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old);
            }
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue(name) is string) { run.DeleteValue(name); if (!Startup.IsEnabled()) Startup.Set(true); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); }
        return true;
    }

    static string LogPath => Path.Combine(DockSettings.Dir, "handoff.log");

    static void Report(Exception? e, bool fatal)
    {
        Log(e);
        MessageBox.Show($"handoff hit an unexpected error{(fatal ? " and has to close" : "")}.\n\n" +
                        $"{Engine.Redact(e?.Message ?? "unknown")}\n\nDetails: {LogPath}",
            "handoff", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// Local only, redacted, capped at ~1 MB.
    internal static void Log(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(DockSettings.Dir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {Engine.Redact(e?.ToString() ?? "unknown")}\n\n");
        }
        catch { }
    }
}
