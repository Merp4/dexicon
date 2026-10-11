using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dexicon.Tests;

/// <summary>
/// Whether a key reaches every corpus is a setting on the key, <c>AllCorpora</c>. It used to be read
/// from the absence of rows in <c>token_corpora</c>, and removing a corpus deletes the rows that name
/// it, so removing the corpus a key was restricted to made it reach every corpus.
///
/// Held here: removing a corpus leaves a restricted key restricted, the requests that set a key's
/// reach say which of the two they mean, and the migration gives existing keys what they reached.
/// </summary>
public sealed class KeyReachTests
{
    private static RequestContext AsAdmin() => new()
    {
        Principal = new Principal("admin-session", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode.ShouldNotBeNull();

    private static async Task<IndexingHarness> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        return harness;
    }

    private static async Task<(ApiToken Key, Corpus Papers)> RestrictedToPapersAsync(IndexingHarness harness, CatalogDbContext db)
    {
        var papers = (await harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("papers"), default)).Value!;
        var (key, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("agent", [Scopes.Search, Scopes.Configure], null, [papers.Id]);
        return (key, await db.Corpora.SingleAsync(c => c.Id == papers.Id));
    }

    private static Principal Agent(ApiToken key) =>
        new(key.Id, key.Name, new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Configure });

    /// <summary>What the catalogue holds for the key, read on a context of its own: the setting, then the rows.</summary>
    private static async Task<string> StoredAsync(IndexingHarness harness, string keyId)
    {
        await using var check = harness.NewContext();
        var all = (await check.Tokens.AsNoTracking().SingleAsync(t => t.Id == keyId)).AllCorpora;
        var rows = await check.TokenCorpora.Where(tc => tc.TokenId == keyId).Select(tc => tc.CorpusId).OrderBy(c => c).ToListAsync();
        return $"{(all ? "every corpus" : "restricted")}; rows: [{string.Join(", ", rows)}]";
    }

    // ---- removing a corpus ------------------------------------------------------------------------

    [Fact]
    public async Task RemovingTheCorpusAKeyWasRestrictedToLeavesItReachingNothing()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, papers) = await RestrictedToPapersAsync(harness, db);
        var resolver = new ScopeResolver(db);
        (await resolver.VisibleAsync(Agent(key))).Select(c => c.Name).ShouldBe(["papers"]);

        (await harness.NewConfiguration(db).RemoveCorpusAsync(papers, default)).Refusal.ShouldBeNull();

        (await StoredAsync(harness, key.Id)).ShouldBe("restricted; rows: []", "the row went with the corpus, and the key is still restricted");
        (await resolver.VisibleAsync(Agent(key))).ShouldBeEmpty("the corpus that is left was never this key's");
        (await Should.ThrowAsync<ScopeResolutionException>(() => resolver.ResolveReadableAsync(Agent(key), null)))
            .Message.ShouldContain("can reach no corpora");
        await using var listing = harness.NewContext();
        var summary = (await listing.Tokens.Include(t => t.Corpora).SingleAsync(t => t.Id == key.Id)).ToSummary();
        summary.AllCorpora.ShouldBeFalse("what the Access page reads to tell this key from one that reaches every corpus");
        summary.CorpusIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemovingACorpusLeavesAKeyThatReachesEveryCorpusReachingTheRest()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (_, papers) = await RestrictedToPapersAsync(harness, db);
        var (everything, _) = await new TokenService(db, TimeProvider.System).CreateAsync("everything", [Scopes.Search], null);

        await harness.NewConfiguration(db).RemoveCorpusAsync(papers, default);

        (await new ScopeResolver(db).VisibleAsync(Agent(everything))).Select(c => c.Name).ShouldBe(["notes"]);
    }

    [Fact]
    public async Task ACorpusMadeAfterTheRemovalIsNotReachedByTheKeyThatLostItsOwn()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, papers) = await RestrictedToPapersAsync(harness, db);
        await harness.NewConfiguration(db).RemoveCorpusAsync(papers, default);

        await harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("later"), default);

        (await new ScopeResolver(db).VisibleAsync(Agent(key))).ShouldBeEmpty();
    }

    // ---- creating a key ---------------------------------------------------------------------------

    [Fact]
    public async Task AKeyIssuedWithNoReachSaysItReachesEveryCorpus()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent"), AsAdmin(),
            new TokenService(db, TimeProvider.System), db, default);

        var token = result.ShouldBeOfType<Ok<CreatedTokenResponse>>().Value!.Token;
        token.AllCorpora.ShouldBeTrue();
        token.CorpusIds.ShouldBeEmpty();
        (await StoredAsync(harness, token.Id)).ShouldBe("every corpus; rows: []");
    }

    [Fact]
    public async Task AnEmptyListOnANewKeyIsRefusedAndNoKeyIsSaved()
    {
        // The list does not say whether it means none or every corpus, so a script that builds it from a
        // filter and finds nothing does not mint a key that reaches every corpus.
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent", CorpusIds: []), AsAdmin(),
            new TokenService(db, TimeProvider.System), db, default);

        StatusOf(result).ShouldBe(400);
        (await db.Tokens.CountAsync(t => t.Name == "agent")).ShouldBe(0);
    }

    [Fact]
    public async Task AnEmptyListBesideEveryCorpusIsAKeyThatReachesEveryCorpus()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(
            new CreateTokenRequest("agent", CorpusIds: [], AllCorpora: true), AsAdmin(),
            new TokenService(db, TimeProvider.System), db, default);

        result.ShouldBeOfType<Ok<CreatedTokenResponse>>().Value!.Token.AllCorpora.ShouldBeTrue();
    }

    [Fact]
    public async Task AListOfMoreThanTheMostCorporaIsRefusedBeforeItReachesAQuery()
    {
        // SQLite refuses a query with too many parameters, which was a 500 for a request of 200,000 ids.
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var ids = Enumerable.Range(0, SystemEndpoints.MaxReachIds + 1).Select(i => $"id-{i}").ToList();

        var create = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent", CorpusIds: ids), AsAdmin(),
            new TokenService(db, TimeProvider.System), db, default);
        var (key, _) = await new TokenService(db, TimeProvider.System).CreateAsync("other", [Scopes.Search], null);
        db.ChangeTracker.Clear();
        var replace = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest(ids), AsAdmin(), db, default);

        StatusOf(create).ShouldBe(400);
        StatusOf(replace).ShouldBe(400);
        (await StoredAsync(harness, key.Id)).ShouldBe("every corpus; rows: []");
    }

    [Fact]
    public async Task AKeyIssuedRestrictedToNoCorpusReachesNothing()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent", CorpusIds: [], AllCorpora: false),
            AsAdmin(), new TokenService(db, TimeProvider.System), db, default);

        var token = result.ShouldBeOfType<Ok<CreatedTokenResponse>>().Value!.Token;
        token.AllCorpora.ShouldBeFalse();
        (await StoredAsync(harness, token.Id)).ShouldBe("restricted; rows: []");
        (await new ScopeResolver(db).VisibleAsync(new Principal(token.Id, "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search })))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task AKeyIssuedForEveryCorpusAndNamingSomeIsRefusedAndNotSaved()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(
            new CreateTokenRequest("agent", CorpusIds: [IndexingHarness.CorpusId], AllCorpora: true),
            AsAdmin(), new TokenService(db, TimeProvider.System), db, default);

        StatusOf(result).ShouldBe(400);
        (await db.Tokens.CountAsync(t => t.Name == "agent")).ShouldBe(0);
    }

    [Fact]
    public async Task AKeyIssuedRestrictedWithoutSayingToWhatIsRefusedAndNotSaved()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.CreateTokenAsync(new CreateTokenRequest("agent", AllCorpora: false),
            AsAdmin(), new TokenService(db, TimeProvider.System), db, default);

        StatusOf(result).ShouldBe(400);
        (await db.Tokens.CountAsync(t => t.Name == "agent")).ShouldBe(0);
    }

    // ---- replacing a key's reach ------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyListWithoutASettingIsRefusedAndTheKeyStaysRestricted()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, papers) = await RestrictedToPapersAsync(harness, db);
        db.ChangeTracker.Clear();

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest([]), AsAdmin(), db, default);

        StatusOf(result).ShouldBe(400);
        (await StoredAsync(harness, key.Id)).ShouldBe($"restricted; rows: [{papers.Id}]");
    }

    [Fact]
    public async Task SayingEveryCorpusLiftsTheRestrictionAndClearsTheRows()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, _) = await RestrictedToPapersAsync(harness, db);

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest(AllCorpora: true),
            AsAdmin(), db, default);

        var summary = result.ShouldBeOfType<Ok<TokenSummary>>().Value!;
        summary.AllCorpora.ShouldBeTrue();
        summary.CorpusIds.ShouldBeEmpty();
        (await StoredAsync(harness, key.Id)).ShouldBe("every corpus; rows: []");
        (await new ScopeResolver(db).VisibleAsync(Agent(key))).Select(c => c.Name).ShouldBe(["notes", "papers"]);
    }

    [Fact]
    public async Task SayingNoneRestrictsTheKeyToNoCorpus()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, _) = await RestrictedToPapersAsync(harness, db);

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest([], AllCorpora: false),
            AsAdmin(), db, default);

        var summary = result.ShouldBeOfType<Ok<TokenSummary>>().Value!;
        summary.AllCorpora.ShouldBeFalse();
        summary.CorpusIds.ShouldBeEmpty();
        (await StoredAsync(harness, key.Id)).ShouldBe("restricted; rows: []");
        (await new ScopeResolver(db).VisibleAsync(Agent(key))).ShouldBeEmpty();
    }

    [Fact]
    public async Task AListRestrictsAKeyThatReachedEveryCorpus()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (everything, _) = await new TokenService(db, TimeProvider.System).CreateAsync("everything", [Scopes.Search], null);

        var result = await SystemEndpoints.ReplaceCorporaAsync(everything.Id,
            new UpdateTokenCorporaRequest([IndexingHarness.CorpusId]), AsAdmin(), db, default);

        var summary = result.ShouldBeOfType<Ok<TokenSummary>>().Value!;
        summary.AllCorpora.ShouldBeFalse();
        summary.CorpusIds.ShouldBe([IndexingHarness.CorpusId]);
        (await StoredAsync(harness, everything.Id)).ShouldBe($"restricted; rows: [{IndexingHarness.CorpusId}]");
    }

    [Fact]
    public async Task EveryCorpusAndAListTogetherAreRefusedAndTheKeyIsKept()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, papers) = await RestrictedToPapersAsync(harness, db);
        db.ChangeTracker.Clear();

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id,
            new UpdateTokenCorporaRequest([IndexingHarness.CorpusId], AllCorpora: true), AsAdmin(), db, default);

        StatusOf(result).ShouldBe(400);
        (await StoredAsync(harness, key.Id)).ShouldBe($"restricted; rows: [{papers.Id}]");
    }

    [Fact]
    public async Task RestrictingWithoutAListIsRefusedAndTheKeyIsKept()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, papers) = await RestrictedToPapersAsync(harness, db);
        db.ChangeTracker.Clear();

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest(AllCorpora: false),
            AsAdmin(), db, default);

        StatusOf(result).ShouldBe(400);
        (await StoredAsync(harness, key.Id)).ShouldBe($"restricted; rows: [{papers.Id}]");
    }

    [Fact]
    public async Task AnUnknownCorpusIsRefusedAndTheReachIsKept()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, papers) = await RestrictedToPapersAsync(harness, db);
        db.ChangeTracker.Clear();

        var result = await SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest(["missing"]),
            AsAdmin(), db, default);

        StatusOf(result).ShouldBe(400);
        (await StoredAsync(harness, key.Id)).ShouldBe($"restricted; rows: [{papers.Id}]");
    }

    [Fact]
    public async Task AReplacementThatWaitedForTheLockWritesWhatItWasAskedFor()
    {
        // The key was read before the wait, so a change made meanwhile by the request that held the lock
        // went unseen: the setting assigned equalled the stale one, no update was sent, and the rows were
        // replaced beside a setting that no longer matched them.
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, _) = await new TokenService(db, TimeProvider.System).CreateAsync("agent", [Scopes.Search], null);
        db.ChangeTracker.Clear();
        await using var other = harness.NewContext();

        Task<IResult> everyCorpus;
        await CorpusConfiguration.Naming.WaitAsync();
        try
        {
            everyCorpus = SystemEndpoints.ReplaceCorporaAsync(key.Id, new UpdateTokenCorporaRequest(AllCorpora: true),
                AsAdmin(), db, default);
            await Task.Delay(300);
            everyCorpus.IsCompleted.ShouldBeFalse("the replacement waits for the lock, which is the window");

            var held = await other.Tokens.SingleAsync(t => t.Id == key.Id);
            held.AllCorpora = false;
            other.TokenCorpora.Add(new TokenCorpus { TokenId = key.Id, CorpusId = IndexingHarness.CorpusId });
            await other.SaveChangesAsync();
        }
        finally
        {
            CorpusConfiguration.Naming.Release();
        }

        var summary = (await everyCorpus.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeOfType<Ok<TokenSummary>>().Value!;
        summary.AllCorpora.ShouldBeTrue();
        summary.CorpusIds.ShouldBeEmpty();
        (await StoredAsync(harness, key.Id)).ShouldBe("every corpus; rows: []", "the last request decides");
    }

    [Fact]
    public async Task TheSettingIsWrittenEvenWhenTheContextHoldsAStaleCopyOfTheKey()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, _) = await new TokenService(db, TimeProvider.System).CreateAsync("agent", [Scopes.Search], null);
        db.ChangeTracker.Clear();
        var stale = await db.Tokens.SingleAsync(t => t.Id == key.Id);
        stale.AllCorpora.ShouldBeTrue();
        await using var other = harness.NewContext();
        var held = await other.Tokens.SingleAsync(t => t.Id == key.Id);
        held.AllCorpora = false;
        other.TokenCorpora.Add(new TokenCorpus { TokenId = key.Id, CorpusId = IndexingHarness.CorpusId });
        await other.SaveChangesAsync();

        var (refusal, _) = await SystemEndpoints.MapCorporaAsync(db, key.Id, null, default);

        refusal.ShouldBeNull();
        (await StoredAsync(harness, key.Id)).ShouldBe("every corpus; rows: []");
    }

    [Fact]
    public async Task ReplacingTheReachOfAKeyThatDoesNotExistIsA404()
    {
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();

        var result = await SystemEndpoints.ReplaceCorporaAsync("missing", new UpdateTokenCorporaRequest(AllCorpora: true),
            AsAdmin(), db, default);

        StatusOf(result).ShouldBe(404);

        // The key is looked up first, as it was before the lock moved, so a missing key beside a missing
        // corpus is still the key's 404.
        var both = await SystemEndpoints.ReplaceCorporaAsync("missing", new UpdateTokenCorporaRequest(["no-such-corpus"]),
            AsAdmin(), db, default);

        StatusOf(both).ShouldBe(404);
    }

    [Fact]
    public async Task ARowSavedWithoutTheSettingIsAKeyThatReachesNothing()
    {
        // The default of the property, which is what a row built without it gets.
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        db.Tokens.Add(new ApiToken
        {
            Id = "unset",
            Name = "unset",
            TokenHash = [1],
            TokenSalt = [2],
            Scopes = Scopes.Search,
            CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await StoredAsync(harness, "unset")).ShouldBe("restricted; rows: []");
        (await new ScopeResolver(db).VisibleAsync(new Principal("unset", "unset", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search })))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task ARowLeftBesideTheSettingIsNotListedAsWhatTheKeyReaches()
    {
        // The rows are ignored while the key reaches every corpus, and the summary says so.
        await using var harness = await StartAsync();
        await using var db = harness.NewContext();
        var (key, _) = await new TokenService(db, TimeProvider.System).CreateAsync("agent", [Scopes.Search], null);
        db.TokenCorpora.Add(new TokenCorpus { TokenId = key.Id, CorpusId = IndexingHarness.CorpusId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var summary = (await db.Tokens.Include(t => t.Corpora).SingleAsync(t => t.Id == key.Id)).ToSummary();

        summary.AllCorpora.ShouldBeTrue();
        summary.CorpusIds.ShouldBeEmpty();
        (await new ScopeResolver(db).VisibleAsync(Agent(key))).Select(c => c.Name).ShouldBe(["notes"]);
    }

    // ---- the migration ----------------------------------------------------------------------------

    private const string BeforeIt = "20261008200000_FoldTaggedModelNames";
    private const string TheMigration = "20261011015611_KeyReachIsExplicit";

    [Fact]
    public async Task TheMigrationKeepsWhatEveryKeyReachedWhenItRan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dexicon-reach-{Guid.NewGuid():N}.db");
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeIt);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO corpora (Id, Name, State, CreatedUtc) VALUES ('c1', 'one', 'Ready', '2026-10-01 00:00:00')");
            foreach (var id in new[] { "k-none", "k-one", "k-revoked" })
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO tokens (Id, Name, TokenHash, TokenSalt, Scopes, CreatedUtc, RevokedUtc) "
                    + "VALUES ({0}, {0}, zeroblob(32), zeroblob(32), 'search', '2026-10-01 00:00:00', {1})",
                    id, id == "k-revoked" ? "2026-10-02 00:00:00" : null!);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO token_corpora (TokenId, CorpusId) VALUES ('k-one', 'c1')");

            await db.GetService<IMigrator>().MigrateAsync(TheMigration);

            var after = await db.Tokens.AsNoTracking().Select(t => new { t.Id, t.AllCorpora }).ToDictionaryAsync(t => t.Id, t => t.AllCorpora);
            after.ShouldBe(new Dictionary<string, bool>
            {
                ["k-none"] = true,
                ["k-one"] = false,
                ["k-revoked"] = true,
            }, ignoreOrder: true);
            (await db.TokenCorpora.AsNoTracking().Select(tc => tc.CorpusId).ToListAsync()).ShouldBe(["c1"], "the rows are left as they were");
        }
        finally
        {
            await db.DisposeAsync();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AKeyInsertedAfterTheColumnExistsWithoutItReachesNothing()
    {
        // A version that does not know the column inserts a key without it, and the column's default is
        // restricted, not every corpus.
        var path = Path.Combine(Path.GetTempPath(), $"dexicon-reach-{Guid.NewGuid():N}.db");
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        try
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO tokens (Id, Name, TokenHash, TokenSalt, Scopes, CreatedUtc) "
                + "VALUES ('k', 'k', zeroblob(32), zeroblob(32), 'search', '2026-10-01 00:00:00')");

            (await db.Tokens.AsNoTracking().SingleAsync(t => t.Id == "k")).AllCorpora.ShouldBeFalse();
            (await db.Database.GetAppliedMigrationsAsync()).ShouldContain(TheMigration);
            db.Database.HasPendingModelChanges().ShouldBeFalse();
        }
        finally
        {
            await db.DisposeAsync();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
