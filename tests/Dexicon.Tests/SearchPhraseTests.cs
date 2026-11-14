using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// The phrase argument of a search: what is refused before a query is made, what is sent to the
/// store, and where a returned chunk holds the phrase.
///
/// Qdrant does the matching (a <c>match.phrase</c> condition on a field with no payload index, which
/// tokenises on letters and digits and ignores case), so these tests hold the part that is ours: a
/// phrase that matches nothing is refused instead of searched, because an empty result for it would
/// read as "not in the index", and the locator that centres the returned window follows the same rules
/// as Qdrant's tokeniser.
/// </summary>
public sealed class SearchPhraseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t \r\n")]
    public void ABlankPhraseIsNoPhrase(string? phrase)
    {
        // An absent argument and an empty string are the same request. A blank phrase that was searched
        // would match every chunk or none, and neither is what the caller meant.
        SearchPhrase.Create(phrase).ShouldBeNull();
    }

    [Fact]
    public void EachRunOfWhitespaceBecomesOneSpaceAndTheEndsAreTrimmed()
    {
        SearchPhrase.Create("  chunk \t set\r\n\r\n id \n")!.Text.ShouldBe("chunk set id");
    }

    [Fact]
    public void PunctuationStaysInTheTextSentToTheStore()
    {
        // Qdrant ignores it when matching. The phrase is echoed to the caller as written.
        SearchPhrase.Create("D-39 (draft)")!.Text.ShouldBe("D-39 (draft)");
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("=>")]
    [InlineData("   ::  ")]
    [InlineData("()")]
    [InlineData("⊗")]
    public void APhraseWithNoLetterOrDigitIsRefused(string phrase)
    {
        // Measured against the live Qdrant 1.20: a phrase of punctuation matches 0 of the 297 chunks it
        // was tried on. Searched, it would answer "nothing found" for something it never looked for.
        var thrown = Should.Throw<InvalidSearchPhraseException>(() => SearchPhrase.Create(phrase));

        thrown.Message.ShouldContain("at least one letter or digit");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("٣")]
    [InlineData("a")]
    [InlineData("= x =")]
    [InlineData("²")]
    public void ADigitOrASingleLetterIsAWord(string phrase)
    {
        SearchPhrase.Create(phrase).ShouldNotBeNull();
    }

    [Fact]
    public void ALetterOutsideTheBasicMultilingualPlaneIsAWord()
    {
        // An ideograph (U+20000) and a mathematical bold letter (U+1D431) are two UTF-16 code units each, and
        // a regular expression class over code units takes them for separators. Qdrant's tokenizer counts
        // them as words, so a phrase made of them is a phrase and must not be refused.
        foreach (var codePoint in new[] { 0x20000, 0x1D431 })
            SearchPhrase.Create(char.ConvertFromUtf32(codePoint)).ShouldNotBeNull($"U+{codePoint:X}");
    }

    [Fact]
    public void ASymbolOutsideTheBasicMultilingualPlaneIsNotAWord()
    {
        // An emoji, U+1F642: Qdrant matches a phrase of it against nothing.
        Should.Throw<InvalidSearchPhraseException>(() => SearchPhrase.Create(char.ConvertFromUtf32(0x1F642)));
    }

    [Fact]
    public void ALocatorThatTimesOutPlacesNothingInsteadOfFailing()
    {
        // The locator only centres the window. A hit it cannot place is windowed on the query, so the search
        // answers. The phrase occurs once, at the end, so the answer without a timeout is a match.
        var phrase = string.Join(' ', Enumerable.Repeat("a", 200)) + " b";
        var text = string.Concat(Enumerable.Repeat("a ", 60_000)) + "b";

        SearchPhrase.Create(phrase)!.TryFind(text, out var index, out _).ShouldBeTrue();
        index.ShouldBeGreaterThan(0);

        var timesOut = SearchPhrase.Create(phrase, TimeSpan.FromMilliseconds(1))!;
        timesOut.TryFind(text, out index, out var length).ShouldBeFalse();
        index.ShouldBe(-1);
        length.ShouldBe(0);
    }

    [Fact]
    public void AnUnmatchablePhraseIsRefusedWithoutRepeatingIt()
    {
        // The other refusal: this phrase is short enough and has no word in it.
        var forged = "\u001B[;\n[::]\n--- =>";

        var thrown = Should.Throw<InvalidSearchPhraseException>(() => SearchPhrase.Create(forged));

        thrown.Message.ShouldContain("at least one letter or digit");
        thrown.Message.ShouldNotContain("\u001B");
        thrown.Message.ShouldNotContain("---");
    }

    [Fact]
    public void AFiveHundredCharacterPhraseIsAcceptedAndOneMoreIsRefused()
    {
        SearchPhrase.Create(new string('a', SearchPhrase.MaxChars)).ShouldNotBeNull();

        var thrown = Should.Throw<InvalidSearchPhraseException>(
            () => SearchPhrase.Create(new string('a', SearchPhrase.MaxChars + 1)));

        thrown.Message.ShouldContain("500");
        thrown.Message.ShouldContain("501");
    }

    [Fact]
    public void ARefusalDoesNotRepeatTheCallersText()
    {
        // The phrase is the caller's, and the message is logged and returned. An escape or a line break in
        // it would reach the log as part of a line that reads as another entry.
        var forged = "\u001B[2J\n[10:00:00Z INF] forged " + new string('z', 600);

        var thrown = Should.Throw<InvalidSearchPhraseException>(() => SearchPhrase.Create(forged));

        thrown.Message.ShouldNotContain("\u001B");
        thrown.Message.ShouldNotContain("forged");
        thrown.Message.ShouldNotContain("zzz");
    }

    [Theory]
    [InlineData("chunk set id", "see chunk_set_id here", true)]
    [InlineData("Event Driven", "an event-driven design", true)]
    [InlineData("refresh token", "the refresh\n   token is", true)]
    [InlineData("REFRESH TOKEN", "a refresh token", true)]
    [InlineData("ÉCOLE normale", "une école normale", true)]
    [InlineData("refresh", "a Refresh() call", true)]
    [InlineData("refresh", "RefreshAsync()", false)]
    [InlineData("token refresh", "refresh token", false)]
    [InlineData("set id", "subset identifier", false)]
    [InlineData("set id", "a subset id here", false)]
    [InlineData("set id", "set identifier", false)]
    [InlineData("1,500 characters", "1 500 characters", true)]
    [InlineData("1,500 characters", "1500 characters", false)]
    public void TheLocatorFollowsTheStoresRules(string phrase, string text, bool found)
    {
        // Whole words in order, case and punctuation ignored, anything non-alphanumeric between them:
        // the rules of the tokeniser Qdrant applies to a field with no full-text index.
        SearchPhrase.Create(phrase)!.TryFind(text, out _, out _).ShouldBe(found);
    }

    [Fact]
    public void TheLocatorSaysWhereTheFirstOccurrenceStartsAndHowLongItIs()
    {
        const string text = "aaa Refresh the  Token bbb, then refresh the token again";

        SearchPhrase.Create("refresh the token")!.TryFind(text, out var index, out var length).ShouldBeTrue();

        index.ShouldBe(4);
        text.Substring(index, length).ShouldBe("Refresh the  Token");
    }

    [Fact]
    public void WhenNothingIsFoundTheLocatorSaysSoWithoutAPosition()
    {
        SearchPhrase.Create("absent words")!.TryFind("some other text", out var index, out var length).ShouldBeFalse();

        index.ShouldBe(-1);
        length.ShouldBe(0);
    }
}
