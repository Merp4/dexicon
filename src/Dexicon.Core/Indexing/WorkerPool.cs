using Dexicon.Core.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Runs whatever <see cref="WorkScheduler"/> says can run, for the life of the process.
///
/// One pool rather than a worker per kind of work. The kinds differ in what they cost,
/// not in how they are started, and the scheduler already holds that difference as a
/// limit per type — so a second hosted service would be a second copy of this loop
/// reading a second queue to enforce a number the first one already knows.
///
/// A worker per slot, not a worker taking slots. A loop that held a slot while waiting
/// for one would be a loop not reading the queue, which is the shape that made a long
/// corpus own the machine before several could run at once.
///
/// Each item takes its own DI scope and so its own catalogue connection. Nothing is
/// shared between them but the scheduler; what they contend for — the embedding
/// endpoint, the parser, the filesystem — is bounded by <see cref="IndexingLimits"/>,
/// per resource rather than per job.
/// </summary>
public sealed class WorkerPool(
    IServiceScopeFactory scopes,
    WorkScheduler scheduler,
    IndexProgressBroadcaster broadcaster,
    ILogger<WorkerPool> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = scheduler.TotalSlots;
        log.LogInformation(
            "Workers started: {Workers} slots ({Sweep} sweep, {Index} index, {Rebuild} rebuild)",
            workers, scheduler.LimitFor(WorkType.Sweep), scheduler.LimitFor(WorkType.Index),
            scheduler.LimitFor(WorkType.Rebuild));

        await Task.WhenAll(Enumerable.Range(0, workers).Select(i => RunAsync(i, stoppingToken)));

        log.LogInformation("Workers stopped");
    }

    private async Task RunAsync(int worker, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            WorkItem? item;
            try
            {
                // Wait first, then take. A wake does not promise an item — another worker
                // may take it, and a completion wakes one worker whether or not the freed
                // slot helps what it was woken for — so a null is ordinary and the loop
                // simply waits again.
                await scheduler.WaitAsync(stoppingToken);
                item = scheduler.TryTake();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (item is null) continue;

            try
            {
                await RunOneAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                scheduler.Completed(item);
                break;
            }
            catch (Exception ex)
            {
                // The worker must outlive a bad item. A crash here would silently stop
                // every future one, with nothing in the UI to explain why indexing or
                // inventories stopped.
                log.LogError(ex, "{Type} work {Key} on worker {Worker} threw outside its "
                    + "own error handling", item.Type, item.Key, worker);
            }
            finally
            {
                scheduler.Completed(item);
            }
        }
    }

    private async Task RunOneAsync(WorkItem item, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();

        switch (item.Type)
        {
            case WorkType.Sweep:
                var sweeper = scope.ServiceProvider.GetRequiredService<CorpusSweeper>();
                await sweeper.SweepAsync(item.Key, ct);
                break;

            case WorkType.Index:
            case WorkType.Rebuild:
                var indexer = scope.ServiceProvider.GetRequiredService<CorpusIndexer>();
                var progress = new Progress<IndexProgress>(broadcaster.Publish);
                var job = await indexer.RunAsync(item.Key, progress, ct);

                // Still Queued means the corpus was held by something outside this
                // process's scheduler — the lease is what excludes those. Put it back;
                // the scheduler will offer it again once this corpus is free here, and
                // the lease decides the rest.
                if (job.State == JobState.Queued)
                {
                    log.LogInformation(
                        "Job {JobId} deferred: corpus {Corpus} is held elsewhere", item.Key, item.CorpusId);
                    Requeue(item);
                }

                break;

            default:
                throw new InvalidOperationException($"No handler for work type {item.Type}.");
        }
    }

    /// <summary>
    /// Put a deferred item back, after this one's slot is released.
    ///
    /// Detached, because releasing the slot happens in the caller's `finally` and
    /// enqueuing before that would offer the item to a scheduler that still counts this
    /// corpus as busy. A short delay keeps a corpus held by another process from
    /// spinning the loop; it is not a guess at how long that hold lasts.
    /// </summary>
    private void Requeue(WorkItem item) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                scheduler.Enqueue(item);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not re-queue deferred {Type} work {Key}", item.Type, item.Key);
            }
        });
}
