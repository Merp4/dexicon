using System.Text;
using System.Text.RegularExpressions;
using Dexicon.Core.Extraction;

namespace Dexicon.Core.Indexing;

public sealed record TextChunk
{
    /// <summary>The verbatim text, exactly as it appears in the file.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// What gets EMBEDDED, which is not always what gets stored. With heading context
    /// enabled a chunk is embedded with its heading trail prepended, so its vector knows
    /// which section it came from, while <see cref="Content"/> stays verbatim.
    ///
    /// Keeping them apart matters: Content is what search returns, what get_context
    /// stitches, and what a resource read reconstructs a file from. Prepending to it
    /// would put invented lines into a file that reconstructs byte-identically today.
    /// </summary>
    public string EmbedText { get; init; } = string.Empty;

    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string? Section { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public int Index { get; init; }
}

/// <summary>
/// How one chunk set cuts text. Grouped into a record rather than passed as five
/// positional arguments, because the set of knobs grows and a call site reading
/// <c>(768, 100, "blank-line", null, false, true, true)</c> tells a reader nothing.
/// </summary>
public sealed record ChunkOptions
{
    public int ChunkSizeTokens { get; init; } = 768;

    /// <summary>
    /// Characters per token for the model this text will be embedded with, measured by
    /// the probe. Defaults to <see cref="CodeChunker.CharsPerToken"/>, which is what an
    /// unmeasured model gets and what every caller got before the ratio was measured.
    ///
    /// The conversion has to use the model's own number. On embeddinggemma the measured
    /// ratio is 3.8 and the constant is 4, so a 2,065-token budget asked for 8,260
    /// characters where the model reads 2,174 tokens of them against a 2,048-token
    /// context: the chunker was over by 5% before any text was looked at.
    /// </summary>
    public double CharsPerToken { get; init; } = CodeChunker.CharsPerToken;
    public int OverlapTokens { get; init; } = 100;
    public string BoundaryMode { get; init; } = "language-aware";
    public string? CustomBoundaryPattern { get; init; }

    /// <summary>Prefer the document's own units (page, chapter, slide) as split points.</summary>
    public bool UnitAware { get; init; }

    /// <summary>Cut at a sentence rather than a word when splitting an over-long line.</summary>
    public bool SentenceAware { get; init; }

    /// <summary>Embed each chunk with its heading trail prepended.</summary>
    public bool HeadingContext { get; init; }
}

/// <summary>
/// Line-accumulating chunker. Never splits a line, so every chunk carries an exact
/// <c>start_line</c>/<c>end_line</c> and a result is directly openable in an editor.
///
/// With <c>language-aware</c> the file is first split at member boundaries, then each
/// segment is size-chunked, which keeps a method with its signature instead of slicing
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
    /// corpus keeps chunks from a chunker that no longer exists, indefinitely, because an
    /// incremental refresh exists to skip unchanged files. The same reasoning
    /// as the extractor version, applied one stage later in the pipeline.
    ///
    /// 2: size decides WHEN to split and a boundary decides WHERE (chunk size used to be
    ///    dead configuration); a line longer than the whole budget is now split.
    /// 3: the meaning-preserving strategies: heading context, unit-aware boundaries,
    ///    sentence-aware splitting. Only the sets that enable one are affected, but the
    ///    fingerprint cannot tell "off" from "on but implemented differently", and a set
    ///    built against the first cut of these would otherwise keep those chunks forever.
    /// 4: a boundary is only used as a split point when it leaves a chunk worth having.
    ///    Text with a boundary early and then a long stretch without one produced chunks
    ///    a few hundred characters long, one line apart, by the thousand.
    /// 5: the token-to-character conversion uses the model's measured ratio rather than
    ///    a flat 4, and a chunk size above the model's context is clamped to it, so a
    ///    chunk is no longer larger than the model that has to read it.
    /// </summary>
    public const int Version = 5;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Convenience overload for the common case and for tests.</summary>
    public static IReadOnlyList<TextChunk> Chunk(
        string relativePath,
        string content,
        int chunkSizeTokens = 768,
        int overlapTokens = 100,
        string boundaryMode = "language-aware",
        string? customBoundaryPattern = null) =>
        Chunk(relativePath, content, new ChunkOptions
        {
            ChunkSizeTokens = chunkSizeTokens,
            OverlapTokens = overlapTokens,
            BoundaryMode = boundaryMode,
            CustomBoundaryPattern = customBoundaryPattern,
        });

    /// <param name="extracted">
    /// The extraction result, when there is one. Only used for <see cref="ChunkOptions.UnitAware"/>:
    /// its unit offsets become split points, so a chunk does not straddle two chapters.
    /// </param>
    public static IReadOnlyList<TextChunk> Chunk(
        string relativePath,
        string content,
        ChunkOptions options,
        ExtractedText? extracted = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        if (options.OverlapTokens >= options.ChunkSizeTokens)
            throw new ArgumentException(
                $"overlap ({options.OverlapTokens}) must be smaller than chunk size ({options.ChunkSizeTokens}).");

        var language = LanguageMap.Detect(relativePath);
        var lines = SplitLines(content);
        var maxChars = (int)(options.ChunkSizeTokens * options.CharsPerToken);
        var overlapChars = (int)(options.OverlapTokens * options.CharsPerToken);

        var boundaries = ResolveBoundaries(
            options.BoundaryMode, language, options.CustomBoundaryPattern, lines).ToHashSet();

        // A document's own units are the strongest boundary it has, and they are the one
        // kind that FORCES a split rather than merely offering a place for one.
        //
        // Everywhere else the chunker's rule is "size decides when, a boundary decides
        // where", which is right for prose and unsuitable here: a chapter shorter than the
        // budget would simply be swallowed into the next one, and asking for chapter-
        // aligned chunks would produce chunks spanning three chapters. A chunk that
        // straddles two chapters is the thing this setting exists to prevent.
        //
        // The cost is honest and is the caller's choice: a document of very short pages
        // yields short chunks, because that is what page-aligned chunking means.
        var unitBoundaries = options.UnitAware && extracted is { Units.Count: > 0 }
            ? UnitBoundaryLines(extracted, content).ToHashSet()
            : [];

        foreach (var line in unitBoundaries) boundaries.Add(line);

        var symbolPattern = LanguageMap.SymbolPattern(language);
        var isMarkdown = string.Equals(language, "markdown", StringComparison.Ordinal);

        // Resolved per LINE, up front. Reading a running cursor at the moment a chunk was
        // emitted looked equivalent and was not: the accumulator fills PAST a boundary
        // before backing up to it, so the cursor is always ahead of the chunk being
        // flushed. A chunk from the "Point payload" section came out labelled with a
        // heading from further down the file, which is worse than no label, because
        // retrieval then places it under a section it is not in.
        var trails = options.HeadingContext && isMarkdown ? HeadingTrails(lines) : null;

        var chunks = new List<TextChunk>();
        var index = 0;

        foreach (var c in ChunkLines(lines, boundaries, unitBoundaries, maxChars, overlapChars, symbolPattern,
                     isMarkdown, options, trails))
            chunks.Add(c with { Index = index++ });

        return chunks;
    }

    /// <summary>
    /// The heading path in effect at each line, such as "Data model &gt; Point payload". A deeper
    /// heading replaces its peers and discards everything below it, so a stale h3 cannot
    /// trail along underneath the next h2.
    /// </summary>
    private static string?[] HeadingTrails(string[] lines)
    {
        var trails = new string?[lines.Length];
        var stack = new string?[7];
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (IsFenceDelimiter(line)) inFence = !inFence;
            else if (!inFence && TryReadHeading(line, out var heading, out var level))
            {
                stack[level] = heading;
                for (var deeper = level + 1; deeper < stack.Length; deeper++) stack[deeper] = null;
            }

            var parts = stack.Where(h => !string.IsNullOrEmpty(h)).ToList();
            trails[i] = parts.Count == 0 ? null : string.Join(" > ", parts);
        }

        return trails;
    }

    /// <summary>
    /// The 0-based line index at which each extraction unit starts. Units carry character
    /// offsets; the chunker works in lines, so they are converted once here rather than
    /// per chunk.
    /// </summary>
    private static IEnumerable<int> UnitBoundaryLines(ExtractedText extracted, string content)
    {
        var offsets = extracted.Units
            .Select(u => u.StartOffset)
            .Where(o => o > 0 && o <= content.Length)
            .OrderBy(o => o)
            .ToList();

        if (offsets.Count == 0) yield break;

        var line = 0;
        var next = 0;

        for (var i = 0; i < content.Length && next < offsets.Count; i++)
        {
            while (next < offsets.Count && offsets[next] == i)
            {
                yield return line;
                next++;
            }
            if (content[i] == '\n') line++;
        }
    }

    /// <summary>
    /// Size decides WHEN to split; a boundary decides WHERE.
    ///
    /// This used to split at every boundary, which made <c>chunkSize</c> dead
    /// configuration in every mode but <c>none</c>: blank-line mode on prose produced
    /// one chunk per paragraph, measured at a 252-character mean against a 3072
    /// character budget, and two corpora configured 768 and 256 produced byte-identical
    /// output. Chunks that small retrieve badly: there is not enough context in a
    /// paragraph to embed usefully.
    ///
    /// Now the accumulator fills to the budget and then backs up to the most recent
    /// boundary inside the buffer, so a chunk holds as many whole members or paragraphs
    /// as fit and still never ends mid-thought. Falls back to splitting at the current
    /// line when the buffer contains no boundary at all.
    /// </summary>
    /// <param name="trails">Heading trail per line, or null when heading context is off.</param>
    private static IEnumerable<TextChunk> ChunkLines(string[] lines, HashSet<int> boundaries,
        HashSet<int> unitBoundaries, int maxChars, int overlapChars, string? symbolPattern, bool isMarkdown,
        ChunkOptions options, string?[]? trails)
    {
        var start = 0;
        var chars = 0;
        var lastBoundary = -1;

        // How much text had accumulated when lastBoundary was seen. Tracked rather than
        // recomputed so the check below stays O(1) per line.
        var charsAtBoundary = 0;
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

            if (boundaries.Contains(i) && i > start)
            {
                lastBoundary = i;
                charsAtBoundary = chars;
            }

            // A unit boundary ends the current chunk regardless of how little is in it.
            if (unitBoundaries.Contains(i) && i > start && chars > 0)
            {
                yield return Build(Join(lines, start, i), start, i - 1, lastHeading, symbolPattern,
                    TrailAt(trails, start));

                // No overlap across a unit: carrying the tail of chapter 3 into chapter 4
                // is exactly the straddling this is meant to stop.
                start = i;
                lastBoundary = -1;
                charsAtBoundary = 0;
                chars = 0;
            }

            var lineChars = line.Length + 1;

            // A single line that alone exceeds the budget. This used to be emitted whole,
            // on the reasoning that a minified file is unwieldy but not invisible. That
            // was wrong, because the embedding model truncates at its context limit
            // without reporting it. An EPUB whose extractor emitted one line per chapter produced 18
            // chunks averaging 32,000 characters: the book reported itself as indexed
            // while roughly 95% of it existed nowhere in the index.
            //
            // So the line is split. Every piece keeps this line's number, since they are
            // all on it, and a search hit still opens at the right place.
            if (chars == 0 && lineChars > maxChars)
            {
                foreach (var piece in SplitOversizeLine(line, maxChars, overlapChars, options.SentenceAware))
                    yield return Build(piece, i, i, lastHeading, symbolPattern, TrailAt(trails, i));

                start = i + 1;
                lastBoundary = -1;
                continue;
            }

            if (chars > 0 && chars + lineChars > maxChars)
            {
                // Back up to the boundary only if it leaves a chunk worth having.
                //
                // "Size decides when, a boundary decides where" assumes a boundary is near
                // the fill point. When the last one is far behind (a blank line early,
                // then a long listing with none) backing up to it emits a fraction of a
                // chunk, and then the overlap rewind cannot go past previousStart + 1, so
                // the next chunk begins one line later and produces almost the same tiny
                // chunk again. A 458,000-character book of prose interleaved with code
                // came out as 1,051 chunks averaging 388 characters, each a one-line shift
                // of the last, where 70 chunks of ~8,000 were the intent: fifteen times
                // the vectors, the embedding cost and the storage, and a result set full
                // of near-duplicate fragments too small to carry their own context.
                //
                // The threshold is the OVERLAP, not a fraction of the budget, because the
                // overlap is what causes the stall: a chunk must be at least twice it, so
                // that after rewinding the start still advances by at least the overlap.
                //
                // Tying it to the overlap rather than to the budget also keeps deliberate
                // boundaries working. A custom pattern or a markdown heading is a request
                // to split at that point, and a set with no overlap has no stall to
                // prevent: the rule then rejects only a zero-length chunk, as intended.
                var minimumChunk = Math.Max(overlapChars * 2, 1);
                var boundaryIsWorthIt = lastBoundary > start && charsAtBoundary >= minimumChunk;
                var splitAt = boundaryIsWorthIt ? lastBoundary : i;

                yield return Build(Join(lines, start, splitAt), start, splitAt - 1, lastHeading, symbolPattern,
                    TrailAt(trails, start));

                // Overlap is carried by rewinding the start, not by copying text, so
                // line numbers stay exact.
                start = RewindForOverlap(lines, splitAt, start, overlapChars);
                lastBoundary = -1;
                charsAtBoundary = 0;
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
                yield return Build(tail, start, lines.Length - 1, lastHeading, symbolPattern,
                    TrailAt(trails, start));
        }
    }

    /// <summary>
    /// Cuts one over-long line into budget-sized pieces, preferring a word boundary near
    /// the end of each. The only case where the chunker splits within a line; it exists
    /// so that "indexed" cannot mean "the first 8,000 characters were indexed".
    /// </summary>
    private static IEnumerable<string> SplitOversizeLine(
        string line, int maxChars, int overlapChars, bool sentenceAware = false)
    {
        // How far back a cut may reach. A word gap is never far away, so an eighth of the
        // budget is plenty; a sentence end can be a whole sentence away, and with the same
        // narrow window sentence-awareness never fired: every piece still ended
        // mid-sentence, leaving a setting that costs something and does nothing.
        var maxBackup = Math.Max(1, maxChars / 8);
        var sentenceBackup = Math.Max(1, maxChars / 2);
        var pos = 0;

        while (pos < line.Length)
        {
            var end = Math.Min(pos + maxChars, line.Length);

            if (end < line.Length)
            {
                // A sentence end is a better place to cut than a word gap, because half a
                // sentence embeds as something its author never wrote. Falls back to a
                // word boundary when there is no sentence end within reach, which is the
                // usual case for code and for a single very long paragraph.
                var cut = sentenceAware ? LastSentenceEnd(line, pos, end, sentenceBackup) : -1;
                if (cut < 0) cut = line.LastIndexOf(' ', end - 1, Math.Min(end - pos, maxBackup));
                if (cut > pos) end = cut + 1;
            }

            var piece = line[pos..end].Trim();
            if (piece.Length > 0) yield return piece;

            if (end >= line.Length) yield break;

            // Overlap, as between chunks, but never at the cost of progress.
            var next = end - overlapChars;
            pos = next > pos ? next : end;
        }
    }

    /// <summary>
    /// The last sentence terminator within <paramref name="maxBackup"/> characters of
    /// <paramref name="end"/>, or -1. A terminator counts only when followed by a space,
    /// which keeps "e.g." and "3.14" from being read as the end of a thought.
    /// </summary>
    private static int LastSentenceEnd(string line, int pos, int end, int maxBackup)
    {
        var floor = Math.Max(pos, end - maxBackup);

        for (var i = end - 1; i > floor; i--)
        {
            if (line[i] is not ('.' or '!' or '?')) continue;
            if (i + 1 < line.Length && line[i + 1] != ' ') continue;
            return i + 1;   // keep the terminator with the sentence it ends
        }

        return -1;
    }

    private static string? TrailAt(string?[]? trails, int line) =>
        trails is not null && line >= 0 && line < trails.Length ? trails[line] : null;

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
    /// The naive version of this, "starts with # and contains '# '", fired on YAML
    /// comments inside fenced code blocks and labelled a docs chunk with
    /// "spends its first minutes in embedding backoff" as its section. A section that
    /// is confidently wrong is worse than no section, because a reader trusts it.
    /// </summary>
    internal static bool TryReadHeading(string line, out string heading) =>
        TryReadHeading(line, out heading, out _);

    internal static bool TryReadHeading(string line, out string heading, out int level)
    {
        heading = string.Empty;
        level = 0;
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
        level = hashes;
        return true;
    }

    private static TextChunk Build(string content, int startLine, int endLine, string? section,
        string? symbolPattern, string? headingTrail = null) =>
        new()
        {
            Content = content,
            // The trail goes into the EMBEDDED text only. Content stays verbatim, so a
            // file still reconstructs byte-identically from its chunks.
            EmbedText = headingTrail is { Length: > 0 } ? $"{headingTrail}\n\n{content}" : content,
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
