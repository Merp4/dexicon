using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Embedding;

/// <summary>
/// Reads and writes what a probe measured about a model.
///
/// A measurement is stored and found under <see cref="EmbeddingTarget.CanonicalModel"/>, the name
/// without <c>:latest</c>, in lower case. The Models screen probes the name Ollama lists
/// (<c>embeddinggemma:latest</c>) and a chunk set records the name it was created with
/// (<c>embeddinggemma</c>, or <c>EmbeddingGemma</c> if that is what was typed); all of them are the
/// same weights, so all reach the same row.
/// </summary>
public static class MeasuredModels
{
    /// <summary>What the probe stored for this model, or null if it never ran.</summary>
    public static Task<EmbeddingModelMeasurement?> ForAsync(
        CatalogDbContext db, EmbeddingTarget target, CancellationToken ct = default)
    {
        var canonical = target.CanonicalModel;
        return db.ModelMeasurements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Provider == target.Provider && m.Model == canonical, ct);
    }

    /// <summary>The tracked row to write a measurement into, added to the context when there is none.</summary>
    public static async Task<EmbeddingModelMeasurement> RowForAsync(
        CatalogDbContext db, EmbeddingTarget target, CancellationToken ct = default)
    {
        var canonical = target.CanonicalModel;
        var row = await db.ModelMeasurements.FindAsync([target.Provider, canonical], ct);
        if (row is null)
        {
            row = new EmbeddingModelMeasurement { Provider = target.Provider, Model = canonical };
            db.ModelMeasurements.Add(row);
        }

        return row;
    }
}
