using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchGoblins;

public sealed class PolicyException(string message) : Exception(message);

/// One verification command, run when a change touches one of its paths (all paths when none are given) on its
/// operating system (any when none is given).
public sealed record PolicyCheck(string Run, List<string>? Paths = null, string? Os = null);

/// A repository's restrictions: the shared policy below always applies, and a repository can only add to it, from
/// `.patch-goblins.json` on its base branch (what the owner merged, never a worktree's copy) and from the owner's
/// local file outside Git (`policies\<folder>.json` in the Patch Goblins folder), for repositories that should not
/// change. Every field is optional.
public sealed class RepoPolicy
{
    /// The policy format this build reads; a newer one is refused, never half-applied.
    public int Version { get; set; } = 1;
    /// `branch`, or `branch@sha` to pin it: agents refuse to start while the branch has moved past the commit.
    public string? Base { get; set; }
    /// "ask": every push waits for the owner's yes (a push starts the repository's remote CI).
    public string? Push { get; set; }
    /// Paths whose changes get an adversarial review and wait for the owner before they are pushed.
    public List<string> Sensitive { get; set; } = [];
    /// More Claude Code tool rules agents may not use, such as `Bash(ddc *)`. Rules match command text, so they stop
    /// mistakes, not a determined program (docs/agents.md, isolation).
    public List<string> Deny { get; set; } = [];
    public List<PolicyCheck> Checks { get; set; } = [];

    public (string Branch, string? Pin) BaseRef =>
        Base?.Split('@', 2) is [var b, var sha] ? (b, sha) : (Base ?? "", null);
}

public static class Policy
{
    public const int Version = 1;
    public const string FileName = ".patch-goblins.json";

    /// Shared policy v1: changes that can run code on the owner's machine or in CI, change what agents are told, or hold
    /// secrets. Each waits for the owner before it is pushed.
    public static readonly string[] Shared =
    [
        ".github/**", FileName, ".claude/**", ".mcp.json", "AGENTS.md", "CLAUDE.md", "CODEOWNERS",
        ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "id_rsa*", "*.tfvars", ".npmrc", ".pypirc",
    ];

    static readonly string[] Pushes = ["auto", "ask"];

    public static RepoPolicy Parse(string json, string source)
    {
        RepoPolicy? p;
        try { p = JsonSerializer.Deserialize<RepoPolicy>(json, Json.Options); }
        catch (JsonException e) { throw new PolicyException($"{source} is not valid JSON: {e.Message}"); }
        if (p == null) throw new PolicyException($"{source} is empty");
        if (p.Version > Version) throw new PolicyException($"{source} is policy version {p.Version}; this Patch Goblins reads version {Version}. Update Patch Goblins.");
        if (p.Push != null && !Pushes.Contains(p.Push)) throw new PolicyException($"{source}: push must be \"auto\" or \"ask\"");
        if (p.Checks.Any(c => string.IsNullOrWhiteSpace(c.Run))) throw new PolicyException($"{source}: every check needs a `run` command");
        if (p.Deny.Any(d => !Regex.IsMatch(d, @"^\w+(\(.+\))?$"))) throw new PolicyException($"{source}: deny entries are Claude Code tool rules, like Bash(ddc *)");
        return p;
    }

    /// Both restrict: lists add up, "ask" wins, the owner's local base wins over the repository's.
    public static RepoPolicy Merge(RepoPolicy repo, RepoPolicy? local) => local == null ? repo : new()
    {
        Version = Math.Max(repo.Version, local.Version),
        Base = local.Base ?? repo.Base,
        Push = repo.Push == "ask" || local.Push == "ask" ? "ask" : local.Push ?? repo.Push,
        Sensitive = [.. repo.Sensitive.Union(local.Sensitive)],
        Deny = [.. repo.Deny.Union(local.Deny)],
        Checks = [.. repo.Checks.Concat(local.Checks).DistinctBy(c => (c.Run, c.Os, Describe(c.Paths)))],
    };

    public static string LocalPath(string repoPath) =>
        Path.Combine(DockSettings.Dir, "policies", Path.GetFileName(Path.GetFullPath(repoPath).TrimEnd('\\', '/')) + ".json"); // ponytail: by folder name; two clones of one name share it

    /// The repository's policy at `gitRef` merged with the owner's local one. Throws PolicyException when either is
    /// unreadable: an unreadable restriction must stop the work, not be skipped.
    public static async Task<RepoPolicy> LoadAsync(Cli cli, string repoPath, string gitRef, string? localPath = null)
    {
        var repo = new RepoPolicy();
        if ((await cli.GitAsync(repoPath, "ls-tree", "--name-only", gitRef, "--", FileName)).Out.Trim().Length > 0)
        {
            var show = await cli.GitAsync(repoPath, "show", $"{gitRef}:{FileName}");
            repo = show.Ok ? Parse(show.Out, $"{FileName} on {gitRef}") : throw new PolicyException($"could not read {FileName} on {gitRef}: {show.Why}");
        }
        localPath ??= LocalPath(repoPath);
        return Merge(repo, File.Exists(localPath) ? Parse(File.ReadAllText(localPath), localPath) : null);
    }

    /// Changed files the shared or the repository's policy calls sensitive.
    public static List<string> SensitiveIn(RepoPolicy policy, IEnumerable<string> files) =>
        files.Where(f => Shared.Concat(policy.Sensitive).Any(g => Glob(g, f))).ToList();

    /// The checks that cover these changed files, and those that do but need another operating system.
    public static (List<PolicyCheck> Run, List<PolicyCheck> OtherOs) Select(IEnumerable<PolicyCheck> checks, IReadOnlyCollection<string> files, string os = "windows")
    {
        var covering = checks.Where(c => c.Paths is not { Count: > 0 } || files.Any(f => c.Paths.Any(g => Glob(g, f)))).ToList();
        var here = covering.Where(c => c.Os == null || c.Os.Equals(os, StringComparison.OrdinalIgnoreCase)).ToList();
        return (here, covering.Except(here).ToList());
    }

    /// The checks as the owner confirms them: one line each.
    public static string Describe(IEnumerable<PolicyCheck> checks) =>
        string.Join("\n", checks.Select(c => $"`{c.Run}`" + (c.Paths is { Count: > 0 } ? $" when {Describe(c.Paths)} change" : "") + (c.Os is { } o ? $" ({o} only)" : "")));

    static string Describe(List<string>? paths) => paths is { Count: > 0 } ? string.Join(", ", paths) : "";

    /// GitHub skips its workflows for a head commit that says so: such a push proves nothing.
    static readonly Regex SkipCi = new(@"\[(skip ci|ci skip|no ci|skip actions|actions skip)\]|^skip-checks:\s*true\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static bool SkipsCi(string commitMessages) => SkipCi.IsMatch(commitMessages);

    /// gitignore-style: `**` spans folders, `*` and `?` stay within one, a pattern without `/` matches at any depth.
    /// Case-insensitive, as Windows paths are.
    public static bool Glob(string pattern, string path)
    {
        path = path.Replace('\\', '/');
        var p = pattern.Replace('\\', '/').TrimStart('/');
        var rx = Regex.Escape(p).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return Regex.IsMatch(path, (p.Contains('/') ? "^" : "^(.*/)?") + rx + "$", RegexOptions.IgnoreCase);
    }
}
