using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Extraction;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Reaching a document whole, given a corpus and a path.
///
/// The text existing is not the same as the text being reachable. `file_texts` is keyed
/// on a hash of the file's bytes, which the indexer computes and would otherwise throw
/// away, so without the hash on the file row the table is write-only: the cache saves the
/// indexer work and gives a reader nothing. These cover the path from "corpus and file
/// path" to "the document", which is what the file viewer and the MCP resource now walk
/// instead of stitching chunk payloads back together.
/// </summary>
public sealed class DocumentReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"docread-{Guid.NewGuid():N}.db");

    private CatalogDbContext Db()
    {
        var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite($"Data Source={_path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task<(string CorpusId, string SourceId)> CorpusAsync(
        CatalogDbContext db, SourceKind kind = SourceKind.Workspace)
    {
        var corpus = new Corpus { Id = Ulid.NewUlid().ToString(), Name = $"c{Ulid.NewUlid()}", CreatedUtc = DateTime.UtcNow };
        var source = new Source { Id = Ulid.NewUlid().ToString(), CorpusId = corpus.Id, Kind = kind, RootPath = "books" };
        db.Corpora.Add(corpus);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return (corpus.Id, source.Id);
    }

    private static IndexedFile File(string sourceId, string path, string? sha = null, string? blob = null) =>
        new()
        {
            Id = Ulid.NewUlid().ToString(),
            SourceId = sourceId,
            RelativePath = path,
            Sha256 = sha,
            BlobSha256 = blob,
        };

    [Fact]
    public async Task AWorkspaceFileResolvesToItsExtractedText()
    {
        await using var db = Db();
        var (corpusId, sourceId) = await CorpusAsync(db);

        db.FileTexts.Add(new FileText
        {
            Sha256 = "aa11",
            Text = "the whole book",
            UnitsJson = """[{"Number":4,"StartOffset":9,"Label":"Chapter 4"}]""",
            Title = "A Book",
            ExtractedChars = 14,
            Extractor = "PdfTextExtractor",
            ExtractorVersion = ExtractorVersions.Current,
            ExtractedUtc = DateTime.UtcNow,
        });
        db.Files.Add(File(sourceId, "x.pdf", sha: "aa11"));
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        var file = await reader.FileAtAsync(corpusId, "x.pdf", sourceId);
        var body = await reader.ForAsync(file.ShouldNotBeNull());

        body.ShouldNotBeNull();
        body.Text.ShouldBe("the whole book");
        body.Title.ShouldBe("A Book");
        body.Store.ShouldBe("file_texts");
        body.Units.ShouldHaveSingleItem().Label.ShouldBe("Chapter 4");
    }

    [Fact]
    public async Task AnUploadResolvesToItsBlobText()
    {
        await using var db = Db();
        var (corpusId, sourceId) = await CorpusAsync(db, SourceKind.Upload);

        db.Blobs.Add(new Blob { Sha256 = "bb22", SizeBytes = 3, CreatedUtc = DateTime.UtcNow });
        db.BlobTexts.Add(new BlobText
        {
            Sha256 = "bb22",
            Text = "the uploaded document",
            ExtractedChars = 21,
            Extractor = "PdfTextExtractor",
            ExtractorVersion = ExtractorVersions.Current,
            ExtractedUtc = DateTime.UtcNow,
        });
        db.Files.Add(File(sourceId, "y.pdf", blob: "bb22"));
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        var file = await reader.FileAtAsync(corpusId, "y.pdf", sourceId);
        var body = await reader.ForAsync(file.ShouldNotBeNull());

        body.ShouldNotBeNull();
        body.Text.ShouldBe("the uploaded document");
        body.Store.ShouldBe("blob_texts");
    }

    /// <summary>
    /// A code file's text is not cached, because reading it is the extraction. The
    /// caller has to fall back rather than read the null as an empty document.
    /// </summary>
    [Fact]
    public async Task AFileWithNoStoredTextReturnsNothingRatherThanEmpty()
    {
        await using var db = Db();
        var (corpusId, sourceId) = await CorpusAsync(db);
        db.Files.Add(File(sourceId, "src/a.cs", sha: "cc33"));
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        var file = await reader.FileAtAsync(corpusId, "src/a.cs", sourceId);
        (await reader.ForAsync(file.ShouldNotBeNull())).ShouldBeNull();
    }

    /// <summary>
    /// A file indexed before the hash was recorded. Re-indexing fills it in; until then
    /// there is nothing to look the document up by, and saying so is the only honest
    /// answer.
    /// </summary>
    [Fact]
    public async Task AFileIndexedWithoutAHashReturnsNothing()
    {
        await using var db = Db();
        var (corpusId, sourceId) = await CorpusAsync(db);
        db.Files.Add(File(sourceId, "old.pdf"));
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        var file = await reader.FileAtAsync(corpusId, "old.pdf", sourceId);
        (await reader.ForAsync(file.ShouldNotBeNull())).ShouldBeNull();
    }

    /// <summary>
    /// Two sources of one corpus can hold the same relative path, which is why a path
    /// alone does not identify a document. Serving either one's text under the other's
    /// name is the failure this refuses.
    /// </summary>
    [Fact]
    public async Task APathTwoSourcesShareIsNotResolvedByPathAlone()
    {
        await using var db = Db();
        var (corpusId, firstSource) = await CorpusAsync(db);
        var second = new Source
        {
            Id = Ulid.NewUlid().ToString(), CorpusId = corpusId,
            Kind = SourceKind.Workspace, RootPath = "papers",
        };
        db.Sources.Add(second);
        db.Files.Add(File(firstSource, "intro.pdf", sha: "dd44"));
        db.Files.Add(File(second.Id, "intro.pdf", sha: "ee55"));
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        (await reader.FileAtAsync(corpusId, "intro.pdf", sourceId: null)).ShouldBeNull();

        // Named, it resolves: the ambiguity is in the path, not in the data.
        var named = await reader.FileAtAsync(corpusId, "intro.pdf", firstSource);
        named.ShouldNotBeNull().Sha256.ShouldBe("dd44");
    }

    [Fact]
    public async Task APathInAnotherCorpusIsNotFound()
    {
        await using var db = Db();
        var (_, sourceId) = await CorpusAsync(db);
        var (otherCorpus, _) = await CorpusAsync(db);
        db.Files.Add(File(sourceId, "x.pdf", sha: "ff66"));
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        (await reader.FileAtAsync(otherCorpus, "x.pdf", sourceId: null)).ShouldBeNull();
    }

    public void Dispose()
    {
        try { System.IO.File.Delete(_path); } catch (IOException) { }
    }
}
