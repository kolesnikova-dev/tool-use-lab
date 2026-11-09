using System.Text;
using System.Text.RegularExpressions;

namespace ToolUseLab;

/// <summary>A piece of a document. <see cref="Heading"/> is the markdown breadcrumb ("A > B"), empty for other formats.</summary>
internal sealed record DocChunk(string Source, string Heading, string Text)
{
    /// <summary>What gets embedded: the heading helps a short section match a question about its topic.</summary>
    internal string EmbeddingText => Heading.Length == 0 ? Text : $"{Heading}\n\n{Text}";

    /// <summary>How Claude is told to cite it.</summary>
    internal string Citation => Heading.Length == 0 ? Source : $"{Source} > {Heading}";
}

internal interface IChunker
{
    List<DocChunk> Chunk(string source, string text);
}

internal static class ChunkerFactory
{
    /// <summary><c>Auto</c> reads .md as markdown and everything else (.txt) as prose.</summary>
    internal static IChunker For(ChunkingStrategy strategy, string path, int maxChars, int overlap)
    {
        if (strategy == ChunkingStrategy.Auto)
            strategy = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
                ? ChunkingStrategy.Markdown
                : ChunkingStrategy.Sentence;

        return strategy switch
        {
            ChunkingStrategy.Markdown => new MarkdownChunker(maxChars, overlap),
            ChunkingStrategy.Sentence => new SentenceChunker(maxChars, overlap),
            _ => new SizeChunker(maxChars, overlap),
        };
    }
}

/// <summary>Fixed-width windows that back up to whitespace, so a word is never cut in half.</summary>
internal sealed class SizeChunker(int maxChars, int overlap) : IChunker
{
    public List<DocChunk> Chunk(string source, string text) =>
        Windows(text, maxChars, overlap).Select(w => new DocChunk(source, "", w)).ToList();

    internal static IEnumerable<string> Windows(string text, int maxChars, int overlap)
    {
        overlap = Math.Clamp(overlap, 0, maxChars / 2 - 1);
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + maxChars, text.Length);
            if (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                for (int i = end; i > start + maxChars / 2; i--) // look back, but never past half a window
                {
                    if (char.IsWhiteSpace(text[i - 1])) { end = i; break; }
                }
            }

            string piece = text[start..end].Trim();
            if (piece.Length > 0)
                yield return piece;
            if (end >= text.Length)
                yield break;

            int next = Math.Max(end - overlap, start + 1);
            while (next < end && !char.IsWhiteSpace(text[next - 1])) // start the next window on a word boundary
                next++;
            start = next;
        }
    }
}

/// <summary>Packs whole sentences up to the size limit; the overlap repeats the last sentence(s).</summary>
internal sealed partial class SentenceChunker(int maxChars, int overlap) : IChunker
{
    public List<DocChunk> Chunk(string source, string text) =>
        ChunkPacker.Pack(Sentences(text), " ", maxChars, overlap).Select(c => new DocChunk(source, "", c)).ToList();

    // A sentence ends at . ! ? followed by whitespace and a capital letter, digit or opening quote/bracket, or at
    // a blank line. So "3.5" and "e.g. the" stay whole; "Dr. Smith" does split, which only costs a slightly smaller piece.
    internal static IEnumerable<string> Sentences(string text) =>
        SentenceBoundary().Split(text)
            .Select(s => Whitespace().Replace(s, " ").Trim())
            .Where(s => s.Length > 0);

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[\p{Lu}\d""'(\[])|\r?\n[ \t]*\r?\n")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Splits on headings (keeping the breadcrumb), then packs each section's paragraphs.</summary>
internal sealed partial class MarkdownChunker(int maxChars, int overlap) : IChunker
{
    public List<DocChunk> Chunk(string source, string text)
    {
        var chunks = new List<DocChunk>();
        var headings = new List<(int Level, string Title)>();
        var body = new StringBuilder();
        string breadcrumb = "";
        bool inFence = false;

        void Flush()
        {
            var paragraphs = Paragraphs().Split(body.ToString()).Select(p => p.Trim()).Where(p => p.Length > 0);
            foreach (string piece in ChunkPacker.Pack(paragraphs, "\n\n", maxChars, overlap))
                chunks.Add(new DocChunk(source, breadcrumb, piece));
            body.Clear();
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                inFence = !inFence; // a "# comment" inside a code block is not a heading

            var heading = inFence ? Match.Empty : Heading().Match(line);
            if (!heading.Success)
            {
                body.Append(line).Append('\n');
                continue;
            }

            Flush();
            int level = heading.Groups[1].Length;
            headings.RemoveAll(h => h.Level >= level);
            headings.Add((level, heading.Groups[2].Value.Trim()));
            breadcrumb = string.Join(" > ", headings.Select(h => h.Title));
        }
        Flush();
        return chunks;
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex Paragraphs();
}

/// <summary>Shared by the sentence and markdown chunkers: greedy packing of small units into size-limited chunks.</summary>
internal static class ChunkPacker
{
    /// <summary>
    /// Joins units with <paramref name="separator"/> until the next one would exceed <paramref name="maxChars"/>.
    /// A new chunk starts with the last units of the previous one (up to <paramref name="overlapChars"/>) so context
    /// is not lost at the boundary. A unit longer than the limit on its own is cut into windows first.
    /// </summary>
    internal static List<string> Pack(IEnumerable<string> units, string separator, int maxChars, int overlapChars)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        int length = 0; // length of current joined with the separator

        foreach (string unit in units.SelectMany(u => u.Length > maxChars ? SizeChunker.Windows(u, maxChars, overlapChars) : [u]))
        {
            if (current.Count > 0 && length + separator.Length + unit.Length > maxChars)
            {
                chunks.Add(string.Join(separator, current));
                current = Tail(current, separator, Math.Min(overlapChars, maxChars - separator.Length - unit.Length));
                length = string.Join(separator, current).Length;
            }
            length += (current.Count > 0 ? separator.Length : 0) + unit.Length;
            current.Add(unit);
        }

        if (current.Count > 0)
            chunks.Add(string.Join(separator, current));
        return chunks;
    }

    // The trailing units of a finished chunk that fit in the overlap budget.
    private static List<string> Tail(List<string> units, string separator, int budget)
    {
        var tail = new List<string>();
        int length = 0;
        for (int i = units.Count - 1; i >= 0; i--)
        {
            int next = length + units[i].Length + (tail.Count > 0 ? separator.Length : 0);
            if (next > budget)
                break;
            tail.Insert(0, units[i]);
            length = next;
        }
        return tail;
    }
}
