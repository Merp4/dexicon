using System.Threading.Channels;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// One job at a time, in a bounded in-process channel. No broker: jobs are local and
/// there is one instance, so a queue between two parts of the same process is all the
/// coordination that exists to do.
/// </summary>
public sealed class IndexJobQueue(CatalogDbContext db, ILogger<IndexJobQueue> log)
{
    private static readonly Channel<string> Pending = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    public static ChannelReader<string> Reader => Pending.Reader;

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

/// <summary>Drains the queue, one job at a time, for the life of the process.</summary>
public sealed class IndexingBackgroundService(
    IServiceScopeFactory scopes,
    IndexProgressBroadcaster broadcaster,
    ILogger<IndexingBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Indexing worker started");

        await foreach (var jobId in IndexJobQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var indexer = scope.ServiceProvider.GetRequiredService<CorpusIndexer>();
                var progress = new Progress<IndexProgress>(broadcaster.Publish);
                await indexer.RunAsync(jobId, progress, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The worker must outlive a bad job. A crash here would silently stop
                // every future index with nothing in the UI to explain it.
                log.LogError(ex, "Indexing job {JobId} threw outside its own error handling", jobId);
            }
        }

        log.LogInformation("Indexing worker stopped");
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
