using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What `/api/corpora/{name}/coverage` adds over <c>SourceCoverage.Find</c>, which
/// <see cref="SourceCoverageTests"/> already covers: reading the right corpus's sources,
/// and their own size caps, out of the catalogue.
///
/// Scoping is the part worth a test. Reading every source in the database would report one
/// corpus's gaps on another's page, and the answer would still look entirely plausible:
/// real directories, real files, attached to the wrong corpus.
/// </summary>
public sealed class CoverageEndpointTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private readonly string _root = Directory.CreateTempSubdirectory("dexicon-coverage-api-").FullName;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Tenants.Add(new Tenant { Id = "t", DisplayName = "t", CreatedUtc = DateTime.UtcNow });
        _db.Corpora.Add(new Corpus { Id = "c", TenantId = "t", Name = "books", CreatedUtc = DateTime.UtcNow });
        _db.Corpora.Add(new Corpus { Id = "c2", TenantId = "t", Name = "papers", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Write(string relative, string content = "text")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task AddSource(string corpusId, string rootPath, int maxFileBytes = 262_144)
    {
        _db.Sources.Add(new Source
        {
            Id = Guid.NewGuid().ToString("n"),
            CorpusId = corpusId,
            Kind = SourceKind.Workspace,
            RootPath = rootPath,
            MaxFileBytes = maxFileBytes,
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<CoverageReport> Coverage(string corpusId = "c")
    {
        var corpus = await _db.Corpora.FirstAsync(c => c.Id == corpusId);
        return await CorpusEndpoints.CoverageAsync(
            _db, new IndexingOptions { WorkspaceRoot = _root }, corpus, default);
    }

    [Fact]
    public async Task ReportsTheGapAsTheContractDescribesIt()
    {
        Write("books/orly/AI/one.md");
        Write("books/orly/dotnet/two.md");
        Write("books/orly/Internet of Things from Scratch.txt");
        await AddSource("c", "books/orly/AI");
        await AddSource("c", "books/orly/dotnet");

        var report = await Coverage();

        report.Gaps.Count.ShouldBe(1);
        report.Gaps[0].Directory.ShouldBe("books/orly");
        report.Gaps[0].Files.ShouldBe(["Internet of Things from Scratch.txt"]);
    }

    [Fact]
    public async Task ReadsOnlyTheSourcesOfTheCorpusAskedAbout()
    {
        // Two corpora, each with a pair of sources under its own parent and a file loose
        // beside them. Ignoring the corpus id reports both gaps to both.
        Write("books/orly/AI/one.md");
        Write("books/orly/dotnet/two.md");
        Write("books/orly/loose-book.md");
        Write("papers/2024/three.md");
        Write("papers/2025/four.md");
        Write("papers/loose-paper.md");

        await AddSource("c", "books/orly/AI");
        await AddSource("c", "books/orly/dotnet");
        await AddSource("c2", "papers/2024");
        await AddSource("c2", "papers/2025");

        var books = await Coverage("c");
        var papers = await Coverage("c2");

        books.Gaps.Select(g => g.Directory).ShouldBe(["books/orly"]);
        books.Gaps[0].Files.ShouldBe(["loose-book.md"]);

        papers.Gaps.Select(g => g.Directory).ShouldBe(["papers"]);
        papers.Gaps[0].Files.ShouldBe(["loose-paper.md"]);
    }

    [Fact]
    public async Task UsesTheSourcesOwnSizeCapAndNotTheDefault()
    {
        Write("books/orly/AI/one.md");
        Write("books/orly/dotnet/two.md");
        Write("books/orly/big.md", new string('x', 400_000));

        // Both sources take files this large, so the check must not hide one on a cap
        // neither of them uses.
        await AddSource("c", "books/orly/AI", maxFileBytes: 1_000_000);
        await AddSource("c", "books/orly/dotnet", maxFileBytes: 1_000_000);

        var report = await Coverage();

        report.Gaps.Count.ShouldBe(1);
        report.Gaps[0].Files.ShouldBe(["big.md"]);
    }

    [Fact]
    public async Task ACorpusWithNoSourcesReportsNothingRatherThanThrowing()
    {
        Write("books/orly/loose.md");

        (await Coverage()).Gaps.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUploadSourceHasNoPathAndIsNotOneHalfOfAPair()
    {
        Write("books/orly/AI/one.md");
        Write("books/orly/loose.md");

        await AddSource("c", "books/orly/AI");
        _db.Sources.Add(new Source
        {
            Id = "upload", CorpusId = "c", Kind = SourceKind.Upload,
            RootPath = null, CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // One workspace source and one upload source is one source under books/orly, not
        // two, so there is nothing to infer about that directory.
        (await Coverage()).Gaps.ShouldBeEmpty();
    }
}
