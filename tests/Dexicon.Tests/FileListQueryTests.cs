using Dexicon.Api;
using Dexicon.Core.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Filtering and ordering the file list, which the server does so that paging can reach
/// every file.
///
/// It used to be the client's: it filtered and ordered the rows it had fetched, which on
/// a corpus larger than one page is a page. A name that IS in the corpus therefore came
/// back as no match, and a file that had just been added read as one that was never
/// indexed.
///
/// Stability is the part worth holding. An unstable order repeats one row and skips
/// another between pages, and a click-through shows a plausible page either way.
/// </summary>
public sealed class FileListQueryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private readonly List<string> _sources = ["s1"];
    private const string Set = "set1";

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Corpora.Add(new Corpus { Id = "c", Name = "books", CreatedUtc = DateTime.UtcNow });
        _db.Sources.Add(new Source { Id = "s1", CorpusId = "c", Kind = SourceKind.Workspace, RootPath = "lib" });
        _db.ChunkSets.Add(new ChunkSet
        {
            Id = Set, CorpusId = "c", Name = "default", EmbeddingModel = "m",
            EmbeddingDimensions = 8, CollectionName = "col", BoundaryMode = "blank-line",
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task Add(string path, long size = 100, int chunks = 1,
                           FileStatus status = FileStatus.Indexed, bool withState = true)
    {
        var id = Guid.NewGuid().ToString("n");
        _db.Files.Add(new IndexedFile { Id = id, SourceId = "s1", RelativePath = path, SizeBytes = size });
        if (withState)
            _db.FileChunkStates.Add(new FileChunkState
            {
                FileId = id, ChunkSetId = Set, Status = status, ChunkCount = chunks,
            });
        await _db.SaveChangesAsync();
    }

    private IQueryable<CorpusEndpoints.FileRow> Query(string? status = null, string? name = null) =>
        CorpusEndpoints.FilesOf(_db, _sources, Set, status, name);

    private async Task<List<string>> Paths(string? sort = null, string? name = null,
                                           string? status = null, int skip = 0, int take = 100) =>
        (await CorpusEndpoints.SortFiles(Query(status, name), sort)
            .Skip(skip).Take(take).ToListAsync())
        .Select(r => r.File.RelativePath).ToList();

    [Fact]
    public async Task TheNameFilterMatchesAnywhereInThePathWithoutCase()
    {
        await Add("papers/Grokking The Thing.pdf");
        await Add("manuals/networking.pdf");
        await Add("manuals/PAPERS-archive.pdf");

        (await Paths(name: "papers"))
            .ShouldBe(["manuals/PAPERS-archive.pdf", "papers/Grokking The Thing.pdf"]);
    }

    /// <summary>
    /// A caller typing a wildcard is typing a character, not a pattern. Left unescaped,
    /// `%` would match everything and read as the filter doing nothing.
    /// </summary>
    [Fact]
    public async Task WildcardsInTheFilterAreCharactersRatherThanPatterns()
    {
        await Add("a.pdf");
        await Add("b.pdf");
        await Add("100% coverage.pdf");

        (await Paths(name: "%")).ShouldBe(["100% coverage.pdf"]);
    }

    [Fact]
    public async Task TheNameAndStatusFiltersNarrowTogether()
    {
        await Add("manuals/one.pdf", status: FileStatus.Indexed);
        await Add("manuals/two.pdf", status: FileStatus.Failed);
        await Add("papers/three.pdf", status: FileStatus.Failed);

        (await Paths(name: "manuals", status: "failed")).ShouldBe(["manuals/two.pdf"]);
    }

    /// <summary>
    /// A file attached before this set existed has no state row. It is Pending, not
    /// missing, and dropping it would hide the files a new set still has to do.
    /// </summary>
    [Fact]
    public async Task AFileWithNoStateForThisSetIsStillListed()
    {
        await Add("seen.pdf");
        await Add("new-to-this-set.pdf", withState: false);

        (await Paths()).ShouldBe(["new-to-this-set.pdf", "seen.pdf"]);
        (await Paths(status: "pending")).ShouldBe(["new-to-this-set.pdf"]);
    }

    [Fact]
    public async Task SortBySizeIsLargestFirst()
    {
        await Add("small.pdf", size: 10);
        await Add("huge.pdf", size: 9_000);
        await Add("medium.pdf", size: 500);

        (await Paths(sort: "size")).ShouldBe(["huge.pdf", "medium.pdf", "small.pdf"]);
    }

    [Fact]
    public async Task SortByChunksIsMostFirstAndCountsAMissingStateAsNone()
    {
        await Add("many.pdf", chunks: 90);
        await Add("few.pdf", chunks: 2);
        await Add("unindexed.pdf", withState: false);

        (await Paths(sort: "chunks")).ShouldBe(["many.pdf", "few.pdf", "unindexed.pdf"]);
    }

    /// <summary>
    /// The property paging depends on. Every sort key here has duplicates in a real
    /// corpus - sizes, chunk counts, statuses - and without a second key the database
    /// may return them in any order, so page 2 can repeat a row from page 1 and skip
    /// another entirely. The list loses a file, which is the defect this whole change
    /// is about arriving by a different route.
    /// </summary>
    [Theory]
    [InlineData("size")]
    [InlineData("chunks")]
    [InlineData("status")]
    [InlineData("path")]
    public async Task PagingAnOrderWithTiesVisitsEveryFileExactlyOnce(string sort)
    {
        // Every file identical on the first key, so only the tiebreak separates them.
        for (var i = 0; i < 25; i++) await Add($"tied-{i:D2}.pdf", size: 100, chunks: 7);

        var paged = new List<string>();
        for (var skip = 0; skip < 25; skip += 5)
            paged.AddRange(await Paths(sort: sort, skip: skip, take: 5));

        paged.Count.ShouldBe(25);
        paged.Distinct().Count().ShouldBe(25, $"paging by {sort} repeated a row and skipped another");
    }

    [Fact]
    public async Task NoFilterCountsEveryFile()
    {
        await Add("a.pdf");
        await Add("b.pdf");

        (await Query().CountAsync()).ShouldBe(2);
        (await Query(name: "a").CountAsync()).ShouldBe(1);
    }
}
