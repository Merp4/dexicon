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
}
