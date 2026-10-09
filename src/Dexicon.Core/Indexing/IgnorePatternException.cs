using System.Globalization;

namespace Dexicon.Core.Indexing;

/// <summary>
/// An ignore file, or a line of one, or an entry of a glob list, that <see cref="IgnoreRuleSet.AddPatterns"/> or
/// the walk cannot turn into rules. The message names where it was read from, the position in it, and the text,
/// so it can be shown to an operator as it stands. It is an <see cref="ArgumentException"/> because that is what
/// an uncompilable line raised before this type existed.
///
/// The message reaches logs, a job's error text and <c>index_status</c>, and every part of it is text from the
/// indexed tree: the line, and the directory names in the file's path. The whole message is cleaned
/// (<see cref="Clean"/>) and never any one part.
/// </summary>
public sealed class IgnorePatternException : ArgumentException
{
    private const int MaxShownLength = 100;

    private IgnorePatternException(string message, bool budgetExhausted, Exception? inner)
        : base(Clean(message), inner) => BudgetExhausted = budgetExhausted;

    /// <summary>The walk had read as many rules as it may (<see cref="IgnoreRuleSet.MaxRulesPerSource"/>).</summary>
    internal bool BudgetExhausted { get; }

    /// <summary>A pattern at <paramref name="where"/> (<c>.gitignore line 3</c>, <c>excludeGlobs[1]</c>), or the entry itself when null.</summary>
    internal static IgnorePatternException For(string where, string? pattern, string reason, Exception? inner = null) =>
        new(pattern is null ? $"{where} {reason}" : $"{where} ('{Shown(pattern)}') {reason}", false, inner);

    /// <summary>As <see cref="For"/>, for the rule that would go past the limit.</summary>
    internal static IgnorePatternException ForBudget(string where, string pattern, string reason) =>
        new($"{where} ('{Shown(pattern)}') {reason}", true, null);

    /// <summary>A whole file that cannot be read as patterns, named by its path from the scan root.</summary>
    internal static IgnorePatternException ForFile(string path, string reason, Exception? inner = null) =>
        new($"{path} {reason}", false, inner);

    /// <summary>
    /// The first <see cref="MaxShownLength"/> characters of the pattern, cut so that a surrogate pair is kept
    /// whole or left out, with <c>...</c> after a cut.
    /// </summary>
    private static string Shown(string pattern)
    {
        if (pattern.Length <= MaxShownLength) return pattern;

        var cut = MaxShownLength;
        if (char.IsHighSurrogate(pattern[cut - 1])) cut--;
        return pattern[..cut] + "...";
    }

    /// <summary>
    /// <paramref name="text"/> with every character a log reader or a terminal would act on replaced by
    /// <c>?</c>: control characters including the C1 range, line and paragraph separators, the format
    /// characters, which hold the bidirectional controls and the zero-width ones, and a surrogate with no partner.
    /// A line break would split the entry in a log, an escape sequence is run by a terminal that tails it, and a
    /// bidi control reorders the text a reader sees.
    /// </summary>
    internal static string Clean(string text)
    {
        var at = 0;
        while (at < text.Length && !Replaced(text, at)) at++;
        if (at == text.Length) return text;

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Replaced(source, i) ? '?' : source[i];
        });
    }

    private static bool Replaced(string text, int at)
    {
        var c = text[at];
        if (char.IsHighSurrogate(c)) return at + 1 >= text.Length || !char.IsLowSurrogate(text[at + 1]);
        if (char.IsLowSurrogate(c)) return at == 0 || !char.IsHighSurrogate(text[at - 1]);

        return char.GetUnicodeCategory(c) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }
}
