using System.Buffers;
using System.Text;

namespace Dexicon.Core.Search;

/// <summary>A sparse vector as Qdrant wants it: parallel index and value arrays.</summary>
public readonly record struct SparseVector(uint[] Indices, float[] Values)
{
    public static readonly SparseVector Empty = new([], []);
    public bool IsEmpty => Indices.Length == 0;
}

/// <summary>
/// Builds term-frequency sparse vectors in-process. No model, no corpus statistics:
/// the Qdrant sparse index is declared <c>modifier: idf</c>, so Qdrant supplies the
/// IDF component itself at query time. Confirmed in the M0 spike.
///
/// The identifier splitting is the part that earns its place on code. A query for
/// "token refresh" has to be able to reach <c>TokenService.RefreshAsync</c>, which
/// means <c>TokenService</c> must contribute the terms "token" and "service" as well
/// as itself.
/// </summary>
public static class SparseEncoder
{
    // Deliberately short. An aggressive stoplist hurts code search, where "for", "in"
    // and "is" are frequently part of an identifier that matters.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "at", "by", "is", "are",
        "was", "were", "be", "been", "it", "its", "this", "that", "with", "as", "from",
        "how", "what", "why", "we", "you", "i", "do", "does", "did",
    };

    private const int MinTermLength = 2;
    private const int MaxTermLength = 64;

    public static SparseVector Encode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SparseVector.Empty;

        var counts = new Dictionary<uint, float>(capacity: 64);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in Tokenize(text))
        {
            if (token.Length is < MinTermLength or > MaxTermLength) continue;
            if (StopWords.Contains(token)) continue;

            var hash = Hash(token);
            counts[hash] = counts.TryGetValue(hash, out var n) ? n + 1f : 1f;
            seen.Add(token);
        }

        if (counts.Count == 0) return SparseVector.Empty;

        var indices = new uint[counts.Count];
        var values = new float[counts.Count];
        var i = 0;
        foreach (var (k, v) in counts) { indices[i] = k; values[i] = v; i++; }
        return new SparseVector(indices, values);
    }

    /// <summary>
    /// Lowercased alphanumeric runs, plus the sub-words of any compound identifier.
    /// The whole identifier is kept as well as its parts — an exact match on
    /// <c>RefreshAsync</c> should outrank a document that merely says "refresh".
    /// </summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var run = new StringBuilder(32);

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) { run.Append(ch); continue; }
            if (run.Length > 0) { foreach (var t in Emit(run.ToString())) yield return t; run.Clear(); }
        }
        if (run.Length > 0) foreach (var t in Emit(run.ToString())) yield return t;
    }

    private static IEnumerable<string> Emit(string raw)
    {
        yield return raw.ToLowerInvariant();

        // camelCase / PascalCase / digit boundaries. snake_case and kebab-case are
        // already handled, because '_' and '-' are not letters or digits.
        if (!HasInnerBoundary(raw)) yield break;

        var part = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            var isBoundary = i > 0 && (
                (char.IsUpper(c) && !char.IsUpper(raw[i - 1])) ||
                (char.IsDigit(c) != char.IsDigit(raw[i - 1])) ||
                // HTTPServer -> HTTP, Server
                (char.IsUpper(c) && i + 1 < raw.Length && char.IsLower(raw[i + 1]) && char.IsUpper(raw[i - 1])));

            if (isBoundary && part.Length > 0) { yield return part.ToString().ToLowerInvariant(); part.Clear(); }
            part.Append(c);
        }
        if (part.Length > 0) yield return part.ToString().ToLowerInvariant();
    }

    private static bool HasInnerBoundary(string s)
    {
        for (var i = 1; i < s.Length; i++)
        {
            if (char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) return true;
            if (char.IsDigit(s[i]) != char.IsDigit(s[i - 1])) return true;
        }
        return false;
    }

    /// <summary>
    /// Stable 32-bit FNV-1a, masked to a positive value. Deliberately NOT
    /// <see cref="string.GetHashCode()"/>, which is randomised per process — an index
    /// built in one run would not be queryable from the next.
    /// </summary>
    internal static uint Hash(string term)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        var hash = offset;
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(term.Length));
        try
        {
            var n = Encoding.UTF8.GetBytes(term, bytes);
            for (var i = 0; i < n; i++) { hash ^= bytes[i]; hash *= prime; }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }

        return hash & 0x7FFFFFFF;
    }
}
