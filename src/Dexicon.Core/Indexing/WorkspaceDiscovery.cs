using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Which files a source owns: the walk, its filters, and the shadowing between sources.
///
/// Shared by the indexer and the sweep rather than written twice, because the answer has
/// to be the same one. A sweep that disagreed with the pass that follows it would report
/// an inventory the index never fills, or miss files the index then adds, and either reads
/// as a wrong count rather than as two walks differing.
/// </summary>
public static class WorkspaceDiscovery
{
    /// <param name="Owned">What this source is responsible for, shadowing applied.</param>
    /// <param name="Skipped">Excluded by the walk itself, with the reason it gives.</param>
    /// <param name="ShadowedCount">
    /// How many of the walk's files a more specific source owns. Not a skip: those files
    /// are indexed, just not here.
    /// </param>
    public sealed record Result(
        IReadOnlyList<WorkspaceWalker.Candidate> Owned,
        IReadOnlyList<WorkspaceWalker.Skipped> Skipped,
        int ShadowedCount);

    public static Result Walk(Corpus corpus, Source source, string root, IndexingOptions indexing)
    {
        // Through SourceFilters, not off the source: a null field there means the source
        // has no opinion and the corpus default applies. Reading the columns directly
        // indexed a source by its own emptiness.
        var filters = SourceFilters.Resolve(corpus, source, indexing);

        var walk = WorkspaceWalker.Walk(root, filters.UseGitignore,
            filters.IncludeGlobs, filters.ExcludeGlobs, filters.MaxFileBytes,
            indexing.DocumentMaxBytes);

        // The inventory is made distinct ACROSS sources here. A source covers its whole
        // tree, so one added above another makes every file beneath reachable twice, and
        // identity being (source, relative path) would index each of them twice over. The
        // most specific source owns a file; this one keeps what the deeper ones do not
        // claim.
        var shadowed = SourceScope.ShadowedPrefixes(corpus.Sources, source);
        var owned = shadowed.Count == 0
            ? walk.Files
            : walk.Files.Where(f => !SourceScope.IsShadowed(f.RelativePath, shadowed)).ToList();

        return new Result(owned, walk.SkippedFiles, walk.Files.Count - owned.Count);
    }

    /// <summary>
    /// A source's root on disk, refusing anything that resolves outside the workspace.
    ///
    /// The containment rule itself stays in <see cref="CorpusIndexer.IsInside"/>, which is
    /// where its reasoning and its regression tests live. This exists so the sweep reaches
    /// that rule too: it walks the same trees the indexer does and must be held to the same
    /// boundary, and a second implementation of a security check is how the two come to
    /// disagree.
    /// </summary>
    public static string Resolve(string workspaceRoot, string? relative)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var combined = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));

        if (!CorpusIndexer.IsInside(combined, root))
            throw new UnauthorizedAccessException(
                $"Workspace path '{relative}' resolves outside {workspaceRoot} and was refused.");

        return combined;
    }

    /// <summary>
    /// The same directory as <see cref="Resolve"/>, spelled the way the filesystem spells
    /// it, or null when there is no such directory.
    ///
    /// Containment is still <see cref="Resolve"/>'s decision and is made first, so there
    /// is one rule and one set of regression tests for it. What this adds is that the
    /// path is then re-derived from the filesystem rather than from the caller's text:
    /// each segment is matched against the entries <see cref="Directory"/> actually
    /// reports, and the string returned is the one enumeration produced.
    ///
    /// Two things follow, and both matter to a caller about to hand the path to another
    /// program. The directory exists, so "the mount is away" is a distinct answer from
    /// "this resolves outside the workspace" — the first is an operational condition and
    /// the second is a refusal, and a caller that cannot tell them apart reports one as
    /// the other. And no part of the returned value came from the request, which is what
    /// makes it safe to pass to <see cref="System.Diagnostics.ProcessStartInfo"/>: see
    /// <see cref="GitHistory.RepositoryIn"/>.
    ///
    /// The segments are compared, never used as a search pattern: a pattern would give
    /// `*` and `?` in a caller's text their glob meaning.
    /// </summary>
    public static string? ResolveExisting(string workspaceRoot, string? relative)
    {
        var target = Resolve(workspaceRoot, relative);

        var current = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(current)) return null;

        // Containment already holds, so this carries no `..` to walk back through.
        var within = Path.GetRelativePath(current, target);
        if (within == ".") return current;

        foreach (var segment in within.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Directory.EnumerateDirectories(current).FirstOrDefault(
                d => string.Equals(Path.GetFileName(d), segment, CorpusIndexer.PathComparison));

            if (match is null) return null;

            // A link out of the root is the same escape as `..`, by a mechanism the
            // string never shows: `Path.GetFullPath` canonicalises separators and dots
            // and resolves no links, so `workspace/link` passes the boundary check and
            // then IS `/outside`. The walk applies this rule to every directory it
            // descends into (GitignoreFilter.EnumerateFilesSafely); a source root
            // reached this far without it.
            //
            // Refused rather than skipped, which is where this differs from the walk.
            // The walk is enumerating and a link it will not follow is simply not part
            // of the tree; here the operator named this one path, and answering "there
            // is nothing there" about a directory that plainly exists sends them
            // looking at the mount instead of at the link.
            var info = new DirectoryInfo(match);
            if (info.LinkTarget is null)
            {
                current = match;
                continue;
            }

            var linked = Path.GetFullPath(info.ResolveLinkTarget(true)?.FullName ?? match);

            if (!CorpusIndexer.IsInside(linked, Path.GetFullPath(workspaceRoot)))
                throw new UnauthorizedAccessException(
                    $"Workspace path '{relative}' passes through '{segment}', which links to "
                    + $"'{linked}', outside {workspaceRoot}. It was refused.");

            // Followed, not just checked. A link that stays inside is allowed, and a
            // path that kept its spelling would then be compared against a physical one
            // and lose: `rev-parse --show-toplevel` reports the physical working
            // directory — measured on Windows as well as POSIX — so `root/inward` was
            // answered with `root/real`, the two did not match, and the source was
            // reported as not a git repository and never indexed.
            //
            // Resolving here rather than at that comparison also covers a link part way
            // along the path, which resolving only the last component would not.
            current = linked;
        }

        return current;
    }
}
