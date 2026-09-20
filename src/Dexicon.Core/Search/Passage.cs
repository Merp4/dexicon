using System.Text;

namespace Dexicon.Core.Search;

/// <summary>
/// Rebuilding a readable passage from the chunks that were indexed.
///
/// Chunks overlap by design, so joining them is where a passage is either reproduced
/// faithfully or quietly invented. Both failure modes are invisible to whoever reads the
/// result: duplicated lines look like real repetition in the source, and a closed gap
/// looks like contiguous code.
///
/// Used by the MCP `get_context` tool and by `POST /api/context`. See docs/decisions.md
/// D-29.
/// </summary>
public static class Passage
{
    /// <summary>
    /// Joins overlapping chunks of ONE file back into a single readable passage.
    /// A pure function, so the off-by-one that decides whether a model reads duplicated
    /// or missing lines can be tested without a vector store or an MCP server.
    /// </summary>
    /// <param name="pieces">Chunks of one file, in file order (by chunk index).</param>
    /// <param name="lineNumbers">
    /// Prefix each line with its number in the file. The passage is what a model reads
    /// before quoting or editing it, and counting lines down from a header is exactly the
    /// arithmetic it gets wrong. Gap markers stay unnumbered: the lines they stand for are
    /// the ones that are not there.
    /// </param>
    /// <param name="window">
    /// Inclusive line range to emit, or null for all of it. Chunks are selected by overlap
    /// with the window, so without this a caller asking for a few lines receives whole
    /// chunks: a request for one line either side returned forty.
    /// </param>
    public static string Stitch(
        IEnumerable<(int StartLine, int EndLine, string Content)> pieces,
        bool lineNumbers = false,
        (int Lo, int Hi)? window = null)
    {
        var (winLo, winHi) = window ?? (int.MinValue, int.MaxValue);
        var sb = new StringBuilder();
        var emittedThrough = 0;
        var lastRange = (Start: 0, End: 0);

        foreach (var p in pieces)
        {
            // Several chunks can share ONE line number: the chunker splits a line that is
            // longer than the whole budget, and every piece honestly reports that line.
            // Line-based de-overlapping cannot separate those, since by line they are all
            // "already emitted", so they are stitched on their text instead. Without
            // this, get_context on a minified file returned only its first chunk.
            if (p.StartLine == p.EndLine && (p.StartLine, p.EndLine) == lastRange)
            {
                if (p.StartLine >= winLo && p.StartLine <= winHi)
                    AppendWithoutRepeating(sb, p.Content);
                continue;
            }

            // Wholly inside what has already been emitted.
            if (p.EndLine <= emittedThrough) continue;

            // Chunks from one pass tile the file, so a gap here means the index really is
            // missing those lines. Butting the two ends together would hand a model code
            // that reads as contiguous and is not, which the model cannot detect, so it
            // is disclosed instead. This fired on chunks left behind by an older
            // chunker, which is how that staleness was found at all.
            if (emittedThrough > 0 && p.StartLine > emittedThrough + 1)
            {
                var gapLo = Math.Max(emittedThrough + 1, winLo);
                var gapHi = Math.Min(p.StartLine - 1, winHi);
                if (gapLo <= gapHi)
                    sb.Append($"\n… lines {gapLo}-{gapHi} not indexed …\n\n");
            }

            // Drop the leading lines shared with the previous chunk. Overlap is configured
            // in characters, not lines, so the shared span is derived from the line
            // numbers rather than assumed from the setting.
            var skip = Math.Max(0, emittedThrough - p.StartLine + 1);
            var lineNo = p.StartLine + skip;

            foreach (var line in p.Content.Split('\n').Skip(skip))
            {
                if (lineNo >= winLo && lineNo <= winHi)
                {
                    if (lineNumbers) sb.Append(lineNo).Append(": ");
                    sb.Append(line).Append('\n');
                }
                lineNo++;
            }

            emittedThrough = Math.Max(emittedThrough, p.EndLine);
            lastRange = (p.StartLine, p.EndLine);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Lines <paramref name="lo"/> to <paramref name="hi"/> of a whole document, with the
    /// range actually found.
    ///
    /// The counterpart to <see cref="Stitch"/> for text that does not need reassembling.
    /// A window taken from the document cannot have gaps, and its bounds are the caller's
    /// rather than the nearest chunk edge, so asking for one line either side returns one
    /// line either side. Stitch selects chunks by overlap and a request for three lines
    /// used to return forty.
    ///
    /// Counts newlines instead of splitting, because a technical book runs to two or three
    /// million characters and every call would otherwise allocate a string per line of it.
    /// </summary>
    /// <returns>
    /// The text, and the first and last line present. <c>Hi</c> is below <c>Lo</c> when
    /// the window starts past the end of the document, and the text is then empty.
    /// </returns>
    public static (string Text, int Lo, int Hi) Window(string text, int lo, int hi)
    {
        lo = Math.Max(1, lo);
        if (hi < lo || text.Length == 0) return (string.Empty, lo, lo - 1);

        var line = 1;
        var start = 0;
        while (line < lo)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0) return (string.Empty, lo, lo - 1);   // the document ends first
            start = next + 1;
            line++;
        }

        var end = start;
        var last = lo - 1;
        while (line <= hi)
        {
            // Nothing after the last newline, so there is no further line. A document
            // ending in a newline has as many lines as it has newlines, which is the
            // count every other reader here uses; treating the empty tail as a line
            // would put a citation one line past the end of the text it cites.
            if (end >= text.Length) break;

            var next = text.IndexOf('\n', end);
            last = line;
            if (next < 0)
            {
                // A final line with no newline after it. Still a line.
                end = text.Length;
                break;
            }

            end = next + 1;
            line++;
        }

        // The trailing newline belongs to the line after the window, not to this one.
        if (end > start && text[end - 1] == '\n') end--;

        return (text[start..end], lo, last);
    }

    /// <summary>
    /// Appends <paramref name="piece"/>, dropping any prefix already present at the end of
    /// the buffer. Two slices of one line overlap by the configured amount, which this
    /// cannot know, so it measures the repeat instead of assuming it.
    /// </summary>
    private static void AppendWithoutRepeating(StringBuilder sb, string piece)
    {
        // These slices are all one line, so the newline the previous piece ended with does
        // not belong between them, and leaving it there would also block every match,
        // since no chunk's text begins with the end of the last one plus a newline.
        while (sb.Length > 0 && sb[^1] == '\n') sb.Length--;

        // Bounded window: an overlap is a fraction of a chunk, and scanning the whole
        // buffer for each piece would be quadratic on a file that is one very long line.
        var window = Math.Min(Math.Min(sb.Length, piece.Length), 8192);
        if (window == 0) { sb.Append(piece).Append('\n'); return; }

        var tail = sb.ToString(sb.Length - window, window);
        var overlap = LongestPrefixThatIsAlsoASuffix(piece[..window], tail);

        sb.Append(piece.AsSpan(overlap)).Append('\n');
    }

    /// <summary>
    /// The length of the longest prefix of <paramref name="prefixOf"/> that is also a
    /// suffix of <paramref name="suffixOf"/>: the KMP failure function over
    /// <c>prefixOf + sentinel + suffixOf</c>, which gets the answer in one linear pass
    /// rather than testing every candidate length.
    /// </summary>
    private static int LongestPrefixThatIsAlsoASuffix(string prefixOf, string suffixOf)
    {
        // '￿' is a permanent noncharacter, so it cannot appear in either input and
        // cannot let a match run across the join.
        var n = prefixOf.Length + 1 + suffixOf.Length;
        var failure = new int[n];
        var k = 0;

        for (var i = 1; i < n; i++)
        {
            var c = CharAt(i);
            while (k > 0 && c != CharAt(k)) k = failure[k - 1];
            if (c == CharAt(k)) k++;
            failure[i] = k;
        }

        return failure[n - 1];

        char CharAt(int i) =>
            i < prefixOf.Length ? prefixOf[i]
            : i == prefixOf.Length ? '￿'
            : suffixOf[i - prefixOf.Length - 1];
    }
}
