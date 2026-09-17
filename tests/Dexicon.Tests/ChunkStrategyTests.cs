using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// The four meaning-preserving strategies a chunk set can turn on. Each is off by
/// default, so every test here also asserts that the plain path is unchanged. A strategy
/// that alters output when it was not asked for is a regression rather than a feature.
/// </summary>
public sealed class ChunkStrategyTests
{
    private const string Doc = """
        # Data model

        Two stores, with a strict division between them.

        ## Point payload

        Every point carries the corpus it belongs to and the file it came from.

        ### Storage budget

        A payload is repeated per point, so every field costs the chunk count.

        ## Lifecycle

        Deleting a corpus deletes its points.
        """;

    // ── Heading context ──────────────────────────────────────────────────────

    [Fact]
    public void HeadingContextPrependsTheTrailToTheEmbeddedTextOnly()
    {
        var chunks = CodeChunker.Chunk("doc.md", Doc, new ChunkOptions
        {
            ChunkSizeTokens = 32, OverlapTokens = 0, BoundaryMode = "blank-line", HeadingContext = true,
        });

        var deep = chunks.First(c => c.Content.Contains("costs the chunk count", StringComparison.Ordinal));

        deep.EmbedText.ShouldContain("Data model > Point payload > Storage budget");

        // The distinction that matters: Content is what search returns, what get_context
        // stitches and what a resource read rebuilds a file from. Putting the trail in it
        // would insert lines the file never had.
        deep.Content.ShouldNotContain("Data model > Point payload");
        deep.Content.Trim().ShouldStartWith("A payload is repeated");
    }

    [Fact]
    public void ADeeperHeadingReplacesItsPeersAndDiscardsWhatWasUnderThem()
    {
        var lines = Doc.Split('\n');

        var chunks = CodeChunker.Chunk("doc.md", Doc, new ChunkOptions
        {
            ChunkSizeTokens = 32, OverlapTokens = 0, BoundaryMode = "blank-line", HeadingContext = true,
        });

        // The property that the bug broke: a chunk's trail can only mention headings at or
        // BEFORE where it starts. The trail used to be read from a cursor that had already
        // run ahead to find the split point, so chunks were labelled with a section further
        // down the file, and incorrectly.
        foreach (var chunk in chunks.Where(c => c.EmbedText != c.Content))
        {
            var trail = chunk.EmbedText[..chunk.EmbedText.IndexOf("\n\n", StringComparison.Ordinal)];

            foreach (var heading in trail.Split(" > "))
            {
                var headingLine = Array.FindIndex(lines, l => l.TrimStart('#', ' ') == heading && l.StartsWith('#'));
                headingLine.ShouldBeLessThan(chunk.StartLine,
                    $"'{heading}' appears after the chunk it labels (line {chunk.StartLine})");
            }
        }

        // And the narrowing itself: an h2 discards the h3 that was under the previous h2.
        var lifecycle = chunks.First(c => c.Content.Contains("## Lifecycle", StringComparison.Ordinal));
        chunks.SkipWhile(c => c != lifecycle).Skip(1).FirstOrDefault()?.EmbedText
            .ShouldNotContain("Storage budget");
    }

    [Fact]
    public void WithoutHeadingContextTheEmbeddedTextIsTheContent()
    {
        var chunks = CodeChunker.Chunk("doc.md", Doc, new ChunkOptions
        {
            ChunkSizeTokens = 32, OverlapTokens = 0, BoundaryMode = "blank-line",
        });

        chunks.ShouldAllBe(c => c.EmbedText == c.Content);
    }

    // ── Unit-aware boundaries ────────────────────────────────────────────────

    [Fact]
    public void UnitAwareSplitsAtTheDocumentsOwnUnits()
    {
        // Three "pages" that would otherwise sit comfortably in one chunk.
        const string text = "Page one content here.\nPage two content here.\nPage three content here.\n";
        var units = new List<ExtractedUnit>
        {
            new(1, 0, "Page 1"),
            new(2, text.IndexOf("Page two", StringComparison.Ordinal), "Page 2"),
            new(3, text.IndexOf("Page three", StringComparison.Ordinal), "Page 3"),
        };

        var extracted = new ExtractedText(text, units);

        // A budget big enough to hold the whole thing, so the UNITS are what force the
        // split rather than the size. With a small budget both sides split anyway and the
        // test would pass without the feature doing anything.
        var withUnits = CodeChunker.Chunk("book.pdf", text, new ChunkOptions
        {
            ChunkSizeTokens = 512, OverlapTokens = 0, BoundaryMode = "none", UnitAware = true,
        }, extracted);

        var withoutUnits = CodeChunker.Chunk("book.pdf", text, new ChunkOptions
        {
            ChunkSizeTokens = 512, OverlapTokens = 0, BoundaryMode = "none",
        }, extracted);

        withUnits.Count.ShouldBeGreaterThan(withoutUnits.Count,
            "a unit boundary is a split point; ignoring them lets a chunk straddle two pages");

        // And no chunk should contain text from two different pages.
        withUnits.ShouldAllBe(c =>
            !(c.Content.Contains("Page one") && c.Content.Contains("Page three")));
    }

    [Fact]
    public void UnitAwareIsHarmlessWhenThereAreNoUnits()
    {
        // Code has no pages. Asking for unit boundaries must not change anything.
        const string code = "class A\n{\n    void M() { }\n}\n";
        var options = new ChunkOptions { ChunkSizeTokens = 64, OverlapTokens = 0, BoundaryMode = "none" };

        // Compared on content: TextChunk holds an IReadOnlyList of symbols, and record
        // equality on a list is reference equality, so identical chunks are never Equal.
        CodeChunker.Chunk("a.cs", code, options with { UnitAware = true }).Select(c => c.Content)
            .ShouldBe(CodeChunker.Chunk("a.cs", code, options).Select(c => c.Content));
    }

    // ── Sentence-aware splitting ─────────────────────────────────────────────

    [Fact]
    public void SentenceAwareCutsAtASentenceRatherThanAWord()
    {
        // One very long line: the only case where the chunker splits within a line.
        var line = string.Join(' ', Enumerable.Range(1, 200).Select(i => $"This is sentence number {i}."));

        var chunks = CodeChunker.Chunk("prose.txt", line, new ChunkOptions
        {
            ChunkSizeTokens = 40, OverlapTokens = 0, BoundaryMode = "none", SentenceAware = true,
        });

        chunks.Count.ShouldBeGreaterThan(1);

        // Every piece but the last should end on a full stop rather than mid-sentence.
        foreach (var chunk in chunks.Take(chunks.Count - 1))
            chunk.Content.TrimEnd().ShouldEndWith(".");
    }

    [Fact]
    public void SentenceAwareStillSplitsProseWithNoSentenceEndsAtAll()
    {
        // The fallback matters: a line of base64 has no full stop within reach, and the
        // budget guarantee must hold regardless.
        var blob = new string('x', 40 * CodeChunker.CharsPerToken * 5);

        var chunks = CodeChunker.Chunk("blob.txt", blob, new ChunkOptions
        {
            ChunkSizeTokens = 40, OverlapTokens = 0, BoundaryMode = "none", SentenceAware = true,
        });

        chunks.Count.ShouldBeGreaterThan(3);
        chunks.ShouldAllBe(c => c.Content.Length <= 40 * CodeChunker.CharsPerToken);
    }

    [Fact]
    public void SentenceAwareNeverLosesText()
    {
        var line = string.Join(' ', Enumerable.Range(1, 120).Select(i => $"Sentence {i} says something."));

        var chunks = CodeChunker.Chunk("prose.txt", line, new ChunkOptions
        {
            ChunkSizeTokens = 40, OverlapTokens = 0, BoundaryMode = "none", SentenceAware = true,
        });

        var seen = string.Concat(chunks.Select(c => c.Content)).Replace(" ", "", StringComparison.Ordinal);
        for (var i = 1; i <= 120; i++)
            seen.ShouldContain($"Sentence{i}says");
    }

    // ── Custom boundaries ────────────────────────────────────────────────────

    [Fact]
    public void ACustomPatternDecidesWhereChunksBreak()
    {
        const string text = "intro line\n--- SECTION ---\nfirst body\n--- SECTION ---\nsecond body\n";

        var chunks = CodeChunker.Chunk("notes.txt", text, new ChunkOptions
        {
            // Small enough to force a split, so the pattern decides WHERE rather than
            // whether; size decides when.
            ChunkSizeTokens = 8,
            OverlapTokens = 0,
            BoundaryMode = "custom",
            CustomBoundaryPattern = @"^--- SECTION ---$",
        });

        chunks.Count.ShouldBeGreaterThan(1);
        foreach (var chunk in chunks.Skip(1))
            chunk.Content.TrimStart().ShouldStartWith("--- SECTION ---");
    }

    [Fact]
    public void AnUnparseableCustomPatternFailsLoudly()
    {
        // No silent fallback to blank-line: an operator's bad regex must not quietly
        // produce differently-shaped chunks they never asked for.
        var ex = Should.Throw<ArgumentException>(() => CodeChunker.Chunk("n.txt", "a\nb\n", new ChunkOptions
        {
            BoundaryMode = "custom",
            CustomBoundaryPattern = "([unclosed",
        }));

        ex.Message.ShouldContain("Invalid boundary regex");
    }

    // ── The strategies compose ───────────────────────────────────────────────

    [Fact]
    public void AllThreeTogetherStillRespectTheBudget()
    {
        var options = new ChunkOptions
        {
            ChunkSizeTokens = 64,
            OverlapTokens = 8,
            BoundaryMode = "blank-line",
            UnitAware = true,
            SentenceAware = true,
            HeadingContext = true,
        };

        var text = Doc + "\n\n" + string.Join(' ', Enumerable.Range(1, 300).Select(i => $"Filler sentence {i}."));
        var extracted = new ExtractedText(text, [new ExtractedUnit(1, 0, "Page 1")]);

        var chunks = CodeChunker.Chunk("doc.md", text, options, extracted);

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => c.Content.Length <= 64 * CodeChunker.CharsPerToken);

        // The prefix is added to EmbedText, so it is allowed to exceed the budget, by
        // design, and only by the length of a heading trail.
        chunks.ShouldAllBe(c => c.EmbedText.Length >= c.Content.Length);
    }
}
