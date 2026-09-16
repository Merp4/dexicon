using System.Text;
using System.Text.RegularExpressions;

namespace Dexicon.Core.Indexing;

public sealed record TextChunk
{
    public required string Content { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string? Section { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public int Index { get; init; }
}

/// <summary>
/// Line-accumulating chunker. Never splits a line, so every chunk carries an exact
/// <c>start_line</c>/<c>end_line</c> and a result is directly openable in an editor.
///
/// With <c>language-aware</c> the file is first split at member boundaries, then each
/// segment is size-chunked — which keeps a method with its signature instead of slicing
/// it at an arbitrary token count.
/// </summary>
public static class CodeChunker
{
    /// <summary>
    /// Four characters per token. Approximate on purpose: exact tokenization means
    /// shipping and versioning a tokenizer per embedding model, and chunk size is a
    /// target rather than a contract. See docs/04-ingestion.md.
    /// </summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Bumped whenever chunking OUTPUT changes for the same input and settings. Part of
    /// the chunking fingerprint, so a corpus re-chunks itself after an algorithm change.
    ///
    /// Without it, the fingerprint says "same bytes, same settings, nothing to do" and a
    /// corpus keeps chunks from a chunker that no longer exists — indefinitely, because an
    /// incremental refresh's whole purpose is to skip unchanged files. The same reasoning
    /// as the extractor version, applied one stage later in the pipeline.
    ///
    /// 2: size decides WHEN to split and a boundary decides WHERE (chunk size used to be
    ///    dead configuration); a line longer than the whole budget is now split.
    /// </summary>
    public const int Version = 2;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    public static IReadOnlyList<TextChunk> Chunk(
        string relativePath,
        string content,
        int chunkSizeTokens = 768,
        int overlapTokens = 100,
        string boundaryMode = "language-aware",
        string? customBoundaryPattern = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        if (overlapTokens >= chunkSizeTokens)
            throw new ArgumentException($"overlap ({overlapTokens}) must be smaller than chunk size ({chunkSizeTokens}).");

        var language = LanguageMap.Detect(relativePath);
        var lines = SplitLines(content);
        var maxChars = chunkSizeTokens * CharsPerToken;
        var overlapChars = overlapTokens * CharsPerToken;

        var boundaries = ResolveBoundaries(boundaryMode, language, customBoundaryPattern, lines).ToHashSet();
        var symbolPattern = LanguageMap.SymbolPattern(language);
        var isMarkdown = string.Equals(language, "markdown", StringComparison.Ordinal);

        var chunks = new List<TextChunk>();
        var index = 0;

        foreach (var c in ChunkLines(lines, boundaries, maxChars, overlapChars, symbolPattern, isMarkdown))
            chunks.Add(c with { Index = index++ });

        return chunks;
    }

    /// <summary>
    /// Size decides WHEN to split; a boundary decides WHERE.
    ///
    /// This used to split at every boundary, which made <c>chunkSize</c> dead
    /// configuration in every mode but <c>none</c>: blank-line mode on prose produced
    /// one chunk per paragraph, measured at a 252-character mean against a 3072
    /// character budget, and two corpora configured 768 and 256 produced byte-identical
    /// output. Chunks that small retrieve badly — there is not enough context in a
    /// paragraph to embed usefully.
    ///
    /// Now the accumulator fills to the budget and then backs up to the most recent
    /// boundary inside the buffer, so a chunk holds as many whole members or paragraphs
    /// as fit and still never ends mid-thought. Falls back to splitting at the current
    /// line when the buffer contains no boundary at all.
    /// </summary>
    private static IEnumerable<TextChunk> ChunkLines(string[] lines, HashSet<int> boundaries,
        int maxChars, int overlapChars, string? symbolPattern, bool isMarkdown)
    {
        var start = 0;
        var chars = 0;
        var lastBoundary = -1;
        var lastHeading = (string?)null;
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (isMarkdown)
            {
                if (IsFenceDelimiter(line)) inFence = !inFence;
                else if (!inFence && TryReadHeading(line, out var heading)) lastHeading = heading;
            }

            if (boundaries.Contains(i) && i > start) lastBoundary = i;

            var lineChars = line.Length + 1;

            // A single line that alone exceeds the budget. This used to be emitted whole,
            // on the reasoning that a minified file is ugly but not invisible — which was
            // wrong, because the embedding model truncates at its context limit WITHOUT
            // SAYING SO. An EPUB whose extractor emitted one line per chapter produced 18
            // chunks averaging 32,000 characters: the book reported itself as indexed
            // while roughly 95% of it existed nowhere in the index.
            //
            // So the line is split. Every piece keeps this line's number, which is honest
            // — they are all on it — and a search hit still opens at the right place.
            if (chars == 0 && lineChars > maxChars)
            {
                foreach (var piece in SplitOversizeLine(line, maxChars, overlapChars))
                    yield return Build(piece, i, i, lastHeading, symbolPattern);

                start = i + 1;
                lastBoundary = -1;
                continue;
            }

            if (chars > 0 && chars + lineChars > maxChars)
            {
                var splitAt = lastBoundary > start ? lastBoundary : i;

                yield return Build(Join(lines, start, splitAt), start, splitAt - 1, lastHeading, symbolPattern);

                // Overlap is carried by rewinding the start, not by copying text, so
                // line numbers stay exact.
                start = RewindForOverlap(lines, splitAt, start, overlapChars);
                lastBoundary = -1;
                chars = 0;
                i = start - 1;      // re-accumulate from the new start
                continue;
            }

            chars += lineChars;
        }

        if (start < lines.Length)
        {
            var tail = Join(lines, start, lines.Length);
            if (tail.Trim().Length > 0)
                yield return Build(tail, start, lines.Length - 1, lastHeading, symbolPattern);
        }
    }

    /// <summary>
    /// Cuts one over-long line into budget-sized pieces, preferring a word boundary near
    /// the end of each. The only case where the chunker splits within a line; it exists
    /// so that "indexed" cannot mean "the first 8,000 characters were indexed".
    /// </summary>
    private static IEnumerable<string> SplitOversizeLine(string line, int maxChars, int overlapChars)
    {
        // Back up at most an eighth of the budget looking for a space: far enough to
        // avoid cutting mid-word, not so far that a line without spaces (a minified
        // bundle, a base64 blob) loses a meaningful slice of every piece.
        var maxBackup = Math.Max(1, maxChars / 8);
        var pos = 0;

        while (pos < line.Length)
        {
            var end = Math.Min(pos + maxChars, line.Length);

            if (end < line.Length)
            {
                var space = line.LastIndexOf(' ', end - 1, Math.Min(end - pos, maxBackup));
                if (space > pos) end = space + 1;
            }

            var piece = line[pos..end].Trim();
            if (piece.Length > 0) yield return piece;

            if (end >= line.Length) yield break;

            // Overlap, same as between chunks — but never at the cost of progress.
            var next = end - overlapChars;
            pos = next > pos ? next : end;
        }
    }

    private static string Join(string[] lines, int from, int toExclusive) =>
        string.Join('\n', lines[from..toExclusive]);

    /// <summary>Move the next chunk's start back far enough to spend the overlap budget.</summary>
    private static int RewindForOverlap(string[] lines, int splitAt, int previousStart, int overlapChars)
    {
        if (overlapChars <= 0) return splitAt;

        var taken = 0;
        var first = splitAt;
        while (first > previousStart + 1 && taken + lines[first - 1].Length + 1 <= overlapChars)
        {
            first--;
            taken += lines[first].Length + 1;
        }

        // Never rewind to where we started, or the loop makes no progress.
        return Math.Max(first, previousStart + 1);
    }


    /// <summary>Walk back from the split point until the overlap budget is spent.</summary>

    /// <summary>
    /// A fenced block opener or closer: ``` or ~~~, optionally indented up to three
    /// spaces, optionally followed by an info string.
    /// </summary>
    internal static bool IsFenceDelimiter(string line)
    {
        var span = line.AsSpan();
        var indent = 0;
        while (indent < span.Length && span[indent] == ' ' && indent < 4) indent++;
        span = span[indent..];
        return span.StartsWith("```", StringComparison.Ordinal) || span.StartsWith("~~~", StringComparison.Ordinal);
    }

    /// <summary>
    /// An ATX heading: one to six '#' at the start of the line (up to three spaces of
    /// indent), then whitespace, then text.
    ///
    /// The naive version of this — "starts with # and contains '# '" — fired on YAML
    /// comments inside fenced code blocks and labelled a docs chunk with
    /// "spends its first minutes in embedding backoff" as its section. A section that
    /// is confidently wrong is worse than no section, because a reader trusts it.
    /// </summary>
    internal static bool TryReadHeading(string line, out string heading)
    {
        heading = string.Empty;
        var span = line.AsSpan();

        var indent = 0;
        while (indent < span.Length && span[indent] == ' ') indent++;
        if (indent > 3) return false;
        span = span[indent..];

        var hashes = 0;
        while (hashes < span.Length && span[hashes] == '#') hashes++;
        if (hashes is 0 or > 6) return false;
        if (hashes >= span.Length || !char.IsWhiteSpace(span[hashes])) return false;

        var text = span[hashes..].Trim().TrimEnd('#').Trim();
        if (text.IsEmpty) return false;

        heading = text.ToString();
        return true;
    }

    private static TextChunk Build(string content, int startLine, int endLine, string? section, string? symbolPattern) =>
        new()
        {
            Content = content,
            StartLine = startLine + 1,   // 1-based: what an editor shows
            EndLine = endLine + 1,
            Section = section,
            Symbols = ExtractSymbols(content, symbolPattern),
        };

    internal static IReadOnlyList<string> ExtractSymbols(string content, string? pattern)
    {
        if (pattern is null) return [];
        try
        {
            var found = new List<string>();
            foreach (Match m in Regex.Matches(content, pattern, RegexOptions.Multiline, RegexTimeout))
            {
                for (var g = 1; g < m.Groups.Count; g++)
                {
                    var v = m.Groups[g].Value;
                    if (!string.IsNullOrEmpty(v) && !found.Contains(v, StringComparer.Ordinal)) found.Add(v);
                }
                if (found.Count >= 32) break;   // a chunk declaring 32 things has told us enough
            }
            return found;
        }
        catch (RegexMatchTimeoutException)
        {
            // Symbols are a nicety. A pathological file loses them; it does not lose indexing.
            return [];
        }
    }

    /// <summary>Line indices at which a new segment begins.</summary>
    private static List<int> ResolveBoundaries(string mode, string language, string? custom, string[] lines)
    {
        var pattern = mode switch
        {
            "none" => null,
            "blank-line" => @"^\s*$",
            "language-aware" => LanguageMap.BoundaryPattern(language),
            "custom" => custom ?? throw new ArgumentException("boundaryMode 'custom' requires a pattern."),
            _ => throw new ArgumentException($"Unknown boundary mode '{mode}'. Expected none|blank-line|language-aware|custom."),
        };

        var result = new List<int>();
        if (pattern is null) return result;

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Multiline, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            // No silent fallback: an operator's bad regex must fail loudly, not quietly
            // produce differently-shaped chunks they never asked for.
            throw new ArgumentException($"Invalid boundary regex '{pattern}': {ex.Message}", ex);
        }

        for (var i = 1; i < lines.Length; i++)
        {
            try
            {
                if (regex.IsMatch(lines[i])) result.Add(i);
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new ArgumentException(
                    $"Boundary regex timed out after {RegexTimeout.TotalMilliseconds}ms on line {i + 1}.", ex);
            }
        }

        return result;
    }

    private static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
               .Replace('\r', '\n')
               .Split('\n');
}
