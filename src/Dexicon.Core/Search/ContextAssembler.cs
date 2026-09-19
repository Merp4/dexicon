using System.Text;

namespace Dexicon.Core.Search;

/// <summary>
/// One search hit together with the chunks that will be rendered for it: its own, plus
/// any neighbours fetched to read around it.
/// </summary>
/// <param name="Hit">Carries the metadata a citation is written from.</param>
/// <param name="Pieces">Chunks of one file in file order, including the hit's own.</param>
public sealed record ContextCandidate(SearchHit Hit, IReadOnlyList<SearchHit> Pieces);

/// <summary>Where one block of the assembled passage came from.</summary>
public sealed record Citation
{
    /// <summary>The corpus as the caller may name it again: `books`, or `books:fine`.</summary>
    public required string Corpus { get; init; }

    /// <summary>The source root, e.g. `orly/AI`. Null for a corpus with one source.</summary>
    public string? SourceRoot { get; init; }

    public required string FilePath { get; init; }

    /// <summary>`file:start-end`, or `file#page=n` for a document. What a reply quotes.</summary>
    public required string Location { get; init; }

    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public int? Page { get; init; }
    public string? Section { get; init; }

    /// <summary>The best score among the hits that contributed to this block.</summary>
    public float Score { get; init; }

    /// <summary>Characters this block contributed, header included.</summary>
    public int Chars { get; init; }
}

public sealed record AssembledContext
{
    public required string Text { get; init; }
    public required IReadOnlyList<Citation> Citations { get; init; }
    public int UsedChars { get; init; }
    public bool Truncated { get; init; }

    /// <summary>Hits left out because the budget was already spent.</summary>
    public int DroppedHits { get; init; }

    /// <summary>Set when the result needs explaining rather than reading.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Packs ranked hits into one passage within a character budget.
///
/// The unit is a whole chunk, not a window of one. A search result is a preview, sized so
/// an agent can judge whether a hit is worth reading; this is the passage itself, and
/// handing back the middle of it would make the caller fetch the rest.
///
/// The budget is characters because no tokenizer ships, and the model doing the reading
/// is not the model that did the embedding, so a token figure here would be an estimate
/// presented as a budget. See docs/decisions.md D-16, D-27 and D-29.
/// </summary>
public static class ContextAssembler
{
    /// <summary>
    /// Room for a block's header line. An estimate used only while deciding what fits;
    /// the reported figure is measured from the rendered text.
    /// </summary>
    private const int HeaderAllowance = 80;

    /// <param name="candidates">Hits in rank order, best first.</param>
    /// <param name="maxChars">The budget for the whole passage.</param>
    /// <param name="lineNumbers">Prefix each line with its number in the file.</param>
    public static AssembledContext Assemble(
        IReadOnlyList<ContextCandidate> candidates, int maxChars, bool lineNumbers)
    {
        // Chunks of one file, keyed so that two sources holding the same filename stay
        // apart. A file_path is relative to its source root, so within a corpus it is not
        // unique, and merging two books that share a name produces a passage that reads
        // as continuous and is not.
        var blocks = new List<Block>();
        var byKey = new Dictionary<(string Corpus, string? Source, string Path), Block>();

        var spent = 0;
        var dropped = 0;
        var smallestRejected = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var hit = candidate.Hit;
            var key = (hit.CorpusName ?? hit.CorpusId, hit.SourceId, hit.FilePath);

            var existing = byKey.TryGetValue(key, out var found) ? found : null;
            var block = existing ?? new Block(hit);

            // Only what this hit adds. Two hits in one file commonly overlap, and charging
            // for the same chunk twice would drop a later hit that costs nothing.
            var added = candidate.Pieces
                .Where(p => !block.Indexes.Contains(p.ChunkIndex))
                .ToList();

            var cost = added.Sum(p => p.Content.Length) + (existing is null ? HeaderAllowance : 0);

            if (added.Count > 0 && spent + cost > maxChars)
            {
                dropped++;
                smallestRejected = Math.Min(smallestRejected, cost);
                continue;
            }

            if (existing is null)
            {
                byKey[key] = block;
                blocks.Add(block);
            }

            spent += cost;
            block.Add(added, hit.Score);
        }

        var sb = new StringBuilder();
        var citations = new List<Citation>(blocks.Count);

        // Only when it tells the reader something. A corpus with one source would print
        // "(corpus: docs · in docs)" on every block; a corpus with ten needs it, because
        // a file path is relative to its source root and two of its books share one.
        // The citation carries the root either way.
        var spansSources = blocks
            .Select(b => b.Hit.SourceRoot).Where(r => r is { Length: > 0 })
            .Distinct(StringComparer.Ordinal).Count() > 1;

        // Best first. Truncation drops the worst matches, so the caller that reads only
        // the start of the passage reads the strongest part of it.
        foreach (var block in blocks.OrderByDescending(b => b.Score))
        {
            var pieces = block.Pieces.OrderBy(p => p.ChunkIndex).ToList();
            if (pieces.Count == 0) continue;

            var startLine = pieces.Min(p => p.StartLine);
            var endLine = pieces.Max(p => p.EndLine);
            var location = Locate(block.Hit, startLine, endLine);
            var header = Header(block.Hit, location, startLine, endLine, spansSources);
            var body = Passage.Stitch(pieces.Select(p => (p.StartLine, p.EndLine, p.Content)), lineNumbers);

            if (sb.Length > 0) sb.Append('\n');
            var before = sb.Length;
            sb.Append(header).Append('\n').Append(body);

            citations.Add(new Citation
            {
                Corpus = block.Hit.CorpusName ?? block.Hit.CorpusId,
                SourceRoot = block.Hit.SourceRoot,
                FilePath = block.Hit.FilePath,
                Location = location,
                StartLine = startLine,
                EndLine = endLine,
                Page = block.Hit.Page,
                Section = block.Hit.Section,
                Score = block.Score,
                Chars = sb.Length - before,
            });
        }

        var text = sb.ToString();

        // An empty passage with hits behind it reads as "nothing matched", which is a
        // different problem with a different fix, so the difference is stated.
        string? note = null;
        if (text.Length == 0 && candidates.Count > 0)
            note = $"No result fitted a budget of {maxChars:N0} characters; the smallest is "
                 + $"{smallestRejected:N0}. Raise maxChars, or narrow the query.";

        return new AssembledContext
        {
            Text = text,
            Citations = citations,
            UsedChars = text.Length,
            Truncated = dropped > 0,
            DroppedHits = dropped,
            Note = note,
        };
    }

    /// <summary>
    /// The citation for a block spanning several chunks. A document's anchor comes from
    /// the hit itself, which already knows whether its unit is a page, a chapter or a
    /// slide; a line range is rebuilt from the merged span.
    /// </summary>
    private static string Locate(SearchHit hit, int startLine, int endLine) =>
        hit.Page is not null ? hit.Location
        : startLine == endLine ? $"{hit.FilePath}:{startLine}"
        : $"{hit.FilePath}:{startLine}-{endLine}";

    private static string Header(
        SearchHit hit, string location, int startLine, int endLine, bool spansSources)
    {
        var sb = new StringBuilder(location);

        // A page anchor cites the passage and cannot be passed back to get_context, whose
        // handle is a line. Both are given so that reading on from here is possible.
        if (hit.Page is not null) sb.Append($" · lines {startLine}-{endLine}");

        sb.Append($" (corpus: {hit.CorpusName ?? hit.CorpusId}");
        if (spansSources && hit.SourceRoot is { Length: > 0 } root) sb.Append($" · in {root}");
        sb.Append(')');

        if (hit.Section is { Length: > 0 } section) sb.Append($" · {section}");
        return sb.ToString();
    }

    private sealed class Block(SearchHit hit)
    {
        public SearchHit Hit { get; } = hit;
        public List<SearchHit> Pieces { get; } = [];
        public HashSet<int> Indexes { get; } = [];
        public float Score { get; private set; } = float.MinValue;

        public void Add(IReadOnlyList<SearchHit> pieces, float score)
        {
            foreach (var piece in pieces)
                if (Indexes.Add(piece.ChunkIndex))
                    Pieces.Add(piece);

            if (score > Score) Score = score;
        }
    }
}
