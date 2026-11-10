using System.Buffers.Binary;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Embedding;

/// <summary>What a canary check found.</summary>
public enum CanaryOutcome
{
    /// <summary>No baseline existed, and the model's current answer is now the baseline.</summary>
    Recorded = 0,

    /// <summary>The model answers as it did.</summary>
    Unchanged = 1,

    /// <summary>The model answers differently: the name now points at other weights.</summary>
    Changed = 2,

    /// <summary>The provider did not answer. The recorded state is untouched.</summary>
    Unavailable = 3,

    /// <summary>The record changed between this check's read and its write, so what it compared against is gone. State is the other writer's.</summary>
    Superseded = 4,
}

/// <summary>
/// Detects that the model behind a chunk set's name is no longer the model that embedded its vectors.
///
/// A set pins a model by name, and a name does not identify weights: <c>ollama pull</c> on a tag that
/// moved replaces what it points at, and a hosted model can be updated under a stable id. Vectors from
/// two models do not share a space, so a set that keeps indexing after that holds a mixture that ranks
/// quietly worse, with nothing failing. The catalogue records which model a set uses but not that it
/// still is that model.
///
/// The check embeds one fixed string (<see cref="Text"/>) with no framing and compares the vector with
/// the one recorded for the set. It needs only an embedding call, so it works for every provider, and it
/// compares what the model does rather than a digest only Ollama reports.
///
/// Advisory. Nothing here fails a pass or blocks a write: the result is a column the health report
/// reads. A provider that does not answer leaves the recorded state as it was.
/// </summary>
public static class ModelCanary
{
    /// <summary>
    /// The string embedded to identify a model. Fixed: every recorded vector answers this text, so
    /// changing it makes every set read as changed. <c>ModelCanaryTests</c> pins it.
    /// </summary>
    public const string Text = "The quick brown fox jumps over the lazy dog.";

    /// <summary>
    /// Cosine similarity at or above which the model counts as the one that was recorded.
    ///
    /// Measured with Ollama on CPU, as cosine similarity. One model embedded twice agrees to within 3e-16
    /// of 1 (Ollama 0.40.15, 2,768 chunk vectors, 2026-11-09). The upgrade from Ollama 0.32.14 to 0.40.4
    /// gave a lowest similarity of 0.999997 over 23 vectors (2026-10-15). A different quantisation of one
    /// model (embeddinggemma-2 as Q8_0 GGUF against nvfp4 MLX, 0.40.15, 2026-11-09) gives a median of
    /// 0.988 and a minimum of 0.958, with 83% of the top ten results shared. The first two are above the
    /// threshold and the third is below it. Not measured: a hosted provider, and one deployment moving
    /// between CPU and GPU. A false report there is a notice, since nothing acts on it.
    /// </summary>
    public const double SameModel = 0.999;

    /// <summary>
    /// Cosine similarity of two vectors. A different length is a different space, so 0 whatever the
    /// values are, and so is a vector of zeros or one with a component that is not a finite number.
    /// </summary>
    public static double Similarity(ReadOnlySpan<float> recorded, ReadOnlySpan<float> current)
    {
        if (recorded.Length == 0 || recorded.Length != current.Length) return 0;

        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < recorded.Length; i++)
        {
            dot += (double)recorded[i] * current[i];
            a += (double)recorded[i] * recorded[i];
            b += (double)current[i] * current[i];
        }

        var cosine = a == 0 || b == 0 ? 0 : dot / Math.Sqrt(a * b);

        // A component that is not a number makes the result not a number, which SQLite stores as NULL and which
        // compares as neither above nor below the threshold. A vector like that is not the recorded model.
        return double.IsFinite(cosine) ? cosine : 0;
    }

    /// <summary>Little-endian float32, the form <see cref="ChunkSet.CanaryVector"/> stores.</summary>
    public static byte[] Pack(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        return bytes;
    }

    /// <summary>The vector a <see cref="Pack"/> produced. A length that is not whole floats gives an empty vector.</summary>
    public static float[] Unpack(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0) return [];

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        return vector;
    }

    /// <summary>
    /// Embeds <see cref="Text"/> with the set's model and compares it with the set's recorded vector.
    /// A set with none records this one, which is how a set that predates the column gets a baseline: from
    /// the first check, so a change that happened earlier is not visible.
    /// </summary>
    /// <remarks>
    /// Written with an update statement, not through the caller's tracked <paramref name="set"/>. The
    /// indexer saves its sets at the end of a pass, and a pass that loses its corpus lease discards them
    /// unwritten; this describes the model and not the pass, so it is written when it is measured. A
    /// result is written only where the record is still the one it was compared with, so a check that
    /// began before another recorded or re-recorded the set cannot overwrite it with an older answer.
    /// </remarks>
    public static async Task<CanaryOutcome> CheckAsync(
        CatalogDbContext db, IEmbeddingService embedder, ChunkSet set, ILogger log, CancellationToken ct)
    {
        var current = await TryEmbedAsync(embedder, set, log, ct);
        if (current is null) return CanaryOutcome.Unavailable;

        var now = DateTime.UtcNow;
        var id = set.Id;

        if (set.CanaryVector is not { } recorded)
        {
            if (!CanBeRecorded(current))
            {
                LogUnusable(log, set);
                return CanaryOutcome.Unavailable;
            }

            var written = await db.ChunkSets.Where(s => s.Id == id && s.CanaryVector == null)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.CanaryVector, Pack(current))
                    .SetProperty(s => s.CanarySimilarity, 1.0)
                    .SetProperty(s => s.CanaryCheckedUtc, now), ct);

            if (written == 0) return CanaryOutcome.Superseded;

            log.LogInformation(
                "Chunk set {Set} of corpus {CorpusId}: recorded the {Model} canary ({Dimensions} dimensions)",
                set.Name, set.CorpusId, set.EmbeddingModel, current.Length);
            return CanaryOutcome.Recorded;
        }

        var similarity = Similarity(Unpack(recorded), current);

        // Only against the record this was compared with. A full pass that finished while the model
        // was being asked has replaced it, and a similarity to the old one would report a change the
        // set no longer has.
        var compared = await db.ChunkSets.Where(s => s.Id == id && s.CanaryVector == recorded)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.CanarySimilarity, similarity)
                .SetProperty(s => s.CanaryCheckedUtc, now), ct);

        if (compared == 0) return CanaryOutcome.Superseded;
        if (similarity >= SameModel) return CanaryOutcome.Unchanged;

        // On the way in only. The check runs every few hours and again at the start of each pass, and
        // a set that stays changed would repeat this line until a full pass records it again.
        if (set.CanarySimilarity is null or >= SameModel)
            log.LogWarning(
                "Chunk set {Set} of corpus {CorpusId}: {Provider} {Model} no longer answers as it did when "
                + "the set was recorded (similarity {Similarity:F4}, same model at {Threshold}). Files indexed "
                + "from here on are embedded by the current model into a set built by another; a full reindex "
                + "of the corpus re-embeds everything",
                set.Name, set.CorpusId, set.EmbeddingProvider, set.EmbeddingModel, similarity, SameModel);

        return CanaryOutcome.Changed;
    }

    /// <summary>
    /// Replaces the set's recorded vector with the model's current answer. For a set whose every vector
    /// has just been re-embedded, which makes the current model the one the set holds.
    /// </summary>
    /// <returns>False when the provider did not answer, which leaves the earlier record in place.</returns>
    public static async Task<bool> RecordAsync(
        CatalogDbContext db, IEmbeddingService embedder, ChunkSet set, ILogger log, CancellationToken ct)
    {
        var current = await TryEmbedAsync(embedder, set, log, ct);
        if (current is null) return false;

        if (!CanBeRecorded(current))
        {
            LogUnusable(log, set);
            return false;
        }

        var id = set.Id;
        var now = DateTime.UtcNow;
        await db.ChunkSets.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.CanaryVector, Pack(current))
                .SetProperty(s => s.CanarySimilarity, 1.0)
                .SetProperty(s => s.CanaryCheckedUtc, now), ct);

        return true;
    }

    /// <summary>
    /// Whether an answer can stand as a record: it has to be similar to itself, which a vector of zeros, or one with a
    /// component that is not a finite number, is not. Recorded, it would read as changed against every later answer, and
    /// the notice would blame a model change for a model that never answered usefully. Compared with a record, the same
    /// answer is not refused: it reads as a similarity of 0.
    /// </summary>
    private static bool CanBeRecorded(float[] answer) => Similarity(answer, answer) >= SameModel;

    private static void LogUnusable(ILogger log, ChunkSet set) =>
        log.LogWarning(
            "Chunk set {Set} of corpus {CorpusId}: {Model} answered the canary with a vector of zeros or one that is not a "
            + "number, so it was not recorded",
            set.Name, set.CorpusId, set.EmbeddingModel);

    /// <summary>
    /// The model's current answer, or null when the provider could not give one. Unreachable is not
    /// changed: the health report already says the backend is down, and a canary that read an outage as
    /// a different model would report two faults for one.
    /// </summary>
    private static async Task<float[]?> TryEmbedAsync(
        IEmbeddingService embedder, ChunkSet set, ILogger log, CancellationToken ct)
    {
        try
        {
            // Raw: the canary measures the model, and a framing edit already rebuilds the set.
            var vectors = await embedder.EmbedAsync(
                set.Target(), EmbedPurpose.Raw, [Text], "model canary", ct);

            return vectors is [{ Length: > 0 } vector] ? vector : null;
        }
        catch (Exception ex) when (ex is EmbeddingUnavailableException or UnknownEmbeddingProviderException)
        {
            log.LogDebug(ex, "Chunk set {Set} of corpus {CorpusId}: the canary could not be embedded", set.Name, set.CorpusId);
            return null;
        }
    }
}
