using Dexicon.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Indexing;

/// <summary>
/// What kind of work an item is, which is the only thing its concurrency is configured
/// against. Adding a kind is adding a value here and a limit beside it.
/// </summary>
public enum WorkType
{
    /// <summary>Walk a corpus and record what is in it. Cheap, and must not wait.</summary>
    Sweep,

    /// <summary>An incremental index pass: most files are unchanged and cost nothing.</summary>
    Index,

    /// <summary>A full or rebuild pass, which re-embeds everything it walks.</summary>
    Rebuild,
}

/// <summary>
/// One piece of queued work.
/// </summary>
/// <param name="Key">
/// What the handler needs to run it: a job id for <see cref="WorkType.Index"/> and
/// <see cref="WorkType.Rebuild"/>, a corpus id for <see cref="WorkType.Sweep"/>.
/// </param>
public sealed record WorkItem(WorkType Type, string CorpusId, string Key);

/// <summary>
/// A claim on one slot, handed out by <see cref="WorkScheduler.TryTake"/> and given back
/// to <see cref="WorkScheduler.Completed"/>.
///
/// The ticket is what makes releasing safe. Identifying a running item by its type and
/// key is not enough, because the scheduler deliberately lets a second item with the
/// same key queue while the first runs — a sweep asked for again during one. A stale
/// release of the first would then free the SECOND's slot, which is the same
/// over-admission a plain counter allows, arrived at by a different route. A ticket is
/// spent once and belongs to one take.
/// </summary>
public sealed record WorkLease(WorkItem Item, long Ticket);

/// <summary>
/// The one queue, and the one rule for taking from it.
///
/// Work is dispatched to the first pending item whose TYPE has a free slot and whose
/// CORPUS has nothing running. Everything else about scheduling falls out of that:
///
/// - **Nothing starves.** A corpus with five queued jobs runs one, and its second is
///   ineligible while the first holds the corpus, so the next corpus is taken instead.
///   That is the fairness a rotation would have given, without any per-corpus bookkeeping
///   to keep in step.
/// - **Expensive work is capped separately.** A rebuild re-embeds everything it walks,
///   so it gets a lower limit than an incremental pass rather than competing as an equal.
/// - **A busy corpus is skipped, not retried.** This replaces a worker taking a job,
///   discovering the corpus was leased, and putting it back on a fifteen-second timer.
///   Ineligibility is a scheduling decision now, not a failed attempt.
///
/// The per-corpus rule matches what the lease already enforces at the door. The lease
/// stays: it is what excludes a second process, and this only knows about its own.
/// </summary>
public sealed class WorkScheduler(IOptions<DexiconOptions> options) : IDisposable
{
    private readonly Dictionary<WorkType, int> _limits = Limits(options.Value.Indexing);

    private readonly List<WorkItem> _pending = [];
    private readonly Dictionary<WorkType, int> _running = [];
    private readonly HashSet<string> _busyCorpora = new(StringComparer.Ordinal);

    /// <summary>
    /// Tickets currently out, so that releasing twice cannot free two slots.
    ///
    /// A count alone is not enough. Release, a replacement starts in the freed slot,
    /// then a stale second release lands and decrements the count that now belongs to
    /// the replacement — admitting work past the limit. Releasing twice back to back is
    /// harmless and hides it, which is why this is per take.
    ///
    /// Per TICKET rather than per (type, key): the same key may be taken again after the
    /// first finishes, and keying on it would let a stale release of the first free the
    /// second's slot.
    /// </summary>
    private readonly HashSet<long> _inFlight = [];

    private long _nextTicket;

    private readonly Lock _gate = new();

    /// <summary>
    /// Raised when something may have become eligible: an enqueue, or a completion that
    /// freed a slot. Workers wait on this rather than polling, because a filtered take
    /// cannot block the way reading a channel does.
    /// </summary>
    private readonly SemaphoreSlim _signal = new(0);

    private static Dictionary<WorkType, int> Limits(IndexingOptions indexing) => new()
    {
        // Discovery is a walk and a few hundred rows. Two is enough that one slow mount
        // does not hold up every other corpus's inventory, and more would contend for
        // the catalogue's single writer to finish a two-second job marginally sooner.
        [WorkType.Sweep] = Math.Max(1, indexing.MaxConcurrentSweeps),

        [WorkType.Index] = Math.Max(1, indexing.MaxConcurrentCorpora),

        // Lower on purpose. A rebuild re-embeds every file it walks, so several at once
        // saturate the embedding endpoint and make each other slow without finishing any
        // sooner. Incremental passes keep their own slots while one runs.
        [WorkType.Rebuild] = Math.Max(1, indexing.MaxConcurrentRebuilds),
    };

    /// <summary>Total slots across every type: how many workers are worth running.</summary>
    public int TotalSlots => _limits.Values.Sum();

    public int LimitFor(WorkType type) => _limits[type];

    /// <summary>
    /// Queue an item, unless the identical one is already waiting.
    ///
    /// On (type, KEY) rather than (type, corpus), and the difference is load-bearing. A
    /// sweep's key is its corpus, so a second request for one already waiting is the
    /// same sweep and folds in. An index job's key is the job id, and a corpus with two
    /// chunk sets has one job per set — matching on the corpus would have silently
    /// dropped the second, leaving a set that was asked for and never built.
    ///
    /// Pending only. A RUNNING item has already read what it is going to read and
    /// cannot cover work added afterwards.
    /// </summary>
    /// <returns>False when the identical item was already waiting.</returns>
    public bool Enqueue(WorkItem item)
    {
        lock (_gate)
        {
            if (_pending.Exists(p => p.Type == item.Type
                                  && string.Equals(p.Key, item.Key, StringComparison.Ordinal)))
                return false;

            _pending.Add(item);
        }

        _signal.Release();
        return true;
    }

    /// <summary>
    /// Take the next item that can run now, or null when nothing is eligible.
    ///
    /// FIFO among the eligible: the list is walked in order and the first item that
    /// passes both tests is taken, so waiting longest still wins wherever two items are
    /// equally able to run.
    /// </summary>
    public WorkLease? TryTake()
    {
        lock (_gate)
        {
            for (var i = 0; i < _pending.Count; i++)
            {
                var item = _pending[i];

                if (_busyCorpora.Contains(item.CorpusId)) continue;
                if (_running.GetValueOrDefault(item.Type) >= _limits[item.Type]) continue;

                _pending.RemoveAt(i);
                _running[item.Type] = _running.GetValueOrDefault(item.Type) + 1;
                _busyCorpora.Add(item.CorpusId);

                var ticket = ++_nextTicket;
                _inFlight.Add(ticket);
                return new WorkLease(item, ticket);
            }

            return null;
        }
    }

    /// <summary>
    /// Release the slot and the corpus, and wake a worker in case that made something
    /// eligible. Must be called for every item <see cref="TryTake"/> returned.
    /// </summary>
    public void Completed(WorkLease lease)
    {
        lock (_gate)
        {
            // A ticket is spent once. Releasing twice is then inert rather than quietly
            // over-admitting, and a stale release cannot free a slot or a corpus that
            // now belongs to a later take of the same work.
            if (!_inFlight.Remove(lease.Ticket)) return;

            var item = lease.Item;
            var count = _running.GetValueOrDefault(item.Type);
            if (count > 0) _running[item.Type] = count - 1;
            _busyCorpora.Remove(item.CorpusId);
        }

        _signal.Release();
    }

    /// <summary>
    /// Wait until something may be takeable. Returning does not promise an item: another
    /// worker may take it first, and a completion wakes one worker whether or not the
    /// freed slot helps the item it was woken for. The caller loops.
    /// </summary>
    public Task WaitAsync(CancellationToken ct) => _signal.WaitAsync(ct);

    /// <summary>For the UI and tests: what is waiting, and what is running.</summary>
    public (int Pending, int Running) Depth()
    {
        lock (_gate) return (_pending.Count, _running.Values.Sum());
    }

    public void Dispose() => _signal.Dispose();
}
