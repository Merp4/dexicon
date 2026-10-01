using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A job the catalogue says is Running is repaired when nothing is running it, and only then.
///
/// Whether anything is running it is read from the corpus lease, which a live job renews and
/// which lapses when its holder stops. A job that has run for six hours and still holds its
/// lease is alive; one that started a minute ago and whose lease has lapsed is not. Age is
/// never the test, because indexing a library takes hours.
///
/// Each case seeds the state a failed outcome save leaves, rather than producing it with a
/// disk (<see cref="DiskFullTests"/> does that once, end to end), so the rules about which
/// rows are touched can be shown one at a time.
/// </summary>
public sealed class StuckJobRepairTests
{
    private const string StuckId = "stuck";

    private static async Task<(IndexingHarness Harness, DiskFull Disk)> StartAsync(int sets = 1)
    {
        var disk = new DiskFull();
        var harness = await IndexingHarness.StartAsync(disk, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets);
        return (harness, disk);
    }

    /// <summary>
    /// The state a failed outcome save leaves: a job Running for hours, its corpus and every
    /// set Indexing, and the corpus lease named for the job and lapsed unless said otherwise.
    /// </summary>
    private static async Task SeedStuckAsync(
        IndexingHarness harness, string? chunkSetId = null, string? heldBy = $"index-{StuckId}",
        TimeSpan? leaseLeft = null, string? error = null, string corpusId = IndexingHarness.CorpusId)
    {
        await using var db = harness.NewContext();
        var started = DateTime.UtcNow.AddHours(-5);
        db.Jobs.Add(new IndexJob
        {
            Id = corpusId == IndexingHarness.CorpusId ? StuckId : $"stuck-{corpusId}",
            CorpusId = corpusId, ChunkSetId = chunkSetId, Kind = JobKind.Refresh, State = JobState.Running,
            Phase = "embed", FilesTotal = 10, FilesDone = 3, Error = error,
            QueuedUtc = started, StartedUtc = started,
        });
        await db.SaveChangesAsync();

        DateTime? until = heldBy is null ? null : DateTime.UtcNow + (leaseLeft ?? TimeSpan.FromMinutes(-5));
        await db.Corpora.Where(c => c.Id == corpusId).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.State, CorpusState.Indexing)
            .SetProperty(c => c.HeldBy, heldBy)
            .SetProperty(c => c.HeldUntilUtc, until));
        await db.ChunkSets.Where(s => s.CorpusId == corpusId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, CorpusState.Indexing));
    }

    private static async Task FinishedAsync(
        IndexingHarness harness, string id, JobState state, TimeSpan ago, string? chunkSetId = null)
    {
        await using var db = harness.NewContext();
        var at = DateTime.UtcNow - ago;
        db.Jobs.Add(new IndexJob
        {
            Id = id, CorpusId = IndexingHarness.CorpusId, ChunkSetId = chunkSetId, Kind = JobKind.Refresh,
            State = state, QueuedUtc = at, StartedUtc = at, FinishedUtc = at,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(JobState.Succeeded, CorpusState.Ready)]
    [InlineData(JobState.Cancelled, CorpusState.Ready)]
    [InlineData(JobState.Failed, CorpusState.Degraded)]
    [InlineData(JobState.Degraded, CorpusState.Degraded)]
    public async Task The_corpus_returns_to_what_its_most_recent_finished_job_says(
        JobState last, CorpusState expected)
    {
        var (harness, _) = await StartAsync();
        await using var _ = harness;

        // An older job that says the opposite, so it is the most recent that decides.
        await FinishedAsync(harness, "older",
            expected == CorpusState.Ready ? JobState.Failed : JobState.Succeeded, TimeSpan.FromHours(3));
        await FinishedAsync(harness, "last", last, TimeSpan.FromHours(1));
        await SeedStuckAsync(harness);

        await using var db = harness.NewContext();
        (await harness.NewRepair(db).RepairAsync(default)).ShouldBe(1);

        (await db.Corpora.AsNoTracking().SingleAsync()).State.ShouldBe(expected);
        (await db.ChunkSets.AsNoTracking().SingleAsync()).State.ShouldBe(expected);
    }

    [Fact]
    public async Task With_no_finished_job_the_corpus_is_degraded()
    {
        // The only record is the failure being repaired.
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await SeedStuckAsync(harness);

        await using var db = harness.NewContext();
        await harness.NewRepair(db).RepairAsync(default);

        (await db.Corpora.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Degraded);
    }

    [Fact]
    public async Task A_lapsed_job_is_marked_failed_with_the_reason_and_keeps_what_it_recorded()
    {
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await SeedStuckAsync(harness, error: "Workspace path 'x' is not available.");

        await using var db = harness.NewContext();
        (await harness.NewRepair(db).RepairAsync(default)).ShouldBe(1);

        var job = await db.Jobs.AsNoTracking().SingleAsync();
        job.State.ShouldBe(JobState.Failed);
        job.Phase.ShouldBeNull();
        job.FinishedUtc.ShouldNotBeNull();
        job.Error.ShouldBe($"Workspace path 'x' is not available. {StuckJobRepair.Reason}");
        job.FilesDone.ShouldBe(3, "what it had done is still what it did");
    }

    [Fact]
    public async Task A_job_whose_corpus_is_not_held_at_all_is_repaired()
    {
        // The release succeeded and the outcome save did not: no lease, and still Running.
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await SeedStuckAsync(harness, heldBy: null);

        await using var db = harness.NewContext();

        (await harness.NewRepair(db).RepairAsync(default)).ShouldBe(1);
        (await db.Jobs.AsNoTracking().SingleAsync()).State.ShouldBe(JobState.Failed);
    }

    [Fact]
    public async Task A_job_whose_lease_is_live_is_left_alone_however_long_it_has_run()
    {
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await SeedStuckAsync(harness);

        // A holder that is renewing, which is what a job still working looks like. The job
        // row says it started five hours ago, and that is not what is read.
        await using var held = await harness.NewLeases()
            .TryAcquireAsync(IndexingHarness.CorpusId, $"index-{StuckId}", default);
        held.ShouldNotBeNull("the lapsed lease the seed left is free to take, as a job would");

        await using var db = harness.NewContext();

        (await harness.NewRepair(db).RepairAsync(default)).ShouldBe(0);

        (await db.Jobs.AsNoTracking().SingleAsync()).State.ShouldBe(JobState.Running);
        (await db.Corpora.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Indexing);
    }

    [Fact]
    public async Task A_corpus_that_is_held_does_not_hold_up_the_others()
    {
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await SeedStuckAsync(harness, leaseLeft: TimeSpan.FromMinutes(1));

        await using var db = harness.NewContext();
        db.Corpora.Add(new Corpus { Id = "corpus-2", Name = "other", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await SeedStuckAsync(harness, corpusId: "corpus-2", heldBy: "index-stuck-corpus-2");

        (await harness.NewRepair(db).RepairAsync(default)).ShouldBe(1);

        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == StuckId)).State.ShouldBe(JobState.Running);
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == "stuck-corpus-2")).State.ShouldBe(JobState.Failed);
    }

    [Fact]
    public async Task Only_the_indexing_states_are_rewritten()
    {
        // Unavailable was written for a source nobody could reach, and that is still true.
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await FinishedAsync(harness, "last", JobState.Succeeded, TimeSpan.FromHours(1));
        await SeedStuckAsync(harness);

        await using var db = harness.NewContext();
        await db.Corpora.ExecuteUpdateAsync(u => u.SetProperty(c => c.State, CorpusState.Unavailable));

        await harness.NewRepair(db).RepairAsync(default);

        (await db.Corpora.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Unavailable);
        (await db.ChunkSets.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Ready, "the set was Indexing");
    }

    [Fact]
    public async Task A_job_that_names_a_set_repairs_that_set_and_not_the_others()
    {
        // Both sets are seeded Indexing. Only one is the job's, and the other is left as it is:
        // no job explains it, so it is not this repair's to touch.
        var (harness, _) = await StartAsync(sets: 2);
        await using var _ = harness;
        await SeedStuckAsync(harness, chunkSetId: "set-2");

        await using var db = harness.NewContext();
        await harness.NewRepair(db).RepairAsync(default);

        var sets = await db.ChunkSets.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.State);
        sets["set-2"].ShouldBe(CorpusState.Degraded);
        sets["set-1"].ShouldBe(CorpusState.Indexing);
    }

    [Fact]
    public async Task A_set_returns_to_the_last_job_that_covered_it()
    {
        // A later job for the other set says nothing about this one.
        var (harness, _) = await StartAsync(sets: 2);
        await using var _ = harness;
        await FinishedAsync(harness, "set-2-ok", JobState.Succeeded, TimeSpan.FromHours(3), chunkSetId: "set-2");
        await FinishedAsync(harness, "set-1-bad", JobState.Failed, TimeSpan.FromHours(1), chunkSetId: "set-1");
        await SeedStuckAsync(harness);

        await using var db = harness.NewContext();
        await harness.NewRepair(db).RepairAsync(default);

        var sets = await db.ChunkSets.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.State);
        sets["set-2"].ShouldBe(CorpusState.Ready);
        sets["set-1"].ShouldBe(CorpusState.Degraded);
    }

    [Fact]
    public async Task A_repair_that_cannot_write_one_of_its_rows_writes_none_of_them()
    {
        // Refusing the sets' write lands in the middle of the one save, whichever order its
        // rows go in. The job row must not have been kept: failed with the corpus still
        // Indexing is a state nothing would ever look at again.
        var (harness, disk) = await StartAsync();
        await using var _ = harness;
        var logs = new RecordingLoggerFactory();
        await SeedStuckAsync(harness);

        await using var db = harness.NewContext();
        var repair = harness.NewRepair(db, logs.CreateLogger<StuckJobRepair>());
        disk.RefuseWritesTo("chunk_sets");

        (await repair.RepairAsync(default)).ShouldBe(0);

        disk.Fired.ShouldBeTrue("the window has to have been opened");
        (await db.Jobs.AsNoTracking().SingleAsync()).State.ShouldBe(JobState.Running, "not written as failed");
        (await db.Corpora.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Indexing);
        logs.Lines.ShouldContain(l => l.Contains("Could not repair") && l.Contains(IndexingHarness.CorpusId));

        disk.Free();
        (await repair.RepairAsync(default)).ShouldBe(1, "the next pass finds the same job again");
        (await db.Jobs.AsNoTracking().SingleAsync()).State.ShouldBe(JobState.Failed);
    }

    // ---- the refresh tick ------------------------------------------------------------

    private static ServiceProvider TickServices(IndexingHarness harness)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new DexiconOptions
        {
            Indexing = new IndexingOptions { RefreshMinutes = 1 },
        }));
        services.AddDbContext<CatalogDbContext>(
            o => o.UseSqlite($"Data Source={Path.Combine(harness.DataPath, "catalog.db")}")
                  .AddInterceptors(new SqlitePragmas(TimeSpan.FromSeconds(30), NullLogger<SqlitePragmas>.Instance)));
        services.AddSingleton<WorkScheduler>();
        services.AddSingleton<CorpusLeases>();
        services.AddScoped<IndexJobQueue>();
        services.AddScoped<StuckJobRepair>();
        return services.BuildServiceProvider();
    }

    private static ScheduledRefreshService Refresher(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IOptions<DexiconOptions>>(),
            NullLogger<ScheduledRefreshService>.Instance);

    [Fact]
    public async Task The_refresh_tick_repairs_a_stuck_corpus_and_refreshes_it_in_the_same_tick()
    {
        // The selection skips an Indexing corpus, which is what made the stuck state last.
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await SeedStuckAsync(harness);
        await using var services = TickServices(harness);

        await Refresher(services).RunTickAsync(default);

        await using var db = harness.NewContext();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == StuckId)).State.ShouldBe(JobState.Failed);
        (await db.Corpora.AsNoTracking().SingleAsync()).State.ShouldNotBe(CorpusState.Indexing);
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh && j.State == JobState.Queued))
            .ShouldBe(1, "the freed corpus is refreshed by the same tick");
    }

    [Fact]
    public async Task The_refresh_tick_still_skips_a_corpus_a_live_job_holds()
    {
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await SeedStuckAsync(harness);
        await using var services = TickServices(harness);

        await using var held = await services.GetRequiredService<CorpusLeases>()
            .TryAcquireAsync(IndexingHarness.CorpusId, $"index-{StuckId}", default);
        held.ShouldNotBeNull();

        await Refresher(services).RunTickAsync(default);

        await using var db = harness.NewContext();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == StuckId)).State.ShouldBe(JobState.Running);
        (await db.Jobs.CountAsync(j => j.State == JobState.Queued)).ShouldBe(0, "an indexing corpus is not refreshed");
    }
}
