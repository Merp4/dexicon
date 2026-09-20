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
/// The discovery sweep: what a corpus holds, recorded without extracting or embedding any
/// of it.
///
/// The behaviours worth pinning are the ones D-32 argued about. It records the inventory a
/// later index pass would have recorded, per chunk set; it never removes anything, however
/// empty a row looks; and it refuses to run on a corpus another pass is holding.
/// </summary>
public sealed class CorpusSweepTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"sweep-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sweep-root-{Guid.NewGuid():N}");
    private readonly ServiceProvider _services;

    public CorpusSweepTests()
    {
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CatalogDbContext>(
            o => o.UseSqlite($"Data Source={_db}")
                  .AddInterceptors(new SqlitePragmas(
                      TimeSpan.FromSeconds(30), NullLogger<SqlitePragmas>.Instance)),
            ServiceLifetime.Scoped);
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreated();
    }

    private CorpusLeases Leases() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CorpusLeases>.Instance);

    private CorpusSweeper Sweeper(CatalogDbContext db, CorpusLeases? leases = null) =>
        new(db, leases ?? Leases(),
            Options.Create(new DexiconOptions
            {
                Indexing = new IndexingOptions { WorkspaceRoot = Path.GetTempPath() },
            }),
            NullLogger<CorpusSweeper>.Instance);

    private void File(string relative, string content = "hello world")
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
    }

    /// <summary>A corpus with one workspace source pointed at the temp tree, and `sets` chunk sets.</summary>
    private async Task<string> CorpusAsync(int sets = 1)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var id = Ulid.NewUlid().ToString();
        db.Corpora.Add(new Corpus { Id = id, Name = $"c{id}", CreatedUtc = DateTime.UtcNow });
        db.Sources.Add(new Source
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = id,
            Kind = SourceKind.Workspace,
            RootPath = Path.GetFileName(_root),
            CreatedUtc = DateTime.UtcNow,
        });
        for (var i = 0; i < sets; i++)
        {
            db.ChunkSets.Add(new ChunkSet
            {
                Id = Ulid.NewUlid().ToString(),
                CorpusId = id,
                Name = $"set{i}",
                IsDefault = i == 0,
                EmbeddingProvider = "ollama",
                EmbeddingModel = "m",
                EmbeddingDimensions = 768,
                CollectionName = $"test__{i}",
                BoundaryMode = "none",
                CreatedUtc = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<SweepResult> SweepAsync(string corpusId, CorpusLeases? leases = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await Sweeper(db, leases).SweepAsync(corpusId, default);
    }

    private async Task<List<FileChunkState>> StatesAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await db.FileChunkStates.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task ItRecordsWhatIsThereWithoutIndexingIt()
    {
        File("a.md");
        File("docs/b.md");
        var id = await CorpusAsync();

        var result = await SweepAsync(id);

        result.Swept.ShouldBe(2);
        result.Added.ShouldBe(2);
        result.Skipped.ShouldBeFalse();

        var states = await StatesAsync();
        states.Count.ShouldBe(2);
        states.ShouldAllBe(s => s.Status == FileStatus.Pending);
        // Nothing was extracted, so nothing claims to have been.
        states.ShouldAllBe(s => s.ChunkCount == 0 && s.ContentHash == null);
    }

    [Fact]
    public async Task EveryChunkSetGetsItsOwnRow()
    {
        // The status is a property of a file AS CUT BY a set, so a corpus carrying two
        // sets owes two rows per file. Getting this wrong would leave the second set
        // reporting nothing discovered.
        File("a.md");
        File("b.md");
        var id = await CorpusAsync(sets: 2);

        await SweepAsync(id);

        var states = await StatesAsync();
        states.Count.ShouldBe(4);
        states.Select(s => s.ChunkSetId).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task SweepingTwiceAddsNothingTheSecondTime()
    {
        File("a.md");
        var id = await CorpusAsync();

        (await SweepAsync(id)).Added.ShouldBe(1);
        var second = await SweepAsync(id);

        second.Swept.ShouldBe(1);
        second.Added.ShouldBe(0);
        (await StatesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ItNeverOverwritesWhatIndexingDecided()
    {
        // A sweep that reset a status would undo the indexer's work every time it ran.
        File("a.md");
        var id = await CorpusAsync();
        await SweepAsync(id);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var state = await db.FileChunkStates.FirstAsync();
            state.Status = FileStatus.Indexed;
            state.ChunkCount = 12;
            state.ContentHash = "fingerprint";
            await db.SaveChangesAsync();
        }

        await SweepAsync(id);

        var after = (await StatesAsync()).Single();
        after.Status.ShouldBe(FileStatus.Indexed);
        after.ChunkCount.ShouldBe(12);
        after.ContentHash.ShouldBe("fingerprint");
    }

    [Fact]
    public async Task AVanishedFileIsLeftForIndexingToRemove()
    {
        // The rule the ADR settled on. Removing it means removing its vectors from every
        // set's collection, which a sweep cannot do, so a sweep that deleted the row would
        // strand them. Pending is not evidence of "no vectors" either, because the upsert
        // precedes the status write.
        File("a.md");
        File("b.md");
        var id = await CorpusAsync();
        await SweepAsync(id);

        System.IO.File.Delete(Path.Combine(_root, "b.md"));
        var second = await SweepAsync(id);

        second.Swept.ShouldBe(1);
        (await StatesAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ACorpusHeldByAnotherPassIsNotSwept()
    {
        File("a.md");
        var id = await CorpusAsync();
        var leases = Leases();

        await using var indexing = await leases.TryAcquireAsync(id, "index-job-1", default);
        indexing.ShouldNotBeNull();

        var result = await SweepAsync(id, leases);

        result.Skipped.ShouldBeTrue();
        result.Swept.ShouldBe(0);
        (await StatesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AMissingSourceLeavesItsInventoryAlone()
    {
        // A mount being away is an operational condition, not a statement that the files
        // are gone, and wiping an inventory on that reading is the destructive version of
        // the same mistake.
        File("a.md");
        var id = await CorpusAsync();
        await SweepAsync(id);

        Directory.Delete(_root, recursive: true);
        var result = await SweepAsync(id);

        result.Swept.ShouldBe(0);
        result.Skipped.ShouldBeFalse();
        (await StatesAsync()).Count.ShouldBe(1);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { System.IO.File.Delete(_db + suffix); } catch (IOException) { }
        }
    }
}
