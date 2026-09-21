using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Extraction;
using Dexicon.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// `POST /api/context` takes its text from the document, as `get_context` does.
///
/// The two disagreed. `get_context` reads the caller's line range out of the extracted
/// text and the chunks only say where to look; this endpoint glued the chunks either
/// side of the hit together. Anything the chunker did not cut — a heading a boundary
/// rule skipped, the gap where an oversized chunk was divided — was missing from a
/// passage that reads as continuous, and the two surfaces answered the same question
/// differently.
///
/// The chunks still decide WHERE. Only the text changes source, so `neighbours` keeps
/// its meaning.
/// </summary>
public sealed class ContextReadsTheDocumentTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ctxdoc-{Guid.NewGuid():N}.db");

    private CatalogDbContext Db()
    {
        var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite($"Data Source={_path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private const string Document =
        "line one\nline two\nline three\nline four\nline five\nline six\n";

    /// <summary>A corpus with one workspace source, one set, and one extracted file.</summary>
    private static async Task<(string CorpusId, string SetId, string SourceId)> SeedAsync(
        CatalogDbContext db, string text = Document, string path = "book.pdf")
    {
        var corpus = new Corpus { Id = Ulid.NewUlid().ToString(), Name = $"c{Ulid.NewUlid()}", CreatedUtc = DateTime.UtcNow };
        var source = new Source { Id = Ulid.NewUlid().ToString(), CorpusId = corpus.Id, Kind = SourceKind.Workspace, RootPath = "books" };
        var set = new ChunkSet
        {
            Id = Ulid.NewUlid().ToString(), CorpusId = corpus.Id, Name = "default",
            EmbeddingModel = "m", EmbeddingDimensions = 8, CollectionName = "c",
            BoundaryMode = "blank-line", CreatedUtc = DateTime.UtcNow,
        };

        var file = new IndexedFile { Id = Ulid.NewUlid().ToString(), SourceId = source.Id, RelativePath = path };
        file.ChunkStates.Add(new FileChunkState
        {
            FileId = file.Id, ChunkSetId = set.Id, SourceSha256 = "sha1", Status = FileStatus.Indexed,
        });

        db.Corpora.Add(corpus);
        db.Sources.Add(source);
        db.ChunkSets.Add(set);
        db.Files.Add(file);
        db.FileTexts.Add(new FileText
        {
            Sha256 = "sha1", Extractor = "PdfTextExtractor", Text = text,
            ExtractedChars = text.Length, ExtractorVersion = ExtractorVersions.Current,
            ExtractedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return (corpus.Id, set.Id, source.Id);
    }

    private static SearchHit Chunk(string sourceId, int index, int startLine, int endLine, string content) =>
        new()
        {
            CorpusId = "unused", SourceId = sourceId,
            FilePath = "book.pdf", ChunkIndex = index,
            StartLine = startLine, EndLine = endLine, Content = content, Score = 1,
        };

    private static ContextService Service(CatalogDbContext db) =>
        new(search: null!, scopes: null!, vectors: null!, documents: new DocumentReader(db));

    /// <summary>
    /// The defect itself. Two chunks that do not touch — line 3 is in neither — used to
    /// produce a passage with line 3 missing and nothing saying so.
    /// </summary>
    [Fact]
    public async Task ThePassageIncludesWhatTheChunksLeftOut()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db);

        var hit = Chunk(sourceId, 0, 1, 2, "line one\nline two");
        var neighbour = Chunk(sourceId, 1, 4, 5, "line four\nline five");

        var candidate = await Service(db).FromDocumentAsync(corpusId, setId, hit, [hit, neighbour]);

        candidate.ShouldNotBeNull();
        var text = string.Concat(candidate.Pieces.Select(p => p.Content));

        text.ShouldContain("line three", Case.Sensitive,
            "line 3 falls between the two chunks and is only in the document");
        text.ShouldContain("line one");
        text.ShouldContain("line five");
        text.ShouldNotContain("line six", Case.Sensitive, "the span ends at line 5");
    }

    /// <summary>
    /// A window per chunk, not one window for the whole span, and the chunk indexes
    /// survive.
    ///
    /// `ContextAssembler` de-duplicates and budgets on `ChunkIndex`. Collapsing a span
    /// into one piece leaves it one index to charge against, so two hits in the same
    /// file are charged for their whole windows even where those cover the same lines,
    /// and the second is dropped by a budget it actually fits. Keeping a piece per chunk
    /// keeps that accounting right; extending each to where the next begins is what
    /// closes the gaps.
    /// </summary>
    [Fact]
    public async Task ThePiecesKeepTheirChunkIndexesAndDoNotOverlap()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db);

        var hit = Chunk(sourceId, 7, 1, 2, "line one\nline two");
        var neighbour = Chunk(sourceId, 8, 4, 5, "line four\nline five");

        var candidate = await Service(db).FromDocumentAsync(corpusId, setId, hit, [hit, neighbour]);

        var pieces = candidate.ShouldNotBeNull().Pieces.OrderBy(p => p.ChunkIndex).ToList();
        pieces.Count.ShouldBe(2);

        pieces.Select(p => p.ChunkIndex).ShouldBe([7, 8], "the indexes the assembler budgets on");

        // Contiguous and non-overlapping: the first runs up to where the second begins,
        // so no line is charged twice and none is missing between them.
        pieces[0].StartLine.ShouldBe(1);
        pieces[0].EndLine.ShouldBe(3);
        pieces[1].StartLine.ShouldBe(4);
        pieces[1].EndLine.ShouldBe(5);
    }

    /// <summary>
    /// The default request, and the one that matters most.
    ///
    /// `Neighbours` defaults to zero, and zero used to take the hits exactly as search
    /// returned them — so the common call never reached the document at all and the fix
    /// helped almost nobody. At zero the span is the hit's own, and the text still comes
    /// from the document.
    /// </summary>
    [Fact]
    public async Task ZeroNeighboursStillReadsFromTheDocument()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db);

        // A chunk whose stored payload differs from the document, which is how the two
        // sources are told apart. Only the document has the real line.
        var hit = Chunk(sourceId, 1, 2, 2, "STALE PAYLOAD");

        var candidate = await Service(db).FromDocumentAsync(corpusId, setId, hit, [hit]);

        candidate.ShouldNotBeNull();
        var piece = candidate.Pieces.ShouldHaveSingleItem();
        piece.Content.Trim().ShouldBe("line two");
        piece.Content.ShouldNotContain("STALE PAYLOAD");
    }

    [Fact]
    public async Task TheSpanIsStillTheChunksSpan()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db);

        var hit = Chunk(sourceId, 1, 3, 3, "line three");

        var candidate = await Service(db).FromDocumentAsync(corpusId, setId, hit, [hit]);

        var piece = candidate.ShouldNotBeNull().Pieces.ShouldHaveSingleItem();
        piece.StartLine.ShouldBe(3);
        piece.EndLine.ShouldBe(3);
        piece.Content.Trim().ShouldBe("line three");
    }

    /// <summary>
    /// Code and plain text on a mount have no cached document — reading them IS the
    /// extraction — so there is nothing to read a window from and the chunks are the
    /// only copy. Returning null is what makes the caller fall back to them, which is
    /// also what `get_context` does, so the two agree on this path as well.
    /// </summary>
    [Fact]
    public async Task WithNoDocumentItDefersToTheChunks()
    {
        await using var db = Db();
        var corpus = new Corpus { Id = Ulid.NewUlid().ToString(), Name = "c", CreatedUtc = DateTime.UtcNow };
        var source = new Source { Id = Ulid.NewUlid().ToString(), CorpusId = corpus.Id, Kind = SourceKind.Workspace, RootPath = "src" };
        var set = new ChunkSet
        {
            Id = Ulid.NewUlid().ToString(), CorpusId = corpus.Id, Name = "default",
            EmbeddingModel = "m", EmbeddingDimensions = 8, CollectionName = "c",
            BoundaryMode = "blank-line", CreatedUtc = DateTime.UtcNow,
        };
        var file = new IndexedFile { Id = Ulid.NewUlid().ToString(), SourceId = source.Id, RelativePath = "book.pdf" };
        file.ChunkStates.Add(new FileChunkState { FileId = file.Id, ChunkSetId = set.Id, Status = FileStatus.Indexed });

        db.Corpora.Add(corpus);
        db.Sources.Add(source);
        db.ChunkSets.Add(set);
        db.Files.Add(file);
        await db.SaveChangesAsync();

        var hit = Chunk(source.Id, 0, 1, 2, "from the chunk");

        (await Service(db).FromDocumentAsync(corpus.Id, set.Id, hit, [hit])).ShouldBeNull();
    }

    /// <summary>
    /// A document that does not reach the lines the chunks name was cut from a different
    /// version of the file. The hit's line numbers address the chunks, so the chunks are
    /// what is honest to return — a window from the newer text would be quotable and
    /// wrong.
    /// </summary>
    [Fact]
    public async Task ADocumentTooShortForTheSpanDefersToTheChunks()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db, text: "only one line\n");

        var hit = Chunk(sourceId, 9, 40, 44, "from a longer version");

        (await Service(db).FromDocumentAsync(corpusId, setId, hit, [hit])).ShouldBeNull();
    }

    /// <summary>
    /// The harder half of the same case, and the one a start-only guard misses.
    ///
    /// `Passage.Window` returns what it could reach, so a document of six lines answers
    /// a request for 4–20 with 4–6. The start is present, so a guard that only asks
    /// "did we get the first line" passes it and returns a passage shorter than its own
    /// citation claims. Only the exact span is usable.
    /// </summary>
    [Fact]
    public async Task ADocumentThatStopsInsideTheSpanDefersToTheChunks()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db);   // six lines

        var hit = Chunk(sourceId, 3, 4, 20, "chunks from a longer version of the file");

        (await Service(db).FromDocumentAsync(corpusId, setId, hit, [hit])).ShouldBeNull(
            "the window would have been 4-6 under a citation claiming 4-20");
    }

    /// <summary>
    /// An oversized line divided by the embedder's refusal gives every piece of it the
    /// same start and end line.
    ///
    /// Taking each chunk in turn made every slice but the last end before it began, so
    /// they were dropped — and with them their indexes. A hit that WAS one of those
    /// slices then left the assembler unable to find the piece it is citing, and fell
    /// back to whichever piece sorted first.
    /// </summary>
    [Fact]
    public async Task ChunksSplitOnOneLineKeepTheHitsOwnIndex()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db);

        // Three slices of line 2, as dividing one long line produces.
        var first = Chunk(sourceId, 10, 2, 2, "first slice");
        var second = Chunk(sourceId, 11, 2, 2, "second slice");
        var third = Chunk(sourceId, 12, 2, 2, "third slice");

        // The hit is the FIRST slice, which the per-chunk loop dropped.
        var candidate = await Service(db).FromDocumentAsync(
            corpusId, setId, first, [first, second, third]);

        var pieces = candidate.ShouldNotBeNull().Pieces;
        pieces.ShouldHaveSingleItem().ChunkIndex.ShouldBe(10,
            "one line is one slice, and it must carry the index of the hit being cited");
        pieces[0].Content.Trim().ShouldBe("line two");
    }

    /// <summary>
    /// Several hits in one book is the ordinary shape of a result, and a document is the
    /// whole extracted text — hundreds of thousands of characters for a technical book.
    /// Reading it once per hit is the same load repeated.
    /// </summary>
    [Fact]
    public async Task TheDocumentIsReadOncePerFileRatherThanOncePerHit()
    {
        await using var db = Db();
        var (corpusId, setId, sourceId) = await SeedAsync(db);

        var cache = new Dictionary<(string Corpus, string Set, string? Source, string Path), DocumentBody?>();
        var service = Service(db);

        foreach (var line in new[] { 1, 2, 3, 4 })
        {
            var hit = Chunk(sourceId, line, line, line, $"chunk {line}");
            (await service.FromDocumentAsync(corpusId, setId, hit, [hit], cache)).ShouldNotBeNull();
        }

        cache.Count.ShouldBe(1, "four hits in one file is one document, read once");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }
}
