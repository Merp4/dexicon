using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;

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
    /// <summary>
    /// Which backend and model this set embeds with. The provider is part of it: two
    /// providers can serve a model of the same name, and they are not the same vectors.
    /// </summary>
    public static EmbeddingTarget Target(this ChunkSet set) => new(set.EmbeddingProvider, set.EmbeddingModel);

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
