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

    /// <param name="registry">Replaces how the extractor is chosen, for a test that needs the real ones or none.</param>
    private static DocumentService ServiceWith(
        IndexingHarness harness, CatalogDbContext db, ITextExtractor? extractor, ILogger<DocumentService>? log = null,
        Func<string, ITextExtractor?>? registry = null) =>
        new(db,
            Options.Create(new DexiconOptions
            {
                Storage = new StorageOptions { DataPath = harness.DataPath },
                Indexing = new IndexingOptions { ExtractionTimeoutSeconds = 1 },
            }),
            log ?? NullLogger<DocumentService>.Instance)
        {
            ExtractorFor = registry ?? (name => name.EndsWith(".slow", StringComparison.Ordinal) ? extractor : null),
        };

    /// <summary>Reads a byte at a time with a pause between reads, as a parser working through a damaged file does.</summary>
    private class ReadingExtractor : ITextExtractor
    {
        public TimeSpan Pause { get; set; }

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
        public int Calls { get; private set; }

        /// <summary>Runs when the extraction starts, for a test that acts while the extractor is working.</summary>
        public Action? OnStart { get; init; }

        public bool CanHandle(string extension) => extension == ".slow";

        public ExtractedText Extract(Stream content, string fileName)
        {
            Calls++;
            OnStart?.Invoke();
            throw new ExtractionFailedException("could not be read: disk fault", new IOException("disk fault"));
        }
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

        slow.BytesRead.ShouldBeLessThan(Bytes.Length, "the exception came from the deadline cutting the reading short");
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

        var thrown = await Should.ThrowAsync<ExtractionTimeoutException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        thrown.InnerException.ShouldBeOfType<ExtractionFailedException>().Message.ShouldBe("could not be read as a document");
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
        warning.Message.ShouldContain("timed out; keeping");
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
        var log = new RecordingLog();
        var documents = ServiceWith(harness, db, new EnvironmentFailingExtractor(), log);

        var thrown = await Should.ThrowAsync<ExtractionFailedException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "flaky.slow"));

        var warning = log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("flaky.slow");
        warning.Message.ShouldContain("failed for a reason that is not a verdict on the file");
        warning.Message.ShouldNotContain("did not finish within");
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
        var log = new RecordingLog();
        await using var db = harness.NewContext();

        var returned = await ServiceWith(harness, db, new EnvironmentFailingExtractor(), log).CurrentTextFor(sha, "doc.slow");

        var warning = log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("failed; keeping");
        warning.Message.ShouldNotContain("timed out");
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

    /// <summary>An extractor whose every attempt fails: by the deadline, or by an I/O error that is not a verdict on the file.</summary>
    private static (ITextExtractor Extractor, Func<int> Calls) Failing(bool ioError)
    {
        if (ioError)
        {
            var broken = new EnvironmentFailingExtractor();
            return (broken, () => broken.Calls);
        }

        var slow = Slow('n');
        return (slow, () => slow.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHashThatFailedThisPassIsNotExtractedAgainInTheSamePass(bool ioError)
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();
        var (extractor, calls) = Failing(ioError);
        var documents = ServiceWith(harness, db, extractor);
        var failedThisPass = new HashSet<string>();

        await documents.CurrentTextFor(sha, "doc.slow", failedThisPass);
        var again = await documents.CurrentTextFor(sha, "doc.slow", failedThisPass);

        calls().ShouldBe(1, "the second chunk set did not pay for the extraction again");
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

    /// <summary>A stale upload attached to a corpus with two chunk sets, for a job that reads it once per set.</summary>
    private static async Task<IndexingHarness> StartWithAStaleDocumentAsync()
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using var setup = harness.NewContext();
        var corpus = await setup.Corpora.SingleAsync();
        var documents = ServiceWith(harness, setup, Fast('g'));
        var stored = await documents.StoreAsync(new MemoryStream(Bytes), "doc.slow");
        await documents.AttachAsync(corpus, stored.Sha256, "doc.slow");
        await setup.BlobTexts.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExtractorVersion, 0));
        return harness;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AJobOverTwoChunkSetsExtractsAStaleDocumentThatFailsOnce(bool ioError)
    {
        await using var harness = await StartWithAStaleDocumentAsync();
        var (extractor, calls) = Failing(ioError);

        await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, extractor));

        calls().ShouldBe(1, "two chunk sets read the same stale document in one job");
        await using var check = harness.NewContext();
        (await check.BlobTexts.AsNoTracking().SingleAsync()).ExtractorVersion.ShouldBe(0);
    }

    [Fact]
    public async Task AStaleDocumentThatFailedInOneJobIsExtractedAgainByTheNextJobOnTheSameIndexer()
    {
        // What a pass remembers is its own: the indexer is one per job in the app, and clears the set
        // when a run starts so that one reused would not carry a failure into the next.
        await using var harness = await StartWithAStaleDocumentAsync();
        var extractor = Slow('n');

        await harness.RunTwoJobsOnOneIndexerAsync(
            db => ServiceWith(harness, db, extractor), betweenJobs: () => extractor.Pause = TimeSpan.Zero);

        extractor.Calls.ShouldBe(2, "the first job timed out, the second extracted again");
        await using var check = harness.NewContext();
        (await check.BlobTexts.AsNoTracking().SingleAsync()).ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public async Task AStaleRowIsNotRewrittenOnceThePassTokenWasCancelledDuringItsExtraction()
    {
        // The hook cancels the pass while the extraction runs, and the extraction itself goes on and succeeds.
        // The rest of the work on the row takes the pass's token, so it stops before the row changes.
        await using var harness = await StartWithAStaleDocumentAsync();
        using var cts = new CancellationTokenSource();
        var extractor = new ReadingExtractor { Marker = 'n', OnStart = () => cts.Cancel() };

        try
        {
            await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, extractor), cancel: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The pass may end in the cancellation or record it, depending on where it was caught.
        }

        extractor.Calls.ShouldBe(1, "the token was cancelled during the extraction");
        await using var check = harness.NewContext();
        var row = await check.BlobTexts.AsNoTracking().SingleAsync();
        row.ExtractorVersion.ShouldBe(0);
        row.Text.ShouldBe(new string('g', Bytes.Length));
    }

    [Fact]
    public async Task AFailureThatIsNotATimeoutAfterAConcurrentSaveReportsThatBlob()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        await using var otherDb = harness.NewContext();
        var other = ServiceWith(harness, otherDb, Fast('g'));
        var failing = new EnvironmentFailingExtractor
        {
            OnStart = () => other.StoreAsync(new MemoryStream(Bytes), "stuck.slow").GetAwaiter().GetResult(),
        };
        var log = new RecordingLog();

        var stored = await ServiceWith(harness, db, failing, log).StoreAsync(new MemoryStream(Bytes), "stuck.slow");

        stored.AlreadyExisted.ShouldBeTrue();
        stored.ExtractedChars.ShouldBe(Bytes.Length);
        log.Entries.ShouldContain(e => e.Level == LogLevel.Information
            && e.Message.Contains("saved by another upload while this one failed to extract it"));
        log.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task AStoreCancelledDuringItsExtractionStillReportsTheConcurrentBlob()
    {
        // The lookup after a failure must not take the request's token: it is cancelled here, so a lookup
        // that did would fail and the upload would be told to send the file again.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        await using var otherDb = harness.NewContext();
        using var cts = new CancellationTokenSource();
        var other = ServiceWith(harness, otherDb, Fast('g'));
        var slow = new ReadingExtractor
        {
            Pause = TimeSpan.FromMilliseconds(20),
            OnStart = () =>
            {
                other.StoreAsync(new MemoryStream(Bytes), "stuck.slow").GetAwaiter().GetResult();
                cts.Cancel();
            },
        };

        var stored = await ServiceWith(harness, db, slow).StoreAsync(new MemoryStream(Bytes), "stuck.slow", cts.Token);

        cts.IsCancellationRequested.ShouldBeTrue();
        stored.AlreadyExisted.ShouldBeTrue();
    }

    [Fact]
    public async Task AStaleDocumentWhoseReExtractionFailedIsChunkedAgainWhenALaterPassSucceeds()
    {
        // The failed pass chunks the old text and stamps it with the current fingerprint, which names the
        // extractor version and not the text's. The later pass rewrites the row at that version, so nothing
        // the skip check compares has changed and the index kept the old text.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using (var setup = harness.NewContext())
        {
            var corpus = await setup.Corpora.SingleAsync();
            var documents = ServiceWith(harness, setup, Fast('g'));
            var stored = await documents.StoreAsync(new MemoryStream(Bytes), "doc.slow");
            await documents.AttachAsync(corpus, stored.Sha256, "doc.slow");
            await setup.BlobTexts.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExtractorVersion, 0));
        }

        var failed = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, Slow('n')));
        var later = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, Fast('n')));

        failed.FilesDone.ShouldBe(1, "the pass with the failed re-extraction chunks the text it had");
        later.FilesDone.ShouldBe(1, "the pass that extracts again chunks the new text");
        later.FilesSkipped.ShouldBe(0);
        await using var check = harness.NewContext();
        var row = await check.BlobTexts.AsNoTracking().SingleAsync();
        row.Text.ShouldBe(new string('n', Bytes.Length));
        row.ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public void ADamagedEpubFixtureThrowsWhenAnEntryIsRead()
    {
        // The instrument for the tests below: a directory that reads and an entry that does not.
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(TestEpubs.WithADamagedEntry()));
        var entry = zip.Entries.ShouldHaveSingleItem();

        // Opening works and the data does not inflate: the deflate stream reports that as InvalidDataException.
        using var data = entry.Open();
        Should.Throw<InvalidDataException>(() => data.CopyTo(Stream.Null));
    }

    [Fact]
    public async Task AnEpubWithADamagedEntryIsRecordedAsTheDocumentsReason()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var documents = ServiceWith(harness, db, null, registry: ExtractorRegistry.For);

        var stored = await documents.StoreAsync(new MemoryStream(TestEpubs.WithADamagedEntry()), "broken.epub");

        stored.EmptyReason.ShouldNotBeNull().ShouldContain("not a readable .epub");
        (await db.BlobTexts.SingleAsync()).Extractor.ShouldBe(nameof(EpubTextExtractor));
    }

    [Fact]
    public async Task AStaleRowWhoseEpubIsDamagedRecordsThatVerdictAndEscapesNothing()
    {
        // The row is made stale from bytes the plain-text path read, then extracted again as an EPUB.
        await using var harness = await IndexingHarness.StartAsync("notes");
        string sha;
        await using (var setup = harness.NewContext())
        {
            var stored = await ServiceWith(harness, setup, null, registry: _ => null)
                .StoreAsync(new MemoryStream(TestEpubs.WithADamagedEntry()), "broken.epub");
            sha = stored.Sha256;
            await setup.BlobTexts.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExtractorVersion, 0));
        }

        await using var db = harness.NewContext();
        var returned = await ServiceWith(harness, db, null, registry: ExtractorRegistry.For).CurrentTextFor(sha, "broken.epub");

        returned.ShouldNotBeNull().EmptyReason.ShouldNotBeNull().ShouldContain("not a readable .epub");
        returned.ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public async Task ABlobOfPlainTextThatCannotBeReadIsAFailureThatIsNotAVerdictOnTheFile()
    {
        // An IOException from the read of the stored bytes is not an ExtractionFailedException. It is
        // classified like one, so the caller lists the file and the batch goes on.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var sha = ShaOf(Bytes);
        var documents = ServiceWith(harness, db, null, registry: _ =>
        {
            foreach (var file in Directory.GetFiles(Path.Combine(harness.DataPath, "blobs"), sha, SearchOption.AllDirectories))
                File.Delete(file);

            return null;
        });

        var thrown = await Should.ThrowAsync<ExtractionFailedException>(
            () => documents.StoreAsync(new MemoryStream(Bytes), "gone.txt"));

        thrown.ShouldNotBeOfType<UnreadableDocumentException>();
        thrown.InnerException.ShouldBeAssignableTo<IOException>();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.Blobs.CountAsync()).ShouldBe(0);
    }

    /// <summary>Fails the nth SELECT from <c>blobs</c>, as a busy catalogue fails a lookup.</summary>
    private sealed class FailNthBlobSelect(int nth) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _seen;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FROM \"blobs\"", StringComparison.OrdinalIgnoreCase)
                && ++_seen == nth)
                throw new InvalidOperationException("the catalogue is busy");

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task AFailedLookupForAConcurrentSaveLeavesTheExtractionFailureAsTheAnswer()
    {
        // The first SELECT from blobs is the look for stored bytes before extracting; the second is the one
        // made after the extraction failed. Its failure must not replace the extraction's.
        await using var harness = await IndexingHarness.StartAsync(new FailNthBlobSelect(2), "notes");
        await using var db = harness.NewContext();
        var log = new RecordingLog();

        var thrown = await Should.ThrowAsync<Exception>(
            () => ServiceWith(harness, db, Slow(), log).StoreAsync(new MemoryStream(Bytes), "stuck.slow"));

        thrown.ShouldBeOfType<ExtractionTimeoutException>();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("saved by another upload failed"));
    }

    [Fact]
    public void ADamagedEpubEntryIsAnUnreadableDocumentToTheExtractorItself()
    {
        // The indexing path calls the extractor without the classification the upload path adds, and a
        // raw InvalidDataException there is logged as a failure to index on every pass.
        using var content = new MemoryStream(TestEpubs.WithADamagedEntry());

        var thrown = Should.Throw<UnreadableDocumentException>(() => new EpubTextExtractor().Extract(content, "broken.epub"));

        thrown.Message.ShouldContain("not a readable .epub");
        thrown.GetBaseException().ShouldBeOfType<InvalidDataException>();
    }

    /// <summary>A stream whose reads fail as a disk does.</summary>
    private sealed class FaultyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("disk fault");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AnIoErrorReadingAnHtmlFileIsAnExtractionFailureThatIsNotAVerdictOnIt()
    {
        var thrown = Should.Throw<ExtractionFailedException>(() => new HtmlTextExtractor().Extract(new FaultyStream(), "page.html"));

        thrown.ShouldNotBeOfType<UnreadableDocumentException>();
        thrown.Message.ShouldStartWith("'page.html' is not a readable HTML document: ");
        thrown.InnerException.ShouldBeOfType<IOException>();
    }

    /// <summary>Throws what <paramref name="make"/> builds, as an extractor with a fault in it does.</summary>
    private sealed class ThrowingExtractor(Func<Exception> make) : ITextExtractor
    {
        public int Calls { get; private set; }

        /// <summary>Runs when the extraction starts, for a test that acts while the extractor is working.</summary>
        public Action? OnStart { get; init; }

        public bool CanHandle(string extension) => extension == ".slow";

        public ExtractedText Extract(Stream content, string fileName)
        {
            Calls++;
            OnStart?.Invoke();
            throw make();
        }
    }

    [Fact]
    public async Task AnIndexBuiltOnStaleTextWhileAnotherJobRewroteTheRowIsChunkedAgainByTheNextPass()
    {
        // Job one reads the stale row and then fails to extract it. While it does, another job extracts the
        // same blob and rewrites the row. Job one goes on to chunk the old text and saves its state last, so
        // the state has to name the text it chunked or the later pass finds nothing to redo.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using (var setup = harness.NewContext())
        {
            var corpus = await setup.Corpora.SingleAsync();
            var documents = ServiceWith(harness, setup, Fast('g'));
            var stored = await documents.StoreAsync(new MemoryStream(Bytes), "doc.slow");
            await documents.AttachAsync(corpus, stored.Sha256, "doc.slow");
            await setup.BlobTexts.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExtractorVersion, 0));
        }

        var sha = ShaOf(Bytes);
        await using var otherDb = harness.NewContext();
        var other = ServiceWith(harness, otherDb, Fast('n'));
        var failing = new EnvironmentFailingExtractor
        {
            OnStart = () => other.CurrentTextFor(sha, "doc.slow").GetAwaiter().GetResult(),
        };

        var first = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, failing));
        var fast = Fast('n');
        var later = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, fast));

        first.FilesDone.ShouldBe(1, "the first job chunked the text it had");
        later.FilesDone.ShouldBe(1, "the later pass chunks the text the other job wrote");
        later.FilesSkipped.ShouldBe(0);
        await using var check = harness.NewContext();
        var row = await check.BlobTexts.AsNoTracking().SingleAsync();
        row.Text.ShouldBe(new string('n', Bytes.Length));
        row.ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public async Task AnIndexBuiltOnDamagedTextWhileAnotherJobRepairedTheRowIsChunkedAgainByTheNextPass()
    {
        // The same race as above, for a row whose text was cut: the job that chunks the head must not stamp
        // what the repaired text will have.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using (var setup = harness.NewContext())
        {
            var corpus = await setup.Corpora.SingleAsync();
            var documents = ServiceWith(harness, setup, Fast('g'));
            var stored = await documents.StoreAsync(new MemoryStream(Bytes), "doc.slow");
            await documents.AttachAsync(corpus, stored.Sha256, "doc.slow");
            await setup.BlobTexts.ExecuteUpdateAsync(s => s.SetProperty(t => t.Text, new string('g', 100)));
        }

        var sha = ShaOf(Bytes);
        await using var otherDb = harness.NewContext();
        var other = ServiceWith(harness, otherDb, Fast('n'));
        var failing = new EnvironmentFailingExtractor
        {
            OnStart = () => other.CurrentTextFor(sha, "doc.slow").GetAwaiter().GetResult(),
        };

        var first = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, failing));
        var later = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, Fast('n')));

        first.FilesDone.ShouldBe(1, "the first job chunked the head it had");
        later.FilesDone.ShouldBe(1, "the later pass chunks the repaired text");
        later.FilesSkipped.ShouldBe(0);
    }

    [Fact]
    public async Task ADamagedRowThatIsRepairedIsChunkedAgainInEveryChunkSet()
    {
        // Both sets were chunked from the whole text before the row was damaged, so their states carry the
        // plain fingerprint, which the repaired text also has. Only the clearing makes both redo it.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using (var setup = harness.NewContext())
        {
            var corpus = await setup.Corpora.SingleAsync();
            var documents = ServiceWith(harness, setup, Fast('g'));
            var stored = await documents.StoreAsync(new MemoryStream(Bytes), "doc.slow");
            await documents.AttachAsync(corpus, stored.Sha256, "doc.slow");
        }

        var first = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, Fast('g')));
        await using (var damage = harness.NewContext())
            await damage.BlobTexts.ExecuteUpdateAsync(s => s.SetProperty(t => t.Text, new string('g', 100)));
        var later = await harness.RunIndexAsync(documentsFor: db => ServiceWith(harness, db, Fast('n')));

        first.FilesDone.ShouldBe(2);
        later.FilesDone.ShouldBe(2, "the repair has to reach the state of every set that holds the blob");
        later.FilesSkipped.ShouldBe(0);
        await using var check = harness.NewContext();
        (await check.BlobTexts.AsNoTracking().SingleAsync()).Text.ShouldBe(new string('n', Bytes.Length));
    }

    [Fact]
    public async Task AFaultInTheExtractorKeepsTheTextOfAStaleRowAndNamesTheExtractorAndTheType()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        var log = new RecordingLog();
        var failedThisPass = new HashSet<string>();
        await using var db = harness.NewContext();
        var faulty = new ThrowingExtractor(Activator.CreateInstance<NullReferenceException>);

        var returned = await ServiceWith(harness, db, faulty, log).CurrentTextFor(sha, "doc.slow", failedThisPass);

        returned.ShouldNotBeNull().Text.ShouldBe(new string('g', Bytes.Length));
        failedThisPass.ShouldBe([sha]);
        var warning = log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("unexpected NullReferenceException");
        warning.Message.ShouldContain(nameof(ThrowingExtractor));
        await using var check = harness.NewContext();
        var row = await check.BlobTexts.AsNoTracking().SingleAsync();
        row.Text.ShouldBe(new string('g', Bytes.Length));
        row.ExtractorVersion.ShouldBe(0);
        row.EmptyReason.ShouldBeNull();
    }

    [Fact]
    public async Task AFaultThatAHandlerInTheExtractorWrappedKeepsTheTextOfAStaleRowToo()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();
        var faulty = new ThrowingExtractor(
            () => ExtractionFailures.Of("could not be read as a document", new InvalidOperationException("bad state")));

        var returned = await ServiceWith(harness, db, faulty).CurrentTextFor(sha, "doc.slow");

        returned.ShouldNotBeNull().Text.ShouldBe(new string('g', Bytes.Length));
        returned.ExtractorVersion.ShouldBe(0);
    }

    [Fact]
    public async Task AParserExceptionForAMalformedFileReplacesTheTextOfAStaleRow()
    {
        // The control: a parser's own exception is a verdict on the bytes, and the row records it.
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();
        var parser = new ThrowingExtractor(() => new InvalidDataException("bad block"));

        var returned = await ServiceWith(harness, db, parser).CurrentTextFor(sha, "doc.slow");

        returned.ShouldNotBeNull().EmptyReason.ShouldNotBeNull().ShouldContain("bad block");
        returned.ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public async Task AFaultInTheExtractorIsStillRecordedAsTheVerdictOnANewUpload()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var faulty = new ThrowingExtractor(Activator.CreateInstance<NullReferenceException>);

        var stored = await ServiceWith(harness, db, faulty).StoreAsync(new MemoryStream(Bytes), "doc.slow");

        stored.EmptyReason.ShouldNotBeNull().ShouldContain("could not be read");
        (await db.BlobTexts.SingleAsync()).ExtractorVersion.ShouldBe(ExtractorVersions.Current);
    }

    [Fact]
    public async Task ACancellationTheCallerDidNotAskForIsAFailureOfTheExtractorThatIsNotAVerdict()
    {
        // A library reports a timeout of its own as a cancellation. Nobody cancelled the request, so this is
        // not the request's cancellation and the upload lists the file, as for any failure of the host.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        var cancelling = new ThrowingExtractor(() => new OperationCanceledException("the library gave up"));

        var thrown = await Should.ThrowAsync<ExtractionFailedException>(
            () => ServiceWith(harness, db, cancelling).StoreAsync(new MemoryStream(Bytes), "doc.slow"));

        thrown.ShouldNotBeOfType<UnreadableDocumentException>();
        thrown.InnerException.ShouldBeOfType<OperationCanceledException>();
        (await db.Blobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ACancellationOfTheCallersTokenDuringExtractionStaysACancellation()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();
        using var cts = new CancellationTokenSource();
        var log = new RecordingLog();
        var cancelling = new ThrowingExtractor(() => new OperationCanceledException(cts.Token))
        {
            OnStart = cts.Cancel,
        };

        await Should.ThrowAsync<OperationCanceledException>(
            () => ServiceWith(harness, db, cancelling, log).StoreAsync(new MemoryStream(Bytes), "doc.slow", cts.Token));

        (await db.Blobs.CountAsync()).ShouldBe(0);
        log.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning, "it was not recorded as a verdict on the file");
    }

    [Fact]
    public async Task ACancellationTheCallerDidNotAskForKeepsTheTextOfAStaleRow()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        var sha = await StoreStaleAsync(harness);
        await using var db = harness.NewContext();
        var cancelling = new ThrowingExtractor(() => new OperationCanceledException("the library gave up"));

        var returned = await ServiceWith(harness, db, cancelling).CurrentTextFor(sha, "doc.slow");

        returned.ShouldNotBeNull().Text.ShouldBe(new string('g', Bytes.Length));
        returned.ExtractorVersion.ShouldBe(0);
    }
}
