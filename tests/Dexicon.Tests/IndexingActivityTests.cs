using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Catalog.Migrations;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Whether a corpus or a set is being indexed is read from the jobs and the corpus lease, and
/// is not stored on its row.
///
/// It used to be written by the job that started and cleared by the one that finished, and a
/// job that could not save its outcome left it set, with the scheduled refresh skipping the
/// corpus from then on. Each case here seeds the rows it names and reads the answer, so a rule
/// is shown on its own: what counts as work, what does not, and that a job which stops leaves
/// the row as the last pass wrote it.
/// </summary>
public sealed class IndexingActivityTests
{
    private const string JobId = "job-1";

    private static async Task<IndexingHarness> StartAsync(int sets = 1)
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets);
        return harness;
    }

    private static async Task AddJobAsync(
        IndexingHarness harness, JobState state, string? chunkSetId = null,
        string corpusId = IndexingHarness.CorpusId, string id = JobId)
    {
        await using var db = harness.NewContext();
        var at = DateTime.UtcNow.AddHours(-2);
        db.Jobs.Add(new IndexJob
        {
            Id = id, CorpusId = corpusId, ChunkSetId = chunkSetId, Kind = JobKind.Refresh, State = state,
            QueuedUtc = at,
            StartedUtc = state == JobState.Queued ? null : at,
            FinishedUtc = state is JobState.Queued or JobState.Running ? null : at,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The corpus lease as a holder leaves it: named, and expiring after <paramref name="left"/>.</summary>
    private static async Task HoldAsync(IndexingHarness harness, string? by, TimeSpan left)
    {
        await using var db = harness.NewContext();
        await db.Corpora.ExecuteUpdateAsync(u => u
            .SetProperty(c => c.HeldBy, by)
            .SetProperty(c => c.HeldUntilUtc, by == null ? (DateTime?)null : DateTime.UtcNow + left));
    }

    private static async Task<(CorpusState Corpus, Dictionary<string, CorpusState> Sets)> ReadAsync(
        IndexingHarness harness, string corpusId = IndexingHarness.CorpusId)
    {
        await using var db = harness.NewContext();
        var activity = await IndexingActivity.ReadAsync(db, [corpusId]);
        var corpus = await db.Corpora.AsNoTracking().SingleAsync(c => c.Id == corpusId);
        var sets = await db.ChunkSets.AsNoTracking().Where(s => s.CorpusId == corpusId).ToListAsync();
        return (activity.Of(corpus), sets.ToDictionary(s => s.Id, s => activity.Of(s)));
    }

    [Fact]
    public async Task A_queued_job_makes_the_corpus_and_every_set_it_covers_indexing()
    {
        await using var harness = await StartAsync(sets: 2);
        await AddJobAsync(harness, JobState.Queued);

        var (corpus, sets) = await ReadAsync(harness);

        corpus.ShouldBe(CorpusState.Indexing);
        sets.Values.ShouldAllBe(s => s == CorpusState.Indexing);
    }

    [Fact]
    public async Task A_job_for_one_set_makes_that_set_and_the_corpus_indexing_and_not_the_other_set()
    {
        // The live set is complete while its replacement backfills, and says so.
        await using var harness = await StartAsync(sets: 2);
        await AddJobAsync(harness, JobState.Queued, chunkSetId: "set-2");

        var (corpus, sets) = await ReadAsync(harness);

        corpus.ShouldBe(CorpusState.Indexing);
        sets["set-2"].ShouldBe(CorpusState.Indexing);
        sets["set-1"].ShouldBe(CorpusState.Ready);
    }

    [Fact]
    public async Task A_running_job_counts_while_it_holds_a_live_lease_and_stops_counting_when_the_lease_lapses()
    {
        await using var harness = await StartAsync();
        await AddJobAsync(harness, JobState.Running);
        await HoldAsync(harness, $"{IndexingActivity.HolderPrefix}{JobId}", TimeSpan.FromMinutes(1));

        (await ReadAsync(harness)).Corpus.ShouldBe(CorpusState.Indexing);

        // Nothing renews it. The row still says Running, and nothing has written anything.
        await HoldAsync(harness, $"{IndexingActivity.HolderPrefix}{JobId}", TimeSpan.FromMinutes(-1));

        var (corpus, sets) = await ReadAsync(harness);
        corpus.ShouldBe(CorpusState.Ready);
        sets.Values.ShouldAllBe(s => s == CorpusState.Ready);
    }

    [Fact]
    public async Task A_running_job_that_never_took_or_has_released_the_lease_does_not_count()
    {
        await using var harness = await StartAsync();
        await AddJobAsync(harness, JobState.Running);
        await HoldAsync(harness, by: null, TimeSpan.Zero);

        (await ReadAsync(harness)).Corpus.ShouldBe(CorpusState.Ready);
    }

    [Fact]
    public async Task A_running_job_does_not_count_on_a_lease_that_is_someone_elses()
    {
        // A sweep holds the corpus. That is not this job working, and a row left Running by a
        // job that died must not read as work for as long as a sweep happens to run.
        await using var harness = await StartAsync();
        await AddJobAsync(harness, JobState.Running);
        await HoldAsync(harness, "sweep-1", TimeSpan.FromMinutes(1));

        (await ReadAsync(harness)).Corpus.ShouldBe(CorpusState.Ready);
    }

    [Theory]
    [InlineData(JobState.Succeeded)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Degraded)]
    [InlineData(JobState.Cancelled)]
    public async Task A_finished_job_does_not_count_whatever_lease_is_left(JobState state)
    {
        await using var harness = await StartAsync();
        await AddJobAsync(harness, state);
        await HoldAsync(harness, $"{IndexingActivity.HolderPrefix}{JobId}", TimeSpan.FromMinutes(1));

        (await ReadAsync(harness)).Corpus.ShouldBe(CorpusState.Ready);
    }

    [Fact]
    public async Task A_job_for_another_corpus_does_not_count()
    {
        await using var harness = await StartAsync();
        await using (var db = harness.NewContext())
        {
            db.Corpora.Add(new Corpus { Id = "corpus-2", Name = "other", CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await AddJobAsync(harness, JobState.Queued, corpusId: "corpus-2");

        (await ReadAsync(harness)).Corpus.ShouldBe(CorpusState.Ready);
        (await ReadAsync(harness, "corpus-2")).Corpus.ShouldBe(CorpusState.Indexing);
    }

    [Theory]
    [InlineData(CorpusState.Degraded)]
    [InlineData(CorpusState.Unavailable)]
    public async Task A_job_that_stops_leaves_the_outcome_of_the_last_pass(CorpusState outcome)
    {
        // What a reader sees once nothing counts is what was stored, so there is nothing to
        // reconstruct from the job history.
        await using var harness = await StartAsync();
        await using (var db = harness.NewContext())
        {
            await db.Corpora.ExecuteUpdateAsync(u => u.SetProperty(c => c.State, outcome));
            await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.State, outcome));
        }
        await AddJobAsync(harness, JobState.Running);
        await HoldAsync(harness, $"{IndexingActivity.HolderPrefix}{JobId}", TimeSpan.FromMinutes(-5));

        var (corpus, sets) = await ReadAsync(harness);

        corpus.ShouldBe(outcome);
        sets.Values.ShouldAllBe(s => s == outcome);
    }

    // ---- what the API reports ---------------------------------------------------------

    [Fact]
    public async Task The_corpus_summary_says_indexing_while_a_job_works_and_the_stored_outcome_after()
    {
        await using var harness = await StartAsync(sets: 2);
        await AddJobAsync(harness, JobState.Queued, chunkSetId: "set-2");

        await using var db = harness.NewContext();
        var corpus = await db.Corpora.AsNoTracking().SingleAsync();

        var during = await CorpusEndpoints.Summarise(db, corpus, harness.Settings.Value.Indexing, default);
        during.State.ShouldBe("indexing");
        during.ChunkSets.Single(s => s.Name == "alt-1").State.ShouldBe("indexing");
        during.ChunkSets.Single(s => s.Name == "default").State.ShouldBe("ready");

        await db.Jobs.ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobState.Succeeded));

        var after = await CorpusEndpoints.Summarise(db, corpus, harness.Settings.Value.Indexing, default);
        after.State.ShouldBe("ready");
        after.ChunkSets.ShouldAllBe(s => s.State == "ready");
    }

    // ---- the refresh tick -------------------------------------------------------------

    private static async Task<int> QueuedRefreshesAsync(IndexingHarness harness, Func<IQueryable<IndexJob>, IQueryable<IndexJob>>? where = null)
    {
        await using var db = harness.NewContext();
        return await (where?.Invoke(db.Jobs) ?? db.Jobs)
            .CountAsync(j => j.Kind == JobKind.Refresh && j.State == JobState.Queued);
    }

    [Fact]
    public async Task The_refresh_tick_refreshes_a_corpus_nothing_is_working_on()
    {
        await using var harness = await StartAsync();

        await harness.RunRefreshTickAsync();

        (await QueuedRefreshesAsync(harness)).ShouldBe(1);
    }

    [Fact]
    public async Task The_refresh_tick_leaves_a_corpus_a_live_job_is_working_on()
    {
        await using var harness = await StartAsync();
        await AddJobAsync(harness, JobState.Running);
        await HoldAsync(harness, $"{IndexingActivity.HolderPrefix}{JobId}", TimeSpan.FromMinutes(1));

        await harness.RunRefreshTickAsync();

        (await QueuedRefreshesAsync(harness)).ShouldBe(0, "a refresh queued behind a running one would only wait for it");
    }

    [Fact]
    public async Task The_refresh_tick_leaves_a_corpus_that_has_a_queued_job()
    {
        await using var harness = await StartAsync();
        await AddJobAsync(harness, JobState.Queued);

        await harness.RunRefreshTickAsync();

        await using var db = harness.NewContext();
        (await db.Jobs.CountAsync()).ShouldBe(1, "the job already queued is what will run");
    }

    [Fact]
    public async Task The_refresh_tick_refreshes_a_corpus_whose_job_stopped_without_saying_so()
    {
        // The state a full disk left: the job reads Running and nothing renews its lease. It
        // held the corpus out of every refresh for hours, until a restart.
        await using var harness = await StartAsync();
        await AddJobAsync(harness, JobState.Running);
        await HoldAsync(harness, $"{IndexingActivity.HolderPrefix}{JobId}", TimeSpan.FromMinutes(-5));

        await harness.RunRefreshTickAsync();

        (await QueuedRefreshesAsync(harness)).ShouldBe(1);
    }

    // ---- the migration ----------------------------------------------------------------

    [Fact]
    public async Task The_migration_is_applied_to_a_new_catalogue_with_nothing_left_pending()
    {
        // The harness builds its catalogue without migrations, so this is the one test that
        // shows this one is found and runs, and that it left the model as the snapshot has it.
        var path = Path.Combine(Path.GetTempPath(), $"dexicon-migrate-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);

            await db.Database.MigrateAsync();

            (await db.Database.GetAppliedMigrationsAsync())
                .ShouldContain("20261007020000_IndexingIsNotStored");
            (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
            db.Database.HasPendingModelChanges().ShouldBeFalse();
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task The_migration_clears_a_stored_indexing_state_and_leaves_every_other_one()
    {
        await using var harness = await StartAsync(sets: 3);
        await using var db = harness.NewContext();

        // Written as text, which is how the column holds the enum.
        await db.Database.ExecuteSqlRawAsync("UPDATE corpora SET State = 'Indexing'");
        await db.Database.ExecuteSqlRawAsync("UPDATE chunk_sets SET State = 'Indexing' WHERE Id = 'set-1'");
        await db.Database.ExecuteSqlRawAsync("UPDATE chunk_sets SET State = 'Unavailable' WHERE Id = 'set-2'");

        await db.Database.ExecuteSqlRawAsync(IndexingIsNotStored.ClearStoredIndexingSql);

        (await db.Corpora.AsNoTracking().SingleAsync()).State.ShouldBe(CorpusState.Degraded);
        var sets = await db.ChunkSets.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.State);
        sets["set-1"].ShouldBe(CorpusState.Degraded);
        sets["set-2"].ShouldBe(CorpusState.Unavailable, "not Indexing, so not touched");
        sets["set-3"].ShouldBe(CorpusState.Ready);
    }
}
