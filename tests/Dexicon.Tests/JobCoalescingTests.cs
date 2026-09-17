using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// When a new index job is folded into an existing one, and when it must not be.
///
/// Found by adding nine folders to a corpus that was already indexing. Each addition
/// queues a refresh; every one of them was handed back the job that was already RUNNING,
/// which had taken its list of sources when it started. The folders added after that were
/// never walked. The corpus then reported `ready`, with no job pending, 46 files of a
/// 96-file shelf, and nothing anywhere saying half of it was missing.
///
/// The distinction is about what a job has already decided:
///   queued  — has not read the corpus yet, so new work is included for free.
///   running — has its list; anything added now is not in it and never will be.
/// </summary>
public sealed class JobCoalescingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private IndexJobQueue _queue = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Tenants.Add(new Tenant { Id = "t", DisplayName = "t", CreatedUtc = DateTime.UtcNow });
        _db.Corpora.Add(new Corpus { Id = "c", TenantId = "t", Name = "c", CreatedUtc = DateTime.UtcNow });
        // A job's ChunkSetId is a foreign key, so the set it names has to exist.
        _db.ChunkSets.Add(new ChunkSet
        {
            Id = "set-2", CorpusId = "c", Name = "second", EmbeddingModel = "embeddinggemma",
            CollectionName = "dexicon_second", BoundaryMode = "blank-line",
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        _queue = new IndexJobQueue(_db, NullLogger<IndexJobQueue>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<IndexJob> Existing(JobState state)
    {
        var job = new IndexJob
        {
            Id = Ulid.NewUlid().ToString(), CorpusId = "c", ChunkSetId = null,
            Kind = JobKind.Refresh, State = state, QueuedUtc = DateTime.UtcNow.AddMinutes(-1),
            StartedUtc = state == JobState.Running ? DateTime.UtcNow.AddMinutes(-1) : null,
        };
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task A_queued_job_absorbs_the_new_request()
    {
        // It has not looked at the corpus yet, so it will see the new source when it runs.
        // Queuing a second job here would index the same thing twice.
        var queued = await Existing(JobState.Queued);

        var result = await _queue.EnqueueAsync("c", JobKind.Refresh);

        result.Id.ShouldBe(queued.Id);
        (await _db.Jobs.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_running_job_does_not_absorb_it()
    {
        // The bug. A running job's source list is already fixed; handing back its id says
        // "your new folder is covered" when it is not, and nothing ever revisits it.
        var running = await Existing(JobState.Running);

        var result = await _queue.EnqueueAsync("c", JobKind.Refresh);

        result.Id.ShouldNotBe(running.Id);
        result.State.ShouldBe(JobState.Queued);
        (await _db.Jobs.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Repeated_requests_during_one_run_still_queue_only_one_job()
    {
        // The reason coalescing exists at all. Nine sources added while a job runs must
        // produce one follow-up job, not nine — the first creates it, the rest fold in.
        await Existing(JobState.Running);

        var first = await _queue.EnqueueAsync("c", JobKind.Refresh);
        for (var i = 0; i < 8; i++)
            (await _queue.EnqueueAsync("c", JobKind.Refresh)).Id.ShouldBe(first.Id);

        (await _db.Jobs.CountAsync()).ShouldBe(2); // the running one, and one follow-up
    }

    [Fact]
    public async Task A_finished_job_never_absorbs_anything()
    {
        await Existing(JobState.Succeeded);

        var result = await _queue.EnqueueAsync("c", JobKind.Refresh);

        result.State.ShouldBe(JobState.Queued);
        (await _db.Jobs.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Jobs_for_different_chunk_sets_do_not_collide()
    {
        // Pre-existing behaviour worth keeping: backfilling a new set must not be handed
        // the live set's refresh, report success, and build nothing.
        var queuedForDefault = await Existing(JobState.Queued);

        var forOtherSet = await _queue.EnqueueAsync("c", JobKind.Refresh, chunkSetId: "set-2");

        forOtherSet.Id.ShouldNotBe(queuedForDefault.Id);
    }
}
