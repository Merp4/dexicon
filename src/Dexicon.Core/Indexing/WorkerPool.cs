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
            WorkLease? lease;
            try
            {
                // Wait first, then take. A wake does not promise an item — another worker
                // may take it, and a completion wakes one worker whether or not the freed
                // slot helps what it was woken for — so a null is ordinary and the loop
                // simply waits again.
                await scheduler.WaitAsync(stoppingToken);
                lease = scheduler.TryTake();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (lease is null) continue;

            try
            {
                await RunOneAsync(lease.Item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // No release here: the `finally` does it, once. Releasing in both freed
                // the slot twice, and the first release wakes another worker — so a
                // replacement could start and then have ITS count decremented and ITS
                // corpus marked free by the second call, admitting work past the limit.
                break;
            }
            catch (Exception ex)
            {
                // The worker must outlive a bad item. A crash here would silently stop
                // every future one, with nothing in the UI to explain why indexing or
                // inventories stopped.
                log.LogError(ex, "{Type} work {Key} on worker {Worker} threw outside its "
                    + "own error handling", lease.Item.Type, lease.Item.Key, worker);
            }
            finally
            {
                scheduler.Completed(lease);
            }
        }
    }

    internal async Task RunOneAsync(WorkItem item, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();

        switch (item.Type)
        {
            case WorkType.Sweep:
                var sweeper = scope.ServiceProvider.GetRequiredService<CorpusSweeper>();
                var sweep = await sweeper.SweepAsync(item.Key, ct);

                // Held means the lease belongs to someone else, and under this scheduler
                // that someone is another process: a corpus busy here is never
                // dispatched. So it is the same case as a deferred index job and gets the
                // same answer, and discarding the result instead lost the sweep outright.
                //
                // NoSuchCorpus is terminal. Putting that back would be a loop with no end
                // and no work in it, which is the reason the two are separate outcomes
                // rather than one "skipped" flag.
                if (sweep.Outcome == SweepOutcome.Held)
                {
                    log.LogInformation(
                        "Sweep of corpus {Corpus} deferred: {Reason}", item.CorpusId, sweep.Reason);
                    Requeue(item, ct);
                }

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
                    Requeue(item, ct);
                }

                break;

            default:
                throw new InvalidOperationException($"No handler for work type {item.Type}.");
        }
    }

    /// <summary>
    /// How long a deferred item waits before it is offered again. Long enough that a
    /// corpus held by another process is not spun on, short enough that the work is not
    /// forgotten. Settable so a test does not have to wait it out.
    /// </summary>
    internal TimeSpan RequeueAfter { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Put a deferred item back, after this one's slot is released.
    ///
    /// Detached, because releasing the slot happens in the caller's `finally` and
    /// enqueuing before that would offer the item to a scheduler that still counts this
    /// corpus as busy. A short delay keeps a corpus held by another process from
    /// spinning the loop; it is not a guess at how long that hold lasts.
    ///
    /// On the host's token, so a shutdown during the delay drops the requeue instead of
    /// putting work into a scheduler whose workers have stopped. That would leave a
    /// Queued row nothing will ever take, visible as a pending job until the next start
    /// reconciles it as interrupted.
    /// </summary>
    private void Requeue(WorkItem item, CancellationToken ct) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RequeueAfter, ct);
                scheduler.Enqueue(item);
            }
            catch (OperationCanceledException) { /* shutting down; the row is reconciled at startup */ }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not re-queue deferred {Type} work {Key}", item.Type, item.Key);
            }
        }, ct);
}
