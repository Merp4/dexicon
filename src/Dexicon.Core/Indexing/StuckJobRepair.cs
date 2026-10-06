using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Repairs a job the catalogue says is Running while nothing is running it.
///
/// A worker records how its job ended with a catalogue write, and that write can fail. A
/// full data disk refused it for about four minutes: the two jobs in flight logged their
/// failure, could not save it, and could not release their lease either, so both rows stayed
/// Running and both corpora stayed Indexing. The scheduled refresh skips an Indexing corpus,
/// so it passed over them for six hours while the process sat idle. Startup reconciles
/// orphaned jobs, which meant a restart was the only repair.
///
/// Whether a job is alive is read from its lease and from nothing else. A live job renews
/// the lease every <see cref="CorpusLeases.Renew"/>, so a job that is still working always
/// holds it, however long it has run, and indexing a large library takes hours. Taking the
/// lease here is the same conditional update a sweep or an index job uses: it succeeds only
/// when the lease is free or has lapsed, so a live job turns this away without any age or
/// progress threshold to tune. The lease is then held while the rows are corrected, which
/// keeps a sweep or a new job from starting on a half-repaired corpus.
///
/// A job is marked Failed and never Succeeded, and is not queued again here: what it left
/// unfinished stays unfinished until a refresh runs. The jobs, the corpus and the sets they
/// targeted are written in one save, so a failure partway leaves all of them as they were
/// and the next pass finds the same job again. Without that, a repair that failed after the
/// job row was written would leave the corpus Indexing with no Running job to find.
///
/// The lease is advisory (see <see cref="CorpusLeases"/>). A holder stalled past its expiry
/// can still write before it notices it lost the lease, and a job in that state is recorded
/// as Failed here and again by the job when it wakes. Both writes say Failed.
/// </summary>
public sealed class StuckJobRepair(CatalogDbContext db, CorpusLeases leases, ILogger<StuckJobRepair> log)
{
    internal const string Reason =
        "Marked failed: this job was still running when nothing was working on it and the lease "
        + "on its corpus had lapsed, so its outcome was never recorded. A refresh re-indexes any "
        + "file it had not finished.";

    /// <summary>
    /// Repair every corpus with a job that is Running and not held. Returns how many jobs were
    /// marked failed.
    ///
    /// A corpus that cannot be repaired is logged and left as it is, and the others still are.
    /// Nothing is written as failed because the repair itself could not write.
    /// </summary>
    public async Task<int> RepairAsync(CancellationToken ct)
    {
        var corpusIds = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Running)
            .Select(j => j.CorpusId)
            .Distinct()
            .ToListAsync(ct);

        var repaired = 0;

        foreach (var corpusId in corpusIds)
        {
            try
            {
                repaired += await RepairCorpusAsync(corpusId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(ex,
                    "Could not repair the running jobs of corpus {Corpus}; they are left as they are "
                    + "and the next pass tries again", corpusId);
            }
            finally
            {
                // Whatever this corpus left tracked belongs to it. Carried into the next,
                // a failed save would be retried there under that corpus's lease.
                db.ChangeTracker.Clear();
            }
        }

        return repaired;
    }

    private async Task<int> RepairCorpusAsync(string corpusId, CancellationToken ct)
    {
        // Read before the claim, which overwrites both. They go in the log: how long ago a
        // lease lapsed is what tells a full disk from a process that was killed.
        var heldBefore = await db.Corpora.AsNoTracking()
            .Where(c => c.Id == corpusId)
            .Select(c => new { c.HeldBy, c.HeldUntilUtc })
            .FirstOrDefaultAsync(ct);

        await using var hold = await leases.TryAcquireAsync(corpusId, $"repair-{Ulid.NewUlid()}", ct);
        if (hold is null)
        {
            // Someone holds it and is renewing: a job still working, or a sweep. Either way
            // there is nothing here to repair yet.
            log.LogDebug("Corpus {Corpus} is held; leaving its running jobs alone", corpusId);
            return 0;
        }

        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct, hold.Lost);
        ct = leaseLost.Token;

        // Read under the lease, not from the listing above: a job that finished between the
        // two is no longer Running, and the claim is what makes this read stable.
        var stuck = await db.Jobs
            .Where(j => j.CorpusId == corpusId && j.State == JobState.Running)
            .ToListAsync(ct);
        if (stuck.Count == 0) return 0;

        var corpus = await db.Corpora.Include(c => c.ChunkSets)
            .FirstOrDefaultAsync(c => c.Id == corpusId, ct);
        if (corpus is null) return 0;

        var now = DateTime.UtcNow;
        foreach (var job in stuck)
        {
            job.State = JobState.Failed;
            job.Phase = null;
            job.FinishedUtc = now;

            // Whatever the job had recorded stays: an unreachable source found before it
            // died is still true.
            job.Error = job.Error is { Length: > 0 } recorded ? $"{recorded} {Reason}" : Reason;
        }

        // The sets the jobs targeted, the way the indexer chose them: a job names one set
        // or, with none named, covers all of them.
        var targeted = stuck.Any(j => j.ChunkSetId is not { Length: > 0 })
            ? corpus.ChunkSets
            : [.. corpus.ChunkSets.Where(s => stuck.Any(j => j.ChunkSetId == s.Id))];

        var before = corpus.State;

        // Indexing is the state a dead job leaves behind, and the only one rewritten.
        // Unavailable and Degraded were written for a reason that is still true.
        if (corpus.State == CorpusState.Indexing)
            corpus.State = await LastOutcomeAsync(corpusId, null, ct);

        foreach (var set in targeted.Where(s => s.State == CorpusState.Indexing))
            set.State = await LastOutcomeAsync(corpusId, set.Id, ct);

        await db.SaveChangesAsync(ct);

        var lease = heldBefore is { HeldBy: { } holder, HeldUntilUtc: { } until }
            ? $"held by {holder} until {until:u}"
            : "not held";

        foreach (var job in stuck)
            log.LogWarning(
                "Job {JobId} on corpus {Corpus} was Running with nothing working on it (lease {Lease}). "
                + "Marked it Failed; the corpus went from {Before} to {After}",
                job.Id, corpus.Name, lease, before, corpus.State);

        return stuck.Count;
    }

    /// <summary>
    /// What the most recent job to finish says the corpus, or one of its sets, should read.
    ///
    /// Only the outcome is recorded on a job, so an Unavailable corpus cannot be told from a
    /// Degraded one here: a Degraded job reads as Degraded. The refresh that follows sets it
    /// again from what it finds.
    ///
    /// A job that never completed is excluded, which is why the ones being repaired cannot
    /// answer for themselves: they are still Running when this reads. With nothing finished
    /// at all the answer is Degraded, since the only record is a failure.
    /// </summary>
    private async Task<CorpusState> LastOutcomeAsync(string corpusId, string? setId, CancellationToken ct)
    {
        var last = await db.Jobs.AsNoTracking()
            .Where(j => j.CorpusId == corpusId
                        && (j.State == JobState.Succeeded || j.State == JobState.Degraded
                            || j.State == JobState.Failed || j.State == JobState.Cancelled)
                        && (setId == null || j.ChunkSetId == null || j.ChunkSetId == setId))
            .OrderByDescending(j => j.FinishedUtc)
            .ThenByDescending(j => j.Id)
            .Select(j => (JobState?)j.State)
            .FirstOrDefaultAsync(ct);

        // A cancelled pass left the corpus Ready, as the indexer does when it is stopped.
        return last is JobState.Succeeded or JobState.Cancelled ? CorpusState.Ready : CorpusState.Degraded;
    }
}
