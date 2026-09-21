using System.Threading.Channels;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Writes the job row and hands it to <see cref="WorkScheduler"/>. No broker: jobs are
/// local and there is one instance, so a queue between two parts of the same process is
/// all the coordination that exists to do.
///
/// The queue used to be a channel here, with its own worker loop and a fifteen-second
/// requeue for a job whose corpus was busy. Both are gone: what may run at once is one
/// question for the whole process, and answering it in one place is what lets a sweep,
/// an incremental pass and a rebuild carry different limits without three copies of the
/// same loop.
/// </summary>
public sealed class IndexJobQueue(CatalogDbContext db, WorkScheduler scheduler, ILogger<IndexJobQueue> log)
{
    /// <summary>
    /// A rebuild re-embeds every file it walks and an incremental pass mostly does not,
    /// so they are scheduled against different limits. This is the one place that
    /// mapping is made.
    /// </summary>
    internal static WorkType TypeOf(JobKind kind) =>
        kind is JobKind.Full or JobKind.Rebuild ? WorkType.Rebuild : WorkType.Index;

    /// <summary>
    /// Enqueue a job for a corpus. Queuing a refresh for a corpus that already has one
    /// pending is a no-op returning the existing id, since two identical scans in a row
    /// is wasted work rather than throughput.
    /// </summary>
    /// <param name="chunkSetId">
    /// The one set to index, or null for every set in the corpus. Naming a set is what
    /// lets a replacement backfill while the live set keeps serving search.
    /// </param>
    public async Task<IndexJob> EnqueueAsync(string corpusId, JobKind kind, string? chunkSetId = null,
        CancellationToken ct = default)
    {
        // Deduplicated per (corpus, SET). Matching on the corpus alone would hand back a
        // job for a different set, so a request to backfill a new set would return the
        // live set's refresh, report success, and build nothing.
        //
        // QUEUED ONLY, and that word is the whole of it. A queued job has not yet read the
        // corpus, so whatever changes before it starts is included and coalescing is free.
        // A RUNNING job has already taken its list of sources: anything added afterwards is
        // not in it and never will be. Coalescing onto one returned that job's id as though
        // it covered the new work, so adding nine folders to a corpus mid-index indexed
        // the ones that happened to be there when the walk began, left the rest out, and
        // reported the corpus `ready` with no job pending and nothing wrong on its face.
        //
        // At most one queued job can exist per (corpus, set), so refusing to coalesce onto
        // a running one adds a single job, not a pile.
        var existing = await db.Jobs
            .Where(j => j.CorpusId == corpusId && j.ChunkSetId == chunkSetId
                        && j.State == JobState.Queued)
            .OrderByDescending(j => j.QueuedUtc)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            log.LogInformation("Corpus {Corpus} already has job {JobId} queued; not queuing another",
                corpusId, existing.Id);
            return existing;
        }

        var job = new IndexJob
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = corpusId,
            ChunkSetId = chunkSetId,
            Kind = kind,
            State = JobState.Queued,
            QueuedUtc = DateTime.UtcNow,
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        scheduler.Enqueue(new WorkItem(TypeOf(kind), corpusId, job.Id));

        log.LogInformation("Queued {Kind} job {JobId} for corpus {Corpus}", kind, job.Id, corpusId);
        return job;
    }
}

/// <summary>
/// Fans indexing progress out to SSE subscribers. Coalesced by the endpoint rather
/// than here; the indexer reports every 25 files, which is already a reasonable rate.
/// </summary>
public sealed class IndexProgressBroadcaster
{
    private readonly List<Channel<IndexProgress>> _subscribers = [];
    private readonly Lock _gate = new();

    public IDisposable Subscribe(out ChannelReader<IndexProgress> reader)
    {
        var channel = Channel.CreateBounded<IndexProgress>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

        lock (_gate) _subscribers.Add(channel);
        reader = channel.Reader;
        return new Subscription(this, channel);
    }

    public void Publish(IndexProgress progress)
    {
        lock (_gate)
        {
            foreach (var s in _subscribers) s.Writer.TryWrite(progress);
        }
    }

    private sealed class Subscription(IndexProgressBroadcaster owner, Channel<IndexProgress> channel) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
