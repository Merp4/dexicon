using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// Where the returned window of a hit sits when the hit was kept for containing a phrase.
///
/// The window is centred on the densest run of query terms. For a search whose query is a topic and
/// whose phrase is a detail, that run can be anywhere else in the chunk, and a hit returned for a phrase
/// whose window does not show the phrase looks like a hit that does not match.
/// </summary>
public sealed class PhraseWindowTests
{
    private const string Query = "alpha beta";
    private const string Needle = "needle in a haystack";

    private static string Chunk(string line35 = "the needle in a haystack stands alone")
    {
        // Query terms fill the head of the chunk and the phrase sits near its tail, so the two centres
        // are far enough apart that a 300 character window cannot hold both.
        var head = Enumerable.Range(1, 30).Select(i => $"alpha beta gamma delta {i}");
        var before = Enumerable.Range(31, 4).Select(i => $"filler words only {i}");
        var after = Enumerable.Range(36, 25).Select(i => $"filler words only {i}");
        return string.Join('\n', head.Concat(before).Append(line35).Concat(after));
    }

    [Fact]
    public void TheWindowShowsThePhraseWhereTheQueryTermsAreElsewhere()
    {
        var text = Chunk();

        SearchPresentation.Window(text, Query, 300, SearchPhrase.Create(Needle))
            .ShouldContain(Needle);
    }

    [Fact]
    public void WithoutAPhraseTheSameWindowIsCentredOnTheQueryTermsAsBefore()
    {
        // The control for the test above: it only shows the phrase because it was told to look for it.
        var text = Chunk();

        var window = SearchPresentation.Window(text, Query, 300);

        window.ShouldNotContain(Needle);
        window.ShouldContain("alpha beta");
    }

    [Fact]
    public void AnOccurrenceThatWrapsAcrossLinesIsKeptWhole()
    {
        var text = Chunk("the needle in\na haystack stands alone");

        var window = SearchPresentation.Window(text, Query, 300, SearchPhrase.Create(Needle));

        window.ShouldContain("needle in\na haystack");
    }

    [Fact]
    public void AWrappedOccurrenceIsKeptWholeEvenWhenTheLinesAroundItAreCheaper()
    {
        // The window grows by whichever neighbouring line costs less. Here every line around the occurrence
        // costs 2 characters and the second half of the occurrence costs 55, so a window that started from
        // only one of the two lines would spend its budget on the cheap ones and never reach the other.
        var lines = Enumerable.Repeat("x", 30)
            .Append("the needle in")
            .Append("a haystack stands alone with extra words trailing here")
            .Concat(Enumerable.Repeat("y", 30));

        var window = SearchPresentation.Window(string.Join('\n', lines), Query, 90, SearchPhrase.Create(Needle));

        window.ShouldContain("the needle in\na haystack stands alone");
    }

    [Fact]
    public void APhraseTheLocatorCannotFindLeavesTheWindowWhereTheQueryPutIt()
    {
        // Qdrant and the locator can disagree about a character. The hit is kept and windowed as any other.
        var text = Chunk();

        SearchPresentation.Window(text, Query, 300, SearchPhrase.Create("words that are not there"))
            .ShouldBe(SearchPresentation.Window(text, Query, 300));
    }

    [Fact]
    public void AChunkWithNoLineBreaksIsCutAroundThePhrase()
    {
        // A PDF page extracted as one line. The window is cut on characters, around the phrase.
        var text = string.Concat(Enumerable.Repeat("alpha beta ", 300)) + Needle + string.Concat(Enumerable.Repeat(" omega", 300));

        var window = SearchPresentation.Window(text, Query, 400, SearchPhrase.Create(Needle));

        window.ShouldContain(Needle);
        window.Length.ShouldBeLessThanOrEqualTo(400 + 4);
    }

    [Fact]
    public void AChunkThatFitsTheBudgetIsReturnedWhole()
    {
        var text = "short chunk with the needle in a haystack inside";

        SearchPresentation.Window(text, Query, 300, SearchPhrase.Create(Needle)).ShouldBe(text);
    }
}
