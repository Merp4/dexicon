using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Indexing;

/// <summary>
/// The discovery lane: its own channel and its own worker, so a sweep never waits behind
/// an index job.
///
/// A separate lane rather than another <c>JobKind</c> on the existing queue, which is one
/// channel with a single reader. A discovery job there would wait for precisely the work
/// it exists to get in front of: a corpus added while another indexes read as empty for as
/// long as that took, which on a library of 1,800 PDFs is hours. See D-32.
///
/// Coalesced per corpus. A sweep that has not started yet covers whatever the tree looks
/// like when it does, so a second request for the same corpus is the same sweep; queuing
/// both would walk the tree twice for one answer. That is safe here in a way it is not for
/// indexing, where a queued job and a running one differ, because a sweep takes the lease
/// and a duplicate would be turned away at the door anyway.
/// </summary>
public sealed class SweepQueue(ILogger<SweepQueue> log)
{
    private static readonly Channel<string> Pending = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private static readonly ConcurrentDictionary<string, byte> Queued = new(StringComparer.Ordinal);

    internal static ChannelReader<string> Reader => Pending.Reader;

    /// <summary>Marks a corpus as no longer queued. Called by the worker as it starts one.</summary>
    internal static void Dequeued(string corpusId) => Queued.TryRemove(corpusId, out _);

    /// <summary>
    /// Ask for a sweep. Returns false when one is already waiting for this corpus, which
    /// is not a failure: the sweep already queued will see whatever is there when it runs.
    /// </summary>
    public bool Enqueue(string corpusId)
    {
        if (!Queued.TryAdd(corpusId, 0)) return false;

        if (Pending.Writer.TryWrite(corpusId)) return true;

        // Unbounded, so this does not happen short of the channel being completed. Undo
        // the mark rather than leave a corpus that can never be queued again.
        Queued.TryRemove(corpusId, out _);
        log.LogWarning("Could not queue a sweep for corpus {Corpus}", corpusId);
        return false;
    }
}

/// <summary>
/// Runs sweeps, one at a time, independently of the indexing worker.
///
/// One at a time because the work is short and the catalogue has a single writer: running
/// several would contend for it with each other as well as with indexing, to finish a
/// two-second walk marginally sooner.
/// </summary>
public sealed class SweepWorker(IServiceScopeFactory scopes, ILogger<SweepWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Discovery worker started");

        await foreach (var corpusId in SweepQueue.Reader.ReadAllAsync(stoppingToken))
        {
            SweepQueue.Dequeued(corpusId);

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sweeper = scope.ServiceProvider.GetRequiredService<CorpusSweeper>();
                await sweeper.SweepAsync(corpusId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The worker outlives a bad sweep. A crash here would stop every future
                // one with nothing in the UI to explain why inventories stopped updating.
                log.LogError(ex, "Sweeping corpus {Corpus} threw", corpusId);
            }
        }

        log.LogInformation("Discovery worker stopped");
    }
}
