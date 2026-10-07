using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Indexing;

/// <summary>
/// What is being indexed now. Read from the jobs and the corpus lease, and never stored.
///
/// Corpus and set rows used to carry an Indexing state that a job wrote when it started and
/// cleared when it finished. A job that could not save its outcome, because the disk was full
/// or its lease was lost to a refused renewal, left the flag set with nothing behind it, and
/// the scheduled refresh skips an Indexing corpus. It stayed that way until a restart.
///
/// The flag was a copy of two facts the catalogue already holds. A job is queued, or a job is
/// running under a lease that is still renewed. The lease expires on its own, so a job whose
/// holder has died stops counting without anything having to notice. A row now holds only the
/// last outcome, which a dead job leaves as it was, and Indexing is this.
/// </summary>
public sealed class IndexingActivity
{
    /// <summary>
    /// The holder a job takes its corpus with. The one place the format is written, because the
    /// query below has to recognise it.
    /// </summary>
    public const string HolderPrefix = "index-";

    private readonly List<(string CorpusId, string? ChunkSetId)> _live;

    private IndexingActivity(List<(string CorpusId, string? ChunkSetId)> live) => _live = live;

    /// <summary>
    /// The jobs that count as working: queued, or running under the lease they took. A Running
    /// row whose lease has lapsed, or is now someone else's, is a job that stopped without
    /// saying so, and is not work.
    /// </summary>
    public static IQueryable<IndexJob> LiveJobs(CatalogDbContext db, DateTime now) =>
        db.Jobs.Where(j => j.State == JobState.Queued
                           || (j.State == JobState.Running
                               && j.Corpus!.HeldBy == HolderPrefix + j.Id
                               && j.Corpus.HeldUntilUtc > now));

    public static async Task<IndexingActivity> ReadAsync(
        CatalogDbContext db, IReadOnlyCollection<string> corpusIds, CancellationToken ct = default)
    {
        var rows = await LiveJobs(db, DateTime.UtcNow).AsNoTracking()
            .Where(j => corpusIds.Contains(j.CorpusId))
            .Select(j => new { j.CorpusId, j.ChunkSetId })
            .ToListAsync(ct);

        return new([.. rows.Select(r => (r.CorpusId, r.ChunkSetId))]);
    }

    /// <summary>The corpus as it reads now: Indexing while a job works on it, else its last outcome.</summary>
    public CorpusState Of(Corpus corpus) =>
        _live.Any(l => l.CorpusId == corpus.Id) ? CorpusState.Indexing : corpus.State;

    /// <summary>
    /// A set, which a job covers when it names it or names none. A job for another set of the
    /// same corpus says nothing about this one: the live set is complete while its replacement
    /// backfills.
    /// </summary>
    public CorpusState Of(ChunkSet set) =>
        _live.Any(l => l.CorpusId == set.CorpusId && (l.ChunkSetId is not { Length: > 0 } || l.ChunkSetId == set.Id))
            ? CorpusState.Indexing
            : set.State;
}
