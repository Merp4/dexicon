using Dexicon.Core.Catalog;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Which source owns a file when two of them cover it.
///
/// A source covers its whole tree, so adding one above an existing source makes every file
/// beneath reachable twice. File identity is (source, relative path), so the same file
/// under two sources is two rows, two chunkings and two sets of vectors: the corpus
/// silently doubles, every search returns the same passage twice, and the second copy is
/// paid for in embedding time. Nothing failed, and no count said which files were affected.
///
/// The inventory is therefore made distinct ACROSS sources before anything is indexed. A
/// file is owned by the most specific source that covers it, which is the one whose root is
/// deepest: that source was created for that content, and its filters and size cap are the
/// more deliberate statement about it. A source higher up keeps everything the deeper ones
/// do not claim, which is what makes "index the loose files in this folder" work without
/// anybody maintaining a list of exclusions that goes stale the moment a source is added.
///
/// Overlap is decidable from the root paths alone. Two sources intersect only if one root
/// is inside the other, since each covers exactly its own subtree, so this needs no walk
/// and no inventory held in memory.
/// </summary>
public static class SourceScope
{
    /// <summary>
    /// Relative paths under <paramref name="source"/> that a more specific source owns.
    /// Forward slashes, no leading or trailing separator.
    ///
    /// Ordering breaks the remaining tie: two sources on the SAME root shadow each other
    /// otherwise, and neither would index anything. The lower id wins, so the choice is
    /// stable across runs rather than dependent on the order rows came back.
    /// </summary>
    public static IReadOnlyList<string> ShadowedPrefixes(IEnumerable<Source> corpusSources, Source source)
    {
        var mine = Normalise(source.RootPath);
        if (mine is null) return [];

        var shadowed = new List<string>();

        foreach (var other in corpusSources)
        {
            if (ReferenceEquals(other, source) || other.Kind != SourceKind.Workspace) continue;

            var theirs = Normalise(other.RootPath);
            if (theirs is null) continue;

            // Strictly deeper: their root is inside mine.
            if (IsUnder(theirs, mine))
            {
                shadowed.Add(mine.Length == 0 ? theirs : theirs[(mine.Length + 1)..]);
                continue;
            }

            // Same root. One of us has to yield or the folder is indexed twice; one of us
            // has to keep it or it is indexed not at all.
            if (string.Equals(theirs, mine, PathComparison)
                && string.CompareOrdinal(other.Id, source.Id) < 0)
            {
                shadowed.Add(string.Empty);
            }
        }

        return shadowed;
    }

    /// <summary>
    /// Whether a candidate's path, relative to its source root, belongs to a more specific
    /// source instead. An empty prefix shadows everything, which is how a duplicate source
    /// on the same root yields to its twin.
    /// </summary>
    public static bool IsShadowed(string relativePath, IReadOnlyList<string> shadowedPrefixes)
    {
        foreach (var prefix in shadowedPrefixes)
        {
            if (prefix.Length == 0) return true;
            if (IsUnder(relativePath, prefix)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is inside <paramref name="root"/>, on a
    /// separator boundary. `books/orlyx` is not inside `books/orly`, however much of the
    /// string the two share; the same test the workspace boundary uses, and for the same
    /// reason.
    /// </summary>
    private static bool IsUnder(string candidate, string root) =>
        root.Length == 0
        || candidate.StartsWith(root + "/", PathComparison);

    private static string? Normalise(string? rootPath) =>
        rootPath?.Replace('\\', '/').Trim().Trim('/');

    /// <summary>Case-insensitive only where the filesystem is, as everywhere else here.</summary>
    private static StringComparison PathComparison => CorpusIndexer.PathComparison;
}
