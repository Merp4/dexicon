using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// The chunker's one hard guarantee: no chunk exceeds the configured budget.
///
/// It did not hold, and the way it failed was invisible. An EPUB extractor emitted one
/// line per chapter (AngleSharp's TextContent concatenates everything with no
/// separators), the chunker never splits within a line, so a 578,000-character book
/// became 18 chunks averaging 32,000 characters. The embedding model truncates at its
/// context limit silently, so about 95% of that book was in no index anywhere — while
/// the corpus, the job and the file all reported success.
///
/// Nothing in the system could have detected that. Hence a guarantee, not a convention.
/// </summary>
public sealed class OversizeLineTests
{
    private const int Size = 100;      // tokens
    private const int Budget = Size * CodeChunker.CharsPerToken;

    [Fact]
    public void ASingleEnormousLineIsSplitToFitTheBudget()
    {
        var oneLine = string.Join(' ', Enumerable.Repeat("prose", 12_000));   // ~72k chars, no newlines

        var chunks = CodeChunker.Chunk("chapter.txt", oneLine, Size, 10, "blank-line");

        chunks.Count.ShouldBeGreaterThan(15);
        chunks.ShouldAllBe(c => c.Content.Length <= Budget);
    }

    [Fact]
    public void SplittingALongLineKeepsAllOfTheWords()
    {
        // The point of the exercise: no content may go missing.
        var words = Enumerable.Range(0, 5_000).Select(i => $"w{i}").ToArray();

        var chunks = CodeChunker.Chunk("chapter.txt", string.Join(' ', words), Size, 0, "none");

        var seen = chunks.SelectMany(c => c.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
        foreach (var w in words) seen.ShouldContain(w);
    }

    [Fact]
    public void PiecesOfOneLineAllCarryThatLineNumber()
    {
        var content = "short first line\n" + string.Join(' ', Enumerable.Repeat("prose", 5_000));

        var chunks = CodeChunker.Chunk("chapter.txt", content, Size, 10, "none");

        // Line 1 is the short one; everything after comes from line 2.
        chunks.Where(c => c.StartLine == 2).ShouldAllBe(c => c.EndLine == 2);
        chunks.Count(c => c.StartLine == 2).ShouldBeGreaterThan(5);
    }

    [Fact]
    public void ALineWithNoSpacesIsStillSplit()
    {
        // A minified bundle or a base64 blob has no word boundary to back up to. It must
        // still be cut, or the same silent truncation returns by another route.
        var blob = new string('x', Budget * 6);

        var chunks = CodeChunker.Chunk("bundle.min.js", blob, Size, 10, "none");

        chunks.Count.ShouldBeGreaterThan(4);
        chunks.ShouldAllBe(c => c.Content.Length <= Budget);
    }

    [Fact]
    public void NormalTextIsUnaffected()
    {
        // The guard must not reshape ordinary input.
        var prose = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"Line {i} of ordinary prose."));

        var chunks = CodeChunker.Chunk("notes.txt", prose, Size, 10, "blank-line");

        chunks.ShouldAllBe(c => c.Content.Length <= Budget);
        chunks.ShouldAllBe(c => c.StartLine <= c.EndLine);
    }

    [Fact]
    public void ChunkingIsBoundedForEveryBudget()
    {
        // Property check: whatever the settings, no chunk exceeds the budget and the
        // chunker terminates.
        var content = string.Join('\n',
        [
            string.Join(' ', Enumerable.Repeat("alpha", 4_000)),
            "short",
            new string('z', 20_000),
            string.Join(' ', Enumerable.Repeat("beta", 1_000)),
        ]);

        foreach (var (size, overlap) in new[] { (32, 0), (64, 8), (256, 40), (768, 100) })
        {
            var chunks = CodeChunker.Chunk("mixed.txt", content, size, overlap, "blank-line");
            chunks.ShouldNotBeEmpty();
            chunks.ShouldAllBe(c => c.Content.Length <= size * CodeChunker.CharsPerToken);
        }
    }

    [Fact]
    public void HtmlBlocksBecomeSeparateLines()
    {
        // The root cause. TextContent gave "OneTwoThree" on a single line.
        const string html = "<html><body><h1>Title</h1><p>One</p><p>Two</p><div>Three</div></body></html>";

        var text = new HtmlTextExtractor().Extract(Stream(html), "page.html").Text;

        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .ShouldBe(["Title", "One", "Two", "Three"]);
    }

    [Fact]
    public void InlineMarkupDoesNotBreakASentence()
    {
        // The opposite failure: a line break at every <em> would shred the prose.
        const string html = "<body><p>A <em>very</em> <strong>emphatic</strong> sentence.</p></body>";

        new HtmlTextExtractor().Extract(Stream(html), "page.html").Text.Trim()
            .ShouldBe("A very emphatic sentence.");
    }

    [Fact]
    public void ScriptAndStyleAreNotIndexedAsProse()
    {
        const string html = "<body><script>var x = 1;</script><style>p{color:red}</style><p>Real text</p></body>";

        var text = new HtmlTextExtractor().Extract(Stream(html), "page.html").Text;

        text.ShouldNotContain("var x");
        text.ShouldNotContain("color:red");
        text.Trim().ShouldBe("Real text");
    }

    [Fact]
    public void ListItemsAndTableCellsAreTheirOwnLines()
    {
        const string html = "<body><ul><li>One</li><li>Two</li></ul><table><tr><td>A</td><td>B</td></tr></table></body>";

        var lines = new HtmlTextExtractor().Extract(Stream(html), "page.html").Text
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        lines.ShouldBe(["One", "Two", "A", "B"]);
    }

    [Fact]
    public void SourceIndentationIsNotTreatedAsContent()
    {
        const string html = """
            <body>
              <p>
                Wrapped   across
                several       lines
              </p>
            </body>
            """;

        new HtmlTextExtractor().Extract(Stream(html), "page.html").Text.Trim()
            .ShouldBe("Wrapped across several lines");
    }

    [Fact]
    public void WhitespaceCollapsesAcrossInlineBoundaries()
    {
        // Each text node is collapsed separately, so the state has to carry over: a
        // trailing space followed by a leading space is still one space.
        const string html = "<body><p>A <em> very </em> <span> spaced </span> sentence.</p></body>";

        new HtmlTextExtractor().Extract(Stream(html), "page.html").Text.Trim()
            .ShouldBe("A very spaced sentence.");
    }

    [Fact]
    public void ConsecutiveBlocksDoNotProduceBlankLines()
    {
        // Nested blocks would otherwise emit a newline on the way in and on the way out.
        const string html = "<body><div><div><p>One</p></div></div><p>Two</p></body>";

        new HtmlTextExtractor().Extract(Stream(html), "page.html").Text
            .TrimEnd('\n').Split('\n').ShouldBe(["One", "Two"]);
    }

    [Fact]
    public void ABreakTagEndsTheLine()
    {
        const string html = "<body><p>First<br>Second</p></body>";

        new HtmlTextExtractor().Extract(Stream(html), "page.html").Text
            .TrimEnd('\n').Split('\n').ShouldBe(["First", "Second"]);
    }

    private static MemoryStream Stream(string s) => new(System.Text.Encoding.UTF8.GetBytes(s));
}
