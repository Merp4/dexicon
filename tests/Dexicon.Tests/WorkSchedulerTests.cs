using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The one rule the scheduler enforces: take the first pending item whose TYPE has a
/// free slot and whose CORPUS has nothing running.
///
/// Everything the old arrangement did with two channels, two workers and a
/// fifteen-second requeue timer is meant to fall out of that, so these assert the
/// properties rather than the mechanism: nothing starves, expensive work is capped
/// separately, and a busy corpus is skipped rather than retried.
/// </summary>
public sealed class WorkSchedulerTests
{
    private static WorkScheduler With(int sweeps = 2, int index = 4, int rebuilds = 1) =>
        new(Options.Create(new DexiconOptions
        {
            Indexing = new IndexingOptions
            {
                MaxConcurrentSweeps = sweeps,
                MaxConcurrentCorpora = index,
                MaxConcurrentRebuilds = rebuilds,
            },
        }));

    private static WorkItem Index(string corpus) => new(WorkType.Index, corpus, $"job-{corpus}");
    private static WorkItem Rebuild(string corpus) => new(WorkType.Rebuild, corpus, $"rebuild-{corpus}");
    private static WorkItem Sweep(string corpus) => new(WorkType.Sweep, corpus, corpus);

    [Fact]
    public void TakesInOrderAmongTheEligible()
    {
        using var s = With();
        s.Enqueue(Index("a"));
        s.Enqueue(Index("b"));

        s.TryTake()!.CorpusId.ShouldBe("a");
        s.TryTake()!.CorpusId.ShouldBe("b");
    }

    /// <summary>
    /// The fairness the whole design rests on, and the reason no rotation is kept.
    ///
    /// One corpus queuing several jobs must not take several slots: its second is
    /// ineligible while its first runs, so the next corpus is served instead. Without
    /// this, four jobs on one corpus fill every slot and a corpus with one small job
    /// waits for all of them.
    /// </summary>
    [Fact]
    public void OneCorpusCannotOccupyEverySlot()
    {
        using var s = With(index: 4);
        s.Enqueue(new WorkItem(WorkType.Index, "busy", "job-1"));
        s.Enqueue(new WorkItem(WorkType.Index, "busy", "job-2"));
        s.Enqueue(Index("other"));

        var first = s.TryTake();
        var second = s.TryTake();

        first!.CorpusId.ShouldBe("busy");
        second!.CorpusId.ShouldBe("other", "the busy corpus's second job must not take a slot");
        s.TryTake().ShouldBeNull("nothing else is eligible while both corpora are running");
    }

    [Fact]
    public void TheSecondJobBecomesEligibleOnceTheCorpusIsFree()
    {
        using var s = With(index: 4);
        s.Enqueue(new WorkItem(WorkType.Index, "busy", "job-1"));
        s.Enqueue(new WorkItem(WorkType.Index, "busy", "job-2"));

        var running = s.TryTake()!;
        s.TryTake().ShouldBeNull();

        s.Completed(running);

        s.TryTake()!.Key.ShouldBe("job-2");
    }

    /// <summary>
    /// A rebuild re-embeds everything it walks, so its limit is lower than an
    /// incremental pass's. The point is that the cap is per type: incremental work keeps
    /// its own slots while a rebuild holds the only one it has.
    /// </summary>
    [Fact]
    public void ExpensiveWorkIsCappedWithoutBlockingTheCheapKind()
    {
        using var s = With(index: 4, rebuilds: 1);
        s.Enqueue(Rebuild("a"));
        s.Enqueue(Rebuild("b"));
        s.Enqueue(Index("c"));

        s.TryTake()!.Type.ShouldBe(WorkType.Rebuild);

        var next = s.TryTake();
        next!.Type.ShouldBe(WorkType.Index, "the second rebuild is over its limit; the index job is not");
        next.CorpusId.ShouldBe("c");
    }

    /// <summary>
    /// What D-32 bought, kept: a sweep never waits behind indexing. Index work saturating
    /// its own limit cannot occupy a sweep's slots.
    /// </summary>
    [Fact]
    public void IndexingAtItsLimitDoesNotDelayASweep()
    {
        using var s = With(sweeps: 2, index: 1);
        s.Enqueue(Index("a"));
        s.Enqueue(Index("b"));
        s.Enqueue(Sweep("c"));

        s.TryTake()!.Type.ShouldBe(WorkType.Index);

        var next = s.TryTake();
        next!.Type.ShouldBe(WorkType.Sweep, "a sweep has its own slots and must not queue behind indexing");
    }

    /// <summary>
    /// A corpus with two chunk sets has one job per set, and both must survive queuing.
    ///
    /// The first version of this scheduler coalesced on (type, corpus), which dropped
    /// the second silently: the set was asked for, reported queued, and never built.
    /// That is the same class of defect as coalescing onto a RUNNING job, which the job
    /// queue already refuses to do for the same reason.
    /// </summary>
    [Fact]
    public void TwoJobsForOneCorpusSurviveQueuing()
    {
        using var s = With(index: 4);

        s.Enqueue(new WorkItem(WorkType.Index, "c", "job-default-set")).ShouldBeTrue();
        s.Enqueue(new WorkItem(WorkType.Index, "c", "job-backfill-set")).ShouldBeTrue();

        s.Depth().Pending.ShouldBe(2, "one job per chunk set, neither folded into the other");

        var first = s.TryTake()!;
        s.Completed(first);
        s.TryTake()!.Key.ShouldNotBe(first.Key, "the other set's job must still be there");
    }

    [Fact]
    public void AnEquivalentItemIsNotQueuedTwice()
    {
        using var s = With();

        s.Enqueue(Sweep("a")).ShouldBeTrue();
        s.Enqueue(Sweep("a")).ShouldBeFalse("a sweep already waiting covers whatever it finds when it runs");

        s.Depth().Pending.ShouldBe(1);
    }

    /// <summary>
    /// Coalescing is on PENDING only. Once an item is running it has read what it is
    /// going to read, so a later request is new work and has to queue.
    /// </summary>
    [Fact]
    public void AnItemThatIsRunningDoesNotAbsorbTheNextRequest()
    {
        using var s = With();
        s.Enqueue(Sweep("a"));
        var running = s.TryTake()!;

        s.Enqueue(Sweep("a")).ShouldBeTrue("the running sweep cannot cover what was asked for after it started");

        s.Depth().ShouldBe((Pending: 1, Running: 1));
    }

    [Fact]
    public void CompletingReleasesTheSlotForTheSameType()
    {
        using var s = With(rebuilds: 1);
        s.Enqueue(Rebuild("a"));
        s.Enqueue(Rebuild("b"));

        var first = s.TryTake()!;
        s.TryTake().ShouldBeNull();

        s.Completed(first);

        s.TryTake()!.CorpusId.ShouldBe("b");
    }

    [Fact]
    public async Task AWorkerWaitingIsWokenWhenWorkArrives()
    {
        using var s = With();
        var waiting = s.WaitAsync(CancellationToken.None);

        waiting.IsCompleted.ShouldBeFalse("nothing is queued yet");

        s.Enqueue(Index("a"));

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        s.TryTake().ShouldNotBeNull();
    }

    /// <summary>
    /// Releasing the same item twice must not free a slot twice.
    ///
    /// The worker did exactly that: its cancellation catch released, and so did its
    /// `finally`. The first release wakes another worker, so a replacement could start
    /// and then have ITS type count decremented and ITS corpus marked free by the second
    /// call — admitting work past the limit, quietly and only under shutdown.
    ///
    /// The pool no longer double-releases. This holds the scheduler to the same
    /// guarantee, since it is the thing whose counts were corrupted.
    /// </summary>
    [Fact]
    public void ReleasingTheSameItemTwiceDoesNotFreeASlotTwice()
    {
        using var s = With(rebuilds: 1);
        s.Enqueue(Rebuild("a"));
        s.Enqueue(Rebuild("b"));
        s.Enqueue(Rebuild("c"));

        var first = s.TryTake()!;

        // The order that matters, and the one the pool produced: release, a replacement
        // starts in the freed slot, THEN the stale second release lands. Releasing twice
        // back to back proves nothing — the count cannot go below zero, so the guard
        // already absorbs it.
        s.Completed(first);
        var replacement = s.TryTake();
        replacement.ShouldNotBeNull();

        s.Completed(first);   // the bug: the first item released a second time

        s.TryTake().ShouldBeNull(
            "the replacement still holds the only rebuild slot; the stale release must not free it");
    }

    [Fact]
    public void TotalSlotsIsWhatTheLimitsAddUpTo()
    {
        using var s = With(sweeps: 2, index: 4, rebuilds: 1);
        s.TotalSlots.ShouldBe(7);
    }
}
