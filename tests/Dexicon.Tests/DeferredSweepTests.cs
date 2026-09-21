using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A sweep that could not take the lease is still outstanding, and has to come back.
///
/// The scheduler skips a corpus busy in THIS process, so a sweep that reaches the lease
/// and is refused was refused by another process — the one case no amount of scheduling
/// can see, and the one the fifteen-second timer exists for. The pool discarded the
/// sweep's result and only ever re-queued index jobs, so that sweep was lost, while D-33
/// and the class comment both said it was retried.
///
/// The other half matters as much: a sweep for a corpus that no longer exists must NOT
/// come back, or it is a loop with no end and no work in it.
/// </summary>
public sealed class DeferredSweepTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"defer-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public DeferredSweepTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new DexiconOptions
        {
            Indexing = new IndexingOptions { WorkspaceRoot = Path.GetTempPath() },
        }));
        services.AddDbContext<CatalogDbContext>(
            o => o.UseSqlite($"Data Source={_db}")
                  .AddInterceptors(new SqlitePragmas(
                      TimeSpan.FromSeconds(30), NullLogger<SqlitePragmas>.Instance)),
            ServiceLifetime.Scoped);
        services.AddSingleton<CorpusLeases>();
        services.AddScoped<CorpusSweeper>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreated();
    }

    private WorkerPool Pool(WorkScheduler scheduler) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), scheduler,
            new IndexProgressBroadcaster(), NullLogger<WorkerPool>.Instance)
        {
            // The wait is not what is under test; that it happens at all is.
            RequeueAfter = TimeSpan.Zero,
        };

    private static WorkScheduler Scheduler() =>
        new(Options.Create(new DexiconOptions
        {
            Indexing = new IndexingOptions { MaxConcurrentSweeps = 1 },
        }));

    private async Task<string> CorpusAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var id = Ulid.NewUlid().ToString();
        db.Corpora.Add(new Corpus { Id = id, Name = $"c{id}", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>The item the scheduler offers next, or null after waiting for one.</summary>
    private static async Task<WorkLease?> TakeWithinAsync(WorkScheduler scheduler, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            if (scheduler.TryTake() is { } lease) return lease;
            await Task.Delay(10);
        }
        return null;
    }

    [Fact]
    public async Task ASweepRefusedTheLeaseComesBack()
    {
        var corpusId = await CorpusAsync();
        var scheduler = Scheduler();

        // Another holder, standing in for the other process. Taken before the sweep runs,
        // so the sweep reaches the lease and is turned away.
        var leases = _services.GetRequiredService<CorpusLeases>();
        await using var held = await leases.TryAcquireAsync(corpusId, "someone-else", default);
        held.ShouldNotBeNull();

        await Pool(scheduler).RunOneAsync(new WorkItem(WorkType.Sweep, corpusId, corpusId), default);

        var back = await TakeWithinAsync(scheduler, TimeSpan.FromSeconds(5));

        back.ShouldNotBeNull("a sweep refused the lease is still outstanding");
        back.Item.Type.ShouldBe(WorkType.Sweep);
        back.Item.CorpusId.ShouldBe(corpusId);
    }

    [Fact]
    public async Task ASweepForACorpusThatIsGoneDoesNot()
    {
        var scheduler = Scheduler();
        var missing = Ulid.NewUlid().ToString();

        await Pool(scheduler).RunOneAsync(new WorkItem(WorkType.Sweep, missing, missing), default);

        // Long enough that a zero delay would have landed many times over.
        await Task.Delay(200);

        scheduler.TryTake().ShouldBeNull("a deleted corpus is terminal, not deferred");
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_db + suffix); } catch (IOException) { }
        }
    }
}
