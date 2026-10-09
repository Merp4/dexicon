using System.Security.Cryptography;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// What an extraction that outruns its deadline leaves behind. A timeout says how busy the host was, so
/// it is not recorded as the document's text: a new blob is not stored, a cached row is kept as it was,
/// and the next attempt extracts again. Other extraction failures are still recorded as the blob's reason.
/// </summary>
public sealed class DocumentExtractionTimeoutTests
{
    private static readonly byte[] Bytes = [.. Enumerable.Repeat((byte)'s', 300)];

    private static string ShaOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static DocumentService ServiceWith(
        IndexingHarness harness, CatalogDbContext db, ITextExtractor extractor, ILogger<DocumentService>? log = null) =>
        new(db,
            Options.Create(new DexiconOptions
            {
                Storage = new StorageOptions { DataPath = harness.DataPath },
                Indexing = new IndexingOptions { ExtractionTimeoutSeconds = 1 },
            }),
            log ?? NullLogger<DocumentService>.Instance)
        {
            ExtractorFor = name => name.EndsWith(".slow", StringComparison.Ordinal) ? extractor : null,
        };

    /// <summary>Reads a byte at a time with a pause between reads, as a parser working through a damaged file does.</summary>
    private class ReadingExtractor : ITextExtractor
    {
        public TimeSpan Pause { get; init; }

        public char Marker { get; init; } = 'x';

        public int BytesRead { get; private set; }

        public int Calls { get; private set; }

        /// <summary>Runs when the extraction starts, for a test that acts while the extractor is working.</summary>
        public Action? OnStart { get; init; }

        public bool CanHandle(string extension) => extension == ".slow";

        public virtual ExtractedText Extract(Stream content, string fileName)
        {
            Calls++;
            OnStart?.Invoke();
            BytesRead = 0;
            var one = new byte[1];
            while (content.Read(one, 0, 1) == 1)
            {
                BytesRead++;
                if (Pause > TimeSpan.Zero) Thread.Sleep(Pause);
            }

            return new ExtractedText(new string(Marker, BytesRead), []);
        }
    }

    /// <summary>A 300 byte file at 20 ms a byte is six seconds of reading against the one second budget.</summary>
    private static ReadingExtractor Slow(char marker = 'n') => new() { Pause = TimeSpan.FromMilliseconds(20), Marker = marker };

    private static ReadingExtractor Fast(char marker = 'g') => new() { Marker = marker };

    /// <summary>Wraps the timeout in an extraction failure of its own, as PdfPig's handlers do.</summary>
    private sealed class WrappingExtractor : ReadingExtractor
    {
        public override ExtractedText Extract(Stream content, string fileName)
        {
            try { return base.Extract(content, fileName); }
            catch (ExtractionTimeoutException ex) { throw new ExtractionFailedException("could not be read as a document", ex); }
        }
    }

    /// <summary>Catches the timeout and returns what it had.</summary>
    private sealed class SwallowingExtractor : ReadingExtractor
    {
        public override ExtractedText Extract(Stream content, string fileName)
        {
            try { return base.Extract(content, fileName); }
            catch (ExtractionTimeoutException) { return new ExtractedText("partial", []); }
        }
    }

    private sealed class BrokenExtractor : ITextExtractor
    {
        public bool CanHandle(string extension) => extension == ".slow";

        public ExtractedText Extract(Stream content, string fileName) =>
            throw new UnreadableDocumentException("could not be read as a document");
    }

    /// <summary>Fails the way ExtractionFailures.Of reports an I/O error: an extraction failure that is not a verdict on the file.</summary>
    private sealed class EnvironmentFailingExtractor : ITextExtractor
    {
        public bool CanHandle(string extension) => extension == ".slow";

        public ExtractedText Extract(Stream content, string fileName) =>
            throw new ExtractionFailedException("could not be read: disk fault", new IOException("disk fault"));
    }

    private sealed class RecordingLog : ILogger<DocumentService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task AStoreWhoseExtractionTimesOutLeavesNothingTrackedOrSaved()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var slow = Slow();
        var documents = ServiceWith(harness, db, slow);

        await Should.ThrowAsync<ExtractionTimeoutException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        slow.BytesRead.ShouldBeInRange(1, Bytes.Length - 1, "the exception came from the deadline cutting the reading short");
        db.ChangeTracker.Entries().ShouldBeEmpty("a tracked blob would be saved by the next save on this context");
        (await db.SaveChangesAsync()).ShouldBe(0);
        (await db.Blobs.CountAsync()).ShouldBe(0);
        (await db.BlobTexts.CountAsync()).ShouldBe(0);
        File.Exists(documents.PathFor(ShaOf(Bytes))).ShouldBeTrue("the bytes stay in the blob store, and a retry reuses them");
    }

    [Fact]
    public async Task AFileAttachedAfterATimedOutStoreDoesNotSaveTheTimedOutBlobWithIt()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = ServiceWith(harness, db, Slow());
        var corpus = await db.Corpora.SingleAsync();
        await Should.ThrowAsync<ExtractionTimeoutException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        var stored = await documents.StoreAsync(new MemoryStream("another document"u8.ToArray()), "next.txt");
        await documents.AttachAsync(corpus, stored.Sha256, "next.txt");

        (await db.Blobs.Select(b => b.Sha256).ToListAsync()).ShouldBe([stored.Sha256]);
        (await db.BlobTexts.Select(t => t.Sha256).ToListAsync()).ShouldBe([stored.Sha256]);
    }

    [Fact]
    public async Task AWrappedTimeoutIsNotRecordedAsTheBlobsReason()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var documents = ServiceWith(harness, db, new WrappingExtractor { Pause = TimeSpan.FromMilliseconds(20) });

        await Should.ThrowAsync<ExtractionTimeoutException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.Blobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AnExtractorThatCaughtTheTimeoutAndReturnedPartialTextIsNotRecorded()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var documents = ServiceWith(harness, db, new SwallowingExtractor { Pause = TimeSpan.FromMilliseconds(20) });

        await Should.ThrowAsync<ExtractionTimeoutException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        (await db.Blobs.CountAsync()).ShouldBe(0);
        (await db.BlobTexts.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AnOrdinaryExtractionFailureIsStillRecordedAsTheBlobsEmptyReason()
    {
        // The control: only a timeout is left unrecorded.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var documents = ServiceWith(harness, db, new BrokenExtractor());

        var stored = await documents.StoreAsync(new MemoryStream(Bytes), "broken.slow");

        stored.EmptyReason.ShouldBe("could not be read as a document");
        (await db.BlobTexts.SingleAsync()).EmptyReason.ShouldBe("could not be read as a document");
    }

    [Fact]
    public async Task UploadingTheSameBytesAfterATimeoutExtractsThemAgain()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        await Should.ThrowAsync<ExtractionTimeoutException>(
            () => ServiceWith(harness, db, Slow()).StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        var again = await ServiceWith(harness, db, Fast()).StoreAsync(new MemoryStream(Bytes), "stuck.slow");

        again.AlreadyExisted.ShouldBeFalse("the first upload stored nothing to reuse");
        again.EmptyReason.ShouldBeNull();
        again.ExtractedChars.ShouldBe(Bytes.Length);
        (await db.BlobTexts.SingleAsync()).Text.ShouldBe(new string('g', Bytes.Length));
    }

    /// <summary>A stored document whose row was written by an older extractor, so the next read extracts it again.</summary>
    private static async Task<string> StoreStaleAsync(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        var stored = await ServiceWith(harness, db, Fast('g')).StoreAsync(new MemoryStream(Bytes), "doc.slow");
        await db.BlobTexts.Where(t => t.Sha256 == stored.Sha256)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExtractorVersion, 0));
        return stored.Sha256;
    }

    [Fact]
    public async Task AStaleRowWhoseReExtractionTimesOutKeepsItsTextVersionAndReason()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        var log = new RecordingLog();
        await using var db = harness.NewContext();

        var returned = await ServiceWith(harness, db, Slow('n'), log).CurrentTextFor(sha, "doc.slow");

        returned.ShouldNotBeNull().Text.ShouldBe(new string('g', Bytes.Length), "the caller carries on with the text it had");
        await using var check = harness.NewContext();
        var row = await check.BlobTexts.AsNoTracking().SingleAsync();
        row.Text.ShouldBe(new string('g', Bytes.Length));
        row.ExtractorVersion.ShouldBe(0, "still stale, so a later pass tries again");
        row.EmptyReason.ShouldBeNull();
        row.ExtractedChars.ShouldBe(Bytes.Length);
        var warning = log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("doc.slow");
        warning.Message.ShouldContain(sha[..12]);
    }

    [Fact]
    public async Task AStaleRowIsExtractedOnALaterPassOnceTheTimeoutHasPassed()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using (var slowDb = harness.NewContext())
            await ServiceWith(harness, slowDb, Slow('n')).CurrentTextFor(sha, "doc.slow");
        await using var db = harness.NewContext();

        var returned = await ServiceWith(harness, db, Fast('n')).CurrentTextFor(sha, "doc.slow");

        var text = returned.ShouldNotBeNull();
        text.Text.ShouldBe(new string('n', Bytes.Length));
        text.ExtractorVersion.ShouldBe(ExtractorVersions.Current);
        await using var check = harness.NewContext();
        (await check.BlobTexts.AsNoTracking().SingleAsync()).ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public async Task ATimedOutStoreIsLoggedWithTheFileAndTheBudget()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var log = new RecordingLog();

        await Should.ThrowAsync<ExtractionTimeoutException>(
            () => ServiceWith(harness, db, Slow(), log).StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        var warning = log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("stuck.slow");
        warning.Message.ShouldContain("did not finish within 1 s");
        warning.Message.ShouldContain("no document record was created");
    }

    [Fact]
    public async Task AFailureThatIsNotAVerdictOnTheFileStoresNothingAndLeavesNothingTracked()
    {
        // An I/O error says how the host was. Recorded as the blob's reason it would be read back as
        // the document's, and nothing extracts a row at the current version again.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var documents = ServiceWith(harness, db, new EnvironmentFailingExtractor());

        var thrown = await Should.ThrowAsync<ExtractionFailedException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "flaky.slow"));

        thrown.ShouldNotBeOfType<ExtractionTimeoutException>();
        thrown.ShouldNotBeOfType<UnreadableDocumentException>();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.Blobs.CountAsync()).ShouldBe(0);
        (await db.BlobTexts.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AStaleRowWhoseReExtractionFailsOnAnIoErrorKeepsItsTextAndIsNotEmptied()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();

        var returned = await ServiceWith(harness, db, new EnvironmentFailingExtractor()).CurrentTextFor(sha, "doc.slow");

        returned.ShouldNotBeNull().Text.ShouldBe(new string('g', Bytes.Length));
        await using var check = harness.NewContext();
        var row = await check.BlobTexts.AsNoTracking().SingleAsync();
        row.Text.ShouldBe(new string('g', Bytes.Length));
        row.ExtractorVersion.ShouldBe(0, "still stale, so a later pass tries again");
        row.EmptyReason.ShouldBeNull();
    }

    [Fact]
    public async Task AStaleRowWhoseDocumentIsNowUnreadableRecordsThatVerdict()
    {
        // The control: a verdict on the bytes is still written over the old text.
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();

        var returned = await ServiceWith(harness, db, new BrokenExtractor()).CurrentTextFor(sha, "doc.slow");

        returned.ShouldNotBeNull().EmptyReason.ShouldBe("could not be read as a document");
        await using var check = harness.NewContext();
        (await check.BlobTexts.AsNoTracking().SingleAsync()).ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public async Task AStoreThatTimesOutAfterAnotherUploadSavedTheSameBytesReportsThatBlob()
    {
        // The other upload runs inside this one's extraction, so it saves first without a sleep to line them up.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        await using var otherDb = harness.NewContext();
        var other = ServiceWith(harness, otherDb, Fast('g'));
        var slow = new ReadingExtractor
        {
            Pause = TimeSpan.FromMilliseconds(20),
            OnStart = () => other.StoreAsync(new MemoryStream(Bytes), "stuck.slow").GetAwaiter().GetResult(),
        };

        var stored = await ServiceWith(harness, db, slow).StoreAsync(new MemoryStream(Bytes), "stuck.slow");

        slow.BytesRead.ShouldBeLessThan(Bytes.Length, "this upload did time out");
        stored.AlreadyExisted.ShouldBeTrue();
        stored.ExtractedChars.ShouldBe(Bytes.Length);
        (await db.Blobs.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AHashThatFailedThisPassIsNotExtractedAgainInTheSamePass()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();
        var slow = Slow('n');
        var documents = ServiceWith(harness, db, slow);
        var failedThisPass = new HashSet<string>();

        await documents.CurrentTextFor(sha, "doc.slow", failedThisPass);
        var again = await documents.CurrentTextFor(sha, "doc.slow", failedThisPass);

        slow.Calls.ShouldBe(1, "the second chunk set did not pay the budget again");
        again.ShouldNotBeNull().Text.ShouldBe(new string('g', Bytes.Length));
        failedThisPass.ShouldBe([sha]);
    }

    [Fact]
    public async Task WithNoPassSetEachCallExtractsAgain()
    {
        // The control for the test above.
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();
        var slow = Slow('n');
        var documents = ServiceWith(harness, db, slow);

        await documents.CurrentTextFor(sha, "doc.slow");
        await documents.CurrentTextFor(sha, "doc.slow");

        slow.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task AJobOverTwoChunkSetsExtractsAStaleDocumentThatTimesOutOnce()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using (var setup = harness.NewContext())
        {
            var corpus = await setup.Corpora.SingleAsync();
            var documents = ServiceWith(harness, setup, Fast('g'));
            var stored = await documents.StoreAsync(new MemoryStream(Bytes), "doc.slow");
            await documents.AttachAsync(corpus, stored.Sha256, "doc.slow");
            await setup.BlobTexts.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExtractorVersion, 0));
        }

        var slow = Slow('n');
        await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, slow));

        slow.Calls.ShouldBe(1, "two chunk sets read the same stale document in one job");
        await using var check = harness.NewContext();
        (await check.BlobTexts.AsNoTracking().SingleAsync()).ExtractorVersion.ShouldBe(0);
    }
}
