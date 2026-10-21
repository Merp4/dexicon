using System.Text.RegularExpressions;
using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// <see cref="TextChunk.Section"/> for Markdown: the heading in effect at the chunk's first line, the line
/// <see cref="TextChunk.StartLine"/> names. The chunker used to read it from a cursor that had already passed the
/// end of the chunk, so a chunk ending before a heading carried that heading and a chunk straddling one carried the
/// later heading. On the 16 files of docs/ (267 chunks at 768 tokens, 100 overlap and 3.8 characters per token)
/// 24% carried the right section.
///
/// Small budgets throughout: with 4 characters per token, 25 tokens is 100 characters, which is five 19-character
/// lines, so the line each chunk ends on can be worked out by hand.
/// </summary>
public sealed class ChunkSectionTests
{
    private static string Body(string name, int count) =>
        string.Join('\n', Enumerable.Range(1, count).Select(i => $"{name} line {i:00} text"));

    private static ChunkOptions Options(int overlapTokens = 0, string boundaryMode = "language-aware",
        bool headingContext = false) =>
        new()
        {
            ChunkSizeTokens = 25,
            OverlapTokens = overlapTokens,
            BoundaryMode = boundaryMode,
            HeadingContext = headingContext,
        };

    [Fact]
    public void AChunkEndingJustBeforeAHeadingIsLabelledWithTheSectionItIsIn()
    {
        // Alpha is a heading and three lines, so the chunk fills past "# Beta" and backs up to it.
        var md = $"# Alpha\n{Body("alpha", 3)}\n# Beta\n{Body("beta", 12)}";

        var chunks = CodeChunker.Chunk("notes.md", md, Options());

        var first = chunks[0];
        first.Content.ShouldNotContain("# Beta");
        md.Split('\n')[first.EndLine].ShouldBe("# Beta");
        first.Section.ShouldBe("Alpha");

        chunks[1].StartLine.ShouldBe(first.EndLine + 1);
        chunks[1].Section.ShouldBe("Beta");
    }

    [Fact]
    public void AChunkStraddlingAHeadingIsLabelledWithTheHeadingAtItsFirstLine()
    {
        // No boundaries, so nothing backs the chunk up to the heading: it holds the end of Alpha and the start of Beta.
        var md = $"# Alpha\n{Body("alpha", 2)}\n# Beta\n{Body("beta", 12)}";

        var chunks = CodeChunker.Chunk("notes.md", md, Options(boundaryMode: "none"));

        var first = chunks[0];
        first.Content.ShouldContain("# Beta");
        first.StartLine.ShouldBe(1);
        first.Section.ShouldBe("Alpha");
    }

    [Fact]
    public void AChunkThatOpensWithTheOverlapFromTheSectionBeforeIsLabelledWithThatSection()
    {
        // Six tokens of overlap is 24 characters: one 20-character line is carried into the next chunk. The first
        // chunk ends before "# Beta", so the second opens on the last line of Alpha and reads on into Beta.
        var md = $"# Alpha\n{Body("alpha", 3)}\n# Beta\n{Body("beta", 12)}";

        var chunks = CodeChunker.Chunk("notes.md", md, Options(overlapTokens: 6));

        var lines = md.Split('\n');
        chunks.Count.ShouldBeGreaterThan(2);
        chunks[1].StartLine.ShouldBeLessThan(chunks[0].EndLine + 1, "the second chunk opens inside the first");
        lines[chunks[1].StartLine - 1].ShouldBe("alpha line 03 text");
        chunks[1].Content.ShouldContain("# Beta");
        chunks[1].Section.ShouldBe("Alpha");

        // The chunk after it starts clear of the overlap, in Beta.
        chunks[2].Section.ShouldBe("Beta");
    }

    [Fact]
    public void AnOverlapRewindAcrossAFenceDoesNotMakeAFencedCommentAHeading()
    {
        // The first chunk ends inside the fence, and 12 tokens of overlap is 48 characters, enough to carry the fence
        // line into the second chunk. Reading that line a second time used to flip the fence state, so the comment
        // below it was read as a heading.
        var md = string.Join('\n',
        [
            "# Alpha",
            "alpha line 01 text",
            "alpha line 02 text",
            "```yaml",
            "key: value",
            "# a comment, not a heading",
            "key2: value2",
            "key3: value3",
            "key4: value4",
            "key5: value5",
            "key6: value6",
            "```",
            "# Beta",
            Body("beta", 12),
        ]);
        var lines = md.Split('\n');

        var chunks = CodeChunker.Chunk("notes.md", md, Options(overlapTokens: 12, boundaryMode: "none"));

        chunks.Count.ShouldBeGreaterThan(2);
        chunks[1].Content.ShouldStartWith("```yaml", Case.Sensitive, "the overlap carried the fence line");
        foreach (var chunk in chunks)
        {
            chunk.Section.ShouldNotBe("a comment, not a heading");
            chunk.Section.ShouldBe(HeadingInEffectAt(lines, chunk.StartLine), $"lines {chunk.StartLine}-{chunk.EndLine}");
        }
    }

    [Fact]
    public void TextBeforeTheFirstHeadingHasNoSection()
    {
        var md = $"{Body("preface", 3)}\n# Alpha\n{Body("alpha", 12)}";

        var chunks = CodeChunker.Chunk("notes.md", md, Options(boundaryMode: "none"));

        // The first chunk reaches "# Alpha" but starts above it.
        chunks[0].Content.ShouldContain("# Alpha");
        chunks[0].Section.ShouldBeNull();
        chunks[^1].Section.ShouldBe("Alpha");
    }

    [Fact]
    public void AHeadingOnTheFirstLineOfAChunkIsItsSection()
    {
        var md = $"# Alpha\n{Body("alpha", 3)}\n## Beta\n{Body("beta", 3)}\n### Gamma\n{Body("gamma", 12)}";

        var chunks = CodeChunker.Chunk("notes.md", md, Options());

        var gamma = chunks.First(c => c.Content.StartsWith("### Gamma", StringComparison.Ordinal));
        gamma.Section.ShouldBe("Gamma");
    }

    [Fact]
    public void ASingleLongLineIsLabelledWithTheHeadingAboveIt()
    {
        var longLine = string.Concat(Enumerable.Repeat("word ", 80));
        var md = $"# Alpha\n{longLine}\n# Beta\nbeta";

        var chunks = CodeChunker.Chunk("notes.md", md, Options());

        var pieces = chunks.Where(c => c.StartLine == 2 && c.EndLine == 2).ToList();
        pieces.Count.ShouldBeGreaterThan(1, "the line is longer than the budget");
        pieces.ShouldAllBe(c => c.Section == "Alpha");
    }

    [Fact]
    public void SectionDoesNotDependOnHeadingContext()
    {
        var md = string.Join('\n', Enumerable.Range(1, 8).Select(n => $"## Part {n}\n{Body($"part{n}", 7)}"));

        foreach (var overlap in new[] { 0, 6 })
        {
            var plain = CodeChunker.Chunk("notes.md", md, Options(overlap));
            var withContext = CodeChunker.Chunk("notes.md", md, Options(overlap, headingContext: true));

            withContext.Select(c => (c.StartLine, c.EndLine, c.Section))
                .ShouldBe(plain.Select(c => (c.StartLine, c.EndLine, c.Section)));
            plain.ShouldAllBe(c => c.Section != null);
        }
    }

    [Fact]
    public void EveryChunkIsLabelledWithTheHeadingInEffectAtItsFirstLine()
    {
        var lines = new List<string>();
        for (var n = 1; n <= 30; n++)
        {
            lines.Add($"{new string('#', 1 + n % 3)} Section {n}");
            lines.AddRange(Body($"s{n}", 1 + n * 7 % 11).Split('\n'));
            if (n % 4 == 0)
                lines.AddRange(["```yaml", "# comment inside a fence", "key: value", "```"]);
            lines.Add("");
        }
        var md = string.Join('\n', lines);
        var source = md.Split('\n');
        var checkedChunks = 0;

        var settings = from mode in new[] { "language-aware", "blank-line", "none" }
                       from size in new[] { 25, 40, 90 }
                       from overlap in new[] { 0, 6, 12 }
                       select (mode, size, overlap);

        foreach (var (mode, size, overlap) in settings)
        {
            var chunks = CodeChunker.Chunk("notes.md", md, new ChunkOptions
            {
                ChunkSizeTokens = size,
                OverlapTokens = overlap,
                BoundaryMode = mode,
            });

            chunks.ShouldNotBeEmpty();
            foreach (var chunk in chunks)
            {
                chunk.Section.ShouldBe(HeadingInEffectAt(source, chunk.StartLine),
                    $"{mode}, {size} tokens, {overlap} overlap, lines {chunk.StartLine}-{chunk.EndLine}");
                checkedChunks++;
            }
        }

        checkedChunks.ShouldBeGreaterThan(500, "the sweep has to reach chunks for the loop above to prove anything");
    }

    [Fact]
    public void AFileThatIsNotMarkdownGetsNoSectionFromTheChunker()
    {
        // A heading-shaped line in code or plain text is a comment or a line of prose. The indexer fills the section of
        // those formats from the document's own units (page, slide, chapter), so the chunker leaves it empty.
        var text = $"# Alpha\n{Body("alpha", 12)}";

        foreach (var path in new[] { "script.py", "notes.txt", "page.html" })
            CodeChunker.Chunk(path, text, Options()).ShouldAllBe(c => c.Section == null, path);
    }

    /// <summary>
    /// The heading in effect at a 1-based line, written without the chunker's own code: the last ATX heading at or
    /// above the line, outside a fenced block.
    /// </summary>
    private static string? HeadingInEffectAt(string[] lines, int line)
    {
        string? current = null;
        var inFence = false;

        for (var i = 0; i < line; i++)
        {
            if (Regex.IsMatch(lines[i], "^ {0,3}(```|~~~)")) inFence = !inFence;
            else if (!inFence && Regex.Match(lines[i], @"^ {0,3}#{1,6}[ \t]+(.+?)\s*$") is { Success: true } m)
                current = m.Groups[1].Value;
        }

        return current;
    }
}
