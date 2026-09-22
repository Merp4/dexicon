namespace Dexicon.Core.Indexing;

/// <summary>
/// Files under the workspace root that no source covers, in the one place they are likely
/// to be an oversight rather than a choice.
///
/// A corpus indexes the roots it was given and reports on the files inside them. A file
/// outside every root is not skipped, not failed and not counted: it is absent, and
/// absence has no row. Searching for it returns other documents, which reads exactly like
/// a ranking result. A real library had a book sitting in <c>manuals/</c> while every source
/// was <c>manuals/&lt;topic&gt;/</c>; a keyword search on its exact title returned four other
/// books and nothing said why.
///
/// Reporting every uncovered file under the root would be noise: a corpus that indexes
/// <c>docs/</c> is not missing the repository's README. The signal this uses is that TWO
/// OR MORE of a corpus's sources share a parent directory. That means the parent's
/// children were enumerated deliberately, so a file left loose among them was passed over
/// rather than excluded. One source under a parent says nothing about the parent, and is
/// not reported.
///
/// The directory has no source, so it has no include or exclude globs to apply. What is
/// applied is what holds for any path: the always-exclude list, a <c>.gitignore</c> and a
/// <c>.git/info/exclude</c> in the directory itself — only that one, since this does not
/// descend — the size caps and binary sniffing. A reported file is therefore one that would
/// have been indexed had a source covered it.
/// </summary>
public static class SourceCoverage
{
    /// <summary>A source root as this check needs it: where it points and how big a file it takes.</summary>
    public readonly record struct SourceRoot(string? RootPath, long MaxFileBytes);

    /// <summary>
    /// One directory that leads to sources but is not itself indexed, and the files in it.
    /// <paramref name="DirectoryRelativePath"/> is relative to the workspace root, forward
    /// slashes, empty for the root itself.
    /// </summary>
    public sealed record Gap(string DirectoryRelativePath, IReadOnlyList<string> Files);

    /// <summary>
    /// Bounded by construction: at most one directory listing per distinct parent shared by
    /// two or more sources, and no recursion. A corpus with ten sources in one folder costs
    /// one listing.
    /// </summary>
    public static IReadOnlyList<Gap> Find(
        string workspaceRoot,
        IEnumerable<SourceRoot> sources,
        long? documentMaxBytes = null)
    {
        var comparer = CorpusIndexer.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        var normalised = sources
            .Select(s => (Path: Normalise(s.RootPath), s.MaxFileBytes))
            .Where(s => s.Path is not null)
            .Select(s => (Path: s.Path!, s.MaxFileBytes))
            .ToList();

        var covered = new HashSet<string>(normalised.Select(s => s.Path), comparer);

        var gaps = new List<Gap>();

        foreach (var group in normalised.GroupBy(s => ParentOf(s.Path), comparer))
        {
            // One source under a directory is not evidence about that directory. Two is the
            // user enumerating its children.
            if (group.Count() < 2) continue;

            var parent = group.Key;

            // A source higher up already indexes this, so anything in it is covered. The
            // parent itself being a source is the same case with no ancestor to walk.
            if (SelfOrAncestorCovered(parent, covered, comparer)) continue;

            // The same resolver the indexer and the sweep go through, not a second copy of
            // its rule. This walks a directory that no source names — it is derived from
            // the sources' shared parent — so it is the one place that could reach a path
            // nobody ever created a source on, and a string test would accept a parent
            // whose own segment is a link.
            //
            // A refusal is skipped rather than reported. This is an advisory that asks
            // "did you mean to leave these out", and a path the indexer will refuse out
            // loud is not a gap in the corpus's coverage.
            // IOException as well as the refusal: Resolve enumerates directories now, so a
            // mount going away mid-call raises DirectoryNotFoundException where the old
            // Directory.Exists returned false and this skipped. An advisory that fails the
            // whole endpoint on a transient condition is worse than one that says nothing
            // about a directory it could not read.
            string full;
            try { full = WorkspaceDiscovery.Resolve(workspaceRoot, parent); }
            catch (UnauthorizedAccessException) { continue; }
            catch (ArgumentException) { continue; }
            catch (IOException) { continue; }

            if (!Directory.Exists(full)) continue;

            // The most permissive cap among the sources that share this parent, so the
            // check does not hide a file one of them would have taken.
            var maxFileBytes = group.Max(s => s.MaxFileBytes);

            var walked = WorkspaceWalker.Walk(full, useGitignore: true, includeGlobs: null,
                excludeGlobs: null, maxFileBytes, documentMaxBytes, topLevelOnly: true);

            if (walked.Files.Count == 0) continue;

            gaps.Add(new Gap(parent, walked.Files
                .Select(f => f.RelativePath)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList()));
        }

        return gaps.OrderBy(g => g.DirectoryRelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A stored root path, as a relative POSIX path with no leading or trailing separator.
    /// Null for an upload source, which has no path, and for anything that normalises to
    /// nothing meaningful.
    /// </summary>
    private static string? Normalise(string? rootPath)
    {
        if (rootPath is null) return null;
        var p = rootPath.Replace('\\', '/').Trim().Trim('/');
        return p.Length == 0 ? string.Empty : p;
    }

    private static string ParentOf(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? string.Empty : relative[..slash];
    }

    private static bool SelfOrAncestorCovered(string relative, HashSet<string> covered, StringComparer comparer)
    {
        for (var at = relative; ; at = ParentOf(at))
        {
            if (covered.Contains(at)) return true;
            if (at.Length == 0) return false;
        }
    }
}
