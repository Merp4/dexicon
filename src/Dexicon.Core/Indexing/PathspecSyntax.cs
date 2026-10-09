using System.Text;

namespace Dexicon.Core.Indexing;

/// <summary>A pathspec split into its magic and its path, with what git's parser does to each.</summary>
/// <param name="Path">What follows the magic, or the whole element when it has none. Null when the long form never closes.</param>
/// <param name="PathUnchecked">
/// Git does not look at where the path goes. That is so for <c>top</c> magic (<c>:(top)</c>, or <c>/</c> in the
/// short form), which reads the path from the repository root, and for <c>prefix:N</c> with N of zero or more,
/// which says the first N characters are already a normalised prefix. Such a path is not rejected for being
/// rooted or for climbing out with <c>..</c>.
/// </param>
/// <param name="Malformed">The magic is one git rejects whatever the path: git fails with a fatal error.</param>
public readonly record struct PathspecMagic(string? Path, bool PathUnchecked, bool Malformed);

/// <summary>
/// How git parses the magic at the start of a pathspec, as far as it decides that git fails on every call.
/// Measured on git 2.54.0 on Linux, the version in the image, against 431 hand-written elements and about
/// 3,000 generated ones. Whatever is not decided here is left to git, which reports it as the reason a history
/// source is unavailable.
/// </summary>
public static class PathspecSyntax
{
    /// <summary>The characters git reads as short magic after a colon. Only <c>!</c>, <c>^</c> and <c>/</c> are implemented.</summary>
    private const string ShortMagicCharacters = "!\"#%&',-/;<=>@_`~^";

    /// <summary>
    /// Splits a pathspec. An element not starting with <c>:</c> has no magic.
    ///
    /// The long form is <c>:(</c>, comma-separated words and <c>)</c>. The words are matched exactly and may be
    /// empty: <c>top</c>, <c>literal</c>, <c>icase</c>, <c>glob</c>, <c>exclude</c>, <c>attr</c>, <c>attr:</c> with
    /// a specification, and <c>prefix:</c> with a number. The long form is malformed when it has no closing
    /// <c>)</c>, has any other word, names both <c>glob</c> and <c>literal</c>, has more than one <c>attr:</c>,
    /// has an <c>attr:</c> specification git cannot use (see <see cref="AttrSpecUsable"/>), or has a
    /// <c>prefix:</c> that is not a number, or whose last one is longer than the path.
    ///
    /// The short form is <c>:</c> followed by characters of git's magic set and an optional <c>:</c> that ends it;
    /// of those only <c>!</c>, <c>^</c> and <c>/</c> are implemented, so any other makes it malformed. A character
    /// outside the set, such as a letter, <c>.</c> or <c>*</c>, ends the magic and starts the path.
    /// </summary>
    public static PathspecMagic Parse(string pathspec)
    {
        if (pathspec.Length == 0 || pathspec[0] != ':') return new PathspecMagic(pathspec, false, false);

        return pathspec.Length > 1 && pathspec[1] == '(' ? ParseLong(pathspec) : ParseShort(pathspec);
    }

    private static PathspecMagic ParseShort(string pathspec)
    {
        var at = 1;
        var top = false;
        var malformed = false;

        while (at < pathspec.Length && ShortMagicCharacters.Contains(pathspec[at]))
        {
            if (pathspec[at] == '/') top = true;
            else if (pathspec[at] is not ('!' or '^')) malformed = true;

            at++;
        }

        if (at < pathspec.Length && pathspec[at] == ':') at++;

        return new PathspecMagic(pathspec[at..], top, malformed);
    }

    private static PathspecMagic ParseLong(string pathspec)
    {
        var words = new List<string>();
        var wordStart = 2;
        var closed = false;
        var at = 2;

        for (; at < pathspec.Length; at++)
        {
            var c = pathspec[at];

            // In an attr: word a backslash escapes the next character, so \, and \) stay in the word.
            if (c == '\\' && pathspec.AsSpan(wordStart).StartsWith("attr:", StringComparison.Ordinal))
            {
                if (at + 1 < pathspec.Length) at++;
                continue;
            }

            if (c is not (',' or ')')) continue;

            words.Add(pathspec[wordStart..at]);
            wordStart = at + 1;

            if (c == ')')
            {
                closed = true;
                break;
            }
        }

        if (!closed) return new PathspecMagic(null, false, true);

        var path = pathspec[(at + 1)..];
        var pathUnchecked = false;
        var malformed = false;
        var glob = false;
        var literal = false;
        var attrSpecifications = 0;
        int? prefix = null;

        foreach (var word in words)
        {
            switch (word)
            {
                case "": case "icase": case "exclude": case "attr": break;
                case "top": pathUnchecked = true; break;
                case "glob": glob = true; break;
                case "literal": literal = true; break;
                default:
                    if (word.StartsWith("attr:", StringComparison.Ordinal))
                    {
                        attrSpecifications++;
                        if (!AttrSpecUsable(word["attr:".Length..])) malformed = true;
                    }
                    else if (word.StartsWith("prefix:", StringComparison.Ordinal))
                    {
                        // Each word must be a number, and the last one is the prefix git uses.
                        if (PrefixLength(word["prefix:".Length..]) is { } length) prefix = length;
                        else malformed = true;
                    }
                    else malformed = true;

                    break;
            }
        }

        // A prefix longer than the path is a failed internal check in git, which aborts.
        if (prefix > Encoding.UTF8.GetByteCount(path)) malformed = true;
        else if (prefix >= 0) pathUnchecked = true;

        return new PathspecMagic(path, pathUnchecked, malformed || attrSpecifications > 1 || (glob && literal));
    }

    /// <summary>
    /// Whether git can use the specification after <c>attr:</c>, which it reads as items separated by spaces
    /// (a tab is part of a name, and git fails on it). The specification may be spaces alone but not empty. An
    /// item is a name, set; <c>-name</c>, unset; <c>!name</c>, unspecified; or <c>name=value</c>. A name is not
    /// empty, does not start with <c>-</c> and holds only ASCII letters, digits, <c>-</c>, <c>.</c> and <c>_</c>,
    /// so <c>-</c>, <c>--a</c>, <c>!!a</c>, <c>a/b</c> and <c>-a=b</c> fail. In a value a backslash escapes the next
    /// character, a lone one at the end of an item fails, and every character must be an ASCII letter or digit, or
    /// one of <c>-</c>, <c>_</c> and <c>,</c>: <c>a=b.c</c>, <c>a=b=c</c>, <c>a=\)</c> and a value with a non-ASCII
    /// character fail, and <c>a=\,b</c> does not.
    /// </summary>
    public static bool AttrSpecUsable(string specification)
    {
        if (specification.Length == 0) return false;

        foreach (var item in specification.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string name;
            string? value = null;

            if (item[0] is '-' or '!') name = item[1..];
            else
            {
                var equals = item.IndexOf('=');
                name = equals < 0 ? item : item[..equals];
                if (equals >= 0) value = item[(equals + 1)..];
            }

            if (!AttrNameValid(name)) return false;
            if (value is not null && !AttrValueValid(value)) return false;
        }

        return true;
    }

    private static bool AttrNameValid(string name) =>
        name.Length > 0 && name[0] != '-' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

    private static bool AttrValueValid(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c == '\\')
            {
                if (++i == value.Length) return false;
                c = value[i];
            }

            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ',')) return false;
        }

        return true;
    }

    /// <summary>
    /// The number after <c>prefix:</c>, read as C's <c>strtol</c> in base 10 and then stored in a 32-bit integer:
    /// null unless the whole text is empty (zero) or leading white space, an optional sign and digits. A number
    /// out of range saturates before it is truncated, so <c>99999999999999999999</c> is -1.
    /// </summary>
    private static int? PrefixLength(string text)
    {
        if (text.Length == 0) return 0;

        var at = 0;
        while (at < text.Length && text[at] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r') at++;

        var negative = false;
        if (at < text.Length && text[at] is '+' or '-') negative = text[at++] == '-';

        var digits = at;
        long value = 0;
        var overflow = false;

        for (; at < text.Length && text[at] is >= '0' and <= '9'; at++)
        {
            var digit = text[at] - '0';
            if (value > (long.MaxValue - digit) / 10) overflow = true;
            else value = (value * 10) + digit;
        }

        if (at != text.Length || at == digits) return null;

        var number = overflow ? (negative ? long.MinValue : long.MaxValue) : (negative ? -value : value);
        return unchecked((int)number);
    }
}
