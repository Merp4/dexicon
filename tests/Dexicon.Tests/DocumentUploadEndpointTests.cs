using System.Data.Common;
using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The handler the upload route is mapped to, called with a multipart body that is generated as it is
/// read. What it must do: take each file from the wire straight into the document store, hold every
/// file to the per-file cap as its bytes arrive, bound the request as a whole, and keep the good files
/// of a batch when one is refused. A body of megabytes stands in for gigabytes, because the cap and
/// the bound are scaled down with it.
/// </summary>
public sealed class DocumentUploadEndpointTests
{
    private const long Megabyte = 1024 * 1024;
    private const long Cap = Megabyte;

    private static RequestContext As(params string[] scopes) => new()
    {
        Principal = new Principal("k", "agent", scopes.ToHashSet(StringComparer.Ordinal)),
    };

    private static readonly UploadOptions Limits = new() { MaxFileBytes = Cap };

    private sealed record Posted(IResult Result, long BytesRead, long BodyLength, bool BufferedToATempFile);

    private static async Task<Posted> PostAsync(
        IndexingHarness harness, MultipartBody body, RequestContext? rc = null, bool declareLength = false,
        Func<Stream, Stream>? wrap = null, IndexingOptions? indexing = null,
        Func<string, ITextExtractor?>? extractorFor = null, CancellationToken ct = default)
    {
        await using var db = harness.NewContext();
        var options = Options.Create(new DexiconOptions
        {
            Storage = new StorageOptions { DataPath = harness.DataPath },
            Upload = Limits,
            Indexing = indexing ?? new IndexingOptions(),
        });

        var watch = new TempBufferWatch();
        var stream = body.Open(watch.Observe);
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = MultipartBody.ContentType;
        http.Request.Body = wrap is null ? stream : wrap(stream);
        if (declareLength) http.Request.ContentLength = body.Length;

        var result = await DocumentEndpoints.UploadAsync(
            "notes", http.Request, rc ?? As(Scopes.Search, Scopes.Ingest), new ScopeResolver(db),
            new DocumentService(db, options, NullLogger<DocumentService>.Instance)
            {
                ExtractorFor = extractorFor ?? ExtractorRegistry.For,
            },
            new IndexJobQueue(db, new WorkScheduler(options), NullLogger<IndexJobQueue>.Instance),
            options, ct);

        return new Posted(result, stream.BytesRead, body.Length, watch.SawABufferFile);
    }

    private static async Task<IndexingHarness> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Upload);
        return harness;
    }

    [Fact]
    public async Task AnOverCapFileIsRefusedWithoutBufferingTheBodyToATempFile()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody().File("files", "huge.bin", 3 * Megabyte);

        var posted = await PostAsync(harness, body);

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("huge.bin");
        refused.ProblemDetails.Detail.ShouldContain("DEXICON__UPLOAD__MAXFILEBYTES");
        posted.BufferedToATempFile.ShouldBeFalse(
            "the body was spooled to a temp file (a tmpfs, in the container) before the cap was applied");
        await using var db = harness.NewContext();
        (await db.Blobs.AnyAsync()).ShouldBeFalse("nothing over the cap is stored");
    }

    [Fact]
    public async Task AFileUploadedAgainUnderAnotherDocumentsNameIsListedAndTheFilesBesideItAreStored()
    {
        // a.txt's bytes sent as "b.txt", which another document holds: the rename onto that path failed on
        // the source's unique key and the whole request answered 500, losing c.txt beside it.
        await using var harness = await StartAsync();
        await PostAsync(harness, new MultipartBody().File("files", "a.txt", 200, 'a').File("files", "b.txt", 300, 'b'));
        var again = new MultipartBody().File("files", "b.txt", 200, 'a').File("files", "c.txt", 100, 'c');

        var posted = await PostAsync(harness, again);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["c.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe(["b.txt"]);
        accepted.Failed[0].Error.ShouldContain("'a.txt'");
        await using var db = harness.NewContext();
        (await db.Files.Select(f => f.RelativePath).ToListAsync()).ShouldBe(["a.txt", "b.txt", "c.txt"], ignoreOrder: true);
    }

    [Fact]
    public async Task AnOverCapFileInABatchLeavesTheFilesBesideItStored()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .File("files", "before.txt", 200, 'b')
            .File("files", "huge.bin", 3 * Megabyte)
            .File("files", "after.txt", 300, 'c');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["before.txt", "after.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe(["huge.bin"]);
        accepted.Failed[0].Error.ShouldContain("DEXICON__UPLOAD__MAXFILEBYTES");
        posted.BufferedToATempFile.ShouldBeFalse();
        await using var db = harness.NewContext();
        (await db.Blobs.CountAsync()).ShouldBe(2);
        (await db.Files.CountAsync()).ShouldBe(2, "and both are attached to the corpus");
    }

    [Fact]
    public async Task ARequestDeclaredOverTheBoundIsRefusedWithoutReadingAnyOfIt()
    {
        await using var harness = await StartAsync();
        var bound = Limits.MaxRequestBytes;
        var body = new MultipartBody().File("files", "a.txt", bound);

        var posted = await PostAsync(harness, body, declareLength: true);

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(413);
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain(bound.ToString("N0"));
        refused.ProblemDetails.Detail.ShouldContain("DEXICON__UPLOAD__MAXFILEBYTES");
        posted.BytesRead.ShouldBe(0, "the length the client declared was enough to refuse it");
    }

    [Fact]
    public async Task ARequestWithNoDeclaredLengthStopsBeingReadAtTheBound()
    {
        await using var harness = await StartAsync();
        var bound = Limits.MaxRequestBytes;
        var body = new MultipartBody().File("files", "a.txt", bound + 4 * Megabyte);

        var posted = await PostAsync(harness, body);

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(413);
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain(bound.ToString("N0"));
        posted.BytesRead.ShouldBeLessThan(posted.BodyLength,
            "the rest of the body was read to the end instead of being refused at the bound");
        posted.BytesRead.ShouldBeLessThanOrEqualTo(bound + Megabyte);
    }

    [Fact]
    public async Task FilesStoredBeforeTheBoundIsPassedStayStoredAndTheOverrunIsReported()
    {
        await using var harness = await StartAsync();
        var bound = Limits.MaxRequestBytes;
        var body = new MultipartBody()
            .File("files", "first.txt", 200, 'b')
            .File("files", "huge.bin", bound + 4 * Megabyte);

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["first.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe(["huge.bin", null]);
        accepted.Failed[1].Error.ShouldContain(bound.ToString("N0"));
        posted.BytesRead.ShouldBeLessThan(posted.BodyLength);
    }

    [Fact]
    public async Task TheBoundAllowsABatchOfTenFilesAtTheCap()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
            body.File("files", $"f{i}.txt", Cap, (char)('a' + i));

        var posted = await PostAsync(harness, body, declareLength: true);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Count.ShouldBe(UploadOptions.BatchFiles);
        accepted.Failed.ShouldBeEmpty();
    }

    [Fact]
    public async Task FilesPastTheBatchLimitAreNotReadAndTheFirstTenStayStored()
    {
        // Twelve files, the eleventh large: reading it would show in BytesRead, and storing it in the catalogue.
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
            body.File("files", $"f{i}.txt", 100, (char)('a' + i));
        body.File("files", "eleventh.txt", 3 * Megabyte, 'z').File("files", "twelfth.txt", 100, 'y');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Count.ShouldBe(UploadOptions.BatchFiles);
        var failure = accepted.Failed.ShouldHaveSingleItem();
        failure.File.ShouldBeNull("the failure is the request's, and names no file");
        failure.Error.ShouldContain($"more than {UploadOptions.BatchFiles} files");
        failure.Error.ShouldContain("another request");
        posted.BytesRead.ShouldBeLessThan(64 * 1024, "the body past the tenth file was read instead of being left unread");
        await using var db = harness.NewContext();
        (await db.Blobs.CountAsync()).ShouldBe(UploadOptions.BatchFiles);
        (await db.Files.CountAsync()).ShouldBe(UploadOptions.BatchFiles);
        (await db.Jobs.CountAsync()).ShouldBe(1, "the stored files are queued for indexing");
    }

    [Fact]
    public async Task FormFieldsBeforeAndBetweenTheFilesAreNotCountedAgainstTheBatchLimit()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody().Field("note", "first");
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
            body.File("files", $"f{i}.txt", 100, (char)('a' + i)).Field($"note{i}", "text");

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Count.ShouldBe(UploadOptions.BatchFiles);
        accepted.Failed.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFileAfterFormFieldsPastTheBatchLimitIsStillNotRead()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
            body.File("files", $"f{i}.txt", 100, (char)('a' + i));
        body.Field("note", "between").File("files", "eleventh.txt", 100, 'z');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldNotContain("eleventh.txt");
        accepted.Failed.ShouldHaveSingleItem().File.ShouldBeNull();
    }

    [Fact]
    public async Task AnOverLimitRequestWhoseFirstTenFilesWereAllRefusedIsAnswered400WithTheLimit()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
            body.File("files", $"empty{i}.txt", 0);
        body.File("files", "eleventh.txt", 100, 'z');

        var posted = await PostAsync(harness, body);

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Title.ShouldBe("No files could be stored");
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain($"more than {UploadOptions.BatchFiles} files");
        await using var db = harness.NewContext();
        (await db.Blobs.AnyAsync()).ShouldBeFalse();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ManyFormFieldsAreNotReadPastTheSectionLimitAndAreAnswered400WithIt()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < 5_000; i++) body.Field($"f{i}", "v");

        var posted = await PostAsync(harness, body);

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Title.ShouldBe("No files in the request");
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain($"more than {UploadOptions.MaxSections} parts");
        posted.BodyLength.ShouldBeGreaterThan(256 * 1024);
        posted.BytesRead.ShouldBeLessThan(64 * 1024, "the fields past the limit were read instead of being left unread");
    }

    [Fact]
    public async Task ARequestOfExactlyTheSectionLimitIsReadInFull()
    {
        // Ten files, each followed by nine fields: one hundred sections.
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
        {
            body.File("files", $"f{i}.txt", 100, (char)('a' + i));
            for (var j = 0; j < 9; j++) body.Field($"n{i}-{j}", "v");
        }

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Count.ShouldBe(UploadOptions.BatchFiles);
        accepted.Failed.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneSectionPastTheLimitStopsTheReadAndKeepsTheFilesStoredBeforeIt()
    {
        // Ten files, then ninety-one fields: the hundred and first section is the first not read.
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
            body.File("files", $"f{i}.txt", 100, (char)('a' + i));
        for (var j = 0; j < UploadOptions.MaxSections - UploadOptions.BatchFiles + 1; j++)
            body.Field($"n{j}", "v");

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Count.ShouldBe(UploadOptions.BatchFiles);
        var failure = accepted.Failed.ShouldHaveSingleItem();
        failure.File.ShouldBeNull();
        failure.Error.ShouldContain($"more than {UploadOptions.MaxSections} parts");
    }

    [Fact]
    public async Task AnEleventhFileAsTheHundredAndFirstSectionIsStoppedBySectionsBeforeFiles()
    {
        // Ninety fields, then eleven files: the eleventh file is section 101, and no file is read past ten.
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var j = 0; j < 90; j++) body.Field($"n{j}", "v");
        for (var i = 0; i <= UploadOptions.BatchFiles; i++)
            body.File("files", $"f{i}.txt", 100, (char)('a' + i));

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Count.ShouldBe(UploadOptions.BatchFiles);
        accepted.Failed.ShouldHaveSingleItem().Error.ShouldContain($"more than {UploadOptions.MaxSections} parts");
    }

    [Fact]
    public async Task APartWithAnEmptyFileNameIsAFormFieldThatCountsAsASectionAndNotAsAFile()
    {
        // Ten files with an empty-filename part between each pair: the eleven fields are not files.
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i < UploadOptions.BatchFiles; i++)
            body.File("files", $"f{i}.txt", 100, (char)('a' + i)).File("files", "", 50, 'z');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Count.ShouldBe(UploadOptions.BatchFiles);
        accepted.Failed.ShouldBeEmpty("an empty filename is a form field, so it is neither stored nor refused");
    }

    [Fact]
    public async Task PartsWithEmptyFileNamesCountTowardsTheSectionLimit()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody();
        for (var i = 0; i <= UploadOptions.MaxSections; i++) body.File("files", "", 50, 'z');

        var posted = await PostAsync(harness, body);

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain($"more than {UploadOptions.MaxSections} parts");
    }

    /// <summary>Reads its input a byte at a time with a pause between reads, as a parser working through a damaged file does.</summary>
    private sealed class SlowReadingExtractor(TimeSpan pause) : ITextExtractor
    {
        public int BytesRead { get; private set; }

        public bool CanHandle(string extension) => extension == ".slow";

        public ExtractedText Extract(Stream content, string fileName)
        {
            var one = new byte[1];
            while (content.Read(one, 0, 1) == 1)
            {
                BytesRead++;
                Thread.Sleep(pause);
            }

            return new ExtractedText(new string('x', BytesRead), []);
        }
    }

    private static Func<string, ITextExtractor?> Registry(SlowReadingExtractor slow) =>
        name => name.EndsWith(".slow", StringComparison.Ordinal) ? slow : null;

    [Fact]
    public async Task AFileThatKeepsReadingPastTheExtractionTimeoutIsListedAsNotStoredAndTheFilesBesideItAreStored()
    {
        // 300 bytes at 20 ms each is six seconds of reading against a one second budget. The timeout is
        // a fact about the host's load and not about the document, so no document record is created for the file.
        await using var harness = await StartAsync();
        var slow = new SlowReadingExtractor(TimeSpan.FromMilliseconds(20));
        var body = new MultipartBody()
            .File("files", "before.txt", 200, 'b')
            .File("files", "stuck.slow", 300, 's')
            .File("files", "after.txt", 200, 'c');

        var posted = await PostAsync(
            harness, body, indexing: new IndexingOptions { ExtractionTimeoutSeconds = 1 }, extractorFor: Registry(slow));

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["before.txt", "after.txt"]);
        var failure = accepted.Failed.ShouldHaveSingleItem();
        failure.File.ShouldBe("stuck.slow");
        failure.Error.ShouldContain("DEXICON__INDEXING__EXTRACTIONTIMEOUTSECONDS");
        failure.Error.ShouldContain("within 1 s");
        failure.Error.ShouldContain("No document record was created");
        failure.Error.ShouldContain("sending the file again extracts it again");
        failure.Error.ShouldNotContain("stuck.slow", Case.Sensitive);
        slow.BytesRead.ShouldBeLessThan(300, "the deadline has to cut the extractor's reading short");
        await using var db = harness.NewContext();
        var stored = accepted.Stored.Select(s => s.Sha256).ToList();
        (await db.Blobs.Select(b => b.Sha256).ToListAsync()).ShouldBe(stored, ignoreOrder: true,
            "the file after the timeout is attached and the job is queued, and neither saves a blob for the one that timed out");
        (await db.BlobTexts.Select(t => t.Sha256).ToListAsync()).ShouldBe(stored, ignoreOrder: true);
        (await db.Files.CountAsync()).ShouldBe(2);
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AFileThatTimedOutIsAnswered400WithTheSettingWhenNothingElseWasStored()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody().File("files", "stuck.slow", 300, 's');

        var posted = await PostAsync(
            harness, body, indexing: new IndexingOptions { ExtractionTimeoutSeconds = 1 },
            extractorFor: Registry(new SlowReadingExtractor(TimeSpan.FromMilliseconds(20))));

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Title.ShouldBe("No files could be stored");
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("DEXICON__INDEXING__EXTRACTIONTIMEOUTSECONDS");
        await using var db = harness.NewContext();
        (await db.Blobs.AnyAsync()).ShouldBeFalse();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AFileThatTimedOutIsExtractedAgainWhenItIsSentAgain()
    {
        await using var harness = await StartAsync();
        var body = () => new MultipartBody().File("files", "stuck.slow", 300, 's');
        var first = await PostAsync(
            harness, body(), indexing: new IndexingOptions { ExtractionTimeoutSeconds = 1 },
            extractorFor: Registry(new SlowReadingExtractor(TimeSpan.FromMilliseconds(20))));
        first.Result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(400);

        var posted = await PostAsync(
            harness, body(), indexing: new IndexingOptions { ExtractionTimeoutSeconds = 1 },
            extractorFor: Registry(new SlowReadingExtractor(TimeSpan.Zero)));

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        var stored = accepted.Stored.ShouldHaveSingleItem();
        stored.Deduplicated.ShouldBeFalse();
        stored.Warning.ShouldBeNull();
        stored.ExtractedChars.ShouldBe(300);
        accepted.Failed.ShouldBeEmpty();
    }

    /// <summary>Fails as ExtractionFailures.Of reports an I/O error, with a message that names a path on the server.</summary>
    private sealed class DiskFaultExtractor : ITextExtractor
    {
        public bool CanHandle(string extension) => extension == ".flaky";

        public ExtractedText Extract(Stream content, string fileName) =>
            throw new ExtractionFailedException(
                "could not be read: /data/blobs/ab/abcdef", new IOException("disk fault"));
    }

    [Fact]
    public async Task AnExtractionFailureThatIsNotAVerdictOnTheFileIsListedAsNotStoredAndTheFilesBesideItAreStored()
    {
        // An I/O error is not a timeout and not a bad argument, and used to be stored as the blob's reason.
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .File("files", "before.txt", 100, 'b')
            .File("files", "flaky.flaky", 100, 'f')
            .File("files", "after.txt", 100, 'c');

        var posted = await PostAsync(
            harness, body, extractorFor: name => name.EndsWith(".flaky", StringComparison.Ordinal) ? new DiskFaultExtractor() : null);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["before.txt", "after.txt"]);
        var failure = accepted.Failed.ShouldHaveSingleItem();
        failure.File.ShouldBe("flaky.flaky");
        failure.Error.ShouldContain("No document record was created");
        failure.Error.ShouldNotContain("/data/blobs", Case.Sensitive);
        await using var db = harness.NewContext();
        (await db.Blobs.CountAsync()).ShouldBe(2);
        (await db.BlobTexts.CountAsync()).ShouldBe(2);
        (await db.Files.CountAsync()).ShouldBe(2);
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(0)]
    public async Task AFileThatReadsSlowlyWithinTheTimeoutOrWithNoTimeoutIsExtractedInFull(int timeoutSeconds)
    {
        // The control: the same extractor, over a file it finishes inside the budget, and with the budget off.
        await using var harness = await StartAsync();
        var slow = new SlowReadingExtractor(TimeSpan.FromMilliseconds(5));
        var body = new MultipartBody().File("files", "slow.slow", 100, 's');

        var posted = await PostAsync(
            harness, body, indexing: new IndexingOptions { ExtractionTimeoutSeconds = timeoutSeconds },
            extractorFor: Registry(slow));

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        var stored = accepted.Stored.ShouldHaveSingleItem();
        stored.Warning.ShouldBeNull();
        stored.ExtractedChars.ShouldBe(100);
        slow.BytesRead.ShouldBe(100);
    }

    public static TheoryData<string> RefusedFileNames() => new()
    {
        "next\u0085line.txt",
        "line\u2028break.txt",
        "paragraph\u2029break.txt",
        "bell\u0007.txt",
        "delete\u007f.txt",
        "reversed\u202Egpj.txt",
        "isolated\u2066name.txt",
        new string('n', 257) + ".txt",
    };

    [Theory]
    [MemberData(nameof(RefusedFileNames))]
    public async Task AFileWithAnUnacceptableNameIsListedAndTheFilesBesideItAreStored(string name)
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .File("files", "before.txt", 100, 'b')
            .File("files", name, 100, 'n')
            .File("files", "after.txt", 100, 'c');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["before.txt", "after.txt"]);
        var failure = accepted.Failed.ShouldHaveSingleItem();
        failure.File.ShouldBe(name);
        failure.Error.ShouldNotContain(name, Case.Sensitive);
        failure.Error.ShouldMatch("control character|260 characters");
        await using var db = harness.NewContext();
        (await db.Blobs.CountAsync()).ShouldBe(2, "a refused name stores no bytes");
        (await db.Files.Select(f => f.RelativePath).ToListAsync()).ShouldBe(["before.txt", "after.txt"], ignoreOrder: true);
    }

    [Fact]
    public async Task OnlyPartsWithAFileNameAreStoredWhateverTheirFieldIsCalled()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .Field("note", "not a document")
            .File("attachment", "Résumé & notes (1).txt", 120, 'b');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["Résumé & notes (1).txt"]);
        accepted.Failed.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEmptyFileIsReportedAndTheFilesBesideItAreStored()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .File("files", "empty.txt", 0)
            .File("files", "full.txt", 100, 'b');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["full.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe(["empty.txt"]);
        accepted.Failed[0].Error.ShouldBe("'empty.txt' is empty.", "the Documents screen shows this text as it is");
    }

    [Fact]
    public async Task AFileNamedLikeAMarkerIsStillReportedUnderItsOwnName()
    {
        // A failure of the request as a whole has no file name (null). A file may be called anything,
        // "(request)" among it, and is named in its own failure.
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .File("files", "(request)", 0)
            .File("files", "full.txt", 100, 'b');

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Failed.Select(f => f.File).ShouldBe(["(request)"]);
    }

    [Fact]
    public async Task ABodyWithNoFilesIsRefusedAs400()
    {
        await using var harness = await StartAsync();

        var posted = await PostAsync(harness, new MultipartBody().Field("note", "text only"));

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Title.ShouldBe("No files in the request");
    }

    [Fact]
    public async Task APartWithHeadersOverTheReaderLimitIsRefusedAs400AndKeepsTheFilesStoredBeforeIt()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .File("files", "before.txt", 100, 'b')
            .Field(new string('x', MultipartReader.DefaultHeadersLengthLimit + 1), "value");

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["before.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe([null]);
    }

    [Fact]
    public async Task AMalformedBodyWithNoFileStoredIsRefusedAs400()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody()
            .Field(new string('x', MultipartReader.DefaultHeadersLengthLimit + 1), "value");

        var posted = await PostAsync(harness, body);

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Title.ShouldBe("Malformed multipart upload");
    }

    [Fact]
    public async Task ABodyThatEndsBeforeItsClosingBoundaryIsRefusedAs400AndStoresNothing()
    {
        // The reader reports this as an IOException, which nothing caught: a 500 for a client's
        // mistake.
        await using var harness = await StartAsync();

        var posted = await PostAsync(harness, new MultipartBody().File("files", "only.txt", 100, 'a').Truncated());

        var refused = posted.Result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(400);
        refused.ProblemDetails.Title.ShouldBe("Malformed multipart upload");
        refused.ProblemDetails.Detail.ShouldBe("The body ended before its closing boundary.");
        await using var db = harness.NewContext();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task FilesStoredBeforeATruncatedPartStayStoredAndAreIndexed()
    {
        // The first file completed before the body was cut. Without the catch the request threw
        // after attaching it, so no indexing job was queued for it.
        await using var harness = await StartAsync();
        var body = new MultipartBody().File("files", "first.txt", 100, 'a').File("files", "second.txt", 100, 'b').Truncated();

        var posted = await PostAsync(harness, body);

        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["first.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe([null]);
        accepted.Failed[0].Error.ShouldContain("closing boundary");
        await using var db = harness.NewContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task FilesAttachedBeforeTheCallerIsCancelledAreStillQueuedForIndexing()
    {
        // The attachments are saved, and the job that indexes them was queued on the caller's token:
        // a cancel in that window left documents attached with nothing queued. The interceptor
        // opens the window right after the last row of the attachment is written (the chunk-state
        // rows follow the file row in the same save), and `Fired` shows that it opened.
        var watcher = new CommittedChangeTests.CancelAfterWriteTo("file_chunk_states");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        watcher.Armed = true;

        var posted = await PostAsync(harness, new MultipartBody().File("files", "one.txt", 100, 'a'), ct: cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull()
            .Stored.Select(s => s.FileName).ShouldBe(["one.txt"]);
        await using var db = harness.NewContext();
        (await db.Jobs.CountAsync()).ShouldBe(1, "the attached file has its job");
    }

    [Fact]
    public async Task FilesStoredBeforeTheClientDisconnectedAreStillQueuedForIndexing()
    {
        // The token fires while the second file is being read. The first is attached by then, and
        // the cancellation used to leave the request with no answer and no job.
        await using var harness = await StartAsync();
        using var cts = new CancellationTokenSource();
        var body = new MultipartBody().File("files", "first.txt", 100, 'a').File("files", "second.txt", 20_000, 'b');

        var posted = await PostAsync(harness, body, ct: cts.Token, wrap: s => new CancelOnRead(s, 2, cts));

        cts.IsCancellationRequested.ShouldBeTrue("the cancel has to have happened");
        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["first.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe([null]);
        accepted.Failed[0].Error.ShouldContain("connection closed");
        await using var db = harness.NewContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// Cancels on the <paramref name="nth"/> INSERT into <paramref name="table"/> and then honours the
    /// token it was given, as a driver that supports cancellation does: the save in progress fails and
    /// rolls back, and the rows it was about to write stay tracked on the context.
    /// </summary>
    private sealed class CancelOnInsert(string table, int nth, CancellationTokenSource cts) : DbCommandInterceptor
    {
        private int _seen;

        public bool Fired { get; private set; }

        private void Observe(DbCommand command, CancellationToken token)
        {
            if (!command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains($"\"{table}\"", StringComparison.OrdinalIgnoreCase)
                || ++_seen != nth)
                return;

            Fired = true;
            cts.Cancel();
            token.ThrowIfCancellationRequested();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, cancellationToken);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, cancellationToken);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task AFileWhoseAttachmentWasCancelledMidSaveIsNotSavedWithTheJob()
    {
        // The cancel lands in the second file's save. The rows that save was about to write stay
        // tracked on the context the job is queued through, and the save that queues the job
        // wrote them: a file missing from `stored` was attached anyway.
        using var cts = new CancellationTokenSource();
        var watcher = new CancelOnInsert("files", 2, cts);
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var body = new MultipartBody().File("files", "first.txt", 100, 'a').File("files", "second.txt", 100, 'b');

        var posted = await PostAsync(harness, body, ct: cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        var accepted = posted.Result.ShouldBeOfType<Accepted<UploadResponse>>().Value.ShouldNotBeNull();
        accepted.Stored.Select(s => s.FileName).ShouldBe(["first.txt"]);
        accepted.Failed.Select(f => f.File).ShouldBe([null]);
        await using var db = harness.NewContext();
        (await db.Files.Select(f => f.RelativePath).ToListAsync()).ShouldBe(["first.txt"], "only the reported file is attached");
        (await db.FileChunkStates.CountAsync()).ShouldBe(1);
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ACancelBeforeAnyFileIsStoredPropagatesAndQueuesNothing()
    {
        // Control: with nothing attached there is nothing to finish.
        await using var harness = await StartAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => PostAsync(
            harness, new MultipartBody().File("files", "a.txt", 100, 'a'), ct: cts.Token));

        await using var db = harness.NewContext();
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.Files.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task FilesAttachedBeforeAnUnexpectedFailureAreStillQueuedForIndexing()
    {
        // The second file's read fails for a reason that is the server's. The caller gets that error, and
        // the first file, which is attached, has its job and does not wait for the next scheduled refresh.
        await using var harness = await StartAsync();
        var body = new MultipartBody().File("files", "first.txt", 100, 'a').File("files", "second.txt", 20_000, 'b');

        var thrown = await Should.ThrowAsync<IOException>(
            () => PostAsync(harness, body, wrap: s => new FailingAfter(s, 1_000)));

        thrown.Message.ShouldBe("the disk is full");
        await using var db = harness.NewContext();
        (await db.Files.Select(f => f.RelativePath).ToListAsync()).ShouldBe(["first.txt"]);
        (await db.Jobs.CountAsync()).ShouldBe(1, "the attached file has its job");
    }

    /// <summary>Fails the <paramref name="nth"/> INSERT into <paramref name="table"/> with an error that is not a cancel.</summary>
    private sealed class FailOnInsert(string table, int nth) : DbCommandInterceptor
    {
        private int _seen;

        public bool Fired { get; private set; }

        private void Observe(DbCommand command)
        {
            if (!command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains($"\"{table}\"", StringComparison.OrdinalIgnoreCase)
                || ++_seen != nth)
                return;

            Fired = true;
            throw new InvalidOperationException("the catalogue refused the write");
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task AFailureQueuingTheJobAfterAFailedBatchDoesNotHideTheFirstFailure()
    {
        // The catalogue refuses the job as well. The caller is told what went wrong with the batch.
        var watcher = new FailOnInsert("jobs", 1);
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var body = new MultipartBody().File("files", "first.txt", 100, 'a').File("files", "second.txt", 20_000, 'b');

        var thrown = await Should.ThrowAsync<IOException>(
            () => PostAsync(harness, body, wrap: s => new FailingAfter(s, 1_000)));

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        thrown.Message.ShouldBe("the disk is full");
    }

    [Fact]
    public async Task AFileWhoseAttachmentFailedIsNotSavedWithTheJobOfTheFilesBeforeIt()
    {
        // The second file's save fails and leaves its rows tracked on the context the job is queued
        // through. The save that queues the job would write them: a file the caller was told failed, indexed.
        var watcher = new FailOnInsert("files", 2);
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var body = new MultipartBody().File("files", "first.txt", 100, 'a').File("files", "second.txt", 100, 'b');

        var thrown = await Should.ThrowAsync<DbUpdateException>(() => PostAsync(harness, body));

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        thrown.InnerException.ShouldNotBeNull().Message.ShouldBe("the catalogue refused the write");
        await using var db = harness.NewContext();
        (await db.Files.Select(f => f.RelativePath).ToListAsync()).ShouldBe(["first.txt"], "only the attached file is saved");
        (await db.FileChunkStates.CountAsync()).ShouldBe(1);
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AnIoFailureThatIsNotTheEndOfTheBodyStaysAServerError()
    {
        // Control: only the reader's own "ended early" is the client's mistake. A read that fails
        // for another reason (here partway through a file) is not reported as a malformed upload.
        await using var harness = await StartAsync();

        var thrown = await Should.ThrowAsync<IOException>(() => PostAsync(
            harness, new MultipartBody().File("files", "only.txt", 500, 'a'), wrap: s => new FailingAfter(s, 300)));

        thrown.Message.ShouldBe("the disk is full");
    }

    [Fact]
    public async Task ABodyThatIsNotMultipartIsRefusedAs415WithoutBeingRead()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var options = Options.Create(new DexiconOptions { Storage = new StorageOptions { DataPath = harness.DataPath } });
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new UnreadableStream();

        var result = await DocumentEndpoints.UploadAsync(
            "notes", http.Request, As(Scopes.Ingest), new ScopeResolver(db),
            new DocumentService(db, options, NullLogger<DocumentService>.Instance),
            new IndexJobQueue(db, new WorkScheduler(options), NullLogger<IndexJobQueue>.Instance),
            options, default);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(415);
    }

    [Fact]
    public async Task AKeyWithoutIngestIsRefusedBeforeTheBodyIsRead()
    {
        await using var harness = await StartAsync();
        var body = new MultipartBody().File("files", "a.txt", 100);

        var posted = await PostAsync(harness, body, As(Scopes.Search), declareLength: true);

        posted.Result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(403);
        posted.BytesRead.ShouldBe(0);
        await using var db = harness.NewContext();
        (await db.Blobs.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public void TheRequestBoundIsTenFilesAtTheCapPlusFraming()
    {
        new UploadOptions { MaxFileBytes = 200 * Megabyte }.MaxRequestBytes
            .ShouldBe(10 * 200 * Megabyte + Megabyte);
    }

    [Fact]
    public void TheRequestBoundSaturatesInsteadOfOverflowing()
    {
        new UploadOptions { MaxFileBytes = long.MaxValue / 2 }.MaxRequestBytes.ShouldBe(long.MaxValue);
    }

    /// <summary>Reports whether a file the framework spools request bodies to has appeared.</summary>
    private sealed class TempBufferWatch
    {
        // Where ASP.NET Core spools a buffered body or file part: ASPNETCORE_TEMP, else the temp path.
        private readonly string _directory =
            Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath();
        private readonly HashSet<string> _before;
        private long _nextCheck;

        public TempBufferWatch() => _before = [.. Spooled()];

        public bool SawABufferFile { get; private set; }

        private IEnumerable<string> Spooled() =>
            Directory.EnumerateFiles(_directory, "ASPNETCORE_*.tmp");

        public void Observe(long bytesRead)
        {
            if (SawABufferFile || bytesRead < _nextCheck) return;
            _nextCheck = bytesRead + 128 * 1024;
            SawABufferFile = Spooled().Any(f => !_before.Contains(f));
        }
    }

    /// <summary>Reads through to <paramref name="inner"/>, then fails with an <see cref="IOException"/> once <paramref name="after"/> bytes have been read.</summary>
    private sealed class FailingAfter(Stream inner, long after) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_read >= after) throw new IOException("the disk is full");
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, after - _read)]);
            _read += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));
    }

    /// <summary>Reads through to <paramref name="inner"/> and cancels <paramref name="cts"/> on read number <paramref name="readNumber"/>, as a client disconnecting does.</summary>
    private sealed class CancelOnRead(Stream inner, int readNumber, CancellationTokenSource cts) : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (++_reads == readNumber) cts.Cancel();
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));
    }

    /// <summary>A body that fails the test if anything reads it.</summary>
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("the body was read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A multipart body assembled from parts and produced as it is read, so a multi-megabyte file
    /// is never in memory, and so the reader's progress through it can be observed.
    /// </summary>
    private sealed class MultipartBody
    {
        private const string Boundary = "dexicon-test-boundary";
        private readonly List<Segment> _segments = [];
        private bool _truncated;

        public static string ContentType => $"multipart/form-data; boundary={Boundary}";

        public long Length => _segments.Sum(s => s.Length) + (_truncated ? 0 : Closing.Length);

        /// <summary>Ends the body after the last part, without the closing boundary.</summary>
        public MultipartBody Truncated()
        {
            _truncated = true;
            return this;
        }

        private static byte[] Closing => Encoding.UTF8.GetBytes($"--{Boundary}--\r\n");

        public MultipartBody File(string field, string fileName, long size, char fill = 'a')
        {
            Add($"--{Boundary}\r\nContent-Disposition: form-data; name=\"{field}\"; filename=\"{fileName}\"\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n");
            if (size > 0) _segments.Add(new Segment(null, size, (byte)fill));
            Add("\r\n");
            return this;
        }

        public MultipartBody Field(string name, string value)
        {
            Add($"--{Boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
            return this;
        }

        private void Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _segments.Add(new Segment(bytes, bytes.Length, 0));
        }

        public BodyStream Open(Action<long> onRead) =>
            new(_truncated ? [.. _segments] : [.. _segments, new Segment(Closing, Closing.Length, 0)], onRead);
    }

    /// <summary>A run of a body: literal bytes, or <paramref name="Length"/> copies of <paramref name="Fill"/>.</summary>
    private readonly record struct Segment(byte[]? Bytes, long Length, byte Fill);

    private sealed class BodyStream(IReadOnlyList<Segment> segments, Action<long> onRead) : Stream
    {
        private int _index;
        private long _offset;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override int Read(Span<byte> buffer)
        {
            var copied = 0;
            while (copied < buffer.Length && _index < segments.Count)
            {
                var segment = segments[_index];
                var take = (int)Math.Min(buffer.Length - copied, segment.Length - _offset);
                if (segment.Bytes is { } bytes) bytes.AsSpan((int)_offset, take).CopyTo(buffer[copied..]);
                else buffer.Slice(copied, take).Fill(segment.Fill);

                copied += take;
                _offset += take;
                if (_offset == segment.Length) { _index++; _offset = 0; }
            }

            BytesRead += copied;
            onRead(BytesRead);
            return copied;
        }
    }
}
