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

    /// <summary>
    /// The chunk was cut to fit the budget. <see cref="StartLine"/> and
    /// <see cref="EndLine"/> describe what is actually here, not what the chunk holds, so a
    /// citation is never a claim about text the caller was not given.
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>Characters of this chunk left out. Zero unless <see cref="Partial"/>.</summary>
    public int OmittedChars { get; init; }
}

public sealed record AssembledContext
{
    public required string Text { get; init; }
    public required IReadOnlyList<Citation> Citations { get; init; }
    public int UsedChars { get; init; }
    public bool Truncated { get; init; }

    /// <summary>Hits left out because the budget was already spent.</summary>
    public int DroppedHits { get; init; }

    /// <summary>
    /// Blocks whose chunk was cut to fit, at most one and always the last.
    ///
    /// Separate from <see cref="Truncated"/>, which says hits were dropped. A budget that
    /// dropped results and one that shortened them are different things to know, and one
    /// flag covering both would mean neither could be acted on.
    /// </summary>
    public int PartialBlocks { get; init; }

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
/// With one exception, at the end. Whole chunks alone meant a budget below the smallest
/// matching chunk returned nothing at all, which reads as "no results" when ten matched,
/// and left whatever space was left over unused. The last block may be cut, so the tail of
/// the budget shows the opening of the next result rather than being wasted. It is cut at
/// a line boundary, says how much it dropped, and its citation reports the lines actually
/// present, so it is never mistaken for a whole one.
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

    /// <summary>
    /// The least a cut block may show before it is not worth its header.
    ///
    /// Below this the caller gets a citation, a filename and two lines of prose, which
    /// costs more to read than it returns. The block is dropped instead, and the note says
    /// what budget would have fitted it.
    /// </summary>
    private const int MinPartialChars = 300;

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
        Block? partial = null;

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
                // What is left after the whole chunks. Spend it on the opening of this one
                // if there is enough for a glimpse worth reading, and stop: anything after
                // this is a worse match, and a passage ending in several fragments is a
                // list of beginnings rather than something to read.
                var room = maxChars - spent - HeaderAllowance;

                // Decided here, not at rendering, and it has to include whether the cut
                // yields anything: a chunk with no line break inside the budget produces no
                // whole lines, and admitting it would leave the run with neither a block
                // nor the rejected cost the note is written from.
                var first = added.OrderBy(p => p.ChunkIndex).First();
                if (existing is null && partial is null && room >= MinPartialChars
                    && CutToLines(first.Content, room).Lines > 0)
                {
                    block.CutTo(room);
                    byKey[key] = block;
                    blocks.Add(block);
                    block.Add(added, hit.Score);
                    partial = block;
                    spent = maxChars;
                    continue;
                }

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
        // The cut block last whatever it scored: it is the tail of the budget, and a
        // fragment in the middle would read as the passage breaking off.
        var ordered = blocks.Where(b => b.Cut is null).OrderByDescending(b => b.Score)
            .Concat(blocks.Where(b => b.Cut is not null));

        foreach (var block in ordered)
        {
            var pieces = block.Pieces.OrderBy(p => p.ChunkIndex).ToList();
            if (pieces.Count == 0) continue;

            var omitted = 0;
            if (block.Cut is int room)
            {
                // One piece, so the rendered text is contiguous and the last line shown can
                // be counted. Stitching several would interleave gap markers and make the
                // citation's end line a guess.
                var first = pieces[0];
                var (shown, lines) = CutToLines(first.Content, room);
                if (lines == 0) continue;

                omitted = first.Content.Length - shown.Length;
                pieces = [first with { Content = shown, EndLine = first.StartLine + lines - 1 }];
            }

            var startLine = pieces.Min(p => p.StartLine);
            var endLine = pieces.Max(p => p.EndLine);
            var location = Locate(block.Hit, startLine, endLine);
            var header = Header(block.Hit, location, startLine, endLine, spansSources);
            var body = Passage.Stitch(pieces.Select(p => (p.StartLine, p.EndLine, p.Content)), lineNumbers);

            if (sb.Length > 0) sb.Append('\n');
            var before = sb.Length;
            sb.Append(header).Append('\n').Append(body);

            // Said in the passage as well as in the response, because the text is what gets
            // pasted into a prompt and the flags are not.
            if (omitted > 0)
            {
                if (body.Length > 0 && !body.EndsWith('\n')) sb.Append('\n');
                sb.Append($"… {omitted:N0} characters of this chunk not shown …\n");
            }

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
                Partial = omitted > 0,
                OmittedChars = omitted,
            });
        }

        var text = sb.ToString();

        // An empty passage with hits behind it reads as "nothing matched", which is a
        // different problem with a different fix, so the difference is stated. It now takes
        // a budget too small for even the first line of the best chunk to get here.
        var partialCount = citations.Count(c => c.Partial);

        string? note = null;
        if (text.Length == 0 && candidates.Count > 0)
            note = $"No result fitted a budget of {maxChars:N0} characters; the smallest is "
                 + $"{smallestRejected:N0}. Raise maxChars, or narrow the query.";
        else if (partialCount > 0)
            note = $"The last block is cut to fit {maxChars:N0} characters. Its citation "
                 + "reports the lines actually present. Raise maxChars for the whole chunk.";

        return new AssembledContext
        {
            Text = text,
            Citations = citations,
            UsedChars = text.Length,
            Truncated = dropped > 0,
            DroppedHits = dropped,
            PartialBlocks = partialCount,
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

    /// <summary>
    /// The longest run of whole lines that fits, and how many there are.
    ///
    /// Whole lines because a chunk is read as text and may be rendered with its line
    /// numbers: cutting mid-line gives a fragment whose number is wrong. A first line
    /// already over the budget yields nothing, and the caller drops the block rather than
    /// printing a header over an empty body.
    /// </summary>
    internal static (string Text, int Lines) CutToLines(string content, int budget)
    {
        if (budget <= 0) return ("", 0);
        if (content.Length <= budget) return (content, CountLines(content));

        var cut = content.LastIndexOf('\n', Math.Min(budget, content.Length - 1));
        if (cut <= 0) return ("", 0);

        var text = content[..cut];
        return (text, CountLines(text));
    }

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.AsSpan().Count('\n') + (text.EndsWith('\n') ? 0 : 1);

    private sealed class Block(SearchHit hit)
    {
        public SearchHit Hit { get; } = hit;
        public List<SearchHit> Pieces { get; } = [];
        public HashSet<int> Indexes { get; } = [];
        public float Score { get; private set; } = float.MinValue;

        /// <summary>Characters this block may render, when it was admitted to fill the tail.</summary>
        public int? Cut { get; private set; }

        public void CutTo(int room) => Cut = room;

        public void Add(IReadOnlyList<SearchHit> pieces, float score)
        {
            foreach (var piece in pieces)
                if (Indexes.Add(piece.ChunkIndex))
                    Pieces.Add(piece);

            if (score > Score) Score = score;
        }
    }
}
