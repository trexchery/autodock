using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clautoswap;

/// Where a piece of work stands. Queued → Working → Checking → Open (pull request awaiting review)
/// → Done. NeedsOwner parks it with a question while other work goes on; feedback on an open pull
/// request queues it again.
public enum ItemState { Queued, Working, Checking, Open, NeedsOwner, Done }

/// One unit of work: a GitHub issue, or a request the owner made in the agents terminal.
public sealed class WorkItem
{
    public string Id { get; set; } = "";
    public int? Issue { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int Rank { get; set; }
    public ItemState State { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    /// The owner-facing line: a question, why it stopped, or what was done.
    public string? Note { get; set; }
    /// The last run's own account of its change: the pull request's description.
    public string? Summary { get; set; }
    /// When the question was asked (answers on the issue after this resolve it).
    public DateTimeOffset? AskedAt { get; set; }
    /// The owner's answer or instruction, handed to the next run once.
    public string? Guidance { get; set; }
    /// What the next run must fix (verification output, review findings, PR checks and comments).
    public string? Followup { get; set; }
    public string? Branch { get; set; }
    public string? Worktree { get; set; }
    public int? Pr { get; set; }
    public string? PrUrl { get; set; }
    /// Its pull request was merged (a closed one was not).
    public bool Merged { get; set; }
    /// PR feedback after this moment is new.
    public DateTimeOffset? PrSeen { get; set; }
    public int Failures { get; set; }
    public int Followups { get; set; }
    /// The next run reviews the change instead of working on it.
    public bool ReviewNext { get; set; }
    public int Reviews { get; set; }
    /// The commit the last review approved; a review stands only while HEAD is still this commit.
    public string? ReviewedHead { get; set; }
    /// Findings the last allowed review still had, carried into the pull request for the owner.
    public string? ReviewNotes { get; set; }
    /// Why it waits for the owner before a push ("review", "workflow"); an answer without text approves it.
    public string? HoldFor { get; set; }
    /// The owner cleared the next push.
    public bool PushApproved { get; set; }
    /// The implementing conversation, resumed when its account is available again.
    public Session? Session { get; set; }
    /// The latest process (work or review).
    public Run? Run { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Label => Issue is { } n ? $"#{n} {Title}" : $"{Id} {Title}";
    /// Started and not yet handed to review or parked: the repository's one active item.
    [JsonIgnore] public bool InFlight => State is ItemState.Working or ItemState.Checking || (State == ItemState.Queued && Worktree != null && Pr == null);
}

public sealed record Session(string Id, string Email, string Org)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// A note in the agents' shared memory. Owner notes are the owner's own words or decisions and bind every
/// agent; agent notes are what runs learned: hints to verify.
public sealed record Note(string Text, string Source, DateTimeOffset At, bool Owner = false)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// A worker process: enough to re-attach after a restart.
public sealed class Run
{
    public int Attempt { get; set; }
    /// "work" or "review".
    public string Kind { get; set; } = "work";
    public string Job { get; set; } = "";
    public int Pid { get; set; }
    public long Created { get; set; }
    public string SessionId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Org { get; set; } = "";
    /// The worktree's HEAD when the run started.
    public string Head { get; set; } = "";
    public DateTimeOffset Started { get; set; }
    /// The process may still be running (cleared once its outcome is handled).
    public bool Live { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// A repository the owner authorized for continuous operation.
public sealed class RepoAgents
{
    public string Path { get; set; } = "";
    public string Base { get; set; } = "main";
    /// Command that must pass in the worktree before a branch is pushed.
    public string? Verify { get; set; }
    public bool Authorized { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public bool Paused { get; set; }
    /// Why the repository as a whole is held up (unreadable issues, a failing verification baseline).
    public string? Note { get; set; }
    public DateTimeOffset? LastScan { get; set; }
    public List<int> Skipped { get; set; } = [];
    public List<WorkItem> Items { get; set; } = [];
    public int NextLocal { get; set; } = 1;
    /// What the owner and runs noted about this repository.
    public List<Note> Memory { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Leaf => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;

    /// Short stable key for folder and job names.
    [JsonIgnore] public string Key => AgentStore.KeyFor(Path);

    [JsonIgnore] public bool Running => Authorized && !Paused;
}

public sealed class AgentsState
{
    public List<RepoAgents> Repos { get; set; } = [];
    /// Notes that hold in every repository.
    public List<Note> Memory { get; set; } = [];
    /// Fields an older or newer build wrote, here and on every level below: saved back as they were,
    /// so neither build erases the other's.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// state.json plus each run's files, under the tray's private folder. The tray is the only writer
/// (single instance); every change is a whole-file atomic replace.
public sealed class AgentStore(string root)
{
    public string Root { get; } = root;
    public static string DefaultRoot => System.IO.Path.Combine(DockSettings.Dir, "agents");

    string StatePath => System.IO.Path.Combine(Root, "state.json");

    static readonly JsonSerializerOptions Options = new(Json.Options)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AgentsState Load()
    {
        try { return JsonSerializer.Deserialize<AgentsState>(File.ReadAllText(StatePath), Options) ?? new(); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection instead of silently overwriting it.
            try { File.Copy(StatePath, StatePath + ".corrupt", overwrite: true); } catch (IOException) { }
            return new();
        }
    }

    public void Save(AgentsState s) => AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(s, Options));

    /// A repository was left running, or a worker may still be: the tray starts agents, so open terminals link up
    /// and runs without one stop.
    public static bool HasWork(string root) =>
        new AgentStore(root).Load().Repos.Any(r => r.Authorized || r.Items.Any(i => i.Run is { Live: true }));

    public string RunDir(RepoAgents repo, WorkItem item) => System.IO.Path.Combine(Root, "runs", $"{repo.Key}-{item.Id}");

    public string Worktree(RepoAgents repo, WorkItem item) => System.IO.Path.Combine(Root, "wt", $"{repo.Key}-{item.Id}");

    public string RunFile(RepoAgents repo, WorkItem item, int attempt, string kind) =>
        System.IO.Path.Combine(RunDir(repo, item), $"{kind}-{attempt}.{(kind == "out" ? "jsonl" : kind == "prompt" ? "md" : "log")}");

    public static string KeyFor(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant())))[..6].ToLowerInvariant();
}
