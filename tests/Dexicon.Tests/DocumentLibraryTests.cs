using System.Text;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
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

    /// <summary>A corpus with one default chunk set, which is how the API creates them.</summary>
    private Corpus AddCorpus(string name, int chunkSize, int overlap, string boundary = "blank-line")
    {
        var c = new Corpus
        {
            Id = $"id-{name}",
            TenantId = "t",
            Name = name,
            Visibility = CorpusVisibility.Private,
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        };
        c.ChunkSets.Add(AddSet(c, "default", chunkSize, overlap, boundary, isDefault: true));
        _db.Corpora.Add(c);
        _db.SaveChanges();
        return c;
    }

    private static ChunkSet AddSet(Corpus c, string name, int chunkSize, int overlap,
        string boundary = "blank-line", bool isDefault = false, string model = "nomic-embed-text") => new()
    {
        Id = $"set-{c.Id}-{name}",
        CorpusId = c.Id,
        Name = name,
        EmbeddingModel = model,
        EmbeddingDimensions = 768,
        CollectionName = $"dexicon__{model}__768",
        ChunkSize = chunkSize,
        ChunkOverlap = overlap,
        BoundaryMode = boundary,
        IsDefault = isDefault,
        State = CorpusState.Ready,
        CreatedUtc = DateTime.UtcNow,
    };

    /// <summary>The corpus's default set — what an unqualified search reaches.</summary>
    private static ChunkSet DefaultSet(Corpus c) => c.ChunkSets.First(s => s.IsDefault);

    private FileChunkState StateOf(IndexedFile file, Corpus corpus) =>
        _db.FileChunkStates.Single(s => s.FileId == file.Id && s.ChunkSetId == DefaultSet(corpus).Id);

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

        var coarseChunks = CodeChunker.Chunk("shared.md", text.Text, DefaultSet(coarse).Options());
        var fineChunks = CodeChunker.Chunk("shared.md", text.Text, DefaultSet(fine).Options());

        fineChunks.Count.ShouldBeGreaterThan(coarseChunks.Count,
            "the same document must chunk differently under different chunk sets");

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

        var a = CorpusIndexer.ChunkingFingerprint(DefaultSet(coarse), blob, ModelTemplates.Raw);
        var b = CorpusIndexer.ChunkingFingerprint(DefaultSet(fine), blob, ModelTemplates.Raw);
        a.ShouldNotBe(b, "two chunk sets must not mistake each other's chunking for their own");

        DefaultSet(coarse).ChunkSize = 512;
        CorpusIndexer.ChunkingFingerprint(DefaultSet(coarse), blob, ModelTemplates.Raw).ShouldNotBe(a,
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
        StateOf(second, corpus).ContentHash.ShouldBeNull(
            "a rename invalidates chunks keyed by the old path");

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
    public async Task OneCorpusCanHoldTwoChunkSetsOverTheSameDocument()
    {
        // The headline capability of chunk sets: two chunkings, one document library, one
        // set of grants. Before, this meant duplicating the corpus and its permissions.
        var corpus = AddCorpus("library", 768, 100);
        var fine = AddSet(corpus, "fine", 256, 40);
        _db.ChunkSets.Add(fine);
        await _db.SaveChangesAsync();

        var stored = await _documents.StoreAsync(
            TextStream(string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i}."))), "doc.md");

        var file = await _documents.AttachAsync(corpus, stored.Sha256, "doc.md");

        // One attachment, one blob, one extraction — but outstanding work in BOTH sets.
        (await _db.Files.CountAsync()).ShouldBe(1);
        (await _db.Blobs.CountAsync()).ShouldBe(1);

        var states = await _db.FileChunkStates.Where(s => s.FileId == file.Id).ToListAsync();
        states.Count.ShouldBe(2, "a new attachment is pending in every set, not just the default");
        states.ShouldAllBe(s => s.Status == FileStatus.Pending);

        // And the two sets must not mistake each other's chunking for their own.
        CorpusIndexer.ChunkingFingerprint(DefaultSet(corpus), stored.Sha256, ModelTemplates.Raw)
            .ShouldNotBe(CorpusIndexer.ChunkingFingerprint(fine, stored.Sha256, ModelTemplates.Raw));
    }

    [Fact]
    public async Task AChunkSetOnADifferentModelGetsADifferentFingerprint()
    {
        // The model is part of the fingerprint, so promoting a set on a new model
        // re-embeds rather than trusting vectors from another vector space.
        var corpus = AddCorpus("library", 768, 100);
        var other = AddSet(corpus, "gemma", 768, 100, model: "embeddinggemma");

        CorpusIndexer.ChunkingFingerprint(DefaultSet(corpus), "abc", ModelTemplates.Raw)
            .ShouldNotBe(CorpusIndexer.ChunkingFingerprint(other, "abc", ModelTemplates.Raw));
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

        var state = StateOf(file, corpus);
        state.Status.ShouldBe(FileStatus.Pending);
        state.ChunkCount.ShouldBe(0);
        state.ContentHash.ShouldBeNull();
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
        var state = StateOf(file, corpus);
        state.Status = FileStatus.Indexed;
        state.ContentHash = "whatever-the-last-index-wrote";
        state.ChunkCount = 3;
        await _db.SaveChangesAsync();

        var renamed = await _documents.AttachAsync(corpus, stored.Sha256, "better-name.md");

        renamed.Id.ShouldBe(file.Id, "a rename is a rename, not a second attachment");
        StateOf(renamed, corpus).Status.ShouldBe(FileStatus.Pending);
        StateOf(renamed, corpus).ContentHash.ShouldBeNull();
    }
}
