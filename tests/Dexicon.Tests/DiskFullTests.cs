using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A job that fails while the data disk is full cannot record that it failed, and what that
/// leaves behind has to be recovered without a restart.
///
/// Observed once: for about four minutes every catalogue write was refused. Two jobs in
/// flight logged their failure, could not save it, and could not release their lease. Both
/// rows stayed Running and both corpora stayed Indexing, which the scheduled refresh skips,
/// so nothing moved for six hours. These run a real pass through <see cref="IndexingHarness"/>
/// with the disk opened at the point the pass fails, and read the catalogue afterwards.
/// </summary>
public sealed class DiskFullTests
{
    private static async Task<(IndexingHarness Harness, DiskFull Disk)> StartAsync()
    {
        var disk = new DiskFull();
        var harness = await IndexingHarness.StartAsync(disk, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        return (harness, disk);
    }

    [Fact]
    public async Task A_failure_is_recorded_when_the_disk_recovers_within_the_retries()
    {
        var (harness, disk) = await StartAsync();
        await using var _ = harness;
        var logs = new RecordingLoggerFactory();

        // The pass fails once the job is already saved as running, and the next two saves are
        // refused: both are the failure's own, which has nothing else to write them.
        harness.Vectors.OnEnsureCollection = () =>
        {
            disk.RefuseSaves(2);
            throw DiskFull.Refusal();
        };

        var job = await harness.RunIndexAsync(log: logs.CreateLogger<CorpusIndexer>());

        disk.SavesRefused.ShouldBe(2, "the window has to have been opened, and both refusals were the failure's own save");
        job.State.ShouldBe(JobState.Failed);

        await using var db = harness.NewContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync();
        row.State.ShouldBe(JobState.Failed, "recorded, not left running");
        row.FinishedUtc.ShouldNotBeNull();
        row.Error.ShouldNotBeNull().ShouldContain("database or disk is full");

        var corpus = await db.Corpora.AsNoTracking().SingleAsync();
        corpus.State.ShouldBe(CorpusState.Degraded);
        corpus.HeldBy.ShouldBeNull("released, as it is when the outcome is recorded");
        (await db.ChunkSets.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Degraded);

        logs.Lines.ShouldContain(l => l.Contains("Recorded the outcome of job")
                                      && l.Contains($"attempt 3 of {CorpusIndexer.SaveAttempts}"));
    }

    [Fact]
    public async Task A_failure_that_cannot_be_recorded_is_tried_a_bounded_number_of_times_and_says_what_it_left()
    {
        var (harness, disk) = await StartAsync();
        await using var _ = harness;
        var logs = new RecordingLoggerFactory();

        harness.Vectors.OnEnsureCollection = () =>
        {
            disk.Fill();
            throw DiskFull.Refusal();
        };

        var thrown = await Should.ThrowAsync<SqliteException>(
            () => harness.RunIndexAsync(log: logs.CreateLogger<CorpusIndexer>()));
        disk.Free();

        // The worker is told, as before.
        thrown.Message.ShouldContain("database or disk is full");
        disk.SavesRefused.ShouldBe(CorpusIndexer.SaveAttempts, "the first try and the retries, and no more");
        CorpusIndexer.SaveAttempts.ShouldBeGreaterThan(1, "one attempt is not a retry");

        await using var db = harness.NewContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync();
        row.State.ShouldBe(JobState.Running, "nothing could be written");

        var gaveUp = logs.Lines.Single(l => l.Contains("Gave up recording the outcome"));
        gaveUp.ShouldContain(row.Id);
        gaveUp.ShouldContain($"after {CorpusIndexer.SaveAttempts} attempts");
        gaveUp.ShouldContain("the job reads Running in the catalogue");
        gaveUp.ShouldContain("The next start marks a job that reads Queued or Running failed");
    }

    [Fact]
    public async Task The_retries_wait_between_attempts_and_the_wait_doubles()
    {
        // 20 + 40 + 80 + 160 ms across the four retries. A lower bound only: a delay never
        // returns early, and an upper bound would fail on a loaded machine.
        var (harness, disk) = await StartAsync();
        await using var _ = harness;
        harness.Vectors.OnEnsureCollection = () =>
        {
            disk.Fill();
            throw DiskFull.Refusal();
        };

        var waited = System.Diagnostics.Stopwatch.StartNew();
        await Should.ThrowAsync<SqliteException>(
            () => harness.RunIndexAsync(saveRetryDelay: TimeSpan.FromMilliseconds(20)));
        waited.Stop();

        disk.SavesRefused.ShouldBe(CorpusIndexer.SaveAttempts);
        waited.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(250);
    }

    [Fact]
    public async Task A_job_that_never_reached_running_is_reported_as_still_queued()
    {
        // Failing before the Running save leaves the row Queued, which counts as work and
        // holds a later request out, so the log has to say what clears it.
        var (harness, disk) = await StartAsync();
        await using var _ = harness;
        var logs = new RecordingLoggerFactory();

        await Should.ThrowAsync<SqliteException>(() => harness.RunIndexAsync(
            log: logs.CreateLogger<CorpusIndexer>(), beforePass: disk.Fill));
        disk.Free();

        disk.Fired.ShouldBeTrue("the window has to have been opened");

        await using var db = harness.NewContext();
        (await db.Jobs.AsNoTracking().SingleAsync()).State.ShouldBe(JobState.Queued);

        var gaveUp = logs.Lines.Single(l => l.Contains("Gave up recording the outcome"));
        gaveUp.ShouldContain("the job reads Queued in the catalogue");
        gaveUp.ShouldContain("The next start marks a job that reads Queued or Running failed");
    }

    [Fact]
    public async Task A_job_that_cannot_take_the_corpus_is_recorded_as_failed_and_does_not_strand_later_requests()
    {
        var (harness, disk) = await StartAsync();
        await using var _ = harness;

        // The window covers the corpus table only, so the lease claim (an update on it) is
        // refused while the job's own row can still be written. A job that never held the
        // corpus does not write the corpus or its sets, and had its outcome dropped with them:
        // the row stayed Queued with no reason and no finish time.
        var job = await harness.RunIndexAsync(beforePass: () => disk.RefuseWritesTo("corpora"));
        disk.Free();

        disk.Fired.ShouldBeTrue("the window has to have been opened");
        disk.WritesRefused.ShouldBe(1, "the lease claim, and no later attempt to write the corpus");
        job.State.ShouldBe(JobState.Failed);

        await using var db = harness.NewContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync();
        row.State.ShouldBe(JobState.Failed, "recorded, not left queued");
        row.FinishedUtc.ShouldNotBeNull();
        row.StartedUtc.ShouldBeNull("it never got as far as running");
        row.Error.ShouldNotBeNull().ShouldContain("database or disk is full");

        // Not the job's to write, and not written.
        var corpus = await db.Corpora.AsNoTracking().SingleAsync();
        corpus.State.ShouldBe(CorpusState.Ready);
        corpus.HeldBy.ShouldBeNull();
        (await db.ChunkSets.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Ready);

        // A Queued row is what a later request coalesces onto, and nothing puts such a job
        // back on the scheduler, so it would never run.
        var queue = new IndexJobQueue(db, new WorkScheduler(harness.Settings),
            NullLogger<IndexJobQueue>.Instance);
        var next = await queue.EnqueueAsync(IndexingHarness.CorpusId, JobKind.Refresh);

        next.Id.ShouldNotBe(row.Id, "not coalesced onto the failed job");
        next.State.ShouldBe(JobState.Queued);
        (await db.Jobs.AsNoTracking().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_job_that_cannot_take_the_corpus_keeps_its_outcome_through_refused_saves()
    {
        var (harness, disk) = await StartAsync();
        await using var _ = harness;
        var logs = new RecordingLoggerFactory();

        // The two saves that follow are the failure's own. The job stays tracked between
        // them, so the third records what the first would have.
        var job = await harness.RunIndexAsync(log: logs.CreateLogger<CorpusIndexer>(), beforePass: () =>
        {
            disk.RefuseWritesTo("corpora");
            disk.RefuseSaves(2);
        });
        disk.Free();

        disk.SavesRefused.ShouldBe(2, "the window has to have been opened");
        disk.WritesRefused.ShouldBe(1, "the lease claim, and no later attempt to write the corpus");

        await using var db = harness.NewContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync();
        row.Id.ShouldBe(job.Id);
        row.State.ShouldBe(JobState.Failed);
        row.FinishedUtc.ShouldNotBeNull();
        row.Error.ShouldNotBeNull().ShouldContain("database or disk is full");

        logs.Lines.ShouldContain(l => l.Contains("Recorded the outcome of job")
                                      && l.Contains($"attempt 3 of {CorpusIndexer.SaveAttempts}"));
    }

    [Fact]
    public async Task A_job_that_loses_its_lease_records_its_outcome_and_leaves_the_corpus_as_it_was()
    {
        // Two sets, so the pass reaches a point where it has decided the corpus is
        // Unavailable (a source is missing) and has not yet saved it. The second set's
        // collection is where the full disk is opened, and where the pass waits for the
        // renewal it refuses to cancel it.
        var disk = new DiskFull();
        await using var harness = await IndexingHarness.StartAsync(disk, "notes", "gone");
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        Directory.Delete(harness.SourceDirectories[1]);

        var logs = new RecordingLoggerFactory();
        var leases = harness.NewLeases(renew: TimeSpan.FromMilliseconds(50));

        var collections = 0;
        var cancelled = false;
        harness.Vectors.OnEnsureCollectionAsync = async ct =>
        {
            if (++collections < 2) return;

            disk.RefuseWritesTo("corpora");
            using var give = CancellationTokenSource.CreateLinkedTokenSource(ct);
            give.CancelAfter(TimeSpan.FromSeconds(10));
            try { await Task.Delay(Timeout.Infinite, give.Token); }
            catch (OperationCanceledException) { /* the lease was lost, or the wait ran out */ }
            cancelled = ct.IsCancellationRequested;
        };

        var job = await harness.RunIndexAsync(log: logs.CreateLogger<CorpusIndexer>(), leases: leases);
        disk.Free();

        disk.Fired.ShouldBeTrue("the window has to have been opened");
        cancelled.ShouldBeTrue("a refused renewal has to have cost the pass its lease");
        job.State.ShouldBe(JobState.Failed);
        job.Error.ShouldNotBeNull().ShouldContain("is not available", customMessage: "the pass did decide the corpus was Unavailable");

        // The job says how it ended. It does not write the corpus: whoever holds it now does,
        // and the row keeps the outcome of the last pass. It never held Indexing, so there is
        // nothing for a job that stops to leave behind.
        await using var db = harness.NewContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync();
        row.State.ShouldBe(JobState.Failed, "recorded, not left running");
        row.FinishedUtc.ShouldNotBeNull();
        row.Error.ShouldNotBeNull().ShouldContain("is not available");

        var corpus = await db.Corpora.AsNoTracking().SingleAsync();
        corpus.State.ShouldBe(CorpusState.Ready, "not what this pass decided");
        corpus.HeldBy.ShouldBe($"index-{row.Id}", "a hold that was lost is not released");
        (await db.ChunkSets.AsNoTracking().ToListAsync()).ShouldAllBe(s => s.State == CorpusState.Ready);

        logs.Lines.ShouldContain(l => l.Contains(row.Id) && l.Contains("lost its lease"));

        // And it does not read as indexing: the job is finished, whatever lease is left.
        var activity = await IndexingActivity.ReadAsync(db, [corpus.Id]);
        activity.Of(corpus).ShouldBe(CorpusState.Ready);
    }

    [Fact]
    public async Task A_job_left_running_by_a_full_disk_stops_counting_once_its_lease_has_lapsed()
    {
        var (harness, disk) = await StartAsync();
        await using var _ = harness;

        harness.Vectors.OnEnsureCollection = () =>
        {
            disk.Fill();
            throw DiskFull.Refusal();
        };
        await Should.ThrowAsync<SqliteException>(() => harness.RunIndexAsync());
        disk.Free();

        disk.Fired.ShouldBeTrue("the window has to have been opened");

        // What the outage left, which is the state found in production: the job reads
        // Running, and the lease is still named for it because its release was refused as well.
        await using var db = harness.NewContext();
        var stuck = await db.Jobs.AsNoTracking().SingleAsync();
        stuck.State.ShouldBe(JobState.Running);
        var corpus = await db.Corpora.AsNoTracking().SingleAsync();
        corpus.HeldBy.ShouldBe($"index-{stuck.Id}");
        corpus.State.ShouldBe(CorpusState.Ready, "the job never wrote Indexing, so it cannot have left it");

        // The lease was renewed moments ago. A job that has stopped cannot be told from one that
        // is slow until the lease has had time to lapse, so it still counts.
        (await IndexingActivity.ReadAsync(db, [corpus.Id])).Of(corpus).ShouldBe(CorpusState.Indexing);
        await harness.RunRefreshTickAsync();
        (await db.Jobs.CountAsync(j => j.State == JobState.Queued)).ShouldBe(0, "a corpus a job holds is not refreshed");

        // The time a lease takes to lapse, without waiting it out.
        await db.Corpora.ExecuteUpdateAsync(u => u
            .SetProperty(c => c.HeldUntilUtc, DateTime.UtcNow.AddMinutes(-1)));

        (await IndexingActivity.ReadAsync(db, [corpus.Id])).Of(corpus)
            .ShouldBe(CorpusState.Ready, "nothing renews the lease, so the job no longer counts and the outcome stands");

        // The refresh that was held out for six hours now runs, with nothing repaired first.
        await harness.RunRefreshTickAsync();
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh && j.State == JobState.Queued))
            .ShouldBe(1, "the corpus is refreshed by the next tick");

        // The stopped job's row is not rewritten. Nothing reads it as work, and the next start
        // marks it failed.
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == stuck.Id)).State.ShouldBe(JobState.Running);
    }
}
