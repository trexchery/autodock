using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Autodock;

/// The agents terminal: an ordinary Claude Code session in the repository, opened like any other
/// autodock terminal (`cswap run`, so the folder's assigned account or the default login), plus three
/// session-only files passed on the command line. Nothing in the owner's own settings changes.
///   settings.json  the `autodock · agents` status line, and permission for autodock's read-only tools
///   mcp.json       the autodock MCP bridge for this repository
///   statusline.sh  the owner's own status line (if any), with the label on its last row
public static class AgentsTerminal
{
    public const string Label = "autodock · agents";

    /// Seconds between Claude's own status-line refreshes: each one moves the gradient a step.
    public const int RefreshSeconds = 5;

    public const int FrameCount = 18;

    static string Dir(string root, string repo) => Path.Combine(root, "terminal", AgentStore.KeyFor(repo));

    public static void Open(Engine engine, string root, string repo, bool restoreRc, bool running)
    {
        var dir = Dir(root, repo);
        Directory.CreateDirectory(dir);
        SetRunning(root, repo, running);
        foreach (var old in Directory.GetFiles(dir, "user-statusline*"))
            try { File.Delete(old); } catch (IOException) { } // a terminal still open just renders its own again
        var script = Path.Combine(dir, "statusline.sh");
        // LF only: Git Bash runs it, and a stray CR would end up inside every token.
        File.WriteAllText(script, StatusScript(UserStatusLine(repo), Posix(Path.Combine(dir, "state")), Posix(Path.Combine(dir, "user-statusline"))).ReplaceLineEndings("\n"));
        var settings = Path.Combine(dir, "settings.json");
        File.WriteAllText(settings, new JsonObject
        {
            // Sourced, not started: the status line already runs in a Git Bash; no second process.
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ". " + BashQuote(Posix(script)), ["refreshInterval"] = RefreshSeconds },
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("mcp__autodock__agents_status", "mcp__autodock__agents_details") },
        }.ToJsonString());
        var mcp = Path.Combine(dir, "mcp.json");
        File.WriteAllText(mcp, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["autodock"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = Environment.ProcessPath,
                    ["args"] = new JsonArray("agents-mcp", "--repo", repo),
                },
            },
        }.ToJsonString());
        var leaf = Path.GetFileName(repo.TrimEnd('\\', '/'));
        engine.LaunchConsole($"{Label} · {leaf}", repo, Launch.Run(null, restoreRc, "--settings", settings, "--mcp-config", mcp));
    }

    /// The label moves while agents run here and rests in grey otherwise.
    public static void SetRunning(string root, string repo, bool running)
    {
        var dir = Dir(root, repo);
        if (!Directory.Exists(dir)) return; // no terminal was ever opened for it
        try { File.WriteAllText(Path.Combine(dir, "state"), running ? "on\n" : "off\n"); }
        catch (IOException) { }
    }

    /// The status line the owner already has for this folder, highest-precedence file first.
    internal static string? UserStatusLine(string repo, string? home = null)
    {
        home ??= StoreSecurity.UserProfile;
        foreach (var file in new[]
                 {
                     Path.Combine(repo, ".claude", "settings.local.json"),
                     Path.Combine(repo, ".claude", "settings.json"),
                     Path.Combine(home, ".claude", "settings.json"),
                 })
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file))?["statusLine"] is JsonObject s
                    && s["type"]?.GetValue<string>() == "command" && s["command"]?.GetValue<string>() is { Length: > 0 } c)
                    return c;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        }
        return null;
    }

    /// Bash, and builtins only for the label: the frame follows the clock, so each refresh (every few
    /// seconds, and on every message) shifts the gradient one step. No loop, nothing between refreshes.
    /// The owner's own status line re-runs only when the conversation changed; idle refreshes reuse it
    /// (one cache per session: two terminals on one repository never show each other's).
    internal static string StatusScript(string? userCommand, string statePath, string cachePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# autodock agents label; rewritten whenever the agents terminal opens.");
        sb.AppendLine("IFS= read -r -d '' __ad_in");
        if (userCommand != null)
        {
            sb.AppendLine("__ad_cache=" + BashQuote(cachePath));
            sb.AppendLine("""[[ $__ad_in =~ \"session_id\":\"([^\"]*)\" ]] && __ad_cache+=-${BASH_REMATCH[1]}""");
            sb.AppendLine("__ad_tr=");
            sb.AppendLine("""[[ $__ad_in =~ \"transcript_path\":\"([^\"]*)\" ]] && __ad_tr=${BASH_REMATCH[1]//\\\\//}""");
            sb.AppendLine("""if [[ -z $__ad_tr || ! -f $__ad_cache || $__ad_tr -nt $__ad_cache ]]; then""");
            sb.AppendLine("""  __ad_out=$(printf '%s' "$__ad_in" | eval """ + BashQuote(userCommand) + " 2>/dev/null)");
            sb.AppendLine("""  printf '%s' "$__ad_out" > "$__ad_cache" 2>/dev/null""");
            sb.AppendLine("else");
            sb.AppendLine("""  IFS= read -r -d '' __ad_out < "$__ad_cache" """);
            sb.AppendLine("fi");
            sb.AppendLine("""[ -n "$__ad_out" ] && printf '%s\n' "$__ad_out" """);
        }
        sb.AppendLine("__ad_s=off");
        sb.AppendLine("read -r __ad_s 2>/dev/null < " + BashQuote(statePath));
        sb.AppendLine("""if [ "$__ad_s" = on ]; then""");
        sb.AppendLine("  __ad_f=(");
        foreach (var frame in Frames()) sb.AppendLine($"    $'{frame}'");
        sb.AppendLine("  )");
        sb.AppendLine("  printf -v __ad_t '%(%s)T' -1");
        sb.AppendLine($$"""  printf '%s\n' "${__ad_f[(__ad_t / {{RefreshSeconds}}) % {{FrameCount}}]}" """);
        sb.AppendLine("else");
        sb.AppendLine($"  printf '%s\\n' $'{Rest()}'");
        sb.AppendLine("fi");
        return sb.ToString();
    }

    /// A soft rainbow across the label, each frame 20° further round the wheel (a full turn in 90 s
    /// of refreshes). Pastel, so it reads on dark and light terminals alike; the dot stays neutral.
    internal static IEnumerable<string> Frames()
    {
        for (var f = 0; f < FrameCount; f++)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Label.Length; i++)
            {
                var c = Label[i];
                if (c == ' ') { sb.Append(' '); continue; }
                var (r, g, b) = c == '·' ? (128, 128, 128) : Hsl((f * 20 + i * 13) % 360, 0.55, 0.68);
                sb.Append($@"\e[38;2;{r};{g};{b}m{c}");
            }
            yield return sb.Append(@"\e[0m").ToString();
        }
    }

    static string Rest() => $@"\e[38;2;120;120;120m{Label}\e[0m";

    internal static (int, int, int) Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// Git Bash eats backslashes: C:\x\y becomes C:/x/y.
    static string Posix(string path) => path.Replace('\\', '/');
}
