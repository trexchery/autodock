namespace ClaudeDock;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ClaudeDock-" + Environment.UserName, out var first);
        if (!first) return; // already running in the tray

        ApplicationConfiguration.Initialize();
        Engine engine;
        try { engine = Engine.Locate(); }
        catch (EngineException e)
        {
            MessageBox.Show(e.Message, "Claude Dock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrayApp(engine, minimized: args.Contains("--minimized")));
    }
}
