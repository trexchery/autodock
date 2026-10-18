using System.Text.Json;
using Microsoft.Win32;

namespace Autodock;

/// Dock-only preferences. Auto-switch thresholds live in the engine's
/// settings.json (`cswap config`) so the CLI and the dock never disagree.
public sealed class DockSettings
{
    public bool AutoSwitch { get; set; }
    public bool AutoSwitchPolicyAccepted { get; set; }
    public int RefreshSeconds { get; set; } = 180;

    public const int MinRefreshSeconds = 60;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "autodock");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static DockSettings Load(string? path = null)
    {
        try
        {
            var s = JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(path ?? FilePath)) ?? new();
            s.RefreshSeconds = Math.Max(MinRefreshSeconds, s.RefreshSeconds);
            return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string? path = null) =>
        AtomicFile.WriteAllText(path ?? FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public static class AtomicFile
{
    /// Write to a sibling temp file, then rename over the target, so a crash
    /// never leaves a half-written file.
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "autodock";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
