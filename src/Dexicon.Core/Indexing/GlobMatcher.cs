using System.Buffers;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Matches one ignore pattern against a path. The pattern is read once into tokens (a literal character, <c>?</c>, a
/// bracket class, <c>*</c>, <c>**</c>, and <c>**/</c>) and a match runs them over the path, so the time to match is
/// at most the number of tokens times the length of the path and the memory held per rule is its tokens.
///
/// The pattern means what the regular expression written for it meant, which the differential test keeps as an oracle
/// (<c>GlobOracle</c> in the test project):
/// <code>
/// ^ prefix/ [(?:.*/)?] tokens (?:/.*)?$
/// </code>
/// where a token is a literal, <c>[^/]</c>, a class that never matches <c>/</c>, <c>[^/]*</c>, <c>.*</c> or
/// <c>(?:.*/)?</c>, and the optional group before the tokens is present for a pattern with no slash in it, which
/// matches at any depth. The end of the tokens must fall at the end of the path or at a <c>/</c>, so a pattern that
/// matches a directory matches everything beneath it.
///
/// Why this is not a regular expression. A backtracking engine takes time exponential in the number of wildcards
/// (<c>*a*a*a*a*a*a*a*a*a*a*a*a*b</c>), and its 250 ms match timeout threw from the middle of a walk, and also
/// fired for <c>*.so</c> when the host was starved of CPU. The engine that cannot backtrack
/// (<c>RegexOptions.NonBacktracking</c>) costs about 390 KiB to build each matcher and holds it, which a
/// <c>.gitignore</c> in an indexed tree can multiply into gigabytes. Here the work is bounded by the pattern and the
/// path, so there is no timeout, and a rule is a few tens of bytes per character of the pattern.
///
/// How a match runs. The set of positions in the path the tokens so far can have reached is kept as a window over a
/// byte array. A literal, <c>?</c> or class moves each position forward by one if the character fits; <c>*</c> extends
/// each position to the next <c>/</c>; <c>**</c> extends to the end; <c>**/</c> adds each position after a later
/// <c>/</c>. Each token costs at most the length of the path, which is the bound. Most rules are rejected before
/// that, by a search for the longest run of literal characters the pattern needs.
///
/// Case. Two characters are the same when their invariant lower cases are (<see cref="Fold"/>). That is the case
/// equivalence of <c>RegexOptions.IgnoreCase</c>: <c>CaseFoldTests</c> compares the two over the 2,357 code points
/// of the BMP that have a case and finds no pair they disagree on. It is not <c>StringComparison.OrdinalIgnoreCase</c>,
/// which compares upper cases and so does not pair the Kelvin sign with <c>k</c>.
///
/// Characters are UTF-16 units, as in the regular expression, so <c>?</c> matches one unit and not a whole emoji.
/// </summary>
internal sealed class GlobMatcher
{
    private enum Kind : byte { Literal, One, Class, Star, Any, SlashGlob }

    private readonly record struct Token(Kind Kind, char Char = '\0', CharClass? Class = null);

    private readonly Token[] _tokens;

    // The directory the pattern was written in and a slash, compared with Fold; empty at the scan root.
    private readonly string _prefix;

    // The longest run of literal characters in the tokens, folded. A path without it cannot match.
    private readonly char[]? _required;
    private readonly SearchValues<char>? _requiredFirst;

    /// <summary>The number of tokens, which with the length of a path bounds the steps of a match.</summary>
    internal int TokenCount => _tokens.Length;

    private GlobMatcher(Token[] tokens, string prefix)
    {
        _tokens = tokens;
        _prefix = prefix;

        (_required, _requiredFirst) = RequiredLiteral(tokens);
    }

    /// <exception cref="FormatException">A bracket class that cannot be read; see <see cref="ReadClass"/>.</exception>
    internal static GlobMatcher Compile(string glob, string directoryPrefix)
    {
        var anchored = glob.StartsWith('/');
        if (anchored) glob = glob[1..];

        // A pattern with no interior slash matches at any depth: `*.dll`, `node_modules`.
        var matchAtAnyDepth = !anchored && !glob.TrimEnd('/').Contains('/', StringComparison.Ordinal);

        var tokens = new List<Token>(glob.Length + 1);
        if (matchAtAnyDepth) tokens.Add(new Token(Kind.SlashGlob));

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; tokens.Add(new Token(Kind.SlashGlob)); }
                        else tokens.Add(new Token(Kind.Any));
                    }
                    else tokens.Add(new Token(Kind.Star));
                    break;
                case '?':
                    tokens.Add(new Token(Kind.One));
                    break;
                case '[':
                    {
                        if (ReadClass(glob, i) is not { } cls) { tokens.Add(new Token(Kind.Literal, '[')); break; }

                        tokens.Add(new Token(Kind.Class, Class: new CharClass(cls.Members, cls.Negated)));
                        i = cls.Close;
                        break;
                    }
                default:
                    tokens.Add(new Token(Kind.Literal, c));
                    break;
            }
        }

        return new GlobMatcher([.. tokens], directoryPrefix.Length > 0 ? directoryPrefix + "/" : string.Empty);
    }

    internal bool IsMatch(ReadOnlySpan<char> path)
    {
        long steps = 0;
        return IsMatch(path, ref steps);
    }

    /// <param name="steps">Incremented by the positions looked at, so a test can bound the work of a match.</param>
    internal bool IsMatch(ReadOnlySpan<char> path, ref long steps)
    {
        var start = 0;
        if (_prefix.Length > 0)
        {
            if (path.Length < _prefix.Length) return false;
            for (var i = 0; i < _prefix.Length; i++)
                if (!Fold.Equal(path[i], _prefix[i])) return false;

            start = _prefix.Length;
        }

        if (_required is not null && !ContainsRequired(path[start..])) return false;

        var n = path.Length;
        var size = n + 2;
        byte[]? rented = null;
        Span<byte> buffer = 2 * size <= 1024
            ? stackalloc byte[2 * size]
            : (rented = ArrayPool<byte>.Shared.Rent(2 * size)).AsSpan(0, 2 * size);
        buffer.Clear();

        try
        {
            return Run(path, start, buffer[..size], buffer[size..], ref steps);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool Run(ReadOnlySpan<char> path, int start, Span<byte> cur, Span<byte> next, ref long steps)
    {
        var n = path.Length;
        cur[start] = 1;
        int lo = start, hi = start;

        foreach (var token in _tokens)
        {
            steps += hi - lo + 1;
            int nextLo = lo, nextHi = -1;

            switch (token.Kind)
            {
                case Kind.Literal or Kind.One or Kind.Class:
                    {
                        nextLo = int.MaxValue;
                        var last = Math.Min(hi, n - 1);
                        for (var i = lo; i <= last; i++)
                        {
                            if (cur[i] == 0 || !Accepts(token, path[i])) continue;

                            next[i + 1] = 1;
                            if (i + 1 < nextLo) nextLo = i + 1;
                            nextHi = i + 1;
                        }

                        break;
                    }
                case Kind.Star:
                    {
                        // next[j] = cur[j] or (next[j-1] and the character before j is not a slash)
                        var reached = false;
                        for (var j = lo; j <= n; j++)
                        {
                            var here = (j <= hi && cur[j] != 0) || (reached && path[j - 1] != '/');
                            if (!here && j > hi) break;

                            steps++;
                            reached = here;
                            if (here) { next[j] = 1; nextHi = j; }
                        }

                        break;
                    }
                case Kind.Any:
                    {
                        steps += n - lo;
                        next[lo..(n + 1)].Fill(1);
                        nextHi = n;
                        break;
                    }
                default:
                    {
                        // `**/`: where it is, or after any later slash.
                        for (var j = lo; j <= n; j++)
                        {
                            steps++;
                            if ((j <= hi && cur[j] != 0) || (j > lo && path[j - 1] == '/')) { next[j] = 1; nextHi = j; }
                        }

                        break;
                    }
            }

            cur.Slice(lo, hi - lo + 1).Clear();
            if (nextHi < 0) return false;

            var swap = cur;
            cur = next;
            next = swap;
            lo = nextLo;
            hi = nextHi;
        }

        // The tokens must end at the end of the path or at a slash.
        for (var i = lo; i <= hi; i++)
            if (cur[i] != 0 && (i == n || path[i] == '/')) return true;

        return false;
    }

    private static bool Accepts(Token token, char c) =>
        token.Kind switch
        {
            Kind.Literal => Fold.Equal(c, token.Char),
            Kind.One => c != '/',
            _ => c != '/' && token.Class!.Matches(c),
        };

    // ---- the required literal ----------------------------------------------------------

    private static (char[]?, SearchValues<char>?) RequiredLiteral(Token[] tokens)
    {
        int bestStart = 0, bestLength = 0;
        for (var i = 0; i < tokens.Length;)
        {
            if (tokens[i].Kind != Kind.Literal) { i++; continue; }

            var j = i;
            while (j < tokens.Length && tokens[j].Kind == Kind.Literal) j++;
            if (j - i > bestLength) { bestStart = i; bestLength = j - i; }
            i = j;
        }

        if (bestLength == 0) return (null, null);

        var folded = new char[bestLength];
        for (var k = 0; k < bestLength; k++) folded[k] = Fold.Of(tokens[bestStart + k].Char);

        var first = Fold.Variants(folded[0]);
        return (folded, first.Length > 1 ? SearchValues.Create(first) : null);
    }

    private bool ContainsRequired(ReadOnlySpan<char> text)
    {
        var required = _required!;
        var last = text.Length - required.Length;

        for (var at = 0; at <= last;)
        {
            var found = _requiredFirst is not null
                ? text[at..(last + 1)].IndexOfAny(_requiredFirst)
                : text[at..(last + 1)].IndexOf(required[0]);
            if (found < 0) return false;

            at += found;
            var all = true;
            for (var k = 1; k < required.Length && all; k++)
                all = Fold.Of(text[at + k]) == required[k];

            if (all) return true;
            at++;
        }

        return false;
    }

    // ---- bracket classes ---------------------------------------------------------------

    /// <summary>The characters of one bracket class, which never includes <c>/</c>.</summary>
    internal sealed class CharClass(IReadOnlyList<(char Low, char High)> members, bool negated)
    {
        private readonly (char Low, char High)[] _members = [.. members];

        public bool Matches(char c)
        {
            // A class holds a character if it holds any character that folds to the same one.
            var found = false;
            foreach (var same in Fold.Variants(Fold.Of(c)))
            {
                if (!In(same)) continue;
                found = true;
                break;
            }

            return found != negated;
        }

        private bool In(char c)
        {
            foreach (var (low, high) in _members)
                if (low <= c && c <= high) return true;

            return false;
        }
    }

    /// <summary>
    /// One bracket class of a glob. <paramref name="Close"/> is the index of its closing <c>]</c>,
    /// <paramref name="Negated"/> says it opened with <c>!</c> or <c>^</c>, and <paramref name="Members"/> are the
    /// inclusive character ranges it holds, with <c>/</c> removed.
    /// </summary>
    internal sealed record ClassSpan(int Close, bool Negated, IReadOnlyList<(char Low, char High)> Members);

    /// <summary>
    /// Reads the bracket class that opens at <paramref name="open"/>, or null when nothing closes it, in which case
    /// the <c>[</c> is a literal character.
    ///
    /// The members are read as git reads them (measured with git 2.31.1 on Windows and 2.54.0 on Linux): a
    /// <c>]</c> straight after <c>[</c>, <c>[!</c> or <c>[^</c> is a member; <c>[</c> is a member; a <c>-</c> after a
    /// finished range or at the end is a member; a backslash takes the next character literally, so <c>\]</c> does
    /// not close the class and <c>\d</c> is a <c>d</c>; and no class matches <c>/</c>, positive or negated.
    ///
    /// Two shapes keep the reading they had. <c>[!]x</c>, with no <c>]</c> after the one following the <c>!</c>, is
    /// the one-member class of <c>!</c>. <c>[]x</c> and <c>[^]x</c> are an empty class and throw
    /// <see cref="FormatException"/>, as does a range whose end is before its start such as <c>[z-a]</c>. Git matches
    /// only the start character for that range; here it is refused, so the typo shows.
    /// </summary>
    internal static ClassSpan? ReadClass(string glob, int open)
    {
        var start = open + 1;
        var negated = start < glob.Length && glob[start] is '!' or '^';
        if (negated) start++;

        // A leading ] is a member. Scanning begins after it, and when nothing later closes the class the
        // first ] from the opening bracket does, which is what a one-member `[!]` and an empty `[]` need.
        var at = start < glob.Length && glob[start] == ']' ? start + 1 : start;
        var close = -1;
        for (; at < glob.Length; at++)
        {
            if (glob[at] == '\\' && at + 1 < glob.Length) { at++; continue; }
            if (glob[at] == ']') { close = at; break; }
        }

        if (close < 0)
        {
            if (start >= glob.Length || glob[start] != ']') return null;

            close = start;
            if (negated && glob[open + 1] == '!')
                return new ClassSpan(close, Negated: false, [('!', '!')]);

            throw new FormatException("empty character class");
        }

        var members = new List<(char, char)>();
        for (var p = start; p < close;)
        {
            var low = Member(glob, ref p);
            var high = low;
            if (p + 1 < close && glob[p] == '-')
            {
                p++;
                high = Member(glob, ref p);
                if (high < low) throw new FormatException("reversed character range");
            }

            AddWithoutSlash(members, low, high);
        }

        return new ClassSpan(close, negated, members);
    }

    private static char Member(string glob, ref int p)
    {
        if (glob[p] == '\\' && p + 1 < glob.Length) p++;
        return glob[p++];
    }

    private static void AddWithoutSlash(List<(char, char)> members, char low, char high)
    {
        if (low <= '/' && '/' <= high)
        {
            if (low < '/') members.Add((low, '.'));
            if (high > '/') members.Add(('0', high));
        }
        else members.Add((low, high));
    }
}

/// <summary>
/// Case equivalence for ignore patterns: two characters are the same if their invariant lower cases are. That puts
/// <c>k</c>, <c>K</c> and the Kelvin sign together, and leaves <c>s</c> and the long s, and <c>&#x3C3;</c> and
/// <c>&#x3C2;</c>, apart, as <c>RegexOptions.IgnoreCase</c> does.
/// </summary>
internal static class Fold
{
    public static char Of(char c) => c < 128 ? (c is >= 'A' and <= 'Z' ? (char)(c + 32) : c) : char.ToLowerInvariant(c);

    public static bool Equal(char a, char b) => a == b || Of(a) == Of(b);

    private static readonly Lazy<Dictionary<char, char[]>> ByFold = new(() =>
    {
        var groups = new Dictionary<char, List<char>>();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c)) continue;

            var f = Of(c);
            if (!groups.TryGetValue(f, out var list)) groups[f] = list = [];
            list.Add(c);
        }

        return groups.Where(g => g.Value.Count > 1).ToDictionary(g => g.Key, g => g.Value.ToArray());
    });

    /// <summary>Every character whose fold is <paramref name="folded"/>, itself included.</summary>
    public static char[] Variants(char folded) =>
        ByFold.Value.TryGetValue(folded, out var all) ? all : [folded];
}
