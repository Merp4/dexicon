using System.Globalization;

namespace Dexicon.Core.Indexing;

/// <summary>
/// An ignore file, or a line of one, or an entry of a glob list, that <see cref="IgnoreRuleSet.AddPatterns"/> or
/// the walk cannot turn into rules. The message names where it was read from, the position in it, and the text,
/// so it can be shown to an operator as it stands. It is an <see cref="ArgumentException"/>.
///
/// The message reaches logs, a job's error text and <c>index_status</c>, and every part of it is text from the
/// indexed tree: the line, and the directory names in the file's path. The whole message is cleaned
/// (<see cref="Clean"/>) and never any one part.
/// </summary>
public sealed class IgnorePatternException : ArgumentException
{
    private const int MaxShownLength = 100;

    internal IgnorePatternException(string message, Exception? inner = null) : base(Clean(message), inner) { }

    /// <summary>
    /// What the walk had skipped before it failed, so a caller that reports the failure can report these too.
    /// Set by <see cref="WorkspaceWalker.Walk"/>.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = [];

    /// <summary>How many more warnings were counted than <see cref="Warnings"/> holds.</summary>
    public int WarningsOmitted { get; internal set; }

    /// <summary>A pattern at <paramref name="where"/> (<c>.gitignore line 3</c>, <c>excludeGlobs[1]</c>), or the entry itself when null.</summary>
    internal static string Compose(string where, string? pattern, string reason) =>
        pattern is null ? $"{where} {reason}" : $"{where} ('{Shown(pattern)}') {reason}";

    internal static IgnorePatternException For(string where, string? pattern, string reason, Exception? inner = null) =>
        new(Compose(where, pattern, reason), inner);

    /// <summary>A whole file that cannot be read as patterns, named by its path from the scan root.</summary>
    internal static IgnorePatternException ForFile(string path, string reason, Exception? inner = null) =>
        new($"{path} {reason}", inner);

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
    /// characters, which hold the bidirectional controls, the zero-width ones and the Unicode tags, and a surrogate
    /// with no partner. A pair is classified as the one code point it spells, so the format characters beyond the
    /// Basic Multilingual Plane are replaced too, both of their units. A line break would split the entry in a
    /// log, an escape sequence is run by a terminal that tails it, and a bidi control reorders the text a reader
    /// sees.
    /// </summary>
    internal static string Clean(string text)
    {
        var at = 0;
        while (at < text.Length && !Replaced(text, at, out var width)) at += width;
        if (at >= text.Length) return text;

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length;)
            {
                var replace = Replaced(source, i, out var width);
                for (var k = 0; k < width; k++) span[i + k] = replace ? '?' : source[i + k];
                i += width;
            }
        });
    }

    /// <summary>Whether the code point at <paramref name="at"/> is replaced, and how many units it spans.</summary>
    private static bool Replaced(string text, int at, out int width)
    {
        var c = text[at];
        width = 1;

        if (char.IsHighSurrogate(c))
        {
            if (at + 1 >= text.Length || !char.IsLowSurrogate(text[at + 1])) return true;

            width = 2;
            return IsReplaced(CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, text[at + 1])));
        }

        if (char.IsLowSurrogate(c)) return true;

        return IsReplaced(char.GetUnicodeCategory(c));
    }

    private static bool IsReplaced(UnicodeCategory category) =>
        category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
}
