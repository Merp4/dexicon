using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// Chunks of a document whose extraction names sections, such as the message, stat and diff of a
/// git commit. A search that leaves out the diff, or the message, can only be exact if no chunk holds
/// lines of both. Before sections, a chunk was cut at the size and at blank lines, so on the three history
/// corpora of one installation 7%, 12% and 26% of the chunks held lines of two parts.
///
/// Small budgets throughout: with 4 characters per token, 25 tokens is 100 characters, which is five
/// 19-character lines, so where a chunk ends can be worked out by hand.
/// </summary>
public sealed class NamedSectionChunkingTests
{
    private static string Lines(string name, int count) =>
        string.Join('\n', Enumerable.Range(1, count).Select(i => $"{name} line {i:00} text"));

    private static ChunkOptions Options(int overlapTokens = 0) =>
        new() { ChunkSizeTokens = 25, OverlapTokens = overlapTokens, BoundaryMode = "blank-line" };

    /// <summary>
    /// A document of parts joined by a line feed, and its extraction naming each part. A part's offset is
    /// the character where its first line starts.
    /// </summary>
    private static (string Text, ExtractedText Extracted, int[] FirstLines) Build(
        string separator, params (string Name, string Body)[] parts)
    {
        var text = new System.Text.StringBuilder();
        var sections = new List<ExtractedSection>();
        var firstLines = new List<int>();
        var line = 1;

        foreach (var (name, body) in parts)
        {
            if (text.Length > 0) text.Append(separator);
            sections.Add(new ExtractedSection(name, text.Length));
            firstLines.Add(line);
            text.Append(body);
            line += body.Split('\n').Length;
        }

        return (text.ToString(), new ExtractedText(text.ToString(), [], null, sections), [.. firstLines]);
    }

    private static IReadOnlyList<TextChunk> Chunk(string text, ExtractedText extracted, ChunkOptions options) =>
        CodeChunker.Chunk("commits/x", text, options, extracted);

    [Fact]
    public void ANamedSectionIsNeverHeldWithTextFromAnother()
    {
        var (text, extracted, first) = Build("\n",
            ("message", Lines("message", 2)), ("stat", Lines("stat", 2)), ("diff", Lines("diff", 20)));

        var chunks = Chunk(text, extracted, Options());

        // Every line of every chunk is a line of the part the chunk is labelled with.
        var lines = text.Split('\n');
        foreach (var chunk in chunks)
        {
            chunk.Section.ShouldNotBeNull();
            var owned = lines[(chunk.StartLine - 1)..chunk.EndLine];
            owned.ShouldAllBe(l => l.StartsWith(chunk.Section, StringComparison.Ordinal),
                $"chunk {chunk.Index} is labelled {chunk.Section}");
        }

        chunks.Select(c => c.Section).Distinct().ShouldBe(["message", "stat", "diff"]);
        chunks.Count(c => c.Section == "diff").ShouldBeGreaterThan(1, "the budget cuts a long section into several");
        first.Length.ShouldBe(3);
    }

    [Fact]
    public void AShortSectionIsAChunkOfItsOwn()
    {
        // Three lines in all would fit in one chunk, and each part starts one.
        var (text, extracted, _) = Build("\n", ("message", "message line"), ("stat", "stat line"), ("diff", "diff line"));

        var chunks = Chunk(text, extracted, Options());

        chunks.Select(c => c.Content).ShouldBe(["message line", "stat line", "diff line"]);
        chunks.Select(c => c.Section).ShouldBe(["message", "stat", "diff"]);
        chunks.Select(c => c.Index).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void ASectionStartsAChunkAtItsFirstLineWhateverTheOverlapIs()
    {
        var (text, extracted, first) = Build("\n", ("message", Lines("message", 3)), ("diff", Lines("diff", 12)));

        // 10 tokens of overlap is two of these lines, and would reach back into the message.
        var chunks = Chunk(text, extracted, Options(overlapTokens: 10));

        var firstOfDiff = chunks.First(c => c.Section == "diff");
        firstOfDiff.StartLine.ShouldBe(first[1]);
        chunks.Where(c => c.Section == "message").Max(c => c.EndLine).ShouldBe(first[1] - 1);
    }

    [Fact]
    public void OverlapInsideASectionStaysInsideIt()
    {
        var (text, extracted, first) = Build("\n", ("message", Lines("message", 3)), ("diff", Lines("diff", 20)));

        var diff = Chunk(text, extracted, Options(overlapTokens: 10)).Where(c => c.Section == "diff").ToList();

        diff.Count.ShouldBeGreaterThan(2);
        diff.ShouldAllBe(c => c.StartLine >= first[1], "no chunk of the diff opens in the message");

        // Overlap did happen: the second chunk of the diff opens before the first one ended.
        diff[1].StartLine.ShouldBeLessThanOrEqualTo(diff[0].EndLine);
    }

    [Fact]
    public void EveryChunkOfALongSectionCarriesItsName()
    {
        var (text, extracted, _) = Build("\n", ("message", "one"), ("diff", Lines("diff", 40)));

        var chunks = Chunk(text, extracted, Options());

        chunks.Skip(1).ShouldAllBe(c => c.Section == "diff");
    }

    [Fact]
    public void EveryPieceOfAnOverlongLineCarriesTheSectionItIsIn()
    {
        // 80 words of 4 letters is a line of 399 characters against a budget of 100, so the chunker cuts the line
        // itself. A patch with a minified file or a lockfile in it has such a line.
        var overlong = string.Join(' ', Enumerable.Repeat("word", 80));
        var (text, extracted, _) = Build("\n", ("message", "one"), ("diff", $"before\n{overlong}\nafter"));

        var chunks = Chunk(text, extracted, Options());

        var pieces = chunks.Where(c => c.Content.Contains("word", StringComparison.Ordinal)).ToList();
        pieces.Count.ShouldBeGreaterThan(1, "the line is cut into several chunks");
        pieces.ShouldAllBe(c => c.Section == "diff");
        chunks.ShouldAllBe(c => c.Section != null);
        chunks[0].Section.ShouldBe("message");
    }

    [Fact]
    public void TextWithNoSectionsHasNoSection()
    {
        var chunks = CodeChunker.Chunk("commits/x", Lines("plain", 12), Options(), new ExtractedText(Lines("plain", 12), []));

        chunks.ShouldAllBe(c => c.Section == null);
    }

    [Fact]
    public void ASectionIsFoundAtTheLineTheChunkerCounts()
    {
        // A lone carriage return breaks a line for the chunker, so the stat is one line later than a count of
        // line feeds says. Counting line feeds alone put its first line in the message and cut the message
        // chunk one line short.
        var message = "subject\rcontinued after a bare return\nsecond message line";
        var (text, extracted, _) = Build("\n", ("message", message), ("stat", "stat line"));

        var chunks = Chunk(text, extracted, Options());

        chunks.Select(c => (c.Section, c.Content)).ShouldBe(
            [("message", "subject\ncontinued after a bare return\nsecond message line"), ("stat", "stat line")]);
    }

    [Fact]
    public void ACarriageReturnBeforeALineFeedIsOneBreak()
    {
        var (text, extracted, _) = Build("\r\n", ("message", "one\r\ntwo"), ("diff", "three\r\nfour"));

        var chunks = Chunk(text, extracted, Options());

        chunks.Select(c => c.Section).ShouldBe(["message", "diff"]);
        chunks[1].StartLine.ShouldBe(3);
    }

    [Fact]
    public void ASectionWhoseOffsetIsInsideALineStartsAtThatLine()
    {
        var text = "first\nsecond line\nthird";
        var extracted = new ExtractedText(text, [], null,
            [new ExtractedSection("a", 0), new ExtractedSection("b", text.IndexOf("cond", StringComparison.Ordinal))]);

        var chunks = Chunk(text, extracted, Options());

        chunks.Select(c => (c.Section, c.StartLine)).ShouldBe([("a", 1), ("b", 2)]);
    }

    [Fact]
    public void ASectionOutsideTheTextIsIgnored()
    {
        var text = "one\ntwo";
        var extracted = new ExtractedText(text, [], null,
            [new ExtractedSection("a", 0), new ExtractedSection("b", text.Length), new ExtractedSection("c", -1)]);

        var chunks = Chunk(text, extracted, Options());

        chunks.Select(c => c.Section).ShouldBe(["a"]);
    }

    [Fact]
    public void LinesAboveTheFirstSectionHaveNone()
    {
        var text = "header\nbody one\nbody two";
        var extracted = new ExtractedText(text, [], null, [new ExtractedSection("body", text.IndexOf("body one", StringComparison.Ordinal))]);

        var chunks = Chunk(text, extracted, Options());

        chunks.Select(c => (c.Section, c.Content)).ShouldBe([(null, "header"), ("body", "body one\nbody two")]);
    }

    [Fact]
    public void ASectionSplitIsForcedOnlyByASectionAndNotBySetting()
    {
        // The set's unit-aware option is off in Options(), and the sections still cut.
        var (text, extracted, _) = Build("\n", ("message", "one"), ("stat", "two"));

        Options().UnitAware.ShouldBeFalse();
        Chunk(text, extracted, Options()).Count.ShouldBe(2);
    }

    [Fact]
    public void RemovingNulsMovesTheSectionsWithTheText()
    {
        // Two NULs before the second section, and one inside the first.
        var text = "a\0b\nc\0\0d\nsecond";
        var extracted = new ExtractedText(text, [], null,
            [new ExtractedSection("one", 0), new ExtractedSection("two", text.IndexOf("second", StringComparison.Ordinal))]);

        var clean = extracted.WithoutNul();

        clean.Text.ShouldBe("ab\ncd\nsecond");
        clean.Sections!.Select(s => (s.Name, s.StartOffset)).ShouldBe([("one", 0), ("two", 6)]);
        clean.Text[clean.Sections![1].StartOffset..].ShouldStartWith("second");
    }
}
