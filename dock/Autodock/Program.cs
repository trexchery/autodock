namespace Autodock;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Autodock-" + Environment.UserName, out var first);
        if (!first) return; // already running in the tray

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
            MessageBox.Show(e.Message, "autodock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrayApp(engine, minimized: args.Contains("--minimized")));
    }

    static string LogPath => Path.Combine(DockSettings.Dir, "autodock.log");

    static void Report(Exception? e, bool fatal)
    {
        Log(e);
        MessageBox.Show($"autodock hit an unexpected error{(fatal ? " and has to close" : "")}.\n\n" +
                        $"{Engine.Redact(e?.Message ?? "unknown")}\n\nDetails: {LogPath}",
            "autodock", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
