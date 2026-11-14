using System.Text;
using System.Text.RegularExpressions;

namespace Dexicon.Core.Search;

/// <summary>
/// A phrase refused before any query is made: it is too long, or it has no word in it.
/// An <see cref="ArgumentException"/> with no parameter name, because the message goes to the
/// caller: the exception handler answers 400 and the MCP tool raises it as a tool error. The
/// message does not repeat the phrase, which is the caller's text and may hold anything.
/// </summary>
public sealed class InvalidSearchPhraseException(string message) : ArgumentException(message);

/// <summary>
/// The <c>phrase</c> argument of a search: the words a hit's text must contain, in that order.
///
/// The index stores no term positions (the sparse vector is a bag of terms), so the phrase is
/// not matched here. It goes to Qdrant as a <c>match.phrase</c> condition on the chunk's
/// stored <c>content</c>, in every prefetch and the final query, so every chunk in scope that
/// contains it is a candidate and none is lost to a candidate window. The field has no payload
/// index, which is what keeps the index and the indexing pass as they were: Qdrant reads the
/// text of each chunk the query reaches, and that costs query time instead (docs/05-search.md
/// has the measurements).
///
/// Without a full-text index Qdrant tokenises the text with its <c>word</c> tokenizer and
/// lowercases it (<see href="https://qdrant.tech/documentation/search/filtering/">filtering</see>).
/// So the match is: a run of letters and digits is a word; anything else separates words,
/// including underscores, hyphens, dots and slashes; case is ignored; the words of the phrase
/// must follow each other in the text. "chunk set id" therefore finds <c>chunk_set_id</c>, and
/// "refresh" does not find <c>RefreshAsync</c>. Over 200 phrase variants on the docs corpus,
/// Qdrant returned the chunks that a reference tokeniser selected, which takes a word to be a
/// run of Unicode letters and digits, for all 200.
///
/// The locator below is not the filter. It finds where the phrase sits in a chunk that Qdrant
/// has already returned, so the returned window can be centred on it. When it finds nothing,
/// because its idea of a word and Qdrant's differ on some character, or because its match
/// times out, the hit is kept and windowed as any other. The locator reads UTF-16 code units,
/// so a letter outside the Basic Multilingual Plane counts as a separator there and can only
/// cost centring. <see cref="Create(string?)"/> counts code points, so such a letter still
/// makes a word.
/// </summary>
public sealed class SearchPhrase
{
    /// <summary>
    /// The longest phrase accepted. A phrase is sent to Qdrant three times in a hybrid query
    /// (two prefetches and the final filter), and this bounds the size of that request.
    /// </summary>
    public const int MaxChars = 500;

    private static readonly TimeSpan LocatorTimeout = TimeSpan.FromSeconds(2);

    private const string WordChar = @"[\p{L}\p{N}]";
    private const string NotWordChar = @"[^\p{L}\p{N}]";

    private readonly Regex _locator;

    private SearchPhrase(string text, IEnumerable<string> words, TimeSpan timeout)
    {
        Text = text;

        // Whole words, as Qdrant matches them, and anything non-alphanumeric between them.
        _locator = new Regex(
            $"(?<!{WordChar})" + string.Join($"{NotWordChar}+", words.Select(Regex.Escape)) + $"(?!{WordChar})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            timeout);
    }

    /// <summary>
    /// The phrase with each run of whitespace made one space and the ends trimmed: what is sent to
    /// Qdrant and echoed in the result.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The phrase for a request, or null when there is none: a null, empty or blank phrase is the
    /// absence of the argument, not a phrase that matches everything.
    /// </summary>
    /// <exception cref="InvalidSearchPhraseException">
    /// Longer than <see cref="MaxChars"/>, or without a letter or a digit. Qdrant matches such a
    /// phrase against nothing, and an empty result for it would read as "not in the index".
    /// </exception>
    public static SearchPhrase? Create(string? phrase) => Create(phrase, LocatorTimeout);

    /// <summary>The same, with the locator's match timeout given, for a test that has to reach it.</summary>
    internal static SearchPhrase? Create(string? phrase, TimeSpan locatorTimeout)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;

        if (phrase.Length > MaxChars)
            throw new InvalidSearchPhraseException(
                $"A phrase is limited to {MaxChars} characters; this one has {phrase.Length:N0}.");

        var words = WordsOf(phrase);
        if (words.Count == 0)
            throw new InvalidSearchPhraseException(
                "A phrase needs at least one letter or digit. Phrases are matched word by word and " +
                "punctuation is ignored, so a phrase made only of punctuation matches nothing.");

        var text = string.Join(' ', Regex.Split(phrase.Trim(), @"\s+"));
        return new SearchPhrase(text, words, locatorTimeout);
    }

    /// <summary>
    /// The runs of letters and digits, by code point so that a letter outside the Basic Multilingual
    /// Plane is a letter. A number of any kind (<c>Nd</c>, <c>Nl</c>, <c>No</c>) is a digit here, as it is
    /// in Qdrant's tokenizer.
    /// </summary>
    private static List<string> WordsOf(string phrase)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in phrase.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune))
            {
                word.Append(rune.ToString());
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>
    /// Where the first occurrence starts and how long it is in <paramref name="text"/>. False when there is
    /// none, and also when the match times out: the locator only places the window, so a hit it cannot place
    /// is windowed on the query, as a hit with no phrase is, instead of failing the search.
    /// </summary>
    public bool TryFind(string text, out int index, out int length)
    {
        try
        {
            var m = _locator.Match(text);
            index = m.Success ? m.Index : -1;
            length = m.Success ? m.Length : 0;
            return m.Success;
        }
        catch (RegexMatchTimeoutException)
        {
            index = -1;
            length = 0;
            return false;
        }
    }
}
