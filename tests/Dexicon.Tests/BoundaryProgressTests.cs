using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// A boundary decides WHERE a chunk splits. It must not decide to split almost nothing.
///
/// Found in a real book. "Crafting Clean Code with JavaScript and React" is prose
/// interleaved with code listings: blank lines are frequent in the prose and absent inside
/// a listing. Its PDF produced 1,051 chunks averaging 388 characters where 70 of ~8,000
/// were intended: seventeen times the vectors, the embedding cost and the storage, and a
/// result set of near-duplicate fragments too small to carry their own context. The
/// EPUB of the same book, whose extractor emits no blank lines, produced 61.
///
/// The mechanism is an interaction, which is why neither half looked wrong alone:
///
///   1. the accumulator fills to the budget, then backs up to the last boundary, which
///      is 486 characters in when the next 11,000 characters are a listing with none;
///   2. the overlap rewind refuses to go past previousStart + 1, so a chunk smaller than
///      the overlap budget leaves the next chunk starting one line later;
///   3. which produces the same tiny chunk again, shifted by a line, until the listing ends.
/// </summary>
public class BoundaryProgressTests
{
    private const int ChunkTokens = 2000;   // 8,000 characters at 4 chars/token
    private const int OverlapTokens = 250;  // 1,000 characters

    /// <summary>Prose with blank lines, then a long stretch with none: the real shape.</summary>
    private static string ProseThenListing(int proseChars, int listingChars)
    {
        var sb = new System.Text.StringBuilder();
        // Short prose lines separated by blanks, as a PDF extractor emits them.
        while (sb.Length < proseChars)
            sb.Append("a short line of prose here\n\n");
        // Then a listing: no blank lines at all.
        while (sb.Length < proseChars + listingChars)
            sb.Append("    const value = compute(input, options, context);\n");
        return sb.ToString();
    }

    private static IReadOnlyList<TextChunk> Chunk(string text) =>
        CodeChunker.Chunk("book.pdf", text, ChunkTokens, OverlapTokens, boundaryMode: "blank-line");

    [Fact]
    public void A_boundary_far_behind_the_fill_point_does_not_shred_the_file()
    {
        // 486 characters of prose then 11,000 of listing is the exact ratio from the book.
        var text = ProseThenListing(proseChars: 500, listingChars: 11_000);

        var chunks = Chunk(text);

        // ~8,000 chars per chunk less 1,000 of overlap: two or three, not dozens.
        chunks.Count.ShouldBeLessThan(6,
            $"{chunks.Count} chunks for {text.Length} characters means the splitter is not advancing");
    }

    [Fact]
    public void No_chunk_is_a_one_line_shift_of_the_one_before_it()
    {
        // The signature of the defect: successive chunks ending on the SAME line, each
        // starting one line later. Cheap to assert and impossible to produce by accident.
        var chunks = Chunk(ProseThenListing(500, 11_000));

        for (var i = 1; i < chunks.Count; i++)
        {
            var shiftedByOneLine = chunks[i].StartLine == chunks[i - 1].StartLine + 1
                                   && chunks[i].EndLine == chunks[i - 1].EndLine;
            shiftedByOneLine.ShouldBeFalse(
                $"chunk {i} is chunk {i - 1} shifted by a line ({chunks[i].StartLine}-{chunks[i].EndLine})");
        }
    }

    [Fact]
    public void Every_chunk_carries_a_useful_amount_of_text()
    {
        // A chunk far below budget is not free: it costs a vector, and it lacks the
        // context that makes an embedding worth searching.
        var chunks = Chunk(ProseThenListing(500, 30_000));
        var budget = ChunkTokens * CodeChunker.CharsPerToken;

        // Twice the overlap is the guarantee: below it the rewind swallows the chunk and
        // the splitter stops advancing. The last chunk is the remainder, so it is exempt.
        var overlapChars = OverlapTokens * CodeChunker.CharsPerToken;
        foreach (var chunk in chunks.Take(chunks.Count - 1))
            chunk.Content.Length.ShouldBeGreaterThanOrEqualTo(overlapChars * 2,
                $"a {chunk.Content.Length}-character chunk against a {budget}-character budget "
                + $"and a {overlapChars}-character overlap");
    }

    [Fact]
    public void A_boundary_near_the_fill_point_is_still_used()
    {
        // The fix must not throw away the reason boundaries exist. With blank lines
        // throughout, chunks should end ON one rather than mid-paragraph.
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 40_000)
            sb.Append("a paragraph of prose that runs on for a while and then stops\n\n");

        var chunks = Chunk(sb.ToString());

        chunks.Count.ShouldBeGreaterThan(3);
        // Every chunk but the last should end where a paragraph ends, not mid-sentence.
        foreach (var chunk in chunks.Take(chunks.Count - 1))
            chunk.Content.TrimEnd().ShouldEndWith("stops");
    }

    [Fact]
    public void Text_with_no_boundaries_at_all_still_fills_its_chunks()
    {
        // The control from the real shelf: a PDF whose extraction contains no blank lines
        // chunked correctly all along, which is why this went unnoticed.
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 40_000)
            sb.Append("a line of continuous prose with no blank lines anywhere in it\n");

        var chunks = Chunk(sb.ToString());
        var budget = ChunkTokens * CodeChunker.CharsPerToken;

        foreach (var chunk in chunks.Take(chunks.Count - 1))
            chunk.Content.Length.ShouldBeGreaterThan(budget / 2);
    }

    [Fact]
    public void The_whole_file_is_still_covered()
    {
        // Splitting differently must not lose text. Chunks overlap, so the union of their
        // line ranges, rather than their concatenation, has to cover every line.
        var text = ProseThenListing(500, 20_000);
        var lineCount = text.Split('\n').Length;

        var chunks = Chunk(text);

        var covered = new HashSet<int>();
        foreach (var chunk in chunks)
            for (var line = chunk.StartLine; line <= chunk.EndLine; line++)
                covered.Add(line);

        // Every line that has content must appear in some chunk.
        for (var line = 1; line < lineCount; line++)
            covered.Contains(line).ShouldBeTrue($"line {line} is in no chunk");
    }
}
