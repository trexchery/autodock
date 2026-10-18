using System.Text;
using System.Text.Json;

namespace Autodock;

/// One thing an agent said or did, condensed from a Claude Code `--output-format stream-json` line.
public abstract record StreamEvent;
public sealed record InitEvent(string? SessionId, string? Model) : StreamEvent;
public sealed record TextEvent(string Text) : StreamEvent;
public sealed record ToolEvent(string Name, string? Target) : StreamEvent;
public sealed record TurnEvent(string MessageId, long OutputTokens) : StreamEvent;
public sealed record ResultEvent(bool IsError, string? Subtype, string Text, double? CostUsd, int? Turns, long? OutputTokens) : StreamEvent;

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
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            switch (Str(root, "type"))
            {
                case "system" when Str(root, "subtype") == "init":
                    into.Add(new InitEvent(Str(root, "session_id"), Str(root, "model")));
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
                                into.Add(new ToolEvent(Str(c, "name") ?? "tool", c.TryGetProperty("input", out var input) ? Target(input) : null));
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
                        root.TryGetProperty("usage", out var ru) ? Long(ru, "output_tokens") : null));
                    break;
            }
        }
        catch (JsonException) { }
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
