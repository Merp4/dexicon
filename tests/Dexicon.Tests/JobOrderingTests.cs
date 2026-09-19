using Dexicon.Core.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The jobs list is how both the UI and an operator answer "what is happening right
/// now". Ordering it on StartedUtc, with nulls coalesced to MaxValue so queued work
/// floats to the top, appeared correct but was not: a job that failed before it ever
/// started also has a null StartedUtc, so two long-dead failures sat permanently above
/// the job that was actually running.
///
/// It is a quiet bug. Nothing errors; the list is simply wrong, and anything that reads
/// the first entry to find "the current job" gets a stale answer with full confidence.
/// </summary>
public sealed class JobOrderingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        // Jobs belong to a corpus; without one every insert trips the foreign key.
        _db.Corpora.Add(new Corpus
        {
            Id = "c", Name = "c",
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>The production query, as <c>GET /api/jobs</c> runs it.</summary>
    private Task<List<IndexJob>> NewestFirst() =>
        _db.Jobs.OrderByDescending(j => j.QueuedUtc).ThenByDescending(j => j.Id).ToListAsync();

    [Fact]
    public async Task ARunningJobOutranksAnOlderFailureThatNeverStarted()
    {
        var t0 = new DateTime(2026, 9, 16, 17, 0, 0, DateTimeKind.Utc);

        // Queued first, failed on restart without ever starting: StartedUtc stays null.
        Add("old-failure", queued: t0, started: null, state: JobState.Failed,
            error: "Interrupted — Dexicon restarted while this job was running.");
        Add("running-now", queued: t0.AddMinutes(10), started: t0.AddMinutes(10), state: JobState.Running);

        var jobs = await NewestFirst();

        jobs[0].Id.ShouldBe("running-now", "the live job must not sit below a dead one");
        jobs[1].Id.ShouldBe("old-failure");
    }

    [Fact]
    public async Task AFreshlyQueuedJobIsListedFirst()
    {
        // The other half: work that has not started yet is the newest thing and belongs
        // at the top, which was the original and correct intent behind the null handling.
        var t0 = new DateTime(2026, 9, 16, 17, 0, 0, DateTimeKind.Utc);

        Add("finished", queued: t0, started: t0, finished: t0.AddMinutes(1), state: JobState.Succeeded);
        Add("running", queued: t0.AddMinutes(5), started: t0.AddMinutes(5), state: JobState.Running);
        Add("queued", queued: t0.AddMinutes(9), started: null, state: JobState.Queued);

        (await NewestFirst()).Select(j => j.Id).ShouldBe(["queued", "running", "finished"]);
    }

    [Fact]
    public async Task JobsQueuedInTheSameInstantFallBackToUlidOrder()
    {
        // SQLite stores sub-second precision, but two enqueues can still land on one
        // tick. The ULID id is monotonic, so the tie-break is still creation order
        // rather than whatever the database happens to return.
        var t = new DateTime(2026, 9, 16, 17, 0, 0, DateTimeKind.Utc);
        Add("01AAAA", queued: t, started: null, state: JobState.Queued);
        Add("01BBBB", queued: t, started: null, state: JobState.Queued);

        (await NewestFirst()).Select(j => j.Id).ShouldBe(["01BBBB", "01AAAA"]);
    }

    [Fact]
    public async Task QueuedUtcIsReadBackAsUtc()
    {
        Add("j", queued: DateTime.UtcNow, started: null, state: JobState.Queued);
        _db.ChangeTracker.Clear();

        (await _db.Jobs.SingleAsync()).QueuedUtc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    private void Add(string id, DateTime queued, DateTime? started, JobState state,
        DateTime? finished = null, string? error = null)
    {
        _db.Jobs.Add(new IndexJob
        {
            Id = id,
            CorpusId = "c",
            Kind = JobKind.Refresh,
            State = state,
            QueuedUtc = queued,
            StartedUtc = started,
            FinishedUtc = finished,
            Error = error,
        });
        _db.SaveChanges();
    }
}
