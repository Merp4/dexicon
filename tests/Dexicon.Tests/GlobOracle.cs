using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dexicon.Tests;

/// <summary>
/// A glob as a .NET regular expression, kept here as the oracle the differential test compares
/// <c>GlobMatcher</c> with. It is built with <see cref="RegexOptions.IgnoreCase"/> and <see cref="RegexOptions.CultureInvariant"/>
/// and the backtracking engine, with a timeout long enough for the globs the test generates.
///
/// The wildcards are translated <c>*</c> to <c>[^/]*</c>, <c>**</c> to <c>.*</c>, <c>**/</c> to <c>(?:.*/)?</c> and
/// the end of the pattern to <c>(?:/.*)?$</c>, with three or more stars as a whole segment read as <c>**</c>.
///
/// A bracket class is not read by the code under test. It is read here by a port of the loop git's <c>wildmatch</c>
/// runs over a class (the first character is a member whatever it is, a backslash escapes, a dash after a member
/// makes a range, a dash after a range is a member) and evaluated over the alphabet the test draws from, so that the
/// regular expression holds the characters git would match. Four decisions are made the way the matcher makes them and
/// are not git's: a reversed range, an empty class and a POSIX class make the line unusable, and a class with no
/// closing bracket is a literal bracket. <c>BracketClassMembersTests</c> holds the matcher to what git printed.
///
/// Two things the regular expression does that the matcher does not, and the differential test leaves out by not
/// comparing a path that contains a line feed: <c>.</c> does not match a line feed, so <c>**</c> stops at one in a file
/// name, and <c>$</c> matches before a final one, so <c>x.md</c> then a line feed matches <c>*.md</c>. Git treats a line
/// feed as any other character, and so does the matcher (<c>GlobMatcherTests</c>).
/// </summary>
internal static class GlobOracle
{
    public static Regex Compile(string glob, string directoryPrefix) =>
        new(ToRegex(glob, directoryPrefix), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));

    /// <exception cref="FormatException">The glob holds a class the matcher refuses.</exception>
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

                        // Three or more stars that are a whole path segment are one `**`, as in git.
                        var run = i - 1;
                        while (run + 1 < glob.Length && glob[run + 1] == '*') run++;
                        if ((i - 1 == 0 || glob[i - 2] == '/') && (run + 1 == glob.Length || glob[run + 1] == '/')) i = run;

                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                    {
                        var read = ReadClass(glob, i);
                        if (read.Literal) { sb.Append("\\["); break; }

                        sb.Append(read.Regex);
                        i = read.Close;
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

    private readonly record struct ClassRead(bool Literal, int Close, string Regex);

    /// <summary>Git's loop over the members of a class, from the character after the opening bracket.</summary>
    private static ClassRead ReadClass(string glob, int open)
    {
        var n = glob.Length;
        var q = open + 1;
        var negated = false;
        if (q < n && glob[q] is '!' or '^') { negated = true; q++; }

        var ranges = new List<(char Low, char High)>();
        var first = q;
        char previous = '\0';

        // A class that cannot be read is refused only if it is closed; with no closing bracket the bracket is a
        // literal, as in git the whole pattern then matches nothing.
        string? refusal = null;

        while (true)
        {
            if (q >= n) return Unterminated(glob, open, first);

            var ch = glob[q];
            if (ch == '\\')
            {
                q++;
                if (q >= n) return Unterminated(glob, open, first);

                ch = glob[q];
                ranges.Add((ch, ch));
            }
            else if (ch == '-' && previous != '\0' && q + 1 < n && glob[q + 1] != ']')
            {
                q++;
                var high = glob[q];
                if (high == '\\')
                {
                    q++;
                    if (q >= n) return Unterminated(glob, open, first);

                    high = glob[q];
                }

                if (high < previous) refusal ??= "reversed character range";

                ranges.Add((previous, high));
                ch = '\0';
            }
            else if (ch == '[' && q + 1 < n && glob[q + 1] == ':')
            {
                var end = glob.IndexOf(']', q + 2);
                if (end < 0) return Unterminated(glob, open, first);

                if (glob[end - 1] == ':' && end - 1 > q + 1)
                    refusal ??= "a POSIX character class";

                ranges.Add((ch, ch));
            }
            else ranges.Add((ch, ch));

            previous = ch;
            q++;
            if (q >= n) return Unterminated(glob, open, first);
            if (glob[q] == ']') break;
        }

        if (refusal is not null) throw new FormatException(refusal);

        return new ClassRead(false, q, ClassRegex(ranges, negated));
    }

    /// <summary>No closing bracket: the bracket is a literal, except the two shapes the matcher keeps or refuses.</summary>
    private static ClassRead Unterminated(string glob, int open, int first)
    {
        var leadingBracket = first < glob.Length && glob[first] == ']';
        if (!leadingBracket) return new ClassRead(true, 0, string.Empty);

        // `[!]x` is the one-member class of `!`; `[]x` and `[^]x` are an empty class.
        if (glob[open + 1] == '!') return new ClassRead(false, first, ClassRegex([('!', '!')], negated: false));

        throw new FormatException("empty character class");
    }

    /// <summary>
    /// The class as characters of the alphabet: those a member covers or has a case variant that a member covers. A
    /// negated class is every other character but <c>/</c>, and no class holds <c>/</c>.
    /// </summary>
    private static string ClassRegex(List<(char Low, char High)> ranges, bool negated)
    {
        var members = new StringBuilder();
        foreach (var c in GlobTestAlphabet.Chars)
        {
            if (c == '/') continue;

            if (GlobTestAlphabet.Equivalents(c).Any(v => ranges.Any(r => r.Low <= v && v <= r.High))) Append(members, c);
        }

        if (negated) return $"[^/{members}]";
        return members.Length == 0 ? @"[^\s\S]" : $"[{members}]";
    }

    private static void Append(StringBuilder sb, char c) =>
        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
}

/// <summary>
/// The characters the differential test draws globs and paths from, and which of them the regular expression's
/// case-insensitive comparison treats as the same.
/// </summary>
internal static class GlobTestAlphabet
{
    private static readonly Lazy<(char[] Chars, Dictionary<char, char[]> Equivalents)> Built = new(Build);

    /// <summary>Every character, as a UTF-16 unit, that appears in a glob or a path of <c>GlobDifferentialTests</c>.</summary>
    public static IReadOnlyList<char> Chars => Built.Value.Chars;

    public static IReadOnlyList<char> Equivalents(char c) => Built.Value.Equivalents[c];

    private static (char[], Dictionary<char, char[]>) Build()
    {
        var chars = GlobDifferentialTests.AllPieces().SelectMany(s => s).Concat("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/.-_ ")
            .Distinct().OrderBy(c => c).ToArray();

        var equivalents = new Dictionary<char, char[]>();
        foreach (var a in chars)
        {
            var regex = new Regex("^" + Regex.Escape(a.ToString()) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            equivalents[a] = [.. chars.Where(b => regex.IsMatch(b.ToString()))];
        }

        return (chars, equivalents);
    }
}
