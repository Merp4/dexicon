using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// The scoping guarantee, which replaced the tenant isolation one when D-28 removed
/// tenants. The promise is narrower and it is still worth a test nobody deletes by
/// accident: a key not set to every corpus reaches the corpora it is mapped to and no others, and every failure is
/// an authorization error naming the problem rather than an empty result, because an empty
/// result is indistinguishable from "that content does not exist" and a caller acts on it.
/// </summary>
public sealed class KeyScopingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private ScopeResolver _scopes = null!;

    private const string SecretString = "hunter2-globex-launch-code";

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Corpora.AddRange(Corpus("books"), Corpus("code"), Corpus("private-notes"));
        await _db.SaveChangesAsync();
        _scopes = new ScopeResolver(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Corpus Corpus(string name) => new()
    {
        Id = $"id-{name}",
        Name = name,
        State = CorpusState.Ready,
        CreatedUtc = DateTime.UtcNow,
        ChunkSets =
        [
            new ChunkSet
            {
                Id = $"set-{name}",
                CorpusId = $"id-{name}",
                Name = "default",
                EmbeddingModel = "nomic-embed-text",
                CollectionName = "dexicon__nomic-embed-text__768",
                BoundaryMode = "line",
                IsDefault = true,
                CreatedUtc = DateTime.UtcNow,
            },
        ],
    };

    private static Principal Key(string name, params string[] scopes) =>
        new($"tok-{name}", name, scopes.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal) { Scopes.Search }
            : [.. scopes]);

    private static Principal AdminSession() =>
        new("admin-session", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin });

    /// <summary>
    /// Map a key to corpora, creating the key row first. The mapping carries a real foreign
    /// key to <c>tokens</c>, which is what stops a mapping outliving the key it describes.
    /// </summary>
    private async Task MapAsync(string tokenName, params string[] corpusIds)
    {
        var id = await AddKeyAsync(tokenName, allCorpora: false);

        foreach (var corpusId in corpusIds)
            _db.TokenCorpora.Add(new TokenCorpus { TokenId = id, CorpusId = corpusId });

        await _db.SaveChangesAsync();
    }

    /// <summary>Add the key's row if it is not there yet, and return its id.</summary>
    private async Task<string> AddKeyAsync(string tokenName, bool allCorpora)
    {
        var id = $"tok-{tokenName}";
        if (!await _db.Tokens.AnyAsync(t => t.Id == id))
        {
            _db.Tokens.Add(new ApiToken
            {
                Id = id,
                Name = tokenName,
                TokenHash = [1],
                TokenSalt = [2],
                Scopes = Scopes.Search,
                CreatedUtc = DateTime.UtcNow,
                AllCorpora = allCorpora,
            });
            await _db.SaveChangesAsync();
        }

        return id;
    }

    [Fact]
    public async Task AMappedKeyReachesOnlyItsCorpora()
    {
        await MapAsync("research", "id-books");

        var visible = await _scopes.VisibleAsync(Key("research"), TestContext.Current.CancellationToken);
        visible.Select(c => c.Name).ShouldBe(["books"]);

        // Naming one it cannot reach is an ERROR, not a silent drop.
        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Key("research"), ["private-notes"]));
        ex.Message.ShouldContain("Unknown corpus");
        ex.Message.ShouldContain("private-notes");

        // Guessing the id directly fares no better.
        await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Key("research"), ["id-private-notes"]));
    }

    [Fact]
    public async Task AKeyThatReachesEveryCorpusReachesOnesMadeLater()
    {
        // The setting, not an absence of rows, is what keeps a single-user install from having
        // to configure anything.
        await AddKeyAsync("everything", allCorpora: true);

        (await _scopes.VisibleAsync(Key("everything"), TestContext.Current.CancellationToken)).Select(c => c.Name)
            .ShouldBe(["books", "code", "private-notes"]);

        _db.Corpora.Add(Corpus("later"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await _scopes.VisibleAsync(Key("everything"), TestContext.Current.CancellationToken)).Select(c => c.Name)
            .ShouldBe(["books", "code", "later", "private-notes"]);
    }

    [Fact]
    public async Task AKeyRestrictedToNothingReachesNothing()
    {
        await AddKeyAsync("none", allCorpora: false);

        (await _scopes.VisibleAsync(Key("none"), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await _scopes.VisibleIdsAsync(Key("none"), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Key("none"), null));
        ex.Message.ShouldContain("can reach no corpora");
    }

    [Fact]
    public async Task APrincipalWithNoKeyRowReachesNothing()
    {
        // The row is what says what a key reaches, so a principal built without one is not
        // taken for an unrestricted key.
        (await _scopes.VisibleAsync(Key("ghost"), TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RemovingTheLastCorpusOfAKeyLeavesItReachingNothing()
    {
        await MapAsync("project", "id-books");
        await AddKeyAsync("everything", allCorpora: true);
        (await _scopes.VisibleAsync(Key("project"), TestContext.Current.CancellationToken)).Select(c => c.Name).ShouldBe(["books"]);

        // What removing a corpus does: the corpus goes and the foreign key takes the mapping row with it.
        _db.Corpora.Remove(await _db.Corpora.SingleAsync(c => c.Name == "books", cancellationToken: TestContext.Current.CancellationToken));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await _db.TokenCorpora.CountAsync(tc => tc.TokenId == "tok-project", cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0, "the row went with the corpus");
        (await _scopes.VisibleAsync(Key("project"), TestContext.Current.CancellationToken)).ShouldBeEmpty();
        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Key("project"), null));
        ex.Message.ShouldContain("can reach no corpora");

        // A key that reaches every corpus is not narrowed by the same removal.
        (await _scopes.VisibleAsync(Key("everything"), TestContext.Current.CancellationToken)).Select(c => c.Name).ShouldBe(["code", "private-notes"]);
    }

    [Fact]
    public async Task ChangingTheMappingTakesEffectWithoutReissuingTheKey()
    {
        var key = Key("agent");
        await MapAsync("agent", "id-books");
        (await _scopes.VisibleAsync(key, TestContext.Current.CancellationToken)).Select(c => c.Name).ShouldBe(["books"]);

        // What the operator does in the UI: replace the mapping outright.
        _db.TokenCorpora.RemoveRange(_db.TokenCorpora.Where(tc => tc.TokenId == "tok-agent"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await MapAsync("agent", "id-code");

        // The same principal, unchanged and never reissued, now resolves differently.
        (await _scopes.VisibleAsync(key, TestContext.Current.CancellationToken)).Select(c => c.Name).ShouldBe(["code"]);
    }

    [Fact]
    public async Task AnAdminSessionReachesEveryCorpus()
    {
        // The UI has to list a corpus in order to map a key to it.
        var visible = await _scopes.VisibleAsync(AdminSession(), TestContext.Current.CancellationToken);
        visible.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AnEmptyScopeNeverBecomesSearchEverything()
    {
        // Every corpus deleted, for a key that reached every corpus: a catalogue with no corpora at all.
        await AddKeyAsync("agent", allCorpora: true);
        _db.Corpora.RemoveRange(_db.Corpora);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Key("agent"), null));
        ex.Message.ShouldContain("can reach no corpora");
    }

    [Fact]
    public async Task WritesResolveWithinWhatTheKeyCanReach()
    {
        await MapAsync("agent", "id-books");

        (await _scopes.ResolveWritableAsync(Key("agent"), "books", TestContext.Current.CancellationToken)).Name.ShouldBe("books");

        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveWritableAsync(Key("agent"), "private-notes"));
        ex.Message.ShouldContain("private-notes");
    }

    // ── The repository-level guard, unchanged by D-28 ────────────────────────

    [Fact]
    public async Task VectorStoreRefusesAQueryWithNoCorpusFilter()
    {
        // The second of three independent guards. Even if scope resolution were bypassed
        // entirely, the store will not issue an unfiltered query.
        var store = new QdrantVectorStore(
            Microsoft.Extensions.Options.Options.Create(new Core.Configuration.DexiconOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QdrantVectorStore>.Instance);

        await Should.ThrowAsync<UnscopedQueryException>(() => store.SearchAsync(
            new SearchQuery
            {
                Text = SecretString,
                CorpusIds = [],                       // the bug being guarded
                ChunkSetIds = [],
                CollectionName = "dexicon__nomic-embed-text__768",
            },
            denseVector: new float[768],
            sparse: default,
            ct: default));
    }
}

/// <summary>
/// The admin password and the back-off in front of it. A password is the first credential
/// here that a human chooses, so it is the first that can be guessed.
/// </summary>
public sealed class AdminPasswordTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private TokenService _tokens = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _tokens = new TokenService(_db, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ThePasswordVerifiesAndTheWrongOneDoesNot()
    {
        (await _tokens.HasPasswordAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        await _tokens.SetPasswordAsync("correct horse battery staple", TestContext.Current.CancellationToken);

        (await _tokens.HasPasswordAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await _tokens.VerifyPasswordAsync("correct horse battery staple", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await _tokens.VerifyPasswordAsync("Correct Horse Battery Staple", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await _tokens.VerifyPasswordAsync("", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await _tokens.VerifyPasswordAsync(null, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task ChangingThePasswordInvalidatesTheOldOne()
    {
        await _tokens.SetPasswordAsync("first", TestContext.Current.CancellationToken);
        await _tokens.SetPasswordAsync("second", TestContext.Current.CancellationToken);

        (await _tokens.VerifyPasswordAsync("first", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await _tokens.VerifyPasswordAsync("second", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task AKeyCanNeverCarryAdmin()
    {
        // Requested explicitly, and dropped. The whole point is that no credential sitting
        // in an agent's configuration can delete a corpus or mint another key.
        var (row, _) = await _tokens.CreateAsync("agent", [Scopes.Search, Scopes.Admin], null, ct: TestContext.Current.CancellationToken);
        row.Scopes.ShouldBe("search");

        var principal = await _tokens.VerifyAsync((await _tokens.CreateAsync("other", [Scopes.Search], null, ct: TestContext.Current.CancellationToken)).Issued.Presented, TestContext.Current.CancellationToken);
        principal.ShouldNotBeNull();
        principal.Has(Scopes.Admin).ShouldBeFalse();
    }

    [Fact]
    public async Task AKeysScopesChangeInPlaceUnderTheSameRulesAsIssue()
    {
        // Granting configure to a working agent without issuing it another key. The stored
        // column is read, because a principal strips admin on its own and would hide it.
        var (row, issued) = await _tokens.CreateAsync("agent", [Scopes.Search], null, ct: TestContext.Current.CancellationToken);

        (await _tokens.SetScopesAsync(row.Id, [" Configure ", Scopes.Search, Scopes.Admin, Scopes.Search], TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        _db.ChangeTracker.Clear();
        (await _db.Tokens.AsNoTracking().SingleAsync(t => t.Id == row.Id, cancellationToken: TestContext.Current.CancellationToken)).Scopes.ShouldBe("configure,search");
        var principal = (await _tokens.VerifyAsync(issued.Presented, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        principal.Has(Scopes.Configure).ShouldBeTrue();
    }

    [Fact]
    public async Task ARevokedOrUnknownKeysScopesAreNotChanged()
    {
        var (row, _) = await _tokens.CreateAsync("agent", [Scopes.Search], null, ct: TestContext.Current.CancellationToken);
        await _tokens.RevokeAsync(row.Id, TestContext.Current.CancellationToken);

        (await _tokens.SetScopesAsync(row.Id, [Scopes.Configure], TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await _tokens.SetScopesAsync("no-such-key", [Scopes.Configure], TestContext.Current.CancellationToken)).ShouldBeFalse();
        _db.ChangeTracker.Clear();
        (await _db.Tokens.AsNoTracking().SingleAsync(t => t.Id == row.Id, cancellationToken: TestContext.Current.CancellationToken)).Scopes.ShouldBe("search");
    }

    [Fact]
    public async Task AScopeRemovedIsRefusedOnTheNextRequestThoughThePrincipalWasCached()
    {
        // The principal is cached for a minute to spare a PBKDF2 per call, and the scopes
        // travel on it. Without the eviction a removed configure or ingest scope stayed usable
        // until the entry expired; a test calling VerifyAsync directly never sees the cache.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var evictor = new MemoryCacheEvictor(cache);
        var (row, issued) = await _tokens.CreateAsync("agent", [Scopes.Search, Scopes.Configure], null, ct: TestContext.Current.CancellationToken);

        (await AuthenticateAsync(cache, evictor, issued.Presented)).ShouldNotBeNull()
            .Has(Scopes.Configure).ShouldBeTrue();
        cache.Count.ShouldBe(1, "the principal is cached");

        var result = await SystemEndpoints.SetScopesAsync(row.Id, new UpdateTokenScopesRequest([Scopes.Search]), _tokens, _db, evictor, NullLogger.Instance, TestContext.Current.CancellationToken);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status200OK);
        (await AuthenticateAsync(cache, evictor, issued.Presented)).ShouldNotBeNull().Has(Scopes.Configure)
            .ShouldBeFalse("the next request is judged on the stored scopes, not the cached ones");
    }

    [Fact]
    public async Task APrincipalWrittenAfterAnEvictionIsNeverReadByALaterRequest()
    {
        // The race itself: a request that read the key's row before a scope change, and
        // finished hashing after the eviction, writes the old principal into the cleared
        // cache. Filed under the generation it began in, no later request reads it.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var evictor = new MemoryCacheEvictor(cache);
        var (row, issued) = await _tokens.CreateAsync("agent", [Scopes.Search], null, ct: TestContext.Current.CancellationToken);
        var began = evictor.Generation;

        evictor.EvictPrincipals();
        cache.Set(DexiconAuthMiddleware.PrincipalCacheKey(issued.Presented, began),
            new Principal(row.Id, row.Name, new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Configure }));

        (await AuthenticateAsync(cache, evictor, issued.Presented)).ShouldNotBeNull()
            .Has(Scopes.Configure).ShouldBeFalse("the late write is under the old generation");
    }

    [Fact]
    public async Task ARequestBeginningInALaterGenerationDoesNotReadAnEarlierGenerationsEntry()
    {
        // The generation is part of the cache key, read when the request begins. Only the
        // generation moves here, as an eviction moves it, with the stored scopes changed and
        // nothing cleared: a middleware that ignored the generation would serve the old entry.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var evictor = new SettableGeneration { Generation = 5 };
        var (row, issued) = await _tokens.CreateAsync("agent", [Scopes.Search, Scopes.Configure], null, ct: TestContext.Current.CancellationToken);

        (await AuthenticateAsync(cache, evictor, issued.Presented)).ShouldNotBeNull()
            .Has(Scopes.Configure).ShouldBeTrue();

        await _tokens.SetScopesAsync(row.Id, [Scopes.Search], TestContext.Current.CancellationToken);
        evictor.Generation = 6;

        (await AuthenticateAsync(cache, evictor, issued.Presented)).ShouldNotBeNull()
            .Has(Scopes.Configure).ShouldBeFalse("generation 6 does not read generation 5's entry");
        cache.Count.ShouldBe(2, "one entry per generation, until the clear frees the old one");
    }

    /// <summary>A generation a test moves by hand.</summary>
    private sealed class SettableGeneration : IMemoryCacheEvictor
    {
        public long Generation { get; set; }

        public void EvictPrincipals() { }
    }

    /// <summary>The principal one request authenticates as, through the real middleware and cache.</summary>
    private async Task<Principal?> AuthenticateAsync(IMemoryCache cache, IMemoryCacheEvictor evictor, string presented)
    {
        var request = new RequestContext();
        var middleware = new DexiconAuthMiddleware(_ => Task.CompletedTask, cache, evictor,
            NullLogger<DexiconAuthMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/mcp";
        ctx.Request.Headers.Authorization = $"Bearer {presented}";
        await middleware.InvokeAsync(ctx, _tokens, new AdminSessions(TimeProvider.System), request);
        return request.Principal;
    }

    [Fact]
    public async Task ABootstrapKeyIsAdoptedWithSearchAndIngestOnly()
    {
        // It was adopted with every issuable scope, which gained configure: a value in .env
        // reaching every mounted folder, where docs/12 promises search and ingest (D-36).
        using var services = new ServiceCollection().AddSingleton(_tokens).BuildServiceProvider();
        var options = new DexiconOptions
        {
            Bootstrap = new BootstrapOptions { Token = "dex_01JBXQZ9K7MNPRSTVWXYZ01234_a-secret-nobody-should-read" },
        };

        await Bootstrapper.EnsureBootstrapTokenAsync(services, _db, NullLogger.Instance, options);

        _db.ChangeTracker.Clear();
        var adopted = await _db.Tokens.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        adopted.Scopes.ShouldBe("search,ingest");
        adopted.AllCorpora.ShouldBeTrue("a key with no row in the mapping would reach nothing");
    }

    [Fact]
    public async Task AdoptingAKeyAgainLeavesWhatItReaches()
    {
        const string presented = "dex_01JBXQZ9K7MNPRSTVWXYZ01234_a-secret-nobody-should-read";
        await _tokens.AdoptAsync("bootstrap", Scopes.Bootstrap, presented, TestContext.Current.CancellationToken);
        (await _db.Tokens.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AllCorpora.ShouldBeTrue("adopted reaching every corpus");

        // The operator restricted it on the Access page, then the value in .env was set again.
        _db.Corpora.Add(new Corpus { Id = "c1", Name = "one", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var key = await _db.Tokens.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        key.AllCorpora = false;
        _db.TokenCorpora.Add(new TokenCorpus { TokenId = key.Id, CorpusId = "c1" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();

        await _tokens.AdoptAsync("bootstrap", Scopes.Bootstrap, presented, TestContext.Current.CancellationToken);

        _db.ChangeTracker.Clear();
        (await _db.Tokens.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AllCorpora.ShouldBeFalse();
        (await _db.TokenCorpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public void TheThrottleGrowsThenCapsAndResetsOnSuccess()
    {
        var throttle = new LoginThrottle(TimeProvider.System);

        // The first attempts are free, so a typo costs nothing.
        throttle.Delay().ShouldBe(TimeSpan.Zero);
        throttle.RecordFailure();
        throttle.Delay().ShouldBe(TimeSpan.Zero);

        // Then it doubles.
        throttle.RecordFailure();
        throttle.Delay().ShouldBe(TimeSpan.FromSeconds(1));
        throttle.RecordFailure();
        throttle.Delay().ShouldBe(TimeSpan.FromSeconds(2));
        throttle.RecordFailure();
        throttle.Delay().ShouldBe(TimeSpan.FromSeconds(4));

        // And stops, because an unbounded delay is a lockout by another name and anyone
        // able to reach the port could then inflict one on the owner.
        for (var i = 0; i < 20; i++) throttle.RecordFailure();
        throttle.Delay().ShouldBe(LoginThrottle.Cap);

        // A correct password clears it, so the operator is never left waiting.
        throttle.Reset();
        throttle.Failures.ShouldBe(0);
        throttle.Delay().ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void SessionsAndTheThrottleDoNotShareTheCacheRevocationClears()
    {
        // Revoking a key calls IMemoryCacheEvictor.EvictPrincipals, which clears the shared
        // IMemoryCache outright because MemoryCache has no prefix scan. While sessions lived
        // in that cache, revoking any key signed the operator out mid-action: the browser
        // reported "Invalid credentials" for a request it had just made successfully.
        //
        // Pinned structurally rather than behaviourally. Neither type can be handed the
        // shared cache any more, so the collision cannot be reintroduced by a caller.
        foreach (var type in new[] { typeof(AdminSessions), typeof(LoginThrottle) })
        {
            var parameters = type.GetConstructors().Single().GetParameters();
            parameters.ShouldNotContain(
                p => typeof(IMemoryCache).IsAssignableFrom(p.ParameterType),
                $"{type.Name} must own its store; sharing it means a key revocation wipes it");
        }
    }

    [Fact]
    public void ASessionVerifiesOnlyItsOwnValueAndOnlyUntilRevoked()
    {
        var sessions = new AdminSessions(TimeProvider.System);

        var session = sessions.Issue();
        session.Presented.ShouldStartWith(AdminSessions.Prefix);

        var principal = sessions.Verify(session.Presented);
        principal.ShouldNotBeNull();
        principal.Has(Scopes.Admin).ShouldBeTrue();

        sessions.Verify("dexs_not-a-real-session").ShouldBeNull();
        // A key is not a session, whatever it looks like.
        sessions.Verify("dex_abc_def").ShouldBeNull();

        sessions.Revoke(session.Presented);
        sessions.Verify(session.Presented).ShouldBeNull();
    }
}
