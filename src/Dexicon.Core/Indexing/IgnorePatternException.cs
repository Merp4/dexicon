namespace Dexicon.Core.Indexing;

/// <summary>
/// A line of an ignore file, or an entry of a glob list, that <see cref="IgnoreRuleSet.AddPatterns"/> cannot
/// turn into a rule. The message names where it was read from, the position in it, and the text, so it can be
/// shown to an operator as it stands. It is an <see cref="ArgumentException"/> because that is what the same
/// failure raised before this type existed.
/// </summary>
public sealed class IgnorePatternException : ArgumentException
{
    private const int MaxShownLength = 100;

    private IgnorePatternException(string message, Exception? inner) : base(message, inner) { }

    internal static IgnorePatternException For(
        string source, string positionNoun, int position, string? pattern, string reason, Exception? inner = null) =>
        new(pattern is null
            ? $"{source} {positionNoun} {position} {reason}"
            : $"{source} {positionNoun} {position} ('{Shown(pattern)}') {reason}", inner);

    /// <summary>
    /// The pattern as it goes into a message: control characters replaced, since it came from a file in the
    /// indexed tree and ends up in a job's error text and in log lines, and cut to a length a line can hold.
    /// </summary>
    private static string Shown(string pattern)
    {
        var text = pattern.Length > MaxShownLength ? pattern[..MaxShownLength] + "..." : pattern;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
    }
}
