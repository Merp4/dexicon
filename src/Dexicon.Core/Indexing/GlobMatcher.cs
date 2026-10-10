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
/// Cost. The positions of the path that the tokens so far can have reached are a set of bits (<see cref="PathMasks"/>),
/// one word for every 64 characters of the path. A literal or <c>?</c> keeps the positions whose character fits and
/// moves each on by one; <c>*</c> carries each position through the characters up to the next <c>/</c> with one
/// addition for each word; <c>**</c> fills to the end; <c>**/</c> adds each position after a later <c>/</c>; and a class
/// is asked about each position separately. A token takes at most one operation for each word, which is
/// <c>n / 64 + 1</c> for a path of <c>n</c> characters, and a class at most <c>n + 1</c> position tests. The search for
/// the longest literal run (below) reads each character of the path once. So a match takes at most
/// <c>(tokens + 1) x words + classes x (n + 1) + n</c> steps whatever the pattern, there is no match timeout, and
/// <c>steps</c> counts exactly these. A rule holds its tokens, 16 bytes each, and a class holds its ranges besides;
/// <see cref="Weight"/> counts both, and charges a class for the position tests.
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
    private readonly int[]? _fail;

    /// <summary>The number of tokens, which with the length of a path bounds the steps of a match.</summary>
    internal int TokenCount => _tokens.Length;

    /// <summary>
    /// What the pattern costs to hold and to run, which a walk adds up against a limit: one for each token, and for a
    /// class <see cref="ClassWeight"/> and one more for each member it holds (a single character, or a range; a range
    /// that includes <c>/</c> is two members, since no class matches it).
    /// </summary>
    internal int Weight { get; }

    /// <summary>What a class token weighs besides its ranges.</summary>
    internal const int ClassWeight = 16;

    private GlobMatcher(Token[] tokens, string prefix, int weight)
    {
        _tokens = tokens;
        _prefix = prefix;
        Weight = weight;

        (_required, _requiredFirst, _fail) = RequiredLiteral(tokens);
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
                        weight += ClassWeight + cls.Members.Count;
                        i = cls.Close;
                        break;
                    }
                default:
                    tokens.Add(new Token(Kind.Literal, c));
                    weight++;
                    break;
            }
        }

        matcher = new GlobMatcher([.. tokens], directoryPrefix.Length > 0 ? directoryPrefix + "/" : string.Empty, weight);
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
        return IsMatchBeneath(path, ref steps);
    }

    /// <param name="steps">Increased by the work of the match (see the class summary), so a test can bound it.</param>
    internal bool IsMatch(ReadOnlySpan<char> path, ref long steps) => Match(path, beneath: false, ref steps);

    /// <summary><see cref="IsMatchBeneath(ReadOnlySpan{char})"/>, counting steps.</summary>
    internal bool IsMatchBeneath(ReadOnlySpan<char> path, ref long steps) => Match(path, beneath: true, ref steps);

    private bool Match(ReadOnlySpan<char> path, bool beneath, ref long steps)
    {
        var masks = PathMasks.Rent();
        try { return Match(path, beneath, masks, ref steps); }
        finally { masks.Return(); }
    }

    /// <summary>
    /// A match for a caller that tests one path against many rules and so shares <paramref name="masks"/>, which must
    /// not have been used for another path since it was rented.
    /// </summary>
    internal bool Match(ReadOnlySpan<char> path, bool beneath, PathMasks masks, ref long steps)
    {
        var start = 0;
        if (_prefix.Length > 0)
        {
            if (path.Length < _prefix.Length) return false;

            // The part that is equal as written is found with a vector compare, and only the rest is folded.
            var same = path[.._prefix.Length].CommonPrefixLength(_prefix);
            for (; same < _prefix.Length; same++)
                if (!Fold.Equal(path[same], _prefix[same])) return false;

            start = _prefix.Length;
        }

        if (_required is not null && !ContainsRequired(path[start..], ref steps)) return false;

        masks.Prepare(path);

        var words = masks.Words;
        ulong[]? rented = null;
        Span<ulong> buffer = 2 * words <= 256
            ? stackalloc ulong[2 * words]
            : (rented = ArrayPool<ulong>.Shared.Rent(2 * words)).AsSpan(0, 2 * words);
        buffer.Clear();

        try
        {
            return Run(path, start, beneath, masks, buffer[..words], buffer[words..], ref steps);
        }
        finally
        {
            if (rented is not null) ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    private bool Run(
        ReadOnlySpan<char> path, int start, bool beneath, PathMasks masks, Span<ulong> cur, Span<ulong> next, ref long steps)
    {
        var n = path.Length;
        var words = cur.Length;
        var slash = masks.Slash;
        var nonSlash = masks.NonSlash;

        // The positions reached so far are the set bits of `cur`, and `lo` to `hi` are the words that hold them.
        int lo = start >> 6, hi = lo;
        cur[lo] = 1UL << (start & 63);

        foreach (var token in _tokens)
        {
            steps += hi - lo + 1;
            int nextLo, nextHi;

            switch (token.Kind)
            {
                case Kind.Literal or Kind.One:
                    {
                        // Each position whose character fits moves forward by one.
                        var mask = token.Kind == Kind.One ? nonSlash : masks.CharMask(Fold.Of(token.Char));
                        if (mask.IsEmpty) return false;

                        ulong carry = 0;
                        for (var w = lo; w <= hi; w++)
                        {
                            var fits = cur[w] & mask[w];
                            next[w] = (fits << 1) | carry;
                            carry = fits >> 63;
                        }

                        var top = hi;
                        if (carry != 0) { next[hi + 1] = carry; top = hi + 1; }

                        (nextLo, nextHi) = Occupied(next, lo, top);
                        break;
                    }
                case Kind.Class:
                    {
                        // Only the positions reached are asked, one at a time.
                        var cls = token.Class!;
                        nextLo = int.MaxValue;
                        nextHi = -1;
                        for (var w = lo; w <= hi; w++)
                        {
                            var bits = cur[w] & nonSlash[w];
                            steps += System.Numerics.BitOperations.PopCount(bits);
                            while (bits != 0)
                            {
                                var i = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                                bits &= bits - 1;
                                if (!cls.Matches(path[i])) continue;

                                var j = i + 1;
                                next[j >> 6] |= 1UL << (j & 63);
                                if ((j >> 6) < nextLo) nextLo = j >> 6;
                                if ((j >> 6) > nextHi) nextHi = j >> 6;
                            }
                        }

                        break;
                    }
                case Kind.Star:
                    {
                        // Each position extends to the next `/` or the end. A run of characters that are not `/` is a
                        // run of ones in `nonSlash`, and adding a position to it carries through the rest of the run,
                        // so the bits that changed are the positions from there to the run's end.
                        ulong carry = 0;
                        var w = lo;
                        for (; w < words; w++)
                        {
                            var inside = w <= hi;
                            if (!inside && carry == 0) break;

                            var held = inside ? cur[w] : 0UL;
                            var run = nonSlash[w];
                            var start1 = held & run;
                            var sum = run + start1;
                            var overflow = sum < run;
                            var total = sum + carry;
                            overflow |= total < sum;
                            carry = overflow ? 1UL : 0UL;
                            next[w] = held | (total ^ run) | start1;
                        }

                        steps += Math.Max(0, w - 1 - hi);
                        (nextLo, nextHi) = Occupied(next, lo, w - 1);
                        break;
                    }
                case Kind.Any:
                    {
                        // Every position from the first reached to the end.
                        var first = System.Numerics.BitOperations.TrailingZeroCount(cur[lo]);
                        var valid = masks.Valid;
                        next[lo] = valid[lo] & (ulong.MaxValue << first);
                        for (var w = lo + 1; w < words; w++) next[w] = valid[w];

                        steps += words - 1 - hi;
                        nextLo = lo;
                        nextHi = words - 1;
                        break;
                    }
                default:
                    {
                        // `**/`: where it is, or just after any later slash.
                        var first = System.Numerics.BitOperations.TrailingZeroCount(cur[lo]);
                        var after = masks.AfterSlash;
                        for (var w = lo; w < words; w++)
                        {
                            var held = w <= hi ? cur[w] : 0UL;
                            var later = w > lo ? ulong.MaxValue : first == 63 ? 0UL : ulong.MaxValue << (first + 1);
                            next[w] = held | (after[w] & later);
                        }

                        steps += words - 1 - hi;
                        (nextLo, nextHi) = Occupied(next, lo, words - 1);
                        break;
                    }
            }

            if (nextHi < 0) return false;

            cur.Slice(lo, hi - lo + 1).Clear();
            var spare = cur;
            cur = next;
            next = spare;
            lo = nextLo;
            hi = nextHi;
        }

        // The tokens must end at a slash, or at the end of the path unless a directory is asked for.
        for (var w = lo; w <= hi; w++)
            if ((cur[w] & slash[w]) != 0) return true;

        return !beneath && (n >> 6) >= lo && (n >> 6) <= hi && ((cur[n >> 6] >> (n & 63)) & 1) != 0;
    }

    /// <summary>The first and last words of <paramref name="set"/> between two words that are not zero, or -1 for both.</summary>
    private static (int Lo, int Hi) Occupied(ReadOnlySpan<ulong> set, int from, int to)
    {
        while (from <= to && set[from] == 0) from++;
        if (from > to) return (-1, -1);

        while (set[to] == 0) to--;
        return (from, to);
    }

    // ---- the required literal ----------------------------------------------------------

    // The longest run of literal characters is searched for in the path before a match, with the Knuth-Morris-Pratt
    // search on folded characters, which reads each character of the path once whatever the run. Its failure table is
    // kept with the run.
    private static (char[]?, SearchValues<char>?, int[]?) RequiredLiteral(Token[] tokens)
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

        if (bestLength == 0) return (null, null, null);

        var folded = new char[bestLength];
        for (var k = 0; k < bestLength; k++) folded[k] = Fold.Of(tokens[bestStart + k].Char);

        var fail = new int[bestLength];
        for (int i = 1, matched = 0; i < bestLength; i++)
        {
            while (matched > 0 && folded[i] != folded[matched]) matched = fail[matched - 1];
            if (folded[i] == folded[matched]) matched++;
            fail[i] = matched;
        }

        return (folded, Fold.TryVariants(folded[0], out var variants) ? SearchValues.Create(variants) : null, fail);
    }

    private bool ContainsRequired(ReadOnlySpan<char> text, ref long steps)
    {
        ReadOnlySpan<char> required = _required!;
        ReadOnlySpan<int> fail = _fail!;
        var matched = 0;
        var read = 0;
        var i = 0;

        while (i < text.Length)
        {
            if (matched == 0)
            {
                // Nothing is matched, so the search can jump to the next character that could begin the run.
                var found = _requiredFirst is not null ? text[i..].IndexOfAny(_requiredFirst) : text[i..].IndexOf(required[0]);
                if (found < 0) break;

                i += found;
            }

            read++;
            var c = text[i++];
            c = c < 128 ? ((uint)(c - 'A') <= 'Z' - 'A' ? (char)(c | 0x20) : c) : char.ToLowerInvariant(c);
            while (matched > 0 && c != required[matched]) matched = fail[matched - 1];
            if (c != required[matched]) continue;

            matched++;
            if (matched == required.Length)
            {
                steps += read;
                return true;
            }
        }

        steps += read;
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
            _members = Merge(members);
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

        /// <summary>
        /// The members sorted by their first character, with ranges that overlap or touch joined, so that
        /// <see cref="In"/> finds a character by binary search. A class of 495 members is then about nine comparisons
        /// for each case variant of a non-ASCII character, where a scan of the members was 495.
        /// </summary>
        private static (char Low, char High)[] Merge(IReadOnlyList<(char Low, char High)> members)
        {
            var merged = new List<(char Low, char High)>(members.Count);
            foreach (var (low, high) in members.OrderBy(m => m.Low).ThenBy(m => m.High))
            {
                if (merged.Count > 0 && low <= merged[^1].High + 1)
                    merged[^1] = (merged[^1].Low, (char)Math.Max(merged[^1].High, high));
                else
                    merged.Add((low, high));
            }

            return [.. merged];
        }

        private bool In(char c)
        {
            var from = 0;
            var to = _members.Length - 1;
            while (from <= to)
            {
                var middle = (from + to) >> 1;
                var (low, high) = _members[middle];
                if (c < low) to = middle - 1;
                else if (c > high) from = middle + 1;
                else return true;
            }

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
    /// character for the reversed range, nothing for the empty class, and the named characters for the POSIX class,
    /// or nothing for a name it does not know. A POSIX class, with any name between the colons, is refused and not
    /// read as the members <c>[:alph</c> because that would match nothing git ignores.
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
            if (glob[p] == '[' && IsPosixClassAt(glob, p))
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

    // `[:` followed by anything and then `:]`, with the first `]` after the `[:` being that one. Git reads the name between
    // the colons as a POSIX class (`[:alpha:]`) and abandons the whole match for a name it does not know (`[:a1:]`,
    // `[::]`), so each of them is refused here, as the shape with a known name is.
    private static bool IsPosixClassAt(string glob, int p)
    {
        if (p + 1 >= glob.Length || glob[p + 1] != ':') return false;

        var end = glob.IndexOf(']', p + 2);
        return end > p + 2 && glob[end - 1] == ':';
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
