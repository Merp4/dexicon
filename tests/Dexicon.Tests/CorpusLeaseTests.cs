using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Exclusive use of one corpus, for the two lanes D-32 introduces.
///
/// The property that matters is that taking it is atomic. Reading a state and then acting
/// on it cannot exclude anything, because the gap between the read and the write is where
/// the other pass starts; that is why <c>CorpusState.Indexing</c> could not be used for
/// this. So these run against a real database file with real concurrent connections, since
/// an in-memory database shared by one connection cannot show a race at all.
/// </summary>
public sealed class CorpusLeaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lease-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public CorpusLeaseTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CatalogDbContext>(
            o => o.UseSqlite($"Data Source={_path}")
                  .AddInterceptors(new SqlitePragmas(
                      TimeSpan.FromSeconds(30), NullLogger<SqlitePragmas>.Instance)),
            ServiceLifetime.Scoped);
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.Database.EnsureCreated();
    }

    private CorpusLeases Leases() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CorpusLeases>.Instance);

    private async Task<string> CorpusAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var id = Ulid.NewUlid().ToString();
        // Full id, not a prefix: ULIDs share a timestamp prefix, so two made in the same
        // millisecond collide on the unique name.
        db.Corpora.Add(new Corpus { Id = id, Name = $"c{id}", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(string? Holder, DateTime? Until)> RowAsync(string id)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var c = await db.Corpora.AsNoTracking().FirstAsync(x => x.Id == id);
        return (c.HeldBy, c.HeldUntilUtc);
    }

    [Fact]
    public async Task AFreeCorpusIsTaken()
    {
        var id = await CorpusAsync();
        var leases = Leases();

        await using var hold = await leases.TryAcquireAsync(id, "index-job-1", default);

        hold.ShouldNotBeNull();
        var row = await RowAsync(id);
        row.Holder.ShouldBe("index-job-1");
        row.Until.ShouldNotBeNull();
    }

    [Fact]
    public async Task ASecondHolderIsTurnedAway()
    {
        var id = await CorpusAsync();
        var leases = Leases();

        await using var first = await leases.TryAcquireAsync(id, "index-job-1", default);
        var second = await leases.TryAcquireAsync(id, "sweep-1", default);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task ReleasingLetsTheOtherLaneIn()
    {
        var id = await CorpusAsync();
        var leases = Leases();

        var first = await leases.TryAcquireAsync(id, "index-job-1", default);
        first.ShouldNotBeNull();
        await first.DisposeAsync();

        await using var second = await leases.TryAcquireAsync(id, "sweep-1", default);
        second.ShouldNotBeNull();
        (await RowAsync(id)).Holder.ShouldBe("sweep-1");
    }

    [Fact]
    public async Task AHolderThatStoppedRenewingIsReclaimed()
    {
        // The reason the expiry exists at all. A crashed holder must not keep a corpus
        // forever, and it is renewal that decides, not a guess at how long work takes.
        var id = await CorpusAsync();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var c = await db.Corpora.FirstAsync(x => x.Id == id);
            c.HeldBy = "a-process-that-died";
            c.HeldUntilUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await using var hold = await Leases().TryAcquireAsync(id, "sweep-1", default);

        hold.ShouldNotBeNull();
        (await RowAsync(id)).Holder.ShouldBe("sweep-1");
    }

    [Fact]
    public async Task AHolderStillRenewingKeepsIt()
    {
        var id = await CorpusAsync();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var c = await db.Corpora.FirstAsync(x => x.Id == id);
            c.HeldBy = "a-long-index-job";
            c.HeldUntilUtc = DateTime.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        (await Leases().TryAcquireAsync(id, "sweep-1", default)).ShouldBeNull();
    }

    [Fact]
    public async Task ConcurrentClaimsProduceExactlyOneWinner()
    {
        // The whole point. Twelve contenders at once against one row: a check followed by
        // a write lets several through, a conditional update lets one.
        //
        // They are released by a barrier from the thread pool rather than by awaiting a
        // shared task. A TaskCompletionSource runs its continuations synchronously on the
        // thread that completes it, so awaiting one ran all twelve attempts in sequence and
        // this test passed against a deliberately racy read-then-write implementation.
        //
        // What it does and does not establish, since that was measured rather than assumed:
        // against a read-then-write with a 50ms gap it fails, and against the same code with
        // only a `Task.Yield()` between the read and the write it still passes. It is a
        // regression guard against the obvious mistake, not a proof of atomicity. That rests
        // on the claim being one conditional UPDATE, which SQLite applies under its write
        // lock, and no test of this shape can demonstrate it.
        var id = await CorpusAsync();
        var leases = Leases();
        using var gate = new Barrier(12);

        var attempts = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            gate.SignalAndWait();
            return await leases.TryAcquireAsync(id, $"contender-{i}", default);
        })).ToList();

        var holds = await Task.WhenAll(attempts);

        var won = holds.Where(h => h is not null).ToList();
        won.Count.ShouldBe(1);
        (await RowAsync(id)).Holder.ShouldBe(won[0]!.Holder);

        foreach (var h in won) await h!.DisposeAsync();
    }

    [Fact]
    public async Task TwoDifferentCorporaDoNotExcludeEachOther()
    {
        // A sweep of one corpus while another indexes is the entire point of the lanes.
        var a = await CorpusAsync();
        var b = await CorpusAsync();
        var leases = Leases();

        await using var indexing = await leases.TryAcquireAsync(a, "index-job-1", default);
        await using var sweeping = await leases.TryAcquireAsync(b, "sweep-1", default);

        indexing.ShouldNotBeNull();
        sweeping.ShouldNotBeNull();
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { /* a pooled handle */ }
        }
    }
}
