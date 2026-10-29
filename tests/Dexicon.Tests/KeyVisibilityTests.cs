using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// What a key can learn about, and attach, in corpora it does not reach, and what a revoked key still
/// reaches. Each case here was a way a caller learned that a corpus or a document exists, or kept
/// seeing a corpus, beyond what its key allowed.
/// </summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class KeyVisibilityTests
{
    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("admin-session", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private static Principal Agent(ApiToken key) =>
        new(key.Id, key.Name, new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Ingest, Scopes.Configure });

    private static async Task<IndexingHarness> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        return harness;
    }

    private static async Task<Corpus> AddCorpusAsync(IndexingHarness harness, CatalogDbContext db, string name)
    {
        var made = (await harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest(name), default)).Value!;
        return await db.Corpora.SingleAsync(c => c.Id == made.Id);
    }

    // ---- a key that is revoked or expired --------------------------------------------------------

    [Fact]
    public async Task AKeyRevokedWhileItsPrincipalIsHeldReachesNothingFromThen()
    {
        // The progress stream opens with a principal and reads what it reaches on every heartbeat. A
        // request is refused for a revoked key when it is verified, and the stream is not a request.
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var (key, _) = await tokens.CreateAsync("agent", [Scopes.Search], null, ct: TestContext.Current.CancellationToken);
        var scopes = new ScopeResolver(db);
        (await scopes.VisibleIdsAsync(Agent(key), TestContext.Current.CancellationToken)).ShouldBe([IndexingHarness.CorpusId]);

        (await tokens.RevokeAsync(key.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await scopes.VisibleIdsAsync(Agent(key), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await scopes.VisibleAsync(Agent(key), TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AKeyThatHasExpiredReachesNothingAndOneThatHasNotDoes()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var (expired, _) = await tokens.CreateAsync("expired", [Scopes.Search], DateTime.UtcNow.AddMinutes(-1), ct: TestContext.Current.CancellationToken);
        var (current, _) = await tokens.CreateAsync("current", [Scopes.Search], DateTime.UtcNow.AddMinutes(5), ct: TestContext.Current.CancellationToken);
        var scopes = new ScopeResolver(db);

        (await scopes.VisibleIdsAsync(Agent(expired), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await scopes.VisibleIdsAsync(Agent(current), TestContext.Current.CancellationToken)).ShouldBe([IndexingHarness.CorpusId]);
    }

    [Fact]
    public async Task AProgressStreamOfASessionThatHasSignedOutShowsNothingFromThen()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        using var sessions = new AdminSessions(TimeProvider.System);
        var session = sessions.Issue();
        var principal = sessions.Verify(session.Presented).ShouldNotBeNull();
        var visible = SystemEndpoints.StreamVisibility(new ScopeResolver(db), sessions, principal, session.Presented);
        (await visible(TestContext.Current.CancellationToken)).ShouldBe([IndexingHarness.CorpusId]);

        sessions.Revoke(session.Presented);

        (await visible(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task AKeyReachesNothingFromTheInstantItExpires()
    {
        // Held to ApiToken.IsActive, which is active while the expiry is later than now.
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var instant = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var (key, _) = await new TokenService(db, TimeProvider.System).CreateAsync("agent", [Scopes.Search], instant, ct: TestContext.Current.CancellationToken);

        (await new ScopeResolver(db, new FixedClock(instant.AddTicks(-1))).VisibleIdsAsync(Agent(key), TestContext.Current.CancellationToken))
            .ShouldBe([IndexingHarness.CorpusId]);
        (await new ScopeResolver(db, new FixedClock(instant)).VisibleIdsAsync(Agent(key), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        key.IsActive(instant).ShouldBeFalse();
    }

    // ---- what /healthz reports of the catalogue ----------------------------------------------------

    private static async Task StartRunningJobAsync(IndexingHarness harness, string corpusId)
    {
        await using var db = harness.NewContext();
        var id = Ulid.NewUlid().ToString();
        db.Jobs.Add(new IndexJob
        {
            Id = id,
            CorpusId = corpusId,
            Kind = JobKind.Refresh,
            State = JobState.Running,
            QueuedUtc = DateTime.UtcNow,
            StartedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // A running job counts as active while its corpus is held under the job's lease.
        await db.Corpora.Where(c => c.Id == corpusId).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.HeldBy, IndexingActivity.HolderPrefix + id)
            .SetProperty(c => c.HeldUntilUtc, DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task HealthCountsTheCorporaAKeyReachesAndReportsOnlyAJobInOneOfThem()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var papers = await AddCorpusAsync(harness, db, "papers");
        await StartRunningJobAsync(harness, papers.Id);
        var scopes = new ScopeResolver(db);
        var (key, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("agent", [Scopes.Search], null, [IndexingHarness.CorpusId], TestContext.Current.CancellationToken);

        var forKey = await SystemEndpoints.ReachableActivityAsync(db, await scopes.VisibleIdsAsync(Agent(key), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var forAdmin = await SystemEndpoints.ReachableActivityAsync(db, await scopes.VisibleIdsAsync(AsAdmin().Principal!, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        forKey.Corpora.ShouldBe(1, "the corpus the key does not reach is not counted");
        forKey.ActiveJob.ShouldBeNull("the job runs in a corpus the key does not reach");
        forAdmin.Corpora.ShouldBe(2);
        forAdmin.ActiveJob.ShouldNotBeNull().CorpusId.ShouldBe(papers.Id);

        await StartRunningJobAsync(harness, IndexingHarness.CorpusId);
        (await SystemEndpoints.ReachableActivityAsync(db, await scopes.VisibleIdsAsync(Agent(key), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))
            .ActiveJob.ShouldNotBeNull().CorpusId.ShouldBe(IndexingHarness.CorpusId);
    }

    // ---- attaching a stored document -------------------------------------------------------------

    private static async Task<IResult> AttachAsync(
        IndexingHarness harness, CatalogDbContext db, RequestContext rc, string corpus, string sha256)
    {
        var queue = new IndexJobQueue(db, new WorkScheduler(harness.Settings), NullLogger<IndexJobQueue>.Instance);
        return await DocumentEndpoints.AttachAsync(corpus, new AttachDocumentRequest(sha256), rc, new ScopeResolver(db),
            harness.NewDocumentService(db), db, queue, default);
    }

    [Fact]
    public async Task AKeyCannotAttachADocumentThatNoCorpusItReachesHolds()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var papers = await AddCorpusAsync(harness, db, "papers");
        var documents = harness.NewDocumentService(db);
        var stored = await documents.StoreAsync(new MemoryStream("held by notes only"u8.ToArray()), "a.txt", TestContext.Current.CancellationToken);
        await documents.AttachAsync(await db.Corpora.SingleAsync(c => c.Id == IndexingHarness.CorpusId, cancellationToken: TestContext.Current.CancellationToken), stored.Sha256, "a.txt", TestContext.Current.CancellationToken);
        var (key, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("agent", [Scopes.Search, Scopes.Ingest], null, [papers.Id], TestContext.Current.CancellationToken);
        var rc = new RequestContext { Principal = Agent(key) };

        var hidden = await AttachAsync(harness, db, rc, "papers", stored.Sha256);
        var missing = await AttachAsync(harness, db, rc, "papers", new string('0', 64));

        var refused = hidden.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(404);
        missing.ShouldBeOfType<ProblemHttpResult>().ProblemDetails.Title.ShouldBe(refused.ProblemDetails.Title,
            "a document that is another corpus's and one that does not exist are answered alike");
        (await db.Files.CountAsync(f => f.Source!.CorpusId == papers.Id, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task AKeyAttachesADocumentACorpusItReachesHolds()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var papers = await AddCorpusAsync(harness, db, "papers");
        var documents = harness.NewDocumentService(db);
        var stored = await documents.StoreAsync(new MemoryStream("held by notes"u8.ToArray()), "a.txt", TestContext.Current.CancellationToken);
        await documents.AttachAsync(await db.Corpora.SingleAsync(c => c.Id == IndexingHarness.CorpusId, cancellationToken: TestContext.Current.CancellationToken), stored.Sha256, "a.txt", TestContext.Current.CancellationToken);
        var (key, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("agent", [Scopes.Search, Scopes.Ingest], null, [IndexingHarness.CorpusId, papers.Id], TestContext.Current.CancellationToken);

        var result = await AttachAsync(harness, db, new RequestContext { Principal = Agent(key) }, "papers", stored.Sha256);

        result.ShouldBeOfType<Accepted<DocumentAttached>>();
        (await db.Files.CountAsync(f => f.Source!.CorpusId == papers.Id && f.BlobSha256 == stored.Sha256, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task NobodyAttachesADocumentNoCorpusHolds()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var stored = await harness.NewDocumentService(db).StoreAsync(new MemoryStream("held by no corpus"u8.ToArray()), "a.txt", TestContext.Current.CancellationToken);
        var (key, _) = await new TokenService(db, TimeProvider.System).CreateAsync("agent", [Scopes.Search, Scopes.Ingest], null, ct: TestContext.Current.CancellationToken);

        var byKey = await AttachAsync(harness, db, new RequestContext { Principal = Agent(key) }, "notes", stored.Sha256);
        var byAdmin = await AttachAsync(harness, db, AsAdmin(), "notes", stored.Sha256);

        byKey.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(404);
        byAdmin.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(404);
    }

    // ---- creating a corpus whose name is taken ---------------------------------------------------

    [Fact]
    public async Task ARefusedNameDoesNotRepeatTheOtherCorporasStoredName()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var config = harness.NewConfiguration(db);
        var hidden = await AddCorpusAsync(harness, db, "Quarterly-Results");

        var sameNameOtherCase = await config.CreateCorpusAsync(new CreateCorpusRequest("quarterly-results"), TestContext.Current.CancellationToken);
        var anotherCorpusId = await config.CreateCorpusAsync(new CreateCorpusRequest(hidden.Id), TestContext.Current.CancellationToken);

        var byName = sameNameOtherCase.Refusal.ShouldNotBeNull();
        byName.Status.ShouldBe(409);
        byName.Detail.ShouldNotContain("Quarterly-Results", Case.Sensitive);
        var byId = anotherCorpusId.Refusal.ShouldNotBeNull();
        byId.Status.ShouldBe(409);
        byId.Detail.ShouldNotContain("Quarterly-Results", Case.Insensitive);
    }

    // ---- removing a corpus while a key is created for corpora ------------------------------------

    [Fact]
    public async Task ACorpusRemovalWaitsForTheNamingLock()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var papers = await AddCorpusAsync(harness, db, "papers");
        await using var other = harness.NewContext();
        var config = harness.NewConfiguration(other);

        Task<ConfigOutcome<bool>> removal;
        await CorpusConfiguration.Naming.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            removal = config.RemoveCorpusAsync(await other.Corpora.SingleAsync(c => c.Id == papers.Id, cancellationToken: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            removal.IsCompleted.ShouldBeFalse("the removal waits for a key being created or its reach replaced");
        }
        finally
        {
            CorpusConfiguration.Naming.Release();
        }

        (await removal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task AKeyCreatedForCorporaWaitsForTheNamingLockAndOneForEveryCorpusDoesNot()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        Task<IResult> restricted;
        await CorpusConfiguration.Naming.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            restricted = SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("restricted", CorpusIds: [IndexingHarness.CorpusId]), AsAdmin(), new TokenService(db, TimeProvider.System), db, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            restricted.IsCompleted.ShouldBeFalse("the creation waits from the check of its corpora to the save of its rows");

            await using var other = harness.NewContext();
            var everyCorpus = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("everything"), AsAdmin(), new TokenService(other, TimeProvider.System), other, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            everyCorpus.ShouldBeOfType<Ok<CreatedTokenResponse>>("a key that names no corpus has nothing to check");
        }
        finally
        {
            CorpusConfiguration.Naming.Release();
        }

        (await restricted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).ShouldBeOfType<Ok<CreatedTokenResponse>>();
    }
}
