using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// Requests that answered 500, or were read as something else, because a field was missing or a value
/// had nowhere to go. Found by sending every JSON-body operation an empty object and null, and by
/// reading the attach path for a name that is already taken. Each is refused with a 4xx now, or listed
/// under <c>failed</c> by an upload, and changes nothing.
/// </summary>
public sealed class MalformedRequestTests
{
    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("k", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private static RequestContext AsIngester() => new()
    {
        Principal = new Principal("k", "agent",
            new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Ingest }),
    };

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode.ShouldNotBeNull();

    [Fact]
    public async Task AMappingReplacementWithNoCorpusIdsIsRefusedAndTheMappingKept()
    {
        // The field is a list and an empty list lifts the restriction. Left out, it was null, and the
        // handler threw on it: a 500 where a caller who forgot the field needed to be told.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var (key, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("agent", [Scopes.Search], null, [IndexingHarness.CorpusId]);
        db.ChangeTracker.Clear();

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest(null!), AsAdmin(), db, default);

        StatusOf(result).ShouldBe(400);
        await using var check = harness.NewContext();
        (await check.TokenCorpora.Where(tc => tc.TokenId == key.Id).Select(tc => tc.CorpusId).ToListAsync())
            .ShouldBe([IndexingHarness.CorpusId], "a refused replacement leaves the mapping as it was");
    }

    [Fact]
    public async Task AMappingReplacementByAKeyWithoutAdminIsRefusedBeforeItsBodyIsRead()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.ReplaceCorporaAsync("any", new UpdateTokenCorporaRequest(null!), AsIngester(), db, default);

        StatusOf(result).ShouldBe(403);
    }

    [Fact]
    public async Task AnEmptyCorpusListStillLiftsTheRestriction()
    {
        // The control for the test above: what an empty list means has not changed.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var (key, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("agent", [Scopes.Search], null, [IndexingHarness.CorpusId]);
        db.ChangeTracker.Clear();

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest([]), AsAdmin(), db, default);

        result.ShouldBeOfType<Ok<TokenSummary>>().Value!.CorpusIds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(36_501)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task AnExpiryOutsideTheRangeIsRefusedAndNoKeyIsSaved(int days)
    {
        // DateTime.AddDays threw for the largest values, after the request had been accepted, and a
        // negative number made a key that never expires.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent", ExpiresInDays: days),
            AsAdmin(), new TokenService(db, TimeProvider.System), db, default);

        StatusOf(result).ShouldBe(400);
        (await db.Tokens.CountAsync(t => t.Name == "agent")).ShouldBe(0, "a refused key is not saved");
    }

    [Fact]
    public async Task AnExpiryAtTheCapIsAccepted()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(
            new CreateTokenRequest("agent", ExpiresInDays: 36_500),
            AsAdmin(), new TokenService(db, TimeProvider.System), db, default);

        var expires = result.ShouldBeOfType<Ok<CreatedTokenResponse>>().Value!.Token.ExpiresUtc.ShouldNotBeNull();
        expires.ShouldBeGreaterThan(DateTime.UtcNow.AddDays(36_499));
        SystemEndpoints.MaxExpiryDays.ShouldBe(36_500, "the documents state 36,500");
    }

    [Fact]
    public async Task ASourceRequestWithNoWorkspacePathIsRefusedAndNoSourceIsAdded()
    {
        // Left out, the path was read as the workspace root, so a request that misspelt the field
        // indexed everything mounted.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        var before = await db.Sources.CountAsync();

        var outcome = await harness.NewConfiguration(db).AddSourceAsync(corpus, new AddSourceRequest(null!), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Sources.CountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task AnEmptyWorkspacePathIsStillTheWorkspaceRoot()
    {
        // The control for the test above: an empty string is the root, as documented, and is accepted.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();

        var outcome = await harness.NewConfiguration(db).AddSourceAsync(corpus, new AddSourceRequest(""), default);

        outcome.Refusal.ShouldBeNull();
        outcome.Value!.Source.RootPath.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task AStoredDocumentIsNotRenamedOntoAnotherDocumentsName()
    {
        // a.txt and b.txt are two documents. Attaching a's bytes as "b.txt" renamed a's attachment onto a
        // path the source already holds, and the save failed on the path's unique key.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync();
        var a = await documents.StoreAsync(new MemoryStream("the first document"u8.ToArray()), "a.txt");
        var b = await documents.StoreAsync(new MemoryStream("the second document"u8.ToArray()), "b.txt");
        await documents.AttachAsync(corpus, a.Sha256, "a.txt");
        await documents.AttachAsync(corpus, b.Sha256, "b.txt");

        var thrown = await Should.ThrowAsync<NameTakenException>(() => documents.AttachAsync(corpus, a.Sha256, "b.txt"));

        thrown.Message.ShouldContain("'b.txt'");
        thrown.Message.ShouldContain("'a.txt'");
        thrown.Message.ShouldNotContain("Parameter", Case.Sensitive, "the message goes to the caller, who cannot see an argument name");
        await using var check = harness.NewContext();
        (await check.Files.ToDictionaryAsync(f => f.RelativePath, f => f.BlobSha256))
            .ShouldBe(new Dictionary<string, string?> { ["a.txt"] = a.Sha256, ["b.txt"] = b.Sha256 }, ignoreOrder: true);
    }

    [Fact]
    public async Task AStoredDocumentIsStillRenamedToANameOnlyAnotherCorpusHolds()
    {
        // A path is unique within a source, and each corpus has its own upload source.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync();
        var elsewhere = (await harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("papers"), default)).Value!;
        var a = await documents.StoreAsync(new MemoryStream("the first document"u8.ToArray()), "a.txt");
        var b = await documents.StoreAsync(new MemoryStream("the second document"u8.ToArray()), "b.txt");
        await documents.AttachAsync(corpus, a.Sha256, "a.txt");
        await documents.AttachAsync(elsewhere, b.Sha256, "b.txt");

        await documents.AttachAsync(corpus, a.Sha256, "b.txt");

        await using var check = harness.NewContext();
        (await check.Files.Where(f => f.Source!.CorpusId == corpus.Id).Select(f => f.RelativePath).ToListAsync())
            .ShouldBe(["b.txt"]);
        (await check.Files.CountAsync(f => f.Source!.CorpusId == elsewhere.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task AStoredDocumentIsStillRenamedToAFreeName()
    {
        // The control for the test above: a rename to a name nobody holds is what it was.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync();
        var a = await documents.StoreAsync(new MemoryStream("the first document"u8.ToArray()), "a.txt");
        await documents.AttachAsync(corpus, a.Sha256, "a.txt");

        await documents.AttachAsync(corpus, a.Sha256, "renamed.txt");

        await using var check = harness.NewContext();
        (await check.Files.Select(f => f.RelativePath).ToListAsync()).ShouldBe(["renamed.txt"]);
    }

    [Fact]
    public async Task AnUnknownSearchModeIsAnswered400AndItsMessageIsOneShortLine()
    {
        // The mode was echoed into an ArgumentException that nothing caught: a 500, and a message that
        // reached the console log with the caller's line breaks in it.
        var thrown = Should.Throw<UnknownSearchModeException>(
            () => Mapping.ParseMode("fuzzy\n[10:00:00Z INF] Admin signed in" + new string('x', 200)));

        thrown.Message.ShouldContain("Unknown search mode 'fuzzy [10:00:00Z INF]");
        thrown.Message.ShouldNotContain("\n");
        thrown.Message.Length.ShouldBeLessThan(120);

        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        var handled = await new ScopeExceptionHandler(NullLogger<ScopeExceptionHandler>.Instance)
            .TryHandleAsync(http, thrown, default);

        handled.ShouldBeTrue();
        http.Response.StatusCode.ShouldBe(400);
    }

    [Fact]
    public void ThreeSearchModesAreStillAccepted()
    {
        Mapping.ParseMode(null).ShouldBe(SearchMode.Hybrid);
        Mapping.ParseMode("Keyword").ShouldBe(SearchMode.Keyword);
        Mapping.ParseMode("semantic").ShouldBe(SearchMode.Semantic);
    }

    [Fact]
    public async Task ACorpusListWithANullInItIsRefusedAsAScopeError()
    {
        // A JSON null in the list is a null element. Split threw on it, which answered 500.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveReadableAsync(AsIngester().RequirePrincipal(), [IndexingHarness.CorpusId, null!]));

        thrown.Message.ShouldContain("cannot be null");
    }

    [Fact]
    public async Task ASourceWithANullCharacterInItsPathIsRefusedAndNoSourceIsAdded()
    {
        // Path.GetFullPath throws ArgumentException on it, which nothing caught.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        var before = await db.Sources.CountAsync();

        var outcome = await harness.NewConfiguration(db).AddSourceAsync(corpus, new AddSourceRequest("a\0b"), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Sources.CountAsync()).ShouldBe(before);
    }

    [Theory]
    [InlineData("Café", null, "/api/corpora/Caf%C3%A9")]
    [InlineData("a b/c?d#e", null, "/api/corpora/a%20b%2Fc%3Fd%23e")]
    [InlineData("books", "fine", "/api/corpora/books/chunk-sets/fine")]
    [InlineData("books", "résumé set", "/api/corpora/books/chunk-sets/r%C3%A9sum%C3%A9%20set")]
    public void ALocationHeaderHoldsOnlyAsciiAndEscapesEachNameAsASegment(string corpus, string? set, string expected)
    {
        // Kestrel throws on a header value outside ASCII, after the sweep or the chunk set was saved.
        var location = CorpusEndpoints.LocationOf(corpus, set);

        location.ShouldBe(expected);
        location.ShouldAllBe(c => c < 128);
    }

    [Fact]
    public async Task AttachingToATakenNameIsAnswered409AndChangesNothing()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var db = harness.NewContext();
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.SingleAsync();
        var a = await documents.StoreAsync(new MemoryStream("the first document"u8.ToArray()), "a.txt");
        var b = await documents.StoreAsync(new MemoryStream("the second document"u8.ToArray()), "b.txt");
        await documents.AttachAsync(corpus, a.Sha256, "a.txt");
        await documents.AttachAsync(corpus, b.Sha256, "b.txt");
        var queue = new IndexJobQueue(db, new WorkScheduler(harness.Settings), NullLogger<IndexJobQueue>.Instance);

        var result = await DocumentEndpoints.AttachAsync(
            IndexingHarness.CorpusId, new AttachDocumentRequest(a.Sha256, "b.txt"), AsIngester(), new ScopeResolver(db),
            documents, db, queue, default);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(409);
        problem.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("'b.txt'");
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(0, "a refused attach queues nothing");
    }
}
