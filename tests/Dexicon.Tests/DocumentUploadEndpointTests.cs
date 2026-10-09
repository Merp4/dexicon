using System.Data.Common;
using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
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
        Func<Stream, Stream>? wrap = null, CancellationToken ct = default)
    {
        await using var db = harness.NewContext();
        var options = Options.Create(new DexiconOptions
        {
            Storage = new StorageOptions { DataPath = harness.DataPath },
            Upload = Limits,
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
            new DocumentService(db, options, NullLogger<DocumentService>.Instance),
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
