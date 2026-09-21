namespace Dexicon.Core.Indexing;

/// <summary>
/// Asking for discovery. The lane it used to own is now a work type with its own limit,
/// which is the same guarantee by a smaller mechanism: a sweep never waits behind an
/// index job, because index work cannot occupy a sweep's slots.
///
/// That guarantee is the point of D-32. A corpus added while another indexed read as
/// empty for as long as that took, which on a library of 1,800 PDFs is hours.
///
/// Coalesced per corpus, by the scheduler. A sweep that has not started yet covers
/// whatever the tree looks like when it does, so a second request for the same corpus is
/// the same sweep; queuing both would walk the tree twice for one answer. That is safe
/// here in a way it is not for indexing, where a queued job and a running one differ,
/// because a sweep takes the lease and a duplicate would be turned away at the door.
/// </summary>
public sealed class SweepQueue(WorkScheduler scheduler)
{
    /// <summary>
    /// Ask for a sweep. Returns false when one is already waiting for this corpus, which
    /// is not a failure: the sweep already queued will see whatever is there when it runs.
    ///
    /// The coalescing lives in the scheduler now, which already refuses a second pending
    /// item for the same type and corpus. It used to be a dictionary here beside a
    /// channel, and the pair had to be kept in step by hand — a write that failed after
    /// the mark left a corpus that could never be queued again.
    /// </summary>
    public bool Enqueue(string corpusId) =>
        scheduler.Enqueue(new WorkItem(WorkType.Sweep, corpusId, corpusId));
}
