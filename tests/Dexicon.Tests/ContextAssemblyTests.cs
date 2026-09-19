using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// `POST /api/context` packs ranked hits into one passage within a character budget, for
/// a caller that has no agent loop to fetch the rest with.
///
/// Two things here are worth a test rather than a reading. The budget decides what a
/// script pastes into a prompt, and a budget that silently drops the best match while
/// reporting success is the failure mode this project exists to avoid. And two sources of
/// one corpus can hold the same file path, so the grouping that joins chunks of "one
/// file" decides whether the passage is one book or two spliced together.
/// </summary>
public sealed class ContextAssemblyTests
{
    private static string Lines(int from, int to) =>
        string.Join('\n', Enumerable.Range(from, to - from + 1).Select(i => $"line {i}"));

    private static SearchHit Hit(
        string path = "docs/04-ingestion.md", int start = 1, int end = 10, int index = 0,
        float score = 0.9f, string? content = null, string corpus = "docs",
        string? sourceId = "src-1", int? page = null, string? sourceRoot = null) =>
        new()
        {
            CorpusId = "corpus-1",
            CorpusName = corpus,
            SourceId = sourceId,
            SourceRoot = sourceRoot,
            FilePath = path,
            StartLine = start,
            EndLine = end,
            ChunkIndex = index,
            Page = page,
            Score = score,
            Content = content ?? Lines(start, end),
        };

    private static ContextCandidate Alone(SearchHit hit) => new(hit, [hit]);

    [Fact]
    public void OneHitBecomesOneBlockWithItsCitation()
    {
        var result = ContextAssembler.Assemble([Alone(Hit())], maxChars: 8_000, lineNumbers: false);

        result.Citations.Count.ShouldBe(1);
        result.Citations[0].Location.ShouldBe("docs/04-ingestion.md:1-10");
        result.Citations[0].Corpus.ShouldBe("docs");
        result.Text.ShouldContain("docs/04-ingestion.md:1-10 (corpus: docs)");
        result.Text.ShouldContain("line 5");
        result.Truncated.ShouldBeFalse();
        result.DroppedHits.ShouldBe(0);
    }

    [Fact]
    public void UsedCharsIsMeasuredFromTheTextRatherThanEstimated()
    {
        var result = ContextAssembler.Assemble([Alone(Hit())], maxChars: 8_000, lineNumbers: false);

        result.UsedChars.ShouldBe(result.Text.Length);
    }

    [Fact]
    public void TwoHitsInOneFileAreJoinedIntoOnePassage()
    {
        // Overlapping chunks, exactly as the chunker emits them.
        var result = ContextAssembler.Assemble(
        [
            Alone(Hit(start: 1, end: 10, index: 0, score: 0.9f)),
            Alone(Hit(start: 8, end: 20, index: 1, score: 0.7f)),
        ], maxChars: 8_000, lineNumbers: false);

        result.Citations.Count.ShouldBe(1);
        result.Citations[0].StartLine.ShouldBe(1);
        result.Citations[0].EndLine.ShouldBe(20);

        // The shared lines appear once: the block is stitched, not concatenated.
        var body = result.Text[(result.Text.IndexOf('\n') + 1)..];
        body.Split('\n').Count(l => l == "line 9").ShouldBe(1);
    }

    [Fact]
    public void ABlockIsScoredByItsBestHitAndBlocksAreOrderedByScore()
    {
        var result = ContextAssembler.Assemble(
        [
            Alone(Hit(path: "a.md", score: 0.9f)),
            Alone(Hit(path: "b.md", score: 0.95f)),
        ], maxChars: 8_000, lineNumbers: false);

        // Rank order, not arrival order: a caller that reads only the start of the passage
        // reads the strongest part of it.
        result.Citations.Select(c => c.FilePath).ShouldBe(["b.md", "a.md"]);
        result.Text.IndexOf("b.md", StringComparison.Ordinal)
            .ShouldBeLessThan(result.Text.IndexOf("a.md", StringComparison.Ordinal));
    }

    [Fact]
    public void OneFilePathInTwoSourcesStaysTwoBlocks()
    {
        // The same title held under two source roots is two different books. Merging them
        // produces a passage that reads as continuous and is not.
        var result = ContextAssembler.Assemble(
        [
            Alone(Hit(path: "logic.pdf", sourceId: "ai", sourceRoot: "orly/AI", score: 0.9f)),
            Alone(Hit(path: "logic.pdf", sourceId: "phil", sourceRoot: "orly/Philosophy", score: 0.8f)),
        ], maxChars: 8_000, lineNumbers: false);

        result.Citations.Count.ShouldBe(2);
        result.Citations.Select(c => c.SourceRoot).ShouldBe(["orly/AI", "orly/Philosophy"]);
        result.Text.ShouldContain("in orly/AI");
        result.Text.ShouldContain("in orly/Philosophy");
    }

    [Fact]
    public void OneSourceRootIsNotRepeatedInEveryHeader()
    {
        // Found by calling the endpoint: a corpus with a single source rendered
        // "(corpus: docs · in docs)" on every block, which tells the reader nothing.
        var result = ContextAssembler.Assemble(
        [
            Alone(Hit(path: "a.md", sourceRoot: "docs", score: 0.9f)),
            Alone(Hit(path: "b.md", sourceRoot: "docs", score: 0.8f)),
        ], maxChars: 8_000, lineNumbers: false);

        result.Text.ShouldNotContain("· in docs");

        // The citation still carries it, because a consumer resolving the path needs it.
        result.Citations[0].SourceRoot.ShouldBe("docs");
    }

    [Fact]
    public void AHitThatDoesNotFitIsDroppedAndCounted()
    {
        var big = new string('x', 3_000);

        var result = ContextAssembler.Assemble(
        [
            Alone(Hit(path: "a.md", content: big, score: 0.9f)),
            Alone(Hit(path: "b.md", content: big, score: 0.8f)),
        ], maxChars: 3_200, lineNumbers: false);

        result.Citations.Count.ShouldBe(1);
        result.Citations[0].FilePath.ShouldBe("a.md");
        result.Truncated.ShouldBeTrue();
        result.DroppedHits.ShouldBe(1);
    }

    [Fact]
    public void TheBudgetIsNotExceeded()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(i => Alone(Hit(path: $"f{i}.md", content: new string('x', 900), score: 1f - i / 100f)))
            .ToList();

        var result = ContextAssembler.Assemble(candidates, maxChars: 5_000, lineNumbers: false);

        result.UsedChars.ShouldBeLessThanOrEqualTo(5_000);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public void ASecondHitInAFileAlreadyIncludedIsNotChargedTwice()
    {
        // Both hits are the same chunk. The second adds nothing, so it must not be
        // counted against the budget and must not be reported as dropped.
        var hit = Hit(content: new string('x', 2_000));

        var result = ContextAssembler.Assemble([Alone(hit), Alone(hit)], maxChars: 2_500, lineNumbers: false);

        result.Citations.Count.ShouldBe(1);
        result.DroppedHits.ShouldBe(0);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void NothingFittingSaysSoAndNamesTheCost()
    {
        var result = ContextAssembler.Assemble(
            [Alone(Hit(content: new string('x', 9_000)))], maxChars: 500, lineNumbers: false);

        result.Text.ShouldBeEmpty();
        result.Citations.ShouldBeEmpty();
        result.Note.ShouldNotBeNull();
        result.Note.ShouldContain("9,080");   // the chunk plus the header allowance
        result.Note.ShouldContain("maxChars");
    }

    [Fact]
    public void NoHitsIsNotAnError()
    {
        var result = ContextAssembler.Assemble([], maxChars: 8_000, lineNumbers: false);

        result.Text.ShouldBeEmpty();
        result.Citations.ShouldBeEmpty();
        result.Truncated.ShouldBeFalse();
        result.Note.ShouldBeNull();
    }

    [Fact]
    public void ADocumentKeepsItsUnitAnchorAndAlsoCarriesLines()
    {
        // A page anchor is what a reply cites; the line range is what get_context takes.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(path: "moby-dick.epub", start: 4210, end: 4260, page: 7))],
            maxChars: 8_000, lineNumbers: false);

        result.Citations[0].Location.ShouldBe("moby-dick.epub#chapter=7");
        result.Citations[0].Page.ShouldBe(7);
        result.Text.ShouldContain("moby-dick.epub#chapter=7 · lines 4210-4260");
    }

    [Fact]
    public void LineNumbersAreOffUnlessAskedFor()
    {
        var plain = ContextAssembler.Assemble([Alone(Hit())], maxChars: 8_000, lineNumbers: false);
        var numbered = ContextAssembler.Assemble([Alone(Hit())], maxChars: 8_000, lineNumbers: true);

        plain.Text.ShouldContain("\nline 1\n");
        numbered.Text.ShouldContain("\n1: line 1\n");
    }

    [Fact]
    public void NeighboursJoinTheBlockTheyBelongTo()
    {
        var hit = Hit(start: 11, end: 20, index: 1, score: 0.9f);
        var before = Hit(start: 1, end: 10, index: 0);
        var after = Hit(start: 21, end: 30, index: 2);

        var result = ContextAssembler.Assemble(
            [new ContextCandidate(hit, [before, hit, after])], maxChars: 8_000, lineNumbers: false);

        result.Citations.Count.ShouldBe(1);
        result.Citations[0].StartLine.ShouldBe(1);
        result.Citations[0].EndLine.ShouldBe(30);
        result.Text.ShouldContain("line 3");
        result.Text.ShouldContain("line 28");
    }

    // ── The last block may be cut ────────────────────────────────────────────
    //
    // Whole chunks alone meant a budget under the smallest match returned nothing, which
    // reads as "no results" when ten matched, and left the tail of the budget unspent.

    [Fact]
    public void ABudgetUnderTheChunkNowReturnsItsOpening()
    {
        // 200 lines is roughly 1,800 characters; the budget fits a few hundred of them.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 200))], maxChars: 500, lineNumbers: false);

        result.Text.ShouldNotBeEmpty();
        result.Citations.Count.ShouldBe(1);
        result.PartialBlocks.ShouldBe(1);
        result.Citations[0].Partial.ShouldBeTrue();
        result.Citations[0].OmittedChars.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ACutBlockSaysSoInThePassageItself()
    {
        // The flags do not travel: the text is what gets pasted into a prompt.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 200))], maxChars: 500, lineNumbers: false);

        result.Text.ShouldContain("characters of this chunk not shown");
        result.Note.ShouldNotBeNull();
        result.Note.ShouldContain("cut to fit");
    }

    [Fact]
    public void ACutBlockCitesTheLinesItActuallyHas()
    {
        // The whole point of narrowing: a citation must never claim text the caller was
        // not given, or quoting it back produces a range nobody can check.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 200))], maxChars: 500, lineNumbers: false);

        var citation = result.Citations[0];
        citation.EndLine.ShouldBeLessThan(200);
        citation.StartLine.ShouldBe(1);
        result.Text.ShouldContain($"line {citation.EndLine}");
        result.Text.ShouldNotContain($"line {citation.EndLine + 1}");
    }

    [Fact]
    public void ACutFallsOnALineBoundary()
    {
        // A chunk is read as text and may be rendered with its line numbers, so half a
        // line is a fragment whose number is wrong.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 200))], maxChars: 600, lineNumbers: false);

        var body = result.Text.Split('\n')
            .Where(l => l.StartsWith("line ", StringComparison.Ordinal));

        foreach (var line in body)
            line.ShouldMatch(@"^line \d+$");
    }

    [Fact]
    public void TheBudgetIsStillHonoured()
    {
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 400))], maxChars: 900, lineNumbers: false);

        // The disclosure line is added after the cut, so allow for it rather than
        // pretending the figure is exact.
        result.UsedChars.ShouldBeLessThan(1_100);
        result.UsedChars.ShouldBeGreaterThan(300);
    }

    [Fact]
    public void OnlyOneBlockIsCut_AndItComesLast()
    {
        // A passage ending in several fragments is a list of beginnings, not something to
        // read, so the run stops at the first cut.
        var result = ContextAssembler.Assemble(
            [
                Alone(Hit(path: "a.md", start: 1, end: 20, score: 0.9f)),
                Alone(Hit(path: "b.md", start: 1, end: 200, score: 0.8f)),
                Alone(Hit(path: "c.md", start: 1, end: 200, score: 0.7f)),
            ],
            maxChars: 900, lineNumbers: false);

        result.PartialBlocks.ShouldBe(1);
        result.Citations.Count(c => c.Partial).ShouldBe(1);
        result.Citations[^1].Partial.ShouldBeTrue();
        result.Citations[0].Partial.ShouldBeFalse();
    }

    [Fact]
    public void AWholeChunkThatFitsIsNeverCut()
    {
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 10))], maxChars: 8_000, lineNumbers: false);

        result.PartialBlocks.ShouldBe(0);
        result.Citations[0].Partial.ShouldBeFalse();
        result.Citations[0].OmittedChars.ShouldBe(0);
        result.Text.ShouldNotContain("not shown");
    }

    [Fact]
    public void TooLittleRoomDropsRatherThanTeases()
    {
        // Under the floor the caller gets a citation, a filename and two lines of prose,
        // which costs more to read than it returns.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 200))], maxChars: 200, lineNumbers: false);

        result.Text.ShouldBeEmpty();
        result.PartialBlocks.ShouldBe(0);
        result.DroppedHits.ShouldBe(1);
        result.Note.ShouldNotBeNull();
        result.Note.ShouldContain("Raise maxChars");
    }

    [Fact]
    public void CuttingIsSeparateFromDropping()
    {
        // `truncated` says hits were dropped; `partialBlocks` says one was shortened. A
        // caller acts on them differently, so one flag covering both would serve neither.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 400))], maxChars: 900, lineNumbers: false);

        result.PartialBlocks.ShouldBe(1);
        result.DroppedHits.ShouldBe(0);
        result.Truncated.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveBudgetYieldsNothing(int budget)
    {
        var result = ContextAssembler.Assemble(
            [Alone(Hit(start: 1, end: 200))], maxChars: budget, lineNumbers: false);

        result.Text.ShouldBeEmpty();
        result.PartialBlocks.ShouldBe(0);
    }

    [Fact]
    public void AChunkWithNoLineBreakCannotBeCut()
    {
        // Nothing to cut on, so it is dropped and the note names the cost, rather than the
        // run losing both the block and the figure it would have reported.
        var result = ContextAssembler.Assemble(
            [Alone(Hit(content: new string('x', 9_000)))], maxChars: 2_000, lineNumbers: false);

        result.Text.ShouldBeEmpty();
        result.PartialBlocks.ShouldBe(0);
        result.Note.ShouldNotBeNull();
        result.Note.ShouldContain("9,080");
    }
}
