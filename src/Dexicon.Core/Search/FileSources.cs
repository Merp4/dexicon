using Dexicon.Core.Documents;

namespace Dexicon.Core.Search;

/// <summary>
/// The chunks of one file path, narrowed to the one source the caller is shown.
/// </summary>
/// <param name="Chunks">The chunks to read from, in chunk order, all from <paramref name="SourceId"/>.</param>
/// <param name="SourceId">The source the chunks belong to. Null for an empty path or for points written before sources were recorded.</param>
/// <param name="SourceCount">How many sources of the corpus hold a file at the path, indexed or not: 0 when no chunk came back.</param>
public sealed record FileSource(IReadOnlyList<SearchHit> Chunks, string? SourceId, int SourceCount)
{
    /// <summary>More than one source holds the path, so the other sources' files are not shown.</summary>
    public bool Ambiguous => SourceCount > 1;

    /// <summary>The sentence that tells a reader the path is shared, or null when it is not.</summary>
    public string? Warning => Ambiguous ? FileSources.WarningFor(SourceCount) : null;
}

/// <summary>
/// Resolves a file path to one source's chunks.
///
/// A file path is relative to its source root, so within a corpus it is not unique. A corpus
/// with sources AI/ and Philosophy/ that both hold "Installation Guide.pdf" returns the
/// chunks of both for that path, and ordering them by chunk index interleaves two different
/// books into one passage that reads as continuous. <c>get_context</c>, the file endpoint and
/// the file resource each look a file up by path alone, and each calls this to decide which
/// source they read and whether to say so.
/// </summary>
public static class FileSources
{
    /// <summary>
    /// <see cref="Choose"/>, counting the sources from the catalogue as well as the chunks.
    /// This is the entry point for a read by path: the chunks say which source to read, and
    /// the catalogue says how many sources hold the path. A source whose file there is empty
    /// has no chunks and would otherwise go uncounted.
    /// </summary>
    public static async Task<FileSource> ResolveAsync(
        IReadOnlyList<SearchHit> chunks, DocumentReader documents, string corpusId, string path,
        CancellationToken ct = default)
    {
        // Nothing to read, so nothing to warn about, and no reason to ask the catalogue.
        if (chunks.Count == 0) return Choose(chunks);

        return Choose(chunks, await documents.SourceCountAtAsync(corpusId, path, ct));
    }

    /// <summary>
    /// The source with the most chunks, ties broken by source id so that two calls on an
    /// unchanged index return the same book. A path held by one source comes back unchanged.
    /// </summary>
    /// <param name="chunks">The chunks of the path.</param>
    /// <param name="catalogued">
    /// Sources the catalogue records a file at the path for. The count is the larger of this
    /// and the sources the chunks come from, which keeps chunks with no recorded source (older
    /// points) from adding one.
    /// </param>
    public static FileSource Choose(IReadOnlyList<SearchHit> chunks, int catalogued = 0)
    {
        var bySource = chunks
            .GroupBy(c => c.SourceId ?? string.Empty)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (bySource.Count == 0) return new FileSource(chunks, null, 0);

        var chosen = bySource.Count > 1
            ? [.. bySource[0].OrderBy(c => c.ChunkIndex)]
            : chunks;

        return new FileSource(chosen, chosen[0].SourceId, Math.Max(bySource.Count, catalogued));
    }

    /// <summary>
    /// What a reader is told when a path is shared. <paramref name="sources"/> is the number of
    /// sources that hold it.
    /// </summary>
    public static string WarningFor(int sources) =>
        $"{sources} sources in this corpus contain a file at that path. " +
        "They are different files with the same name. This is one of them, the " +
        "largest; the others are not shown and not mixed in.";
}
