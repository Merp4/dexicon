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
    /// pending is a no-op returning the existing id — two identical scans in a row is
    /// wasted work, not throughput.
    /// </summary>
    public async Task<IndexJob> EnqueueAsync(string corpusId, JobKind kind, CancellationToken ct = default)
    {
        var existing = await db.Jobs
            .Where(j => j.CorpusId == corpusId && (j.State == JobState.Queued || j.State == JobState.Running))
            .OrderByDescending(j => j.StartedUtc)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            log.LogInformation("Corpus {Corpus} already has job {JobId} in state {State}; not queuing another",
                corpusId, existing.Id, existing.State);
            return existing;
        }

        var job = new IndexJob
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = corpusId,
            Kind = kind,
            State = JobState.Queued,
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
/// than here — the indexer reports every 25 files, which is already a sane rate.
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
