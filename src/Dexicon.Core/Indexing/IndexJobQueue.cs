using System.Threading.Channels;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// An in-process channel of job ids. No broker: jobs are local and there is one
/// instance, so a queue between two parts of the same process is all the coordination
/// that exists to do.
///
/// Several readers, one per concurrent corpus. It was a single reader, which made the
/// queue the thing that serialised indexing: a corpus taking hours owned the machine and
/// a sixteen-file refresh behind it waited all of them. Excluding two jobs on ONE corpus
/// is the lease's job, and it does it whether one reader or four takes them.
/// </summary>
public sealed class IndexJobQueue(CatalogDbContext db, ILogger<IndexJobQueue> log)
{
    private static readonly Channel<string> Pending = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false });

    public static ChannelReader<string> Reader => Pending.Reader;

    /// <summary>
    /// Put a job back after a pause, without blocking the caller.
    ///
    /// The pause is so a job whose corpus is busy does not spin round an otherwise empty
    /// queue; it is not a guess at how long the other pass will take, because the job is
    /// simply tried again after it. Detached on purpose: the worker must be free to take
    /// the next job immediately, which is the whole point of deferring.
    /// </summary>
    internal static void RequeueLater(string jobId, ILogger log, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                if (!Pending.Writer.TryWrite(jobId))
                    log.LogWarning("Could not re-queue deferred job {JobId}", jobId);
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }, ct);
    }

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
        await Pending.Writer.WriteAsync(job.Id, ct);

        log.LogInformation("Queued {Kind} job {JobId} for corpus {Corpus}", kind, job.Id, corpusId);
        return job;
    }
}

/// <summary>
/// Drains the queue for the life of the process, <c>MaxConcurrentCorpora</c> jobs at a
/// time.
///
/// Each job takes its own DI scope and therefore its own catalogue connection, which is
/// what makes running several safe: nothing here is shared between them but the queue
/// they read from. What they contend for — the embedding endpoint, the parser, the
/// filesystem — is bounded by <see cref="IndexingLimits"/>, per resource rather than per
/// job, so one corpus indexing alone still uses the whole budget.
///
/// Two jobs on ONE corpus remain excluded, by the lease. A job that cannot take it is
/// left Queued and put back, which is why a worker never waits on another worker.
/// </summary>
public sealed class IndexingBackgroundService(
    IServiceScopeFactory scopes,
    IndexProgressBroadcaster broadcaster,
    IndexingLimits limits,
    ILogger<IndexingBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = limits.MaxConcurrentCorpora;
        log.LogInformation("Indexing workers started: {Workers} corpora at a time", workers);

        // One loop per permit rather than one loop taking permits. The permit count IS
        // the worker count, so a job never sits held inside a worker waiting for one
        // while the queue behind it goes unread.
        await Task.WhenAll(Enumerable.Range(0, workers).Select(i => RunWorkerAsync(i, stoppingToken)));

        log.LogInformation("Indexing workers stopped");
    }

    private async Task RunWorkerAsync(int worker, CancellationToken stoppingToken)
    {
        await foreach (var jobId in IndexJobQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var indexer = scope.ServiceProvider.GetRequiredService<CorpusIndexer>();
                var progress = new Progress<IndexProgress>(broadcaster.Publish);
                var job = await indexer.RunAsync(jobId, progress, stoppingToken);

                // Still Queued means it never ran, because its corpus is held by another
                // pass. Put it back and take the next one: the alternative is this worker
                // waiting, which stops every corpus it could have been indexing instead.
                if (job.State == JobState.Queued) IndexJobQueue.RequeueLater(jobId, log, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The worker must outlive a bad job. A crash here would silently stop
                // every future index with nothing in the UI to explain it.
                log.LogError(ex, "Indexing job {JobId} on worker {Worker} threw outside "
                    + "its own error handling", jobId, worker);
            }
        }
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
