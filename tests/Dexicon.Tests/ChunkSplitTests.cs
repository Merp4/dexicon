using Dexicon.Core.Indexing;
using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// Dividing a chunk the provider refused.
///
/// The refusal is the only exact statement about the model's limit available to this
/// process (D-31), so the split behind it has to be total: every path must produce two
/// halves that together hold the original text, or the file fails where it used to be
/// indexed badly. The cases that matter are the ones with no line to cut on — a minified
/// file, a PDF page that extracted as one line — because those are what the previous
/// truncating path quietly mangled.
/// </summary>
public sealed class ChunkSplitTests
{
    private static Chunk Make(string content, int start = 10, int end = 20,
        int index = 3, string embed = "") =>
        new()
        {
            CorpusId = "c1",
            ChunkSetId = "s1",
            SourceId = "src1",
            FilePath = "docs/x.md",
            FileHash = "hash",
            StartLine = start,
            EndLine = end,
            ChunkIndex = index,
            Content = content,
            EmbedText = embed,
        };

    private static string Lines(int n) =>
        string.Join('\n', Enumerable.Range(1, n).Select(i => $"line {i}"));

    [Fact]
    public void TheTwoHalvesHoldTheWholeOriginal()
    {
        // The property the whole design rests on: splitting never loses text. A vector for
        // less text than its chunk claims is exactly what this replaced.
        var chunk = Make(Lines(40));

        var split = CorpusIndexer.Split(chunk, nextIndex: 99);

        split.ShouldNotBeNull();
        (split.Value.First.Content + split.Value.Second.Content).ShouldBe(chunk.Content);
    }

    [Fact]
    public void TheSecondHalfTakesTheNewIndex()
    {
        // Point identity is (chunk set, source, path, chunk index), so a collision here
        // would have one half silently overwrite the other.
        var split = CorpusIndexer.Split(Make(Lines(40), index: 3), nextIndex: 99);

        split!.Value.First.ChunkIndex.ShouldBe(3);
        split.Value.Second.ChunkIndex.ShouldBe(99);
    }

    [Fact]
    public void ItCutsOnALineWhenThereIsOne()
    {
        var split = CorpusIndexer.Split(Make(Lines(40)), nextIndex: 99);

        split!.Value.First.Content.ShouldEndWith("\n");
        split.Value.Second.Content.ShouldStartWith("line ");
        split.Value.Second.Content.ShouldNotContain("\nline 1\n");
    }

    [Fact]
    public void LineRangesStayInsideTheOriginal()
    {
        // A citation must not claim a line the chunk does not hold.
        var chunk = Make(Lines(40), start: 10, end: 49);

        var (first, second) = CorpusIndexer.Split(chunk, 99)!.Value;

        first.StartLine.ShouldBe(10);
        first.EndLine.ShouldBeLessThanOrEqualTo(chunk.EndLine);
        second.StartLine.ShouldBeGreaterThanOrEqualTo(chunk.StartLine);
        second.EndLine.ShouldBe(49);
        second.StartLine.ShouldBeGreaterThanOrEqualTo(first.EndLine);
    }

    [Fact]
    public void AMinifiedFileStillSplits()
    {
        // One line, no newline anywhere. This is the case that used to be truncated: the
        // old path embedded the opening and dropped the rest under the same id.
        var chunk = Make(new string('x', 9_000), start: 1, end: 1);

        var split = CorpusIndexer.Split(chunk, 99);

        split.ShouldNotBeNull();
        (split.Value.First.Content + split.Value.Second.Content).ShouldBe(chunk.Content);
        split.Value.First.Content.Length.ShouldBeGreaterThan(0);
        split.Value.Second.Content.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ASingleLongLineKeepsItsLineNumber()
    {
        // Both halves are inside the one line they came from, so that is what they report.
        var chunk = Make(new string('x', 9_000), start: 7, end: 7);

        var (first, second) = CorpusIndexer.Split(chunk, 99)!.Value;

        first.StartLine.ShouldBe(7);
        first.EndLine.ShouldBe(7);
        second.StartLine.ShouldBe(7);
        second.EndLine.ShouldBe(7);
    }

    [Fact]
    public void ItPrefersAWordBoundaryOverTheMidpoint()
    {
        var chunk = Make(string.Join(' ', Enumerable.Repeat("word", 2_000)), start: 1, end: 1);

        var (first, second) = CorpusIndexer.Split(chunk, 99)!.Value;

        first.Content.ShouldEndWith(" ");
        second.Content.ShouldStartWith("word");
    }

    [Fact]
    public void TheHeadingTrailIsReappliedToBothHalves()
    {
        // EmbedText is the heading trail followed by the content. Splitting the content
        // alone would leave the second half embedded under text no longer above it.
        var content = Lines(40);
        var chunk = Make(content, embed: "Chapter 3 > Retries\n\n" + content);

        var (first, second) = CorpusIndexer.Split(chunk, 99)!.Value;

        first.EmbedText.ShouldStartWith("Chapter 3 > Retries\n\n");
        second.EmbedText.ShouldStartWith("Chapter 3 > Retries\n\n");
        first.EmbedText.ShouldEndWith(first.Content);
        second.EmbedText.ShouldEndWith(second.Content);
    }

    [Fact]
    public void WithoutAHeadingTrailTheHalvesEmbedTheirOwnContent()
    {
        var (first, second) = CorpusIndexer.Split(Make(Lines(40)), 99)!.Value;

        first.EmbedText.ShouldBeEmpty();
        second.EmbedText.ShouldBeEmpty();
        first.TextToEmbed.ShouldBe(first.Content);
        second.TextToEmbed.ShouldBe(second.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public void NothingToDivideReturnsNull(string content)
    {
        CorpusIndexer.Split(Make(content, start: 1, end: 1), 99).ShouldBeNull();
    }

    [Fact]
    public void RepeatedSplittingTerminates()
    {
        // The recursion in EmbedBatchAsync relies on this: a chunk that keeps being
        // refused must keep getting smaller, and must stop rather than loop.
        var chunk = Make(new string('x', 1_000), start: 1, end: 1);
        var next = 100;

        for (var i = 0; i < 20 && chunk.Content.Length > 1; i++)
        {
            var split = CorpusIndexer.Split(chunk, next++);
            split.ShouldNotBeNull();
            split.Value.First.Content.Length.ShouldBeLessThan(chunk.Content.Length);
            chunk = split.Value.First;
        }

        chunk.Content.Length.ShouldBeLessThanOrEqualTo(1);
    }
}
