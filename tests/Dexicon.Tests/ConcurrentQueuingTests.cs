using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// Two requests queuing a refresh of one corpus at the same moment queue one job.
///
/// A queued job is looked for and, when none is there, added. Two requests that both looked before either
/// saved each added one, and the corpus was indexed twice in a row. The lookup and the save are one step
/// under a lock now.
/// </summary>
public sealed class ConcurrentQueuingTests
{
    /// <summary>
    /// How long the first lookup is held for a second one. The second request is parked on the lock the
    /// first holds, so it cannot arrive, and the first goes on when this has passed. It has to be long
    /// enough for a loaded runner to start the second request meanwhile, or the test would pass without
    /// the two having overlapped.
    /// </summary>
    private static readonly TimeSpan HowLongTheFirstWaits = TimeSpan.FromSeconds(3);

    private static bool IsTheQueuedJobLookup(string sql) =>
        sql.Contains("FROM \"jobs\"", StringComparison.Ordinal)
        && sql.Contains("LIMIT 1", StringComparison.Ordinal);

    private static IndexJobQueue QueueOn(IndexingHarness harness, CatalogDbContext db) =>
        new(db, new WorkScheduler(harness.Settings), NullLogger<IndexJobQueue>.Instance);

    [Fact]
    public async Task TheGateHoldsTwoLookupsForAQueuedJobAtOnce()
    {
        // The control for the test below: with nothing in the way, the gate does put two lookups inside the
        // window, and both find no job.
        var gate = new HoldTheLookup(TimeSpan.FromSeconds(10), IsTheQueuedJobLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        gate.Armed = true;

        var found = await Task.WhenAll(
            first.Jobs.FirstOrDefaultAsync(j => j.State == JobState.Queued),
            second.Jobs.FirstOrDefaultAsync(j => j.State == JobState.Queued));

        gate.Met.ShouldBeTrue("the gate has to have held both lookups at once");
        found.ShouldAllBe(j => j == null);
    }

    [Fact]
    public async Task TwoRefreshesQueuedAtTheSameTimeForOneCorpusAreOneJob()
    {
        var gate = new HoldTheLookup(HowLongTheFirstWaits, IsTheQueuedJobLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var firstQueue = QueueOn(harness, first);
        var secondQueue = QueueOn(harness, second);
        gate.Armed = true;

        var queued = await Task.WhenAll(
            firstQueue.EnqueueAsync(IndexingHarness.CorpusId, JobKind.Refresh),
            secondQueue.EnqueueAsync(IndexingHarness.CorpusId, JobKind.Refresh));

        gate.Arrivals.ShouldBeGreaterThanOrEqualTo(2, "both requests have to have looked for a queued job");
        gate.Met.ShouldBeFalse("the second request was held back until the first had saved its job");
        await using var check = harness.NewContext();
        (await check.Jobs.CountAsync(j => j.State == JobState.Queued)).ShouldBe(1, "one queued job for one corpus");
        queued[1].Id.ShouldBe(queued[0].Id, "the request that lost is answered with the job that won");
    }

    [Fact]
    public async Task RefreshesQueuedAtTheSameTimeForTwoCorporaAreTwoJobs()
    {
        // The lock serialises the lookup and the save and does not merge requests for different corpora.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using (var seeding = harness.NewContext())
        {
            seeding.Corpora.Add(new Corpus
            {
                Id = "corpus-2",
                Name = "other",
                State = CorpusState.Ready,
                CreatedUtc = DateTime.UtcNow,
            });
            await seeding.SaveChangesAsync();
        }

        await using var first = harness.NewContext();
        await using var second = harness.NewContext();

        var queued = await Task.WhenAll(
            QueueOn(harness, first).EnqueueAsync(IndexingHarness.CorpusId, JobKind.Refresh),
            QueueOn(harness, second).EnqueueAsync("corpus-2", JobKind.Refresh));

        queued[1].Id.ShouldNotBe(queued[0].Id);
        await using var check = harness.NewContext();
        (await check.Jobs.CountAsync()).ShouldBe(2);
    }
}
