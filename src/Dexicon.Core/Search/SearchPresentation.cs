using System.Text;
using System.Text.RegularExpressions;

namespace Dexicon.Core.Search;

/// <summary>
/// What a result costs the agent reading it, and whether the same title fills the page.
///
/// Measured over eight questions against a 95-book library, five results each: a search
/// returned a mean of 40,797 characters, about 10,200 tokens. The chunk is the unit that
/// was embedded, and at 2,065 tokens it is the right size for retrieval and far too large
/// to hand back whole. An agent with a 200k window could afford twenty searches.
///
/// Two separate problems, and measurement said which is which:
///
///   truncating each hit to 1,500 chars   40,797 -> 7,500 chars   18% of the cost
///   dropping repeated text                40,797 -> 40,716       99.8%
///   one hit per book                      40,797 -> 38,629       95%
///
/// So length is the cost and duplication is not: dropping a duplicate only promotes
/// another chunk of the same size. Duplication is still worth fixing, because a library
/// holding each title as PDF and EPUB spent 2 of every 5 slots on a book it had already
/// returned, but it is a quality fix and is counted as one.
/// </summary>
public static class SearchPresentation
{
    /// <summary>
    /// A preview centred on what matched, rather than the head of the chunk.
    ///
    /// Head truncation was the obvious implementation and it loses the answer. Measured on
    /// the same corpus, with a 1,500-character window: for 12% of hits the FIRST matching
    /// term already sat past it, so the preview would have carried none of what the query
    /// matched, and only 25% of hits had all their matching terms inside it. Matches sit at
    /// 0.10 of the chunk at the median and 0.56 at the 90th percentile.
    ///
    /// Cut on line boundaries, because a chunk of prose cut mid-sentence reads as damage,
    /// and an elision is marked so a reader can tell a window from a whole chunk.
    /// </summary>
    /// <param name="text">The chunk as it was indexed.</param>
    /// <param name="query">The query, used only to find where to centre.</param>
    /// <param name="maxChars">The budget. Non-positive returns the text unchanged.</param>
    public static string Window(string text, string query, int maxChars)
    {
        if (maxChars <= 0 || string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;

        var lines = text.Split('\n');

        // Where each line starts, so a character offset can be mapped back to a line.
        var starts = new int[lines.Length];
        for (int i = 1, at = 0; i < lines.Length; i++)
        {
            at += lines[i - 1].Length + 1;
            starts[i] = at;
        }

        var centre = BestOffset(text, query);
        var line = LineAt(starts, centre);

        // Grow outwards from the line the match is on, taking whichever neighbour is
        // cheaper, so the window keeps the match roughly central without spending its
        // whole budget on one very long line.
        var lo = line;
        var hi = line;
        var used = lines[line].Length + 1;

        while (used < maxChars)
        {
            var canUp = lo > 0;
            var canDown = hi < lines.Length - 1;
            if (!canUp && !canDown) break;

            var upCost = canUp ? lines[lo - 1].Length + 1 : int.MaxValue;
            var downCost = canDown ? lines[hi + 1].Length + 1 : int.MaxValue;
            var takeUp = upCost <= downCost;
            var cost = takeUp ? upCost : downCost;

            if (used + cost > maxChars) break;
            used += cost;
            if (takeUp) lo--; else hi++;
        }

        var sb = new StringBuilder();
        if (lo > 0) sb.Append("…\n");
        for (var i = lo; i <= hi; i++)
        {
            sb.Append(lines[i]);
            if (i < hi) sb.Append('\n');
        }
        if (hi < lines.Length - 1) sb.Append("\n…");

        var window = sb.ToString();

        // One line longer than the whole budget is the case the loop above cannot serve.
        // Cut it, rather than return a "preview" that costs more than the chunk did.
        return window.Length <= maxChars + 4 ? window : HardCut(text, centre, maxChars);
    }

    /// <summary>
    /// Where in the text to centre. The densest run of query terms, so a passage using
    /// several of them beats an early incidental mention of one; the start of the text when
    /// nothing matches, which is the semantic-only case where there is no term to find.
    /// </summary>
    private static int BestOffset(string text, string query)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return 0;

        var hits = new List<int>();
        foreach (var t in terms)
        {
            var at = 0;
            while ((at = text.IndexOf(t, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                hits.Add(at);
                at += t.Length;
                if (hits.Count > 512) break;   // a common word in a long chunk
            }
        }

        if (hits.Count == 0) return 0;
        hits.Sort();

        // The window that contains the most matches, over a span the size of a preview.
        const int span = 1500;
        int best = hits[0], bestCount = 0, j = 0;
        for (var i = 0; i < hits.Count; i++)
        {
            while (j < hits.Count && hits[j] - hits[i] <= span) j++;
            if (j - i > bestCount)
            {
                bestCount = j - i;
                best = (hits[i] + hits[j - 1]) / 2;
            }
        }

        return best;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "is", "are", "you", "your", "it",
        "for", "on", "with", "when", "how", "what", "do", "does", "should", "not", "that",
        "this", "be", "by", "as", "at", "from", "can", "has", "have", "its", "into", "if",
        "than", "then", "there", "they", "we", "will", "would", "about", "between", "use",
    };

    private static List<string> Terms(string query) =>
        Regex.Matches(query, @"\w+")
            .Select(m => m.Value)
            .Where(w => w.Length > 3 && !Stop.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int LineAt(int[] starts, int offset)
    {
        var lo = 0;
        var hi = starts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>A chunk with no usable line breaks near the match: cut on characters.</summary>
    private static string HardCut(string text, int centre, int maxChars)
    {
        var start = Math.Clamp(centre - maxChars / 2, 0, Math.Max(0, text.Length - maxChars));
        var slice = text.Substring(start, Math.Min(maxChars, text.Length - start));

        return (start > 0 ? "…" : string.Empty) + slice
            + (start + slice.Length < text.Length ? "…" : string.Empty);
    }

    /// <summary>
    /// One hit per title, keeping the best-scoring.
    ///
    /// A library holding each book as PDF and EPUB returns the same title twice, and a
    /// measured 3.0 distinct books per 5 results. The two copies are not redundant on disk
    /// — they are how the two extractors get compared — but returning both to one question
    /// spends a slot on a book the reader already has.
    ///
    /// Hits are assumed already ordered by score, so the first of a title is its best.
    /// </summary>
    public static IReadOnlyList<SearchHit> DistinctByTitle(IEnumerable<SearchHit> hits, int limit)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<SearchHit>();

        foreach (var hit in hits)
        {
            if (!seen.Add(TitleKey(hit)))
            {
                continue;
            }

            kept.Add(hit);
            if (kept.Count == limit) break;
        }

        return kept;
    }

    /// <summary>
    /// What makes two hits the same document. The source root is part of it: a file path is
    /// relative to its source, so two sources holding "Installation Guide.pdf" are two
    /// different books, and collapsing them would hide one behind the other.
    /// </summary>
    internal static string TitleKey(SearchHit hit)
    {
        var name = hit.FilePath.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        var dir = slash < 0 ? string.Empty : name[..(slash + 1)];
        var file = slash < 0 ? name : name[(slash + 1)..];

        var dot = file.LastIndexOf('.');
        if (dot > 0) file = file[..dot];

        return $"{hit.SourceRoot ?? hit.SourceId ?? string.Empty} {dir}{file}";
    }
}
