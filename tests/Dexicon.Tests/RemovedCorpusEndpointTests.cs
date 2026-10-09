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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The upload and attach routes when the corpus is removed while the request waits for the attachment lock.
/// The handlers throw the exception the scope resolver throws for a corpus that is not there, and the
/// exception handler answers it 400, as it does a corpus that was not there to begin with.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class RemovedCorpusEndpointTests
{
    private static RequestContext As(params string[] scopes) => new()
    {
        Principal = new Principal("k", "agent", scopes.ToHashSet(StringComparer.Ordinal)),
    };

    /// <summary>Runs on the nth query that asks whether a corpus exists, before it executes.</summary>
    private sealed class OnTheNthCorpusCheck(int nth, Func<Task> act) : DbCommandInterceptor
    {
        private int _seen;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"corpora\"", StringComparison.Ordinal)
                && command.CommandText.Contains("EXISTS", StringComparison.Ordinal)
                && Interlocked.Increment(ref _seen) == nth)
                await act();

            return result;
        }
    }

    private static async Task<int> StatusOfAsync(Exception thrown)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        (await new ScopeExceptionHandler(NullLogger<ScopeExceptionHandler>.Instance).TryHandleAsync(http, thrown, default))
            .ShouldBeTrue();
        return http.Response.StatusCode;
    }

    private static HttpRequest Multipart(params (string Name, string Content)[] files)
    {
        var body = new StringBuilder();
        foreach (var (name, content) in files)
            body.Append("--b\r\nContent-Disposition: form-data; name=\"files\"; filename=\"").Append(name)
                .Append("\"\r\nContent-Type: text/plain\r\n\r\n").Append(content).Append("\r\n");
        body.Append("--b--\r\n");

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "multipart/form-data; boundary=b";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToString()));
        return http.Request;
    }

    [Fact]
    public async Task AnAttachmentThatWaitedBehindTheRemovalOfItsCorpusIsAnswered400()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");

        Task<IResult> attachment;
        using (await DocumentService.HoldAttachmentsAsync().FinishesAsync("taking the lock"))
        {
            attachment = Task.Run(() => DocumentEndpoints.AttachAsync(
                IndexingHarness.CorpusId, new AttachDocumentRequest(stored.Sha256, "doc.txt"),
                As(Scopes.Search, Scopes.Ingest), new ScopeResolver(db), documents, db,
                new IndexJobQueue(db, new WorkScheduler(harness.Settings), NullLogger<IndexJobQueue>.Instance), default));
            await using var removing = harness.NewContext();
            await removing.Corpora.ExecuteDeleteAsync();
        }

        var refused = await Should.ThrowAsync<ScopeResolutionException>(() => attachment.FinishesAsync("the attachment"));
        (await StatusOfAsync(refused)).ShouldBe(400);
    }

    [Fact]
    public async Task AnUploadWhoseCorpusIsRemovedAfterItsFirstFileIsAnswered400AndQueuesNothing()
    {
        // The first file is attached, the corpus is removed, and the second file finds it gone. The refresh
        // the batch would have queued has no corpus to run on, and failing to queue it is not an error to log.
        var logs = new RecordingLoggerFactory();
        IndexingHarness? harness = null;
        var removal = new OnTheNthCorpusCheck(2, async () =>
        {
            await using var removing = harness!.NewContext();
            await removing.Corpora.ExecuteDeleteAsync();
        });
        harness = await IndexingHarness.StartAsync(removal, "notes");
        await using var _ = harness;
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var options = Options.Create(new DexiconOptions
        {
            Storage = new StorageOptions { DataPath = harness.DataPath },
        });
        var request = Multipart(("a.txt", "the first file"), ("b.txt", "the second file"));
        request.HttpContext.RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(logs).BuildServiceProvider();

        var refused = await Should.ThrowAsync<ScopeResolutionException>(() => DocumentEndpoints.UploadAsync(
            IndexingHarness.CorpusId, request, As(Scopes.Search, Scopes.Ingest), new ScopeResolver(db),
            new DocumentService(db, options, NullLogger<DocumentService>.Instance),
            new IndexJobQueue(db, new WorkScheduler(options), NullLogger<IndexJobQueue>.Instance),
            options, default)).FinishesAsync("the upload");

        (await StatusOfAsync(refused)).ShouldBe(400);
        logs.Lines.ShouldNotContain(l => l.Contains("Queuing the refresh", StringComparison.Ordinal));
        await using var check = harness.NewContext();
        (await check.Jobs.CountAsync()).ShouldBe(0);
    }
}
