namespace Dexicon.Core.Indexing;

/// <summary>
/// The positions of a path that each kind of character occupies, as bit sets, shared by every rule a walk tests the path
/// against. Bit <c>i</c> is the character at index <c>i</c>, and a path of <c>n</c> characters has <c>n + 1</c> bits, the
/// last standing for the end. A match (<see cref="GlobMatcher"/>) moves a set of positions through the pattern with a
/// few word operations per token, so its cost follows <see cref="Words"/> and not the number of positions.
///
/// One instance serves one path at a time: it is built on the first rule that needs it, from the span that rule
/// is given, and every rule tested before <see cref="Return"/> must be given the same path. Instances are kept per
/// thread and reused.
/// </summary>
internal sealed class PathMasks
{
    [ThreadStatic]
    private static PathMasks? s_spare;

    private bool _built;
    private int _words;

    private ulong[] _slash = [];
    private ulong[] _nonSlash = [];
    private ulong[] _afterSlash = [];
    private ulong[] _valid = [];

    // One run of Words entries for each distinct folded character of the path, and where each starts, plus one.
    private ulong[] _slab = [];
    private int _slabUsed;
    private readonly int[] _ascii = new int[128];
    private readonly Dictionary<char, int> _other = [];

    internal static PathMasks Rent()
    {
        var masks = s_spare ?? new PathMasks();
        s_spare = null;
        masks._built = false;
        return masks;
    }

    internal void Return() => s_spare = this;

    /// <summary>The number of 64-bit words in a set of positions of the path.</summary>
    internal int Words => _words;

    /// <summary>The indexes of the <c>/</c> characters.</summary>
    internal ReadOnlySpan<ulong> Slash => _slash.AsSpan(0, _words);

    /// <summary>The indexes of the characters that are not <c>/</c>. The end is not among them.</summary>
    internal ReadOnlySpan<ulong> NonSlash => _nonSlash.AsSpan(0, _words);

    /// <summary>The positions just after a <c>/</c>.</summary>
    internal ReadOnlySpan<ulong> AfterSlash => _afterSlash.AsSpan(0, _words);

    /// <summary>Every position, the end included.</summary>
    internal ReadOnlySpan<ulong> Valid => _valid.AsSpan(0, _words);

    /// <summary>The indexes of the characters whose fold is <paramref name="folded"/>, or empty when the path has none.</summary>
    internal ReadOnlySpan<ulong> CharMask(char folded)
    {
        var at = folded < 128 ? _ascii[folded] : _other.GetValueOrDefault(folded);
        return at == 0 ? default : _slab.AsSpan(at - 1, _words);
    }

    /// <summary>Builds the sets for <paramref name="path"/> unless they were built for it already.</summary>
    internal void Prepare(ReadOnlySpan<char> path)
    {
        if (_built) return;

        var n = path.Length;
        _words = (n >> 6) + 1;
        Array.Clear(_ascii);
        _other.Clear();
        _slabUsed = 0;
        Grow(ref _slash);
        Grow(ref _nonSlash);
        Grow(ref _afterSlash);
        Grow(ref _valid);
        _slash.AsSpan(0, _words).Clear();
        _nonSlash.AsSpan(0, _words).Clear();

        for (var i = 0; i < n; i++)
        {
            var c = path[i];
            var bit = 1UL << (i & 63);
            if (c == '/') _slash[i >> 6] |= bit;
            else _nonSlash[i >> 6] |= bit;

            var folded = Fold.Of(c);
            var at = folded < 128 ? _ascii[folded] : _other.GetValueOrDefault(folded);
            if (at == 0)
            {
                at = Allocate() + 1;
                if (folded < 128) _ascii[folded] = at;
                else _other[folded] = at;
            }

            _slab[at - 1 + (i >> 6)] |= bit;
        }

        ulong carry = 0;
        for (var w = 0; w < _words; w++)
        {
            _afterSlash[w] = (_slash[w] << 1) | carry;
            carry = _slash[w] >> 63;
            _valid[w] = ulong.MaxValue;
        }

        // The last word holds bits 0 to n of the end; the rest of it is past the path.
        var last = n & 63;
        _valid[_words - 1] = last == 63 ? ulong.MaxValue : (1UL << (last + 1)) - 1;
        _built = true;
    }

    private int Allocate()
    {
        var at = _slabUsed;
        if (at + _words > _slab.Length) Array.Resize(ref _slab, Math.Max(_slab.Length * 2, at + _words + 256));
        _slab.AsSpan(at, _words).Clear();
        _slabUsed += _words;
        return at;
    }

    private void Grow(ref ulong[] array)
    {
        if (array.Length < _words) array = new ulong[Math.Max(_words, 8)];
    }
}
