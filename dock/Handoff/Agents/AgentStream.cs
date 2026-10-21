using System.Text;
using System.Text.Json;

namespace Handoff;

/// One thing an agent said or did, condensed from a Claude Code `--output-format stream-json` line.
public abstract record StreamEvent;
public sealed record InitEvent(string? SessionId, string? Model) : StreamEvent;
public sealed record TextEvent(string Text) : StreamEvent;
public sealed record ToolEvent(string Name, string? Target, string? Id = null) : StreamEvent;
/// A tool call's result came back (`user` message content).
public sealed record ToolDoneEvent(string Id) : StreamEvent;
/// Claude's own list of background tasks (shells, subagents, workflows) changed: how many are still running.
public sealed record BackgroundEvent(int Running) : StreamEvent;
public sealed record TurnEvent(string MessageId, long OutputTokens) : StreamEvent;
public sealed record ResultEvent(bool IsError, string? Subtype, string Text, double? CostUsd, int? Turns, long? OutputTokens, Tokens? Tokens = null, long? DurationMs = null) : StreamEvent;
/// A run's tokens: every model's, subagents and workflow agents included when Claude reports `modelUsage`. A resumed
/// session reports the whole conversation's. Quota is measured from the account's usage, never from these or from cost.
public sealed record Tokens(long Input, long Output, long CacheRead, long CacheWrite);

/// Follows a growing stream-json file. Reads only what was appended since the last call, keeps a
/// partial last line for the next one, and skips anything that is not a JSON object (the `cswap run`
/// banner, stray stderr). Holds nothing but an offset and the partial line.
public sealed class StreamTail(string path)
{
    public const int MaxRead = 256 * 1024;
    long _offset;
    readonly List<byte> _partial = [];

    public string Path { get; } = path;
    public long Offset => _offset;

    /// New events since the last read; empty when the file is missing or has not grown.
    public List<StreamEvent> Read(int maxBytes = MaxRead)
    {
        var events = new List<StreamEvent>();
        byte[] buf;
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= _offset) return events;
            fs.Position = _offset;
            buf = new byte[(int)Math.Min(maxBytes, fs.Length - _offset)];
            var n = fs.Read(buf, 0, buf.Length);
            if (n < buf.Length) Array.Resize(ref buf, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return events; }
        _offset += buf.Length;
        var start = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] != (byte)'\n') continue;
            ReadOnlySpan<byte> line = buf.AsSpan(start, i - start);
            if (_partial.Count > 0) { _partial.AddRange(line.ToArray()); line = _partial.ToArray(); _partial.Clear(); }
            Parse(line, events);
            start = i + 1;
        }
        // ponytail: one pathological line over MaxRead is held whole in memory; fine for stream-json.
        _partial.AddRange(buf.AsSpan(start).ToArray());
        return events;
    }

    /// Every event of a finished file, for inspection and hand-off notes.
    public static List<StreamEvent> ReadAll(string path)
    {
        var tail = new StreamTail(path);
        var all = new List<StreamEvent>();
        List<StreamEvent> chunk;
        while ((chunk = tail.Read()).Count > 0 || tail.GrewSinceLastRead()) all.AddRange(chunk);
        return all;
    }

    bool GrewSinceLastRead()
    {
        try { return new FileInfo(Path).Length > _offset; }
        catch (IOException) { return false; }
    }

    internal static void Parse(ReadOnlySpan<byte> line, List<StreamEvent> into)
    {
        line = line.Trim("\r\n\t "u8);
        if (line.Length < 2 || line[0] != (byte)'{') return;
        if (line.StartsWith("{\"type\":\"stream_event\""u8)) return; // partial-message deltas: only their arrival matters (the file grows)
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            switch (Str(root, "type"))
            {
                case "system" when Str(root, "subtype") == "init":
                    into.Add(new InitEvent(Str(root, "session_id"), Str(root, "model")));
                    break;
                case "system" when Str(root, "subtype") == "background_tasks_changed" && root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array:
                    into.Add(new BackgroundEvent(tasks.GetArrayLength()));
                    break;
                case "user" when root.TryGetProperty("message", out var um) && um.ValueKind == JsonValueKind.Object && um.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array:
                    foreach (var c in uc.EnumerateArray())
                        if (Str(c, "type") == "tool_result" && Str(c, "tool_use_id") is { } done) into.Add(new ToolDoneEvent(done));
                    break;
                case "assistant" when root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object:
                    if (Str(msg, "id") is { } id)
                        into.Add(new TurnEvent(id, msg.TryGetProperty("usage", out var u) ? Long(u, "output_tokens") ?? 0 : 0));
                    if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) break;
                    foreach (var c in content.EnumerateArray())
                    {
                        switch (Str(c, "type"))
                        {
                            case "text" when Str(c, "text") is { Length: > 0 } t:
                                into.Add(new TextEvent(t));
                                break;
                            case "tool_use":
                                into.Add(new ToolEvent(Str(c, "name") ?? "tool", c.TryGetProperty("input", out var input) ? Target(input) : null, Str(c, "id")));
                                break;
                        }
                    }
                    break;
                case "result":
                    into.Add(new ResultEvent(
                        root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
                        Str(root, "subtype"),
                        Str(root, "result") ?? "",
                        root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : null,
                        (int?)Long(root, "num_turns"),
                        root.TryGetProperty("usage", out var ru) ? Long(ru, "output_tokens") : null,
                        Usage(root),
                        Long(root, "duration_ms")));
                    break;
            }
        }
        catch (JsonException) { }
    }

    /// `modelUsage` summed over models, else the main loop's `usage`.
    static Tokens? Usage(JsonElement root)
    {
        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object && mu.EnumerateObject().Any())
        {
            var ms = mu.EnumerateObject().Select(p => p.Value).ToList();
            long Sum(string k) => ms.Sum(m => Long(m, k) ?? 0);
            return new(Sum("inputTokens"), Sum("outputTokens"), Sum("cacheReadInputTokens"), Sum("cacheCreationInputTokens"));
        }
        return root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
            ? new(Long(u, "input_tokens") ?? 0, Long(u, "output_tokens") ?? 0, Long(u, "cache_read_input_tokens") ?? 0, Long(u, "cache_creation_input_tokens") ?? 0)
            : null;
    }

    /// The most telling argument of a tool call: a path, a command or a pattern.
    static string? Target(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in (string[])["file_path", "path", "notebook_path", "command", "pattern", "url", "description", "prompt"])
            if (Str(input, key) is { Length: > 0 } v)
            {
                v = v.ReplaceLineEndings(" ").Trim();
                return v.Length > 120 ? v[..120] + "…" : v;
            }
        return null;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    /// Short human line for an event (the inspect view and the live activity column).
    public static string Describe(StreamEvent e) => e switch
    {
        InitEvent i => $"session started{(i.Model is { } m ? " · " + m : "")}",
        ToolEvent t => t.Target is { } target ? $"{t.Name} {ShortenPath(target)}" : t.Name,
        TextEvent x => x.Text.ReplaceLineEndings(" ").Trim(),
        ResultEvent r => r.IsError ? $"ended with an error: {Oneline(r.Text)}" : "finished",
        _ => "",
    };

    static string Oneline(string s) { s = s.ReplaceLineEndings(" ").Trim(); return s.Length > 200 ? s[..200] + "…" : s; }

    /// Paths inside the worktree read better relative; keep the last two segments otherwise.
    static string ShortenPath(string s)
    {
        if (s.IndexOfAny(['\\', '/']) < 0 || s.Contains(' ')) return s;
        var parts = s.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? s : "…/" + string.Join('/', parts[^3..]);
    }
}

/// What a run is waiting on, read from its own stream as it grows: tool calls whose result has not come back, the
/// background tasks Claude reports (shells, subagents, workflows), and whether it already gave its final answer.
/// Silence while something is pending is waiting, not a stall; an answer is final only with a HANDOFF marker, since a
/// run can answer "still running in the background" and go on once that work ends.
public sealed class RunActivity
{
    internal static readonly System.Text.RegularExpressions.Regex FinalMarker = new(@"HANDOFF:\s*(DONE|BLOCKED|APPROVED|CHANGES|PLAN)\b");
    readonly HashSet<string> _tools = [];

    public int Background { get; private set; }
    /// When a result carrying a final marker arrived; cleared when the run goes on after it.
    public DateTimeOffset? Answered { get; private set; }
    public bool Pending => _tools.Count > 0 || Background > 0;

    public void Add(IEnumerable<StreamEvent> events, DateTimeOffset now)
    {
        foreach (var e in events)
            switch (e)
            {
                case ToolEvent { Id: { } id }: _tools.Add(id); Answered = null; break;
                case ToolDoneEvent d: _tools.Remove(d.Id); break;
                case BackgroundEvent b: Background = b.Running; break;
                case ResultEvent r: Answered = FinalMarker.IsMatch(r.Text) ? now : r.Text.Trim().Length > 0 ? null : Answered; _tools.Clear(); break; // a turn ended: its calls are over
                case InitEvent or TurnEvent or TextEvent: Answered = null; break; // it went on
            }
    }
}
