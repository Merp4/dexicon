using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dexicon.Core.Indexing;

namespace Dexicon.Tests;

/// <summary>
/// The translation of a glob to a regular expression that ignore rules were matched with before
/// <c>GlobMatcher</c> replaced it, kept here as the oracle the differential test compares the matcher with. It
/// is built with <see cref="RegexOptions.IgnoreCase"/> and the backtracking engine, with a timeout long enough
/// for the globs the test generates.
///
/// Bracket classes are read by <c>GlobMatcher.ReadClass</c>, the same reader the matcher uses, so the oracle does not
/// check how a class is read. <c>BracketClassMembersTests</c> does, against what git printed.
///
/// Two things the regular expression does that the matcher does not, and the differential test leaves out by not
/// comparing a path that contains a line feed: <c>.</c> does not match <c>\n</c>, so <c>**</c> stopped at a line feed
/// in a file name, and <c>$</c> matches before a final <c>\n</c>, so <c>x.md\n</c> matched <c>*.md</c>. Git treats a
/// line feed as any other character, and so does the matcher (<c>GlobMatcherTests</c>).
/// </summary>
internal static class GlobOracle
{
    public static Regex Compile(string glob, string directoryPrefix) =>
        new(ToRegex(glob, directoryPrefix), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10));

    /// <exception cref="FormatException">A bracket class that cannot be read.</exception>
    public static string ToRegex(string glob, string directoryPrefix)
    {
        var anchored = glob.StartsWith('/');
        if (anchored) glob = glob[1..];

        var matchAtAnyDepth = !anchored && !glob.TrimEnd('/').Contains('/', StringComparison.Ordinal);

        var sb = new StringBuilder("^");
        if (directoryPrefix.Length > 0) sb.Append(Regex.Escape(directoryPrefix)).Append('/');
        if (matchAtAnyDepth) sb.Append("(?:.*/)?");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                    {
                        if (GlobMatcher.ReadClass(glob, i) is not { } cls) { sb.Append("\\["); break; }

                        sb.Append(ClassRegex(cls));
                        i = cls.Close;
                        break;
                    }
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append("(?:/.*)?$");
        return sb.ToString();
    }

    private static string ClassRegex(GlobMatcher.ClassSpan cls)
    {
        if (cls.Members.Count == 0 && !cls.Negated) return @"[^\s\S]";

        var sb = new StringBuilder(cls.Negated ? "[^/" : "[");
        foreach (var (low, high) in cls.Members)
        {
            Escaped(sb, low);
            if (high == low) continue;
            sb.Append('-');
            Escaped(sb, high);
        }

        return sb.Append(']').ToString();
    }

    private static void Escaped(StringBuilder sb, char c)
    {
        if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        else sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
    }
}
