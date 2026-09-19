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

    /// <summary>
    /// The set's settings, reconciled with what the model measured for itself.
    ///
    /// Two corrections, both in the same direction, and the configured size is honoured
    /// wherever it is already inside them:
    ///
    /// The size is capped below the model's context, at the share of it in
    /// <see cref="CodeChunker.ContextShare"/>. A chunk larger than the model reads in one
    /// go has its tail embedded by nothing, and the failure is quiet: Ollama returns a
    /// vector for the part it read. This library ran a 2,065-token size against a
    /// 2,048-token model, so every full-size chunk was over before density was considered.
    ///
    /// Capping AT the context was the first answer and it was not enough. The ratio below
    /// is an estimate, so a chunk sized to land exactly on the ceiling goes over whenever
    /// the estimate is a little high, which for a sampled estimate is often. The margin is
    /// what that error lands in instead.
    ///
    /// The conversion to characters uses the ratio the probe measured for this model
    /// rather than the flat 4. On embeddinggemma that is 3.8, so the flat 4 asked for 5%
    /// more characters than the token budget it claimed to be enforcing.
    ///
    /// An unmeasured model has neither number and keeps the configured size and the
    /// constant, which is what every set had before any of this was measured.
    /// </summary>
    public static ChunkOptions Options(this ChunkSet set, EmbeddingModelMeasurement? measured = null)
    {
        var tokens = measured?.ContextTokens is { } ctx && ctx > 0
            ? Math.Min(set.ChunkSize, CodeChunker.UsableContext(ctx))
            : set.ChunkSize;

        // A ratio at or below zero is not a measurement. Guarded because it reaches
        // division and a multiply that sizes every chunk in the corpus.
        var charsPerToken = measured?.CharsPerToken is { } r && r > 0
            ? r
            : CodeChunker.CharsPerToken;

        return new ChunkOptions
        {
            ChunkSizeTokens = tokens,
            // Strictly below the size, which the chunker requires. The floor is zero and
            // not one: at a size of one token, an overlap of one equals the size and the
            // chunker throws rather than indexing the file.
            OverlapTokens = Math.Min(set.ChunkOverlap, Math.Max(tokens - 1, 0)),
            CharsPerToken = charsPerToken,
            BoundaryMode = set.BoundaryMode,
            CustomBoundaryPattern = set.CustomBoundaryPattern,
            UnitAware = set.UnitAware,
            SentenceAware = set.SentenceAware,
            HeadingContext = set.HeadingContext,
        };
    }
}
