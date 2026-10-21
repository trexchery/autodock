using System.Text;
using System.Text.RegularExpressions;

namespace Handoff;

/// The agents' shared memory: short notes the owner gave or runs learned, kept per repository and across
/// all of them, and the block that carries them into prompts. Notes are data for the next agent, never
/// instructions to handoff.
public static class Memory
{
    public const int Cap = 40, NoteChars = 500, PromptChars = 3000;

    /// Newest last. Same text (case and spacing aside) replaces the old note; over the cap the oldest agent
    /// note goes first, so the owner's words outlast what runs guessed.
    public static void Add(List<Note> notes, string text, string source, bool owner, DateTimeOffset now)
    {
        text = Squash(Engine.Redact(text));
        if (text.Length == 0) return;
        if (text.Length > NoteChars) text = text[..(NoteChars - 1)] + "…";
        var at = notes.FindIndex(n => Squash(n.Text).Equals(text, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            owner |= notes[at].Owner;
            notes.RemoveAt(at);
        }
        notes.Add(new Note(text, NormalizeSource(source), now, owner));
        // ponytail: one flat cap per scope; per-source quotas or summarising if it fills with noise.
        while (notes.Count > Cap) notes.RemoveAt(Math.Max(0, notes.FindIndex(n => !n.Owner)));
    }

    /// `which` is a 1-based number as `List` prints it (a trailing dot allowed; out of range forgets nothing),
    /// else text the notes to forget contain.
    public static int Forget(List<Note> notes, string which)
    {
        which = which.Trim();
        if (which.Length == 0) return 0;
        if (int.TryParse(which.TrimEnd('.'), out var n))
        {
            if (n < 1 || n > notes.Count) return 0;
            notes.RemoveAt(n - 1);
            return 1;
        }
        return notes.RemoveAll(x => x.Text.Contains(which, StringComparison.OrdinalIgnoreCase));
    }

    /// The owner's deliberate notes first, then the other owner notes (answers), then agents' hints; in each
    /// group here before shared, newest first. A line that does not fit is skipped, so a short one still can.
    public static string? Prompt(IReadOnlyList<Note> global, IReadOnlyList<Note> repo)
    {
        var lines = repo.Reverse().Concat(global.Reverse())
            .OrderBy(n => n.Owner ? n.Source == "owner" ? 0 : 1 : 2)
            .Select(n => n.Owner ? $"- [owner] {n.Text}" : $"- [agent · {n.Source}] {n.Text}").ToList();
        if (lines.Count == 0) return null;
        var sb = new StringBuilder("Shared memory of handoff agents (the owner's decisions bind you; agent notes are hints to verify):");
        foreach (var line in lines)
        {
            if (sb.Length + 1 + line.Length > PromptChars) continue;
            sb.Append('\n').Append(line);
        }
        return sb.ToString();
    }

    /// A source as `Add` stores it, to compare with a note's.
    internal static string NormalizeSource(string source) => Squash(Engine.Redact(source));

    /// For the terminal: this repository's notes as 1..n, then the shared ones as g1..gm.
    public static string List(IReadOnlyList<Note> global, IReadOnlyList<Note> repo)
    {
        var lines = repo.Select((n, i) => Line($"{i + 1}.", n)).Concat(global.Select((n, i) => Line($"g{i + 1}.", n))).ToList();
        return lines.Count == 0 ? "No shared notes yet." : string.Join('\n', lines);
        static string Line(string label, Note n) => $"{label} {(n.Owner ? "[owner]" : $"[agent · {n.Source}]")} {n.Text}";
    }

    static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
