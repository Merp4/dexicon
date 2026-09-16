using System.Text;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The document library's load-bearing properties: bytes stored once, extraction cached
/// against them, and chunking owned by the corpus. See docs/04-ingestion.md.
/// </summary>
public sealed class DocumentLibraryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private DocumentService _documents = null!;
    private string _dataPath = null!;

    public async Task InitializeAsync()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"dexicon-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataPath);

        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Tenants.Add(new Tenant { Id = "t", DisplayName = "t", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var options = Options.Create(new DexiconOptions { Storage = new StorageOptions { DataPath = _dataPath } });
        _documents = new DocumentService(_db, options, NullLogger<DocumentService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
    }

    private Corpus AddCorpus(string name, int chunkSize, int overlap, string boundary = "blank-line")
    {
        var c = new Corpus
        {
            Id = $"id-{name}",
            TenantId = "t",
            Name = name,
            Visibility = CorpusVisibility.Private,
            EmbeddingModel = "nomic-embed-text",
            EmbeddingDimensions = 768,
            CollectionName = "dexicon__nomic-embed-text__768",
            ChunkSize = chunkSize,
            ChunkOverlap = overlap,
            BoundaryMode = boundary,
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Corpora.Add(c);
        _db.SaveChanges();
        return c;
    }

    private static MemoryStream TextStream(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task SameBytesUploadedTwice_AreStoredOnce_AndExtractedOnce()
    {
        var content = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i} of the document."));

        var first = await _documents.StoreAsync(TextStream(content), "notes.md");
        var second = await _documents.StoreAsync(TextStream(content), "a-different-name.md");

        second.Sha256.ShouldBe(first.Sha256);
        second.AlreadyExisted.ShouldBeTrue();

        (await _db.Blobs.CountAsync()).ShouldBe(1);
        (await _db.BlobTexts.CountAsync()).ShouldBe(1);

        var onDisk = Directory.EnumerateFiles(Path.Combine(_dataPath, "blobs"), "*", SearchOption.AllDirectories).ToList();
        onDisk.Count.ShouldBe(1, "the bytes must be stored once, not once per upload");
    }

    [Fact]
    public async Task OneDocument_AttachedToTwoCorpora_IsChunkedDifferentlyInEach()
    {
        // The headline capability. Extraction happens once; each corpus chunks it its
        // own way, and the two must not be able to see each other's work.
        var content = string.Join("\n\n", Enumerable.Range(1, 120)
            .Select(i => $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("word", 30))));

        var stored = await _documents.StoreAsync(TextStream(content), "shared.md");

        var coarse = AddCorpus("coarse", 768, 100);
        var fine = AddCorpus("fine", 256, 40);

        await _documents.AttachAsync(coarse, stored.Sha256, "shared.md");
        await _documents.AttachAsync(fine, stored.Sha256, "shared.md");

        var text = await _documents.TextFor(stored.Sha256);
        text.ShouldNotBeNull();

        var coarseChunks = CodeChunker.Chunk("shared.md", text.Text, coarse.ChunkSize, coarse.ChunkOverlap, "blank-line");
        var fineChunks = CodeChunker.Chunk("shared.md", text.Text, fine.ChunkSize, fine.ChunkOverlap, "blank-line");

        fineChunks.Count.ShouldBeGreaterThan(coarseChunks.Count,
            "the same document must chunk differently under different corpus settings");

        // Still one blob and one extraction behind both.
        (await _db.Blobs.CountAsync()).ShouldBe(1);
        (await _db.BlobTexts.CountAsync()).ShouldBe(1);
        (await _db.Files.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task ChunkingFingerprint_DiffersPerCorpus_AndChangesWithTheSettings()
    {
        var coarse = AddCorpus("coarse", 768, 100);
        var fine = AddCorpus("fine", 256, 40);
        const string blob = "abc123";

        var a = CorpusIndexer.ChunkingFingerprint(coarse, blob);
        var b = CorpusIndexer.ChunkingFingerprint(fine, blob);
        a.ShouldNotBe(b, "two corpora must not mistake each other's chunking for their own");

        coarse.ChunkSize = 512;
        CorpusIndexer.ChunkingFingerprint(coarse, blob).ShouldNotBe(a,
            "changing a chunk setting must invalidate the existing chunks");
    }

    [Fact]
    public async Task AttachingTheSameBlobTwiceToOneCorpus_RenamesRatherThanDuplicating()
    {
        // Regression: matching on name alone let identical bytes attach twice under two
        // spellings, so the content was embedded twice and every search returned each
        // hit twice. Observed with a filename mangled by one client and correct from
        // another.
        var stored = await _documents.StoreAsync(TextStream("some content here\n\nand more"), "doc.md");
        var corpus = AddCorpus("one", 768, 100);

        var first = await _documents.AttachAsync(corpus, stored.Sha256, "mangled-name.md");
        var second = await _documents.AttachAsync(corpus, stored.Sha256, "correct-name.md");

        second.Id.ShouldBe(first.Id, "the second attach should rename the first, not create a sibling");
        second.RelativePath.ShouldBe("correct-name.md");
        second.ContentHash.ShouldBeNull("a rename invalidates chunks keyed by the old path");

        var files = await _db.Files.Where(f => f.BlobSha256 == stored.Sha256).ToListAsync();
        files.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DetachingFromOneCorpus_LeavesTheBlobForTheOther()
    {
        var stored = await _documents.StoreAsync(TextStream("shared content\n\nmore"), "shared.md");
        var a = AddCorpus("a", 768, 100);
        var b = AddCorpus("b", 256, 40);

        await _documents.AttachAsync(a, stored.Sha256, "shared.md");
        await _documents.AttachAsync(b, stored.Sha256, "shared.md");

        (await _documents.DetachAsync(a.Id, (await _db.Files.FirstAsync(f => f.Source!.CorpusId == a.Id)).Id))
            .ShouldBeTrue();

        (await _db.Blobs.CountAsync()).ShouldBe(1, "the blob survives — another corpus still holds it");
        (await _db.BlobTexts.CountAsync()).ShouldBe(1);
        (await _db.Files.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task EmptyUpload_IsRejectedWithAReason()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _documents.StoreAsync(new MemoryStream([]), "empty.txt"));
        ex.Message.ShouldContain("empty");
    }

    [Fact]
    public async Task PlainTextUpload_IsExtractedNotTreatedAsAnUnknownBinary()
    {
        var stored = await _documents.StoreAsync(TextStream("# Title\n\nSome prose."), "readme.md");
        stored.ExtractedChars.ShouldBeGreaterThan(0);
        stored.EmptyReason.ShouldBeNull();

        var text = await _documents.TextFor(stored.Sha256);
        text!.Text.ShouldContain("Some prose.");
        text.Extractor.ShouldBe("PlainText");
    }

    [Fact]
    public async Task AFreshlyAttachedDocumentIsPending_NotIndexed()
    {
        // Regression: FileStatus.Indexed was the enum's zero value, so an attachment was
        // born claiming to be indexed. The library then showed every just-uploaded
        // document as "indexed", next to a chunk count of 0 — and a corpus whose
        // indexing job was interrupted looked finished.
        var corpus = AddCorpus("books", 512, 64);
        var stored = await _documents.StoreAsync(TextStream("some prose to chunk"), "book.md");

        var file = await _documents.AttachAsync(corpus, stored.Sha256, "book.md");

        file.Status.ShouldBe(FileStatus.Pending);
        file.ChunkCount.ShouldBe(0);
        file.ContentHash.ShouldBeNull();
    }

    [Fact]
    public void PendingIsTheDefaultFileStatus()
    {
        // The property this rests on: the safe state must be the one you get by
        // forgetting to set it.
        default(FileStatus).ShouldBe(FileStatus.Pending);
    }

    [Fact]
    public async Task RenamingAnAttachmentMakesItPendingAgain()
    {
        // A rename invalidates the chunks, which are keyed by file path. Leaving the
        // status at Indexed would claim chunks exist under a name nothing wrote.
        var corpus = AddCorpus("books", 512, 64);
        var stored = await _documents.StoreAsync(TextStream("some prose to chunk"), "book.md");

        var file = await _documents.AttachAsync(corpus, stored.Sha256, "book.md");
        file.Status = FileStatus.Indexed;
        file.ContentHash = "whatever-the-last-index-wrote";
        file.ChunkCount = 3;
        await _db.SaveChangesAsync();

        var renamed = await _documents.AttachAsync(corpus, stored.Sha256, "better-name.md");

        renamed.Id.ShouldBe(file.Id, "a rename is a rename, not a second attachment");
        renamed.Status.ShouldBe(FileStatus.Pending);
        renamed.ContentHash.ShouldBeNull();
    }
}
