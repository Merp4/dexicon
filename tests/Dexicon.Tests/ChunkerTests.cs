using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

public class ChunkerTests
{
    [Fact]
    public void Chunk_LineNumbers_PointAtTheRealLines()
    {
        // The single most important property: a result must be openable at the line it
        // claims. M1's definition of done was "open the file at the line and find the
        // matched text there", so it gets an automated equivalent.
        var lines = Enumerable.Range(1, 400).Select(i => $"line {i} some content to take up space");
        var content = string.Join('\n', lines);

        var chunks = CodeChunker.Chunk("sample.txt", content, chunkSizeTokens: 100, overlapTokens: 10,
            boundaryMode: "none");

        chunks.ShouldNotBeEmpty();
        var source = content.Split('\n');

        foreach (var chunk in chunks)
        {
            chunk.StartLine.ShouldBeGreaterThanOrEqualTo(1);
            chunk.EndLine.ShouldBeLessThanOrEqualTo(source.Length);

            // The chunk's first line of text must equal the source line it points at.
            var firstLine = chunk.Content.Split('\n')[0];
            source[chunk.StartLine - 1].ShouldBe(firstLine);
        }
    }

    [Fact]
    public void Chunk_NeverSplitsALine()
    {
        var content = string.Join('\n', Enumerable.Range(1, 50).Select(i => new string('x', 200) + i));
        var chunks = CodeChunker.Chunk("a.txt", content, chunkSizeTokens: 60, overlapTokens: 5, boundaryMode: "none");

        var sourceLines = content.Split('\n').ToHashSet(StringComparer.Ordinal);
        foreach (var line in chunks.SelectMany(c => c.Content.Split('\n')))
            sourceLines.ShouldContain(line);
    }

    [Fact]
    public void Chunk_OverlapLargerThanChunkSize_Throws() =>
        Should.Throw<ArgumentException>(() => CodeChunker.Chunk("a.cs", "x", 100, 100));

    [Fact]
    public void Chunk_InvalidCustomBoundaryRegex_ThrowsRatherThanSilentlyFallingBack()
    {
        // No silent fallback: an operator's bad regex must fail loudly, not quietly
        // produce differently-shaped chunks they never asked for.
        var ex = Should.Throw<ArgumentException>(() =>
            CodeChunker.Chunk("a.cs", "content\nmore", boundaryMode: "custom", customBoundaryPattern: "([unclosed"));
        ex.Message.ShouldContain("Invalid boundary regex");
    }

    [Fact]
    public void Chunk_UnknownBoundaryMode_Throws() =>
        Should.Throw<ArgumentException>(() => CodeChunker.Chunk("a.cs", "x", boundaryMode: "wishful"));

    // ── Markdown heading detection ───────────────────────────────────────────
    // Regression: the naive check ("starts with # and contains '# '") fired on YAML
    // comments inside fenced code blocks, and labelled a docs chunk
    // "spends its first minutes in embedding backoff" as its section.

    [Theory]
    [InlineData("# Title", "Title")]
    [InlineData("### D-04 Corpus as the Qdrant tenant key", "D-04 Corpus as the Qdrant tenant key")]
    [InlineData("  ## Indented up to three spaces", "Indented up to three spaces")]
    [InlineData("## Closed ATX ##", "Closed ATX")]
    public void TryReadHeading_RealHeadings_AreRecognised(string line, string expected)
    {
        CodeChunker.TryReadHeading(line, out var heading).ShouldBeTrue();
        heading.ShouldBe(expected);
    }

    [Theory]
    [InlineData("      # a yaml comment inside a code fence")]   // over-indented
    [InlineData("#NoSpaceAfterHash")]
    [InlineData("####### seven hashes is not a heading")]
    [InlineData("code # trailing hash")]
    [InlineData("#")]
    [InlineData("")]
    public void TryReadHeading_NonHeadings_AreRejected(string line) =>
        CodeChunker.TryReadHeading(line, out _).ShouldBeFalse();

    [Fact]
    public void Chunk_Markdown_DoesNotTakeSectionFromInsideACodeFence()
    {
        var md = string.Join('\n',
        [
            "# Deployment",
            "",
            "Some prose about the stack.",
            "",
            "```yaml",
            "services:",
            "  # this is a yaml comment, not a heading",
            "  dexicon:",
            "    image: dexicon",
            "```",
            "",
            "More prose after the fence.",
        ]);

        var chunks = CodeChunker.Chunk("09-deployment.md", md, chunkSizeTokens: 4096, boundaryMode: "none");

        chunks.ShouldNotBeEmpty();
        foreach (var chunk in chunks)
            chunk.Section.ShouldNotBe("this is a yaml comment, not a heading");
        chunks[0].Section.ShouldBe("Deployment");
    }

    // ── Size decides WHEN to split; a boundary decides WHERE ─────────────────
    //
    // The regression these guard: the chunker used to split at EVERY boundary, which
    // made chunkSize dead configuration in every mode but `none`. Two corpora set to
    // 768 and 256 tokens produced byte-identical output: 48 chunks each, at a 252
    // character mean against budgets of 3072 and 1024.

    [Fact]
    public void Chunk_ChunkSize_ChangesOutput_EvenWithABoundaryMode()
    {
        var prose = string.Join("\n\n",
            Enumerable.Range(1, 120).Select(i => $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("word", 30))));

        var coarse = CodeChunker.Chunk("doc.md", prose, chunkSizeTokens: 768, overlapTokens: 100, boundaryMode: "blank-line");
        var fine = CodeChunker.Chunk("doc.md", prose, chunkSizeTokens: 256, overlapTokens: 40, boundaryMode: "blank-line");

        coarse.ShouldNotBeEmpty();
        fine.Count.ShouldBeGreaterThan(
            coarse.Count,
            "a smaller chunk size must produce more chunks — otherwise the setting does nothing");
    }

    [Fact]
    public void Chunk_FillsTowardTheBudget_RatherThanEmittingOneChunkPerParagraph()
    {
        var prose = string.Join("\n\n",
            Enumerable.Range(1, 200).Select(i => $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("word", 25))));

        const int tokens = 768;
        var chunks = CodeChunker.Chunk("doc.md", prose, chunkSizeTokens: tokens, overlapTokens: 100, boundaryMode: "blank-line");
        var budget = tokens * CodeChunker.CharsPerToken;

        var mean = chunks.Average(c => c.Content.Length);

        // Not a tight bound; it must simply not collapse to paragraph-sized
        // chunks again. Half the budget is comfortably above the 252 chars observed.
        mean.ShouldBeGreaterThan(budget * 0.4,
            $"chunks averaged {mean:F0} chars against a {budget} budget — the boundary is forcing splits again");

        foreach (var c in chunks)
            c.Content.Length.ShouldBeLessThanOrEqualTo(budget + 200, "no chunk should blow past the budget");
    }

    [Fact]
    public void Chunk_BoundaryMode_StillDecidesWhereTheSplitLands()
    {
        // Filling to the budget must not mean splitting mid-paragraph: the split should
        // land on a blank line whenever the buffer contains one.
        var prose = string.Join("\n\n",
            Enumerable.Range(1, 60).Select(i => $"Para {i} " + string.Join(' ', Enumerable.Repeat("token", 40))));

        var chunks = CodeChunker.Chunk("doc.md", prose, chunkSizeTokens: 200, overlapTokens: 0, boundaryMode: "blank-line");

        chunks.Count.ShouldBeGreaterThan(1);

        // The property that actually matters: every chunk BEGINS at a paragraph start.
        // (Asserting on the ending was a poor oracle: a chunk that ends correctly at a
        // paragraph boundary necessarily ends with that paragraph's last words.)
        foreach (var c in chunks)
            // A split that landed mid-paragraph would start with "token", not "Para".
            c.Content.TrimStart().ShouldStartWith("Para ");
    }

    [Fact]
    public void Chunk_LineNumbersStayExact_AfterFillingAndBackingUp()
    {
        var lines = Enumerable.Range(1, 300).Select(i => i % 7 == 0 ? "" : $"line {i} content here");
        var content = string.Join('\n', lines);
        var source = content.Split('\n');

        var chunks = CodeChunker.Chunk("doc.md", content, chunkSizeTokens: 120, overlapTokens: 20, boundaryMode: "blank-line");

        foreach (var chunk in chunks)
        {
            var first = chunk.Content.Split('\n')[0];
            source[chunk.StartLine - 1].ShouldBe(first);
        }
    }

    [Fact]
    public void Chunk_TerminatesOnPathologicalInput()
    {
        // The rewind-for-overlap loop must always make forward progress. A file of
        // single characters with a large overlap is the case that would hang it.
        var content = string.Join('\n', Enumerable.Repeat("x", 500));
        var chunks = CodeChunker.Chunk("a.txt", content, chunkSizeTokens: 8, overlapTokens: 7, boundaryMode: "none");
        chunks.ShouldNotBeEmpty();
        chunks.Count.ShouldBeLessThan(2000);
    }

    [Fact]
    public void Chunk_LanguageAware_SplitsCSharpAtMemberBoundaries()
    {
        var code = """
            public class TokenService
            {
                public async Task<TokenPair> RefreshAsync(string token)
                {
                    return new TokenPair();
                }

                private static void Helper()
                {
                }
            }
            """;

        var chunks = CodeChunker.Chunk("TokenService.cs", code, chunkSizeTokens: 20, overlapTokens: 2);
        chunks.ShouldNotBeEmpty();
        chunks.SelectMany(c => c.Symbols).ShouldContain("TokenService");
    }

    [Fact]
    public void Chunk_EmptyOrWhitespaceContent_ProducesNothing()
    {
        CodeChunker.Chunk("a.cs", "").ShouldBeEmpty();
        CodeChunker.Chunk("a.cs", "   \n  \n").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a.cs", "csharp")]
    [InlineData("a.py", "python")]
    [InlineData("a.md", "markdown")]
    [InlineData("Component.razor", "razor")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("whatever.unknown", "text")]
    public void DetectLanguage_MapsExtensions(string path, string expected) =>
        LanguageMap.Detect(path).ShouldBe(expected);

    [Fact]
    public void BoundaryPattern_TemplateLanguages_UseBlankLinesNotHeadings()
    {
        // These are template SOURCE, not HTML documents. Splitting an Angular or Razor
        // template at its <h1> produces slices that mean nothing.
        foreach (var lang in new[] { "html", "razor", "vue", "svelte" })
            LanguageMap.BoundaryPattern(lang).ShouldBe(@"(?m)^\s*$");
    }
}
