using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dexicon.Tests;

/// <summary>
/// The scoping guarantee, which replaced the tenant isolation one when D-28 removed
/// tenants. The promise is narrower and it is still worth a test nobody deletes by
/// accident: a key reaches the corpora it is mapped to and no others, and every failure is
/// an authorization error naming the problem rather than an empty result, because an empty
/// result is indistinguishable from "that content does not exist" and a caller acts on it.
/// </summary>
public sealed class KeyScopingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private ScopeResolver _scopes = null!;

    private const string SecretString = "hunter2-globex-launch-code";

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
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
            });
        }

        foreach (var corpusId in corpusIds)
            _db.TokenCorpora.Add(new TokenCorpus { TokenId = id, CorpusId = corpusId });

        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task AMappedKeyReachesOnlyItsCorpora()
    {
        await MapAsync("research", "id-books");

        var visible = await _scopes.VisibleAsync(Key("research"));
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
    public async Task AnUnmappedKeyReachesEverything()
    {
        // No rows means every corpus, which is what keeps a single-user install from
        // having to configure anything. Distinct from reaching nothing, which is a revoked
        // key rather than an empty mapping.
        var visible = await _scopes.VisibleAsync(Key("unmapped"));
        visible.Select(c => c.Name).ShouldBe(["books", "code", "private-notes"]);
    }

    [Fact]
    public async Task ChangingTheMappingTakesEffectWithoutReissuingTheKey()
    {
        var key = Key("agent");
        await MapAsync("agent", "id-books");
        (await _scopes.VisibleAsync(key)).Select(c => c.Name).ShouldBe(["books"]);

        // What the operator does in the UI: replace the mapping outright.
        _db.TokenCorpora.RemoveRange(_db.TokenCorpora.Where(tc => tc.TokenId == "tok-agent"));
        await _db.SaveChangesAsync();
        await MapAsync("agent", "id-code");

        // The same principal, unchanged and never reissued, now resolves differently.
        (await _scopes.VisibleAsync(key)).Select(c => c.Name).ShouldBe(["code"]);
    }

    [Fact]
    public async Task AnAdminSessionReachesEveryCorpus()
    {
        // The UI has to list a corpus in order to map a key to it.
        var visible = await _scopes.VisibleAsync(AdminSession());
        visible.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AnEmptyScopeNeverBecomesSearchEverything()
    {
        // A key mapped only to a corpus that has since been deleted. The mapping row goes
        // with it, so this is the same shape as a catalogue with no corpora at all.
        _db.Corpora.RemoveRange(_db.Corpora);
        await _db.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Key("agent"), null));
        ex.Message.ShouldContain("can reach no corpora");
    }

    [Fact]
    public async Task WritesResolveWithinWhatTheKeyCanReach()
    {
        await MapAsync("agent", "id-books");

        (await _scopes.ResolveWritableAsync(Key("agent"), "books")).Name.ShouldBe("books");

        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveWritableAsync(Key("agent"), "private-notes"));
        ex.Message.ShouldContain("private-notes");
    }

    // ── The repository-level guard, unchanged by D-28 ────────────────────────

    [Fact]
    public async Task VectorStore_RefusesAQueryWithNoCorpusFilter()
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

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _tokens = new TokenService(_db, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ThePasswordVerifiesAndTheWrongOneDoesNot()
    {
        (await _tokens.HasPasswordAsync()).ShouldBeFalse();

        await _tokens.SetPasswordAsync("correct horse battery staple");

        (await _tokens.HasPasswordAsync()).ShouldBeTrue();
        (await _tokens.VerifyPasswordAsync("correct horse battery staple")).ShouldBeTrue();
        (await _tokens.VerifyPasswordAsync("Correct Horse Battery Staple")).ShouldBeFalse();
        (await _tokens.VerifyPasswordAsync("")).ShouldBeFalse();
        (await _tokens.VerifyPasswordAsync(null)).ShouldBeFalse();
    }

    [Fact]
    public async Task ChangingThePasswordInvalidatesTheOldOne()
    {
        await _tokens.SetPasswordAsync("first");
        await _tokens.SetPasswordAsync("second");

        (await _tokens.VerifyPasswordAsync("first")).ShouldBeFalse();
        (await _tokens.VerifyPasswordAsync("second")).ShouldBeTrue();
    }

    [Fact]
    public async Task AKeyCanNeverCarryAdmin()
    {
        // Requested explicitly, and dropped. The whole point is that no credential sitting
        // in an agent's configuration can delete a corpus or mint another key.
        var (row, _) = await _tokens.CreateAsync("agent", [Scopes.Search, Scopes.Admin], null);
        row.Scopes.ShouldBe("search");

        var principal = await _tokens.VerifyAsync(
            (await _tokens.CreateAsync("other", [Scopes.Search], null)).Issued.Presented);
        principal.ShouldNotBeNull();
        principal.Has(Scopes.Admin).ShouldBeFalse();
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
