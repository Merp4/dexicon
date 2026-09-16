using Dexicon.Core.Catalog;

namespace Dexicon.Core.Indexing;

/// <summary>
/// The bridge between the stored configuration and the chunker's own vocabulary.
///
/// Deliberately a projection rather than the chunker reading <see cref="ChunkSet"/>
/// directly: the chunker knows nothing about corpora, tenants or catalogues, and keeping
/// it that way is what makes it testable with four lines of setup.
/// </summary>
public static class ChunkSetOptions
{
    public static ChunkOptions Options(this ChunkSet set) => new()
    {
        ChunkSizeTokens = set.ChunkSize,
        OverlapTokens = set.ChunkOverlap,
        BoundaryMode = set.BoundaryMode,
        CustomBoundaryPattern = set.CustomBoundaryPattern,
        UnitAware = set.UnitAware,
        SentenceAware = set.SentenceAware,
        HeadingContext = set.HeadingContext,
    };
}
