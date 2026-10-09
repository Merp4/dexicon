using System.Buffers;
using System.Globalization;
using System.Text;

namespace Dexicon.Infrastructure;

/// <summary>
/// Text from a caller, a file name or a stored name, made safe to print on one line of a log or in a reply
/// that a model reads. Used by the log sink, the MCP tools and the API, which is why it is not in either.
/// </summary>
internal static class LogText
{
    /// <summary>What a replaced character becomes.</summary>
    internal const char Marker = (char)0xFFFD;

    /// <summary>
    /// Whether a code point can start a line, hide a line or change how the text around it reads: the
    /// control characters (C0, DEL and C1, which include NEL and CSI), the format characters (the
    /// bidirectional controls, zero-width characters, the byte order mark, the soft hyphen, the tag
    /// characters), and the line and paragraph separators. A zero-width joiner inside an emoji sequence or a
    /// zero-width non-joiner inside a Persian word is one of them, and is replaced too.
    /// </summary>
    /// <param name="includeC0">
    /// Whether U+0000 to U+001F count. <c>{Message:j}</c> already escapes them in a logged string.
    /// </param>
    internal static bool IsHostile(Rune rune, bool includeC0)
    {
        if (rune.Value < 0x20) return includeC0;

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate;
    }

    /// <summary>
    /// <paramref name="text"/> with each hostile code point (see <see cref="IsHostile"/>) and each lone
    /// surrogate replaced by one <see cref="Marker"/>. The same instance when there is nothing to replace.
    /// </summary>
    internal static string Neutralise(string text, bool includeC0)
    {
        var source = text.AsSpan();
        var at = 0;

        while (at < source.Length)
        {
            var status = Rune.DecodeFromUtf16(source[at..], out var rune, out var used);
            if (status != OperationStatus.Done || IsHostile(rune, includeC0)) break;
            at += used;
        }

        if (at == source.Length) return text;

        var held = new StringBuilder(text.Length);
        held.Append(source[..at]);
        while (at < source.Length)
        {
            var status = Rune.DecodeFromUtf16(source[at..], out var rune, out var used);
            if (status == OperationStatus.Done && !IsHostile(rune, includeC0)) held.Append(source.Slice(at, used));
            else held.Append(Marker);
            at += Math.Max(used, 1);
        }

        return held.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="max"/> characters and a "...", not between the halves
    /// of a surrogate pair. The same instance when it is not longer.
    /// </summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;

        var length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return string.Concat(text.AsSpan(0, length), "...");
    }

    /// <summary>
    /// A path, name or other value as one line. A file name on Linux can hold a line break, and printed as it
    /// is, one would end its entry early and could begin a line that reads as another status or reason. A
    /// glob is stored as it was typed, so it can hold one too. Line breaks become spaces, and
    /// <see cref="DexiconAuthMiddleware.OneLine"/> then replaces the characters that remain from
    /// <see cref="IsHostile"/>, such as an escape sequence.
    /// </summary>
    internal static string OneLine(string text) => DexiconAuthMiddleware.OneLine(text.ReplaceLineEndings(" "));

    /// <summary>
    /// A caller's value as an error repeats it: cut at <paramref name="max"/> characters, then held to one
    /// line by <see cref="OneLine"/>. The cut comes first, so a value of any length costs at most
    /// <paramref name="max"/> characters of scanning, and a replacement never lengthens text, so the cut
    /// cannot fall inside one.
    /// </summary>
    internal static string Echo(string? value, int max)
    {
        if (value is null) return string.Empty;
        if (value.Length <= max) return OneLine(value);

        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return OneLine(value[..cut]) + "...";
    }
}
