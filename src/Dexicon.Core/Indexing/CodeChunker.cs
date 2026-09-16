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

        var boundaries = ResolveBoundaries(boundaryMode, language, customBoundaryPattern, lines);
        var symbolPattern = LanguageMap.SymbolPattern(language);
        var isMarkdown = string.Equals(language, "markdown", StringComparison.Ordinal);

        var chunks = new List<TextChunk>();
        var index = 0;
        var segmentStart = 0;

        foreach (var segmentEnd in boundaries.Append(lines.Length))
        {
            if (segmentEnd <= segmentStart) continue;
            foreach (var c in ChunkSegment(lines, segmentStart, segmentEnd, maxChars, overlapChars, symbolPattern, isMarkdown))
                chunks.Add(c with { Index = index++ });
            segmentStart = segmentEnd;
        }

        return chunks;
    }

    private static IEnumerable<TextChunk> ChunkSegment(string[] lines, int from, int to,
        int maxChars, int overlapChars, string? symbolPattern, bool isMarkdown)
    {
        var buffer = new StringBuilder();
        var bufferStart = from;
        var lastHeading = (string?)null;
        var inFence = false;

        for (var i = from; i < to; i++)
        {
            var line = lines[i];

            if (isMarkdown)
            {
                if (IsFenceDelimiter(line)) inFence = !inFence;
                else if (!inFence && TryReadHeading(line, out var heading)) lastHeading = heading;
            }

            // A single line longer than the whole budget still becomes its own chunk
            // rather than being dropped — minified files are ugly, not invisible.
            if (buffer.Length > 0 && buffer.Length + line.Length + 1 > maxChars)
            {
                yield return Build(buffer.ToString(), bufferStart, i - 1, lastHeading, symbolPattern);

                var carried = CarryOverlap(lines, i, bufferStart, overlapChars, out var newStart);
                buffer.Clear();
                buffer.Append(carried);
                bufferStart = newStart;
            }

            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(line);
        }

        if (buffer.Length > 0 && buffer.ToString().Trim().Length > 0)
            yield return Build(buffer.ToString(), bufferStart, to - 1, lastHeading, symbolPattern);
    }

    /// <summary>Walk back from the split point until the overlap budget is spent.</summary>
    private static string CarryOverlap(string[] lines, int splitAt, int bufferStart, int overlapChars, out int newStart)
    {
        if (overlapChars <= 0) { newStart = splitAt; return string.Empty; }

        var taken = 0;
        var first = splitAt;
        while (first > bufferStart && taken + lines[first - 1].Length + 1 <= overlapChars)
        {
            first--;
            taken += lines[first].Length + 1;
        }

        newStart = first;
        return first >= splitAt ? string.Empty : string.Join('\n', lines[first..splitAt]);
    }

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
