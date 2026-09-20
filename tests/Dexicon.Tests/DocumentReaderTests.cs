using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Extraction;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Reaching a document whole, given a corpus, a chunk set and a path.
///
/// The text existing is not the same as the text being reachable. `file_texts` is keyed
/// on a hash of the file's bytes and the extractor that read them, which the indexer
/// computes and would otherwise throw away, so without the hash recorded against the
/// file the table is write-only: the cache saves the indexer work and gives a reader
/// nothing. These cover the path from "corpus, set and file path" to "the document",
/// which the file viewer, the MCP resource and `get_context` now walk instead of
/// stitching chunk payloads back together.
///
/// The hash is per chunk set, and the third test is why: a job can target one set while
/// the others keep serving.
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

    /// <summary>A corpus with one workspace source and one chunk set.</summary>
    private static async Task<(string CorpusId, string SourceId, string SetId)> CorpusAsync(
        CatalogDbContext db, SourceKind kind = SourceKind.Workspace)
    {
        var corpus = new Corpus
        {
            Id = Ulid.NewUlid().ToString(), Name = $"c{Ulid.NewUlid()}", CreatedUtc = DateTime.UtcNow,
        };
        var source = new Source
        {
            Id = Ulid.NewUlid().ToString(), CorpusId = corpus.Id, Kind = kind, RootPath = "books",
        };
        var set = Set(corpus.Id, "default");
        db.Corpora.Add(corpus);
        db.Sources.Add(source);
        db.ChunkSets.Add(set);
        await db.SaveChangesAsync();
        return (corpus.Id, source.Id, set.Id);
    }

    private static ChunkSet Set(string corpusId, string name) => new()
    {
        Id = Ulid.NewUlid().ToString(),
        CorpusId = corpusId,
        Name = name,
        EmbeddingModel = "m",
        EmbeddingDimensions = 8,
        CollectionName = "c",
        BoundaryMode = "blank-line",
        CreatedUtc = DateTime.UtcNow,
    };

    /// <summary>A file, plus what one set made of it.</summary>
    private static IndexedFile File(
        string sourceId, string path, string setId, string? sha = null, string? blob = null)
    {
        var file = new IndexedFile
        {
            Id = Ulid.NewUlid().ToString(),
            SourceId = sourceId,
            RelativePath = path,
            BlobSha256 = blob,
        };
        file.ChunkStates.Add(new FileChunkState
        {
            FileId = file.Id,
            ChunkSetId = setId,
            SourceSha256 = sha,
            Status = FileStatus.Indexed,
        });
        return file;
    }

    private static FileText Text(string sha, string extractor, string body, string? units = null,
                                 string? title = null, int? version = null) => new()
    {
        Sha256 = sha,
        Extractor = extractor,
        Text = body,
        UnitsJson = units,
        Title = title,
        ExtractedChars = body.Length,
        ExtractorVersion = version ?? ExtractorVersions.Current,
        ExtractedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task AWorkspaceFileResolvesToItsExtractedText()
    {
        await using var db = Db();
        var (corpusId, sourceId, setId) = await CorpusAsync(db);

        db.FileTexts.Add(Text("aa11", "PdfTextExtractor", "the whole book",
            units: """[{"Number":4,"StartOffset":9,"Label":"Chapter 4"}]""", title: "A Book"));
        db.Files.Add(File(sourceId, "x.pdf", setId, sha: "aa11"));
        await db.SaveChangesAsync();

        var body = await new DocumentReader(db).ForAsync(corpusId, setId, "x.pdf", sourceId);

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
        var (corpusId, sourceId, setId) = await CorpusAsync(db, SourceKind.Upload);

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
        db.Files.Add(File(sourceId, "y.pdf", setId, blob: "bb22"));
        await db.SaveChangesAsync();

        var body = await new DocumentReader(db).ForAsync(corpusId, setId, "y.pdf", sourceId);

        body.ShouldNotBeNull();
        body.Text.ShouldBe("the uploaded document");
        body.Store.ShouldBe("blob_texts");
    }

    /// <summary>
    /// A job can target one set while the others keep serving, so reindexing set A must
    /// not change what a reader for set B is given. B's hits carry B's line numbers, and
    /// they address the text B was cut from.
    /// </summary>
    [Fact]
    public async Task EachSetReadsTheTextItsOwnChunksWereCutFrom()
    {
        await using var db = Db();
        var (corpusId, sourceId, oldSet) = await CorpusAsync(db);
        var newSet = Set(corpusId, "rebuilt");
        db.ChunkSets.Add(newSet);

        db.FileTexts.Add(Text("old", "PdfTextExtractor", "the first revision"));
        db.FileTexts.Add(Text("new", "PdfTextExtractor", "the second revision"));

        var file = File(sourceId, "x.pdf", oldSet, sha: "old");
        file.ChunkStates.Add(new FileChunkState
        {
            FileId = file.Id, ChunkSetId = newSet.Id, SourceSha256 = "new", Status = FileStatus.Indexed,
        });
        db.Files.Add(file);
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        (await reader.ForAsync(corpusId, oldSet, "x.pdf", sourceId))!.Text.ShouldBe("the first revision");
        (await reader.ForAsync(corpusId, newSet.Id, "x.pdf", sourceId))!.Text.ShouldBe("the second revision");
    }

    /// <summary>
    /// Which extractor runs is decided by extension, so identical bytes under two
    /// extensions are two different parses. Serving one as the other would hand back an
    /// EPUB's text for a file the corpus calls a DOCX, and record it as indexed.
    /// </summary>
    [Fact]
    public async Task TextParsedByAnotherExtractorIsNotServedForThisOne()
    {
        await using var db = Db();
        var (corpusId, sourceId, setId) = await CorpusAsync(db);

        db.FileTexts.Add(Text("shared", "EpubTextExtractor", "the epub reading"));
        db.Files.Add(File(sourceId, "renamed.docx", setId, sha: "shared"));
        await db.SaveChangesAsync();

        (await new DocumentReader(db).ForAsync(corpusId, setId, "renamed.docx", sourceId))
            .ShouldBeNull();
    }

    /// <summary>
    /// A code file's text is not cached, because reading it is the extraction. The
    /// caller has to fall back rather than read the null as an empty document.
    /// </summary>
    [Fact]
    public async Task AFileWithNoStoredTextReturnsNothingRatherThanEmpty()
    {
        await using var db = Db();
        var (corpusId, sourceId, setId) = await CorpusAsync(db);
        db.Files.Add(File(sourceId, "src/a.cs", setId, sha: "cc33"));
        await db.SaveChangesAsync();

        (await new DocumentReader(db).ForAsync(corpusId, setId, "src/a.cs", sourceId)).ShouldBeNull();
    }

    /// <summary>
    /// A file indexed before the hash was recorded. The next pass over it fills the hash
    /// in, including the pass that skips it as unchanged; until then there is nothing to
    /// look the document up by.
    /// </summary>
    [Fact]
    public async Task AFileIndexedWithoutAHashReturnsNothing()
    {
        await using var db = Db();
        var (corpusId, sourceId, setId) = await CorpusAsync(db);
        db.Files.Add(File(sourceId, "old.pdf", setId));
        await db.SaveChangesAsync();

        (await new DocumentReader(db).ForAsync(corpusId, setId, "old.pdf", sourceId)).ShouldBeNull();
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
        var (corpusId, firstSource, setId) = await CorpusAsync(db);
        var second = new Source
        {
            Id = Ulid.NewUlid().ToString(), CorpusId = corpusId,
            Kind = SourceKind.Workspace, RootPath = "papers",
        };
        db.Sources.Add(second);
        db.FileTexts.Add(Text("dd44", "PdfTextExtractor", "the first book"));
        db.FileTexts.Add(Text("ee55", "PdfTextExtractor", "the second book"));
        db.Files.Add(File(firstSource, "intro.pdf", setId, sha: "dd44"));
        db.Files.Add(File(second.Id, "intro.pdf", setId, sha: "ee55"));
        await db.SaveChangesAsync();

        var reader = new DocumentReader(db);
        (await reader.ForAsync(corpusId, setId, "intro.pdf", sourceId: null)).ShouldBeNull();

        // Named, it resolves: the ambiguity is in the path, not in the data.
        (await reader.ForAsync(corpusId, setId, "intro.pdf", firstSource))!
            .Text.ShouldBe("the first book");
    }

    [Fact]
    public async Task APathInAnotherCorpusIsNotFound()
    {
        await using var db = Db();
        var (_, sourceId, setId) = await CorpusAsync(db);
        var (otherCorpus, _, otherSet) = await CorpusAsync(db);
        db.FileTexts.Add(Text("ff66", "PdfTextExtractor", "somebody else's book"));
        db.Files.Add(File(sourceId, "x.pdf", setId, sha: "ff66"));
        await db.SaveChangesAsync();

        (await new DocumentReader(db).ForAsync(otherCorpus, otherSet, "x.pdf", sourceId: null))
            .ShouldBeNull();
    }

    public void Dispose()
    {
        try { System.IO.File.Delete(_path); } catch (IOException) { }
    }
}
