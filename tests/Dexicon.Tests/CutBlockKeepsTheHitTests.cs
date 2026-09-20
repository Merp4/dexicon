using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// A block too big for the remaining budget is cut down to one piece. Which piece decides
/// whether the passage still contains what was found.
///
/// It took <c>pieces[0]</c>, the lowest chunk index, which with neighbours is the chunk
/// furthest BEFORE the hit. So asking for surrounding context returned text that did not
/// contain the match: measured over 55 queries at a 1,500-character budget, the matched
/// line was absent from 43 of them at two neighbours and from none at zero. Nothing
/// failed, the budget was full, and the answer simply was not in it.
///
/// This is the failure 05-search.md records for the search window — "head truncation was
/// the obvious implementation and loses the answer" — arriving one layer up.
/// </summary>
public sealed class CutBlockKeepsTheHitTests
{
    private static string Body(string marker) => string.Join('\n',
        Enumerable.Range(0, 12).Select(i => $"{marker} line {i} with enough text to cost something"));

    private static SearchHit Piece(int index, int start, string marker) => new()
    {
        CorpusId = "corpus-1",
        CorpusName = "docs",
        SourceId = "src-1",
        FilePath = "docs/x.md",
        StartLine = start,
        EndLine = start + 11,
        ChunkIndex = index,
        Score = 0.9f,
        Content = Body(marker),
    };

    /// <summary>Hit at chunk 5, with two neighbours either side, as ExpandAsync builds it.</summary>
    private static ContextCandidate Expanded()
    {
        var before2 = Piece(3, 100, "BEFORE2");
        var before1 = Piece(4, 112, "BEFORE1");
        var hit = Piece(5, 124, "HIT");
        var after1 = Piece(6, 136, "AFTER1");
        var after2 = Piece(7, 148, "AFTER2");
        return new ContextCandidate(hit, [before2, before1, hit, after1, after2]);
    }

    [Fact]
    public void ACutBlockKeepsTheChunkThatMatched()
    {
        // The whole defect in one assertion. A budget that fits roughly one chunk must
        // spend it on the hit, not on the text two chunks before it.
        var result = ContextAssembler.Assemble([Expanded()], maxChars: 900, lineNumbers: false);

        result.Text.ShouldContain("HIT line");
        result.Text.ShouldNotContain("BEFORE2 line");
    }

    [Fact]
    public void TheCitationNamesLinesTheHitIsInside()
    {
        // A citation pointing at lines the match is not in is worse than a short passage:
        // it sends the reader to the wrong part of the file with no indication.
        var result = ContextAssembler.Assemble([Expanded()], maxChars: 900, lineNumbers: false);

        var c = result.Citations.ShouldHaveSingleItem();
        c.StartLine.ShouldBeLessThanOrEqualTo(124);
        c.EndLine.ShouldBeGreaterThanOrEqualTo(124);
    }

    [Fact]
    public void AnUncutBlockStillHoldsItsNeighbours()
    {
        // The fix must not narrow a block that fits. Given room for all five pieces, all
        // five are still there, which is the point of asking for neighbours at all.
        var result = ContextAssembler.Assemble([Expanded()], maxChars: 8_000, lineNumbers: false);

        foreach (var marker in new[] { "BEFORE2", "BEFORE1", "HIT", "AFTER1", "AFTER2" })
            result.Text.ShouldContain($"{marker} line");
    }

    [Fact]
    public void WithNoNeighboursNothingChanges()
    {
        // The hit is the only piece, so it is both pieces[0] and the match: the path that
        // was already correct. Pinned so the fix is known not to have moved the case that
        // carried the behaviour before it.
        //
        // 450 is chosen to land between two other behaviours rather than arbitrarily. The
        // piece is about 576 characters, so it must not fit whole; and the room left after
        // the header has to stay above MinPartialChars, or the block is dropped entire
        // instead of cut, which is a different rule and not what this pins.
        var hit = Piece(5, 124, "HIT");
        var result = ContextAssembler.Assemble(
            [new ContextCandidate(hit, [hit])], maxChars: 450, lineNumbers: false);

        result.PartialBlocks.ShouldBe(1);
        result.Text.ShouldContain("HIT line 0");
    }

    /// <summary>
    /// The second half of the same disagreement. Selection and rendering also sized the
    /// budget differently: selection subtracted a flat 80-character allowance for the
    /// header, rendering subtracted the header it was about to write. A path long enough
    /// to run past the allowance, which a book title reaches easily, let selection admit
    /// a block that rendering then found no whole line in and skipped — after the budget
    /// had been spent on it and without recording a rejection. The result was a hit that
    /// vanished while <c>DroppedHits</c> said nothing was dropped.
    /// </summary>
    [Fact]
    public void ALongHeaderDoesNotAdmitABlockRenderingWillSkip()
    {
        // 200 characters of path, so the header runs to 219 against the flat 80.
        var path = new string('p', 197) + ".md";

        // 300-character lines with no spaces in them: the word-boundary fallback cannot
        // rescue a budget with no newline inside it, so the cut yields nothing at all.
        var dense = string.Join('\n', Enumerable.Repeat(new string('x', 300), 3));

        SearchHit At(string p, string content, int end) => new()
        {
            CorpusId = "corpus-1",
            CorpusName = "docs",
            SourceId = "src-1",
            FilePath = p,
            StartLine = 1,
            EndLine = end,
            ChunkIndex = 0,
            Score = p == "a.md" ? 0.9f : 0.5f,
            Content = content,
        };

        var first = At("a.md", new string('a', 420), 5);
        var second = At(path, dense, 3);

        // 1,000 leaves 500 after the first block. Selection used to see 356 of that as
        // room, enough to clear MinPartialChars and to find one line; rendering sees 216,
        // which holds neither.
        var result = ContextAssembler.Assemble(
            [new ContextCandidate(first, [first]), new ContextCandidate(second, [second])],
            maxChars: 1_000, lineNumbers: false);

        result.Citations.ShouldHaveSingleItem().FilePath.ShouldBe("a.md");
        result.PartialBlocks.ShouldBe(0);

        // The part that was silent: the hit is rejected, so it is counted and the caller
        // is told the budget cost them a result.
        result.DroppedHits.ShouldBe(1);
        result.Truncated.ShouldBeTrue();
    }
}
