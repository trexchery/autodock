using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PatchGoblins;

/// The agents terminal: an ordinary Claude Code session in the repository, opened like any other
/// Patch Goblins terminal (`cswap run`, so the folder's assigned account or the default login), plus
/// session-only files passed on the command line. Nothing in the owner's own settings changes.
///   settings.json  the `Patch Goblins · agents` status line, Patch Goblins' theme, and permission for its read-only tools
///   mcp.json       the Patch Goblins MCP bridge for this repository
///   statusline.sh  the owner's own status line (if any), with the label on its last row
///   theme/         a theme-only plugin: the Patch Goblins theme (green mascot and accent) on the owner's own built-in theme
public static class AgentsTerminal
{
    public const string Label = "Patch Goblins · agents";

    /// Seconds between Claude's own status-line refreshes: each one moves the gradient a step.
    public const int RefreshSeconds = 5;

    /// The green loop in 72 steps. Neighbouring letters sit 3 steps apart, and each refresh moves the
    /// gradient 9 steps, about three letters: a step that reads as motion (a full loop in 40 s).
    public const int Hues = 72;
    const int FrameStep = 9, LetterStep = 3;

    /// The built-in themes the Patch Goblins theme can sit on: the owner's own, so only the greens change.
    /// Any other (auto, a custom theme, none set) gets a dark base.
    static readonly string[] BuiltInThemes = ["dark", "light", "dark-daltonized", "light-daltonized", "dark-ansi", "light-ansi"];

    static string Dir(string root, string repo) => Path.Combine(root, "terminal", AgentStore.KeyFor(repo));

    public static void Open(Engine engine, string root, string repo, bool restoreRc, string state)
    {
        var dir = Dir(root, repo);
        Directory.CreateDirectory(dir);
        SetState(root, repo, state);
        foreach (var old in Directory.GetFiles(dir, "user-statusline*"))
            try { File.Delete(old); } catch (IOException) { } // a terminal still open just renders its own again
        var script = Path.Combine(dir, "statusline.sh");
        // LF only: Git Bash runs it, and a stray CR would end up inside every token.
        File.WriteAllText(script, StatusScript(UserStatusLine(repo), Posix(Path.Combine(dir, "state")), Posix(Path.Combine(dir, "user-statusline"))).ReplaceLineEndings("\n"));
        var plugin = WriteTheme(dir, UserSetting(repo, null, "theme") is JsonValue v && v.TryGetValue<string>(out var theme) ? theme : null);
        var settings = Path.Combine(dir, "settings.json");
        var session = new JsonObject
        {
            // Sourced, not started: the status line already runs in a Git Bash; no second process.
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ". " + BashQuote(Posix(script)), ["refreshInterval"] = RefreshSeconds },
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("mcp__patch-goblins__agents_status", "mcp__patch-goblins__agents_details") },
        };
        session["theme"] = $"custom:{ThemePlugin}:{ThemeSlug}";
        File.WriteAllText(settings, session.ToJsonString());
        var mcp = Path.Combine(dir, "mcp.json");
        File.WriteAllText(mcp, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["patch-goblins"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = Environment.ProcessPath,
                    ["args"] = new JsonArray("agents-mcp", "--repo", repo),
                },
            },
        }.ToJsonString());
        var leaf = Path.GetFileName(repo.TrimEnd('\\', '/'));
        engine.LaunchConsole($"{Label} · {leaf}", repo, Launch.Run(null, restoreRc,
            ["--settings", settings, "--mcp-config", mcp, "--plugin-dir", plugin]));
    }

    const string ThemePlugin = "patch-goblins-agents", ThemeSlug = "patch-goblins";

    /// The Patch Goblins theme, a theme-only plugin loaded for this session alone with --plugin-dir and selected through the
    /// session's --settings: the owner's own built-in theme with a green mascot and green brand accent (the spinner and the
    /// assistant label), as if picked in /theme. Nothing goes into ~/.claude and the owner's theme setting is never touched,
    /// so every other terminal keeps its look. The mascot's pixels are Claude Code's own: only its color is a theme token.
    internal static string WriteTheme(string dir, string? ownerTheme)
    {
        var baseTheme = BuiltInThemes.Contains(ownerTheme) ? ownerTheme! : "dark";
        var (body, accent, shimmer) = baseTheme.StartsWith("light") ? ("#2E7D32", "#2E7D32", "#43A047") : ("#5BD25B", "#6FD36F", "#A6EBA6");
        var plugin = Path.Combine(dir, "theme");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(plugin, "themes"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), new JsonObject
        {
            ["name"] = ThemePlugin,
            ["version"] = "1.0.0",
            ["description"] = "Patch Goblins agents terminal: a green mascot and accent",
        }.ToJsonString());
        File.WriteAllText(Path.Combine(plugin, "themes", ThemeSlug + ".json"), new JsonObject
        {
            ["name"] = "Patch Goblins",
            ["base"] = baseTheme,
            ["overrides"] = new JsonObject { ["clawd_body"] = body, ["claude"] = accent, ["claudeShimmer"] = shimmer },
        }.ToJsonString());
        return plugin;
    }

    /// What the label says after its gradient: working, needs you, paused or off (idle says nothing).
    public static string StateOf(RepoAgents? repo) =>
        repo is not { Authorized: true } ? "off"
        : repo.Paused ? "paused"
        : repo.Items.Any(i => i.State == ItemState.NeedsOwner) || repo.Goals.Any(g => g.State == GoalState.NeedsOwner) ? "attention"
        : repo.Items.Any(i => i.State is ItemState.Working or ItemState.Checking) || repo.Goals.Any(g => g.Run is { Live: true }) ? "working"
        : "idle";

    public static void SetState(string root, string repo, string state)
    {
        var dir = Dir(root, repo);
        if (!Directory.Exists(dir)) return; // no terminal was ever opened for it
        try { File.WriteAllText(Path.Combine(dir, "state"), state + "\n"); }
        catch (IOException) { }
    }

    /// The status line the owner already has for this folder, highest-precedence file first.
    internal static string? UserStatusLine(string repo, string? home = null)
    {
        try
        {
            return UserSetting(repo, home, "statusLine") is JsonObject s && s["type"]?.GetValue<string>() == "command"
                   && s["command"]?.GetValue<string>() is { Length: > 0 } c ? c : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
    }

    /// One of the owner's Claude Code settings for this folder, highest-precedence file first.
    internal static JsonNode? UserSetting(string repo, string? home, string key)
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
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject o && o[key] is { } value) return value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    /// Bash builtins only for the label: the frame follows the clock, so each refresh (every few seconds,
    /// and on every message) shifts the gradient one step. The whole line is one gradient, the state
    /// word included; the dots stay neutral. Nothing runs between refreshes.
    /// The owner's own status line re-runs only when the conversation changed; idle refreshes reuse it
    /// (one cache per session: two terminals on one repository never show each other's).
    internal static string StatusScript(string? userCommand, string statePath, string cachePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Patch Goblins agents label; rewritten whenever the agents terminal opens.");
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
        sb.AppendLine("__ad_p=(" + string.Join(' ', Palette().Select(c => $"'{c}'")) + ")");
        sb.AppendLine("case $__ad_s in");
        foreach (var (state, text) in Lines)
            sb.AppendLine($"  {state}) __ad_c=({string.Join(' ', text.Select(c => BashQuote(c.ToString())))}) ;;");
        sb.AppendLine("esac");
        sb.AppendLine("printf -v __ad_t '%(%s)T' -1");
        sb.AppendLine($"__ad_f=$(( __ad_t / {RefreshSeconds} * {FrameStep} ))");
        sb.AppendLine("__ad_o=");
        sb.AppendLine("""for __ad_i in "${!__ad_c[@]}"; do""");
        sb.AppendLine("  case ${__ad_c[__ad_i]} in");
        sb.AppendLine("    ' ') __ad_o+=' ' ;;");
        sb.AppendLine("""    '·') __ad_o+=$'\e[38;2;128;128;128m·' ;;""");
        sb.AppendLine($$"""    *) __ad_o+=$'\e[38;2;'"${__ad_p[(__ad_f + __ad_i * {{LetterStep}}) % {{Hues}}]}m${__ad_c[__ad_i]}" ;;""");
        sb.AppendLine("  esac");
        sb.AppendLine("done");
        sb.AppendLine("""printf '%s\e[0m\n' "$__ad_o" """);
        return sb.ToString();
    }

    /// The line for each state; working, needs you, paused and off say so, idle shows the label alone
    /// (also for a state file from an older Patch Goblins).
    internal static readonly (string State, string Text)[] Lines =
    [
        ("working", Label + " · working"),
        ("attention", Label + " · needs you"),
        ("paused", Label + " · paused"),
        ("off", Label + " · off"),
        ("*", Label),
    ];

    /// The goblin greens as `r;g;b`, from lime to spring green and back so the loop has no seam: soft enough to read on dark
    /// and light terminals alike.
    internal static IEnumerable<string> Palette() =>
        Enumerable.Range(0, Hues).Select(k => Hsl(80 + 85 * (1 - Math.Cos(2 * Math.PI * k / Hues)) / 2, 0.55, 0.68)).Select(c => $"{c.Item1};{c.Item2};{c.Item3}");


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
