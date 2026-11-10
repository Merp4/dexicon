using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Embedding;

/// <summary>
/// Records the document template a chunk set's vectors were embedded with, so a change to the template
/// the model's profile gives is reported (D-47).
///
/// A set stores no framing. The template comes from the model's profile when a file is embedded and again
/// when a query is sent, so editing a profile, or a release adding a built-in entry for the model, changes
/// the queries of every set on it while their stored vectors stay as they were. Saving a profile queues a
/// rebuild; a built-in entry added by an upgrade queues nothing, and the set answers with framed queries
/// against raw vectors until someone reindexes it.
///
/// Only the document template is kept. It decides what the vectors are; the query template decides what a
/// search sends, and changing it leaves every stored vector valid.
///
/// Advisory, like <see cref="ModelCanary"/>: nothing here fails a pass. It needs no embedding call, so it
/// is recorded and compared whether or not the provider answers.
/// </summary>
public static class ModelFraming
{
    /// <summary>
    /// Records the template the set has now, for a set with none. A set that predates the column gets its
    /// record from the first check, so a framing change made before that is not visible.
    /// </summary>
    /// <remarks>
    /// Written only where the column is still empty, and with an update statement and not through the
    /// caller's tracked <paramref name="set"/>, for the reasons given on <see cref="ModelCanary.CheckAsync"/>.
    /// </remarks>
    /// <returns>True when this call wrote the record.</returns>
    public static async Task<bool> EnsureRecordedAsync(
        CatalogDbContext db, ChunkSet set, ModelTemplates current, ILogger log, CancellationToken ct)
    {
        if (set.FramingRecorded is not null) return false;

        var id = set.Id;
        var written = await db.ChunkSets.Where(s => s.Id == id && s.FramingRecorded == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FramingRecorded, current.Document), ct);

        if (written == 0) return false;

        log.LogInformation(
            "Chunk set {Set} of corpus {CorpusId}: recorded the document framing of {Model} ({Origin})",
            set.Name, set.CorpusId, set.EmbeddingModel, current.Origin);
        return true;
    }

    /// <summary>
    /// Replaces the set's record with the template a full pass embedded with, once every vector of the set
    /// is one that pass wrote.
    /// </summary>
    public static async Task RecordAsync(CatalogDbContext db, ChunkSet set, ModelTemplates used, CancellationToken ct)
    {
        var id = set.Id;
        await db.ChunkSets.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FramingRecorded, used.Document), ct);
    }

    /// <summary>
    /// Whether the vectors of a set recorded with <paramref name="recorded"/> were embedded with another
    /// document template than the profile gives now. A set with no record is not reported.
    /// </summary>
    public static bool Differs(string? recorded, ModelTemplates current) =>
        recorded is not null && !string.Equals(recorded, current.Document, StringComparison.Ordinal);
}
