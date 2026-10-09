using System.Buffers;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Matches one ignore pattern against a path. The pattern is read once into tokens (a literal character, <c>?</c>, a
/// bracket class, <c>*</c>, <c>**</c>, and <c>**/</c>) and a match runs them over the path.
///
/// Meaning. A pattern is anchored at the directory it was written in, and matches
/// <code>
/// prefix/ [any directories/] tokens [/ anything]
/// </code>
/// where the optional directories are present for a pattern with no slash in it, which matches at any depth. A token
/// is a literal, one character that is not <c>/</c>, a class (which never matches <c>/</c>), a run of characters that
/// are not <c>/</c> (<c>*</c>), a run of any characters (<c>**</c>), or any directories ending in <c>/</c>
/// (<c>**/</c>). The tokens must end at the end of the path or at a <c>/</c>, so a pattern that matches a directory
/// matches everything beneath it. The test project keeps the regular expression that spells this out
/// (<c>GlobOracle</c>) and compares the two over random patterns and paths.
///
/// Cost. The set of positions in the path that the tokens so far can have reached is kept as a window over a byte
/// array. A literal, <c>?</c> or class moves each position forward by one if the character fits; <c>*</c> extends
/// each position to the next <c>/</c>; <c>**</c> extends to the end; <c>**/</c> adds each position after a later
/// <c>/</c>. A token looks at each position of the path at most twice, so a match takes at most
/// 2 x (tokens + 1) x (length + 2) steps whatever the pattern, and there is no match timeout. A rule holds its tokens,
/// 16 bytes each, and a class holds its ranges besides, which is what <see cref="Weight"/> counts. Most rules are
/// rejected before a match runs, by a search for the longest run of literal characters the pattern needs.
///
/// Case. Two characters are the same when their invariant lower cases are (<see cref="Fold"/>), which is the case
/// equivalence of <c>RegexOptions.IgnoreCase</c>. Characters are UTF-16 units, so <c>?</c> matches one unit and not a
/// whole emoji.
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

    /// <summary>
    /// What the pattern costs to hold and to run, which a walk adds up against a limit. A token counts one, and a class
    /// one more for each range it holds. A pattern with a wildcard (a <c>*</c>, <c>**</c> or <c>**/</c> other than the
    /// any-depth prefix) counts that in full: a wildcard widens the set of positions to the length of the path and every
    /// token after it then looks at all of them. A pattern without one counts a <see cref="PlainDivisor"/>th of it
    /// (at least one), because its tokens look at a few positions. Measured (IgnoreRuleCostTests), the worst wildcard
    /// pattern costs about twenty times as much per token as the worst pattern without one.
    /// </summary>
    internal int Weight { get; }

    /// <summary>How many tokens of a pattern without a wildcard weigh as one.</summary>
    internal const int PlainDivisor = 20;

    private GlobMatcher(Token[] tokens, string prefix, int weight)
    {
        _tokens = tokens;
        _prefix = prefix;
        Weight = weight;

        (_required, _requiredFirst) = RequiredLiteral(tokens);
    }

    /// <exception cref="FormatException">A bracket class that cannot be read; see <see cref="ReadClass"/>.</exception>
    internal static GlobMatcher Compile(string glob, string directoryPrefix) =>
        TryCompile(glob, directoryPrefix, out var matcher, out var problem)
            ? matcher!
            : throw new FormatException(problem);

    /// <summary>
    /// As <see cref="Compile"/>, with the reason in <paramref name="problem"/> and no exception when a class cannot be
    /// read, for the path a bad line takes thousands of times.
    /// </summary>
    internal static bool TryCompile(string glob, string directoryPrefix, out GlobMatcher? matcher, out string? problem)
    {
        matcher = null;
        problem = null;

        var anchored = glob.StartsWith('/');
        if (anchored) glob = glob[1..];

        // A pattern with no interior slash matches at any depth: `*.dll`, `node_modules`.
        var matchAtAnyDepth = !anchored && !glob.TrimEnd('/').Contains('/', StringComparison.Ordinal);

        var tokens = new List<Token>(glob.Length + 1);
        var weight = 0;
        var wildcard = false;
        if (matchAtAnyDepth) { tokens.Add(new Token(Kind.SlashGlob)); weight++; }

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i = EndOfDoubleStar(glob, i);
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; tokens.Add(new Token(Kind.SlashGlob)); }
                        else tokens.Add(new Token(Kind.Any));
                    }
                    else tokens.Add(new Token(Kind.Star));

                    wildcard = true;
                    weight++;
                    break;
                case '?':
                    tokens.Add(new Token(Kind.One));
                    weight++;
                    break;
                case '[':
                    {
                        if (!TryReadClass(glob, i, out var cls, out problem)) return false;
                        if (cls is null) { tokens.Add(new Token(Kind.Literal, '[')); weight++; break; }

                        tokens.Add(new Token(Kind.Class, Class: new CharClass(cls.Members, cls.Negated)));
                        weight += 1 + cls.Members.Count;
                        i = cls.Close;
                        break;
                    }
                default:
                    tokens.Add(new Token(Kind.Literal, c));
                    weight++;
                    break;
            }
        }

        matcher = new GlobMatcher([.. tokens], directoryPrefix.Length > 0 ? directoryPrefix + "/" : string.Empty,
            wildcard ? weight : (weight + PlainDivisor - 1) / PlainDivisor);
        return true;
    }

    /// <summary>
    /// The index of the last star of the <c>**</c> that begins at <paramref name="first"/>. Three or more stars that are
    /// a whole path segment (<c>***/foo</c>, <c>a/****</c>) are one <c>**</c>, as in git; anywhere else the first two
    /// stars are the <c>**</c> and the rest are read on their own.
    /// </summary>
    private static int EndOfDoubleStar(string glob, int first)
    {
        var last = first + 1;
        while (last + 1 < glob.Length && glob[last + 1] == '*') last++;

        var wholeSegment = (first == 0 || glob[first - 1] == '/') && (last + 1 == glob.Length || glob[last + 1] == '/');
        return wholeSegment ? last : first + 1;
    }

    internal bool IsMatch(ReadOnlySpan<char> path)
    {
        long steps = 0;
        return IsMatch(path, ref steps);
    }

    /// <summary>
    /// Whether the pattern matches a directory that <paramref name="path"/> is inside: some match of the tokens ends at
    /// a <c>/</c> that is not the end. What a directory-only pattern (<c>bin/</c>) asks of a file.
    /// </summary>
    internal bool IsMatchBeneath(ReadOnlySpan<char> path)
    {
        long steps = 0;
        return Match(path, beneath: true, ref steps);
    }

    /// <param name="steps">Incremented by the positions looked at, so a test can bound the work of a match.</param>
    internal bool IsMatch(ReadOnlySpan<char> path, ref long steps) => Match(path, beneath: false, ref steps);

    /// <summary><see cref="IsMatchBeneath(ReadOnlySpan{char})"/>, counting steps.</summary>
    internal bool IsMatchBeneath(ReadOnlySpan<char> path, ref long steps) => Match(path, beneath: true, ref steps);

    private bool Match(ReadOnlySpan<char> path, bool beneath, ref long steps)
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
            return Run(path, start, beneath, buffer[..size], buffer[size..], ref steps);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool Run(ReadOnlySpan<char> path, int start, bool beneath, Span<byte> cur, Span<byte> next, ref long steps)
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

        // The tokens must end at a slash, or at the end of the path unless a directory is asked for.
        for (var i = lo; i <= hi; i++)
            if (cur[i] != 0 && (i < n ? path[i] == '/' : !beneath)) return true;

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

        return (folded, Fold.TryVariants(folded[0], out var variants) ? SearchValues.Create(variants) : null);
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
    internal sealed class CharClass
    {
        private readonly (char Low, char High)[] _members;
        private readonly bool _negated;

        // Which of the 128 ASCII characters the class matches, worked out once, since a match asks per character.
        private readonly ulong _asciiLow;
        private readonly ulong _asciiHigh;

        public CharClass(IReadOnlyList<(char Low, char High)> members, bool negated)
        {
            _members = [.. members];
            _negated = negated;

            for (var c = 0; c < 128; c++)
            {
                if (!Compute((char)c)) continue;

                if (c < 64) _asciiLow |= 1UL << c;
                else _asciiHigh |= 1UL << (c - 64);
            }
        }

        public bool Matches(char c)
        {
            if (c >= 128) return Compute(c);

            return (c < 64 ? (_asciiLow >> c) & 1 : (_asciiHigh >> (c - 64)) & 1) != 0;
        }

        private bool Compute(char c)
        {
            // A class holds a character if it holds any character that folds to the same one.
            var found = false;
            if (Fold.TryVariants(Fold.Of(c), out var same))
            {
                foreach (var variant in same)
                {
                    if (!In(variant)) continue;
                    found = true;
                    break;
                }
            }
            else found = In(c);

            return found != _negated;
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

    /// <exception cref="FormatException">The class cannot be read; see <see cref="TryReadClass"/>.</exception>
    internal static ClassSpan? ReadClass(string glob, int open) =>
        TryReadClass(glob, open, out var span, out var problem) ? span : throw new FormatException(problem);

    /// <summary>
    /// Reads the bracket class that opens at <paramref name="open"/>. Returns false with the reason in
    /// <paramref name="problem"/> when it cannot be read, and true with a null <paramref name="span"/> when nothing
    /// closes it, in which case the <c>[</c> is a literal character.
    ///
    /// The members are read as git reads them (measured with git 2.31.1 on Windows and 2.54.0 on Linux): a
    /// <c>]</c> straight after <c>[</c>, <c>[!</c> or <c>[^</c> is a member; <c>[</c> is a member; a <c>-</c> after a
    /// finished range or at the end is a member; a backslash takes the next character literally, so <c>\]</c> does
    /// not close the class and <c>\d</c> is a <c>d</c>; and no class matches <c>/</c>, positive or negated.
    ///
    /// Four shapes git reads in a way that is refused or kept here. <c>[!]x</c>, with no <c>]</c> after the one
    /// following the <c>!</c>, is the one-member class of <c>!</c>. <c>[]x</c> and <c>[^]x</c> are an empty class,
    /// a range whose end is before its start such as <c>[z-a]</c> is a reversed range, and a POSIX class such as
    /// <c>[[:alpha:]]</c> is not supported: each cannot be read, and the line is unusable. Git matches only the start
    /// character for the reversed range, nothing for the empty class, and the named characters for the POSIX class.
    /// A POSIX class is refused and not read as the members <c>[:alph</c> because that would match nothing git
    /// ignores.
    /// </summary>
    internal static bool TryReadClass(string glob, int open, out ClassSpan? span, out string? problem)
    {
        span = null;
        problem = null;

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
            if (start >= glob.Length || glob[start] != ']') return true;

            if (negated && glob[open + 1] == '!')
            {
                span = new ClassSpan(start, Negated: false, [('!', '!')]);
                return true;
            }

            problem = "empty character class";
            return false;
        }

        var members = new List<(char, char)>();
        for (var p = start; p < close;)
        {
            if (glob[p] == '[' && IsPosixClassAt(glob, p, close))
            {
                problem = "a POSIX character class such as [:alpha:] is not supported";
                return false;
            }

            var low = Member(glob, ref p);
            var high = low;
            if (p + 1 < close && glob[p] == '-')
            {
                p++;
                high = Member(glob, ref p);
                if (high < low)
                {
                    problem = "reversed character range";
                    return false;
                }
            }

            AddWithoutSlash(members, low, high);
        }

        span = new ClassSpan(close, negated, members);
        return true;
    }

    // `[:name:` running to the end of the members, where the class ends at the `]` of `:]`.
    private static bool IsPosixClassAt(string glob, int p, int close)
    {
        if (p + 1 >= close || glob[p + 1] != ':') return false;

        var q = p + 2;
        while (q < close && char.IsAsciiLetter(glob[q])) q++;
        return q == close - 1 && q > p + 2 && glob[q] == ':';
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

    /// <summary>Whether two strings of the same length are equal by <see cref="Equal"/>, character by character.</summary>
    public static bool Equal(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length) return false;

        for (var i = 0; i < a.Length; i++)
            if (!Equal(a[i], b[i])) return false;

        return true;
    }

    /// <summary>Whether <paramref name="text"/> starts with <paramref name="prefix"/> by <see cref="Equal"/>.</summary>
    public static bool StartsWith(ReadOnlySpan<char> text, ReadOnlySpan<char> prefix) =>
        text.Length >= prefix.Length && Equal(text[..prefix.Length], prefix);

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

    /// <summary>
    /// Every character whose fold is <paramref name="folded"/>, when there is more than one. A character with no
    /// case variants has none to list, and nothing is allocated for it.
    /// </summary>
    public static bool TryVariants(char folded, out char[] variants)
    {
        if (ByFold.Value.TryGetValue(folded, out var all))
        {
            variants = all;
            return true;
        }

        variants = [];
        return false;
    }
}
