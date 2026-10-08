using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Catalog.Migrations;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dexicon.Tests;

/// <summary>
/// Detaching a document has a scope of its own, and every key that could detach one still can.
///
/// The endpoint's own check is held by <see cref="DocumentDetachEndpointTests"/>, which call the
/// handler the route is mapped to. What is held here is the rest: where the scope is listed and
/// not listed, that it is independent of <c>ingest</c>, that a key can be given it, what a key
/// refused for lack of a scope is told, and that the migration gives it to exactly the keys that
/// held <c>ingest</c>, through the migrator the app uses and not a copy of its text. See
/// docs/decisions.md D-40.
/// </summary>
public sealed class DestroyScopeTests
{
    private const string BeforeIt = "20261008020000_AddProposals";
    private const string TheMigration = "20261008100000_GrantDestroyToIngestKeys";

    [Fact]
    public void DestroyIsIssuableAndListedAndIsNotGivenToAKeyAdoptedFromTheEnvironment()
    {
        Scopes.Issuable.ShouldContain(Scopes.Destroy);
        Scopes.All.ShouldContain(Scopes.Destroy);
        Scopes.Bootstrap.ShouldNotContain(Scopes.Destroy, "a value sitting in .env must not carry a removal");
        Scopes.Issuable.ShouldNotContain(Scopes.Admin);
    }

    [Fact]
    public void DestroyAndIngestAreIndependentAndTheAdministratorHoldsBoth()
    {
        Principal Key(params string[] scopes) => new("k", "agent", scopes.ToHashSet(StringComparer.Ordinal));

        Key(Scopes.Ingest).Has(Scopes.Destroy).ShouldBeFalse("adding and attaching documents does not carry detaching them");
        Key(Scopes.Destroy).Has(Scopes.Ingest).ShouldBeFalse();
        Key(Scopes.Search, Scopes.Ingest, Scopes.Destroy).Has(Scopes.Destroy).ShouldBeTrue();
        Key(Scopes.Admin).Has(Scopes.Destroy).ShouldBeTrue();
    }

    [Fact]
    public void ARefusedKeyIsToldWhoCanGrantTheScopeAndAdministrationStaysThePasswords()
    {
        var rc = new RequestContext
        {
            Principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Ingest }),
        };

        var refused = rc.RequireScope(Scopes.Destroy).ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(403);
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("needs 'destroy'");
        refused.ProblemDetails.Detail.ShouldContain("Access page");
        refused.ProblemDetails.Detail.ShouldNotContain("password", Case.Insensitive, "a key's scope is not the password's to give");

        var admin = rc.RequireScope(Scopes.Admin).ShouldBeOfType<ProblemHttpResult>();
        admin.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("password");
        rc.RequireScope(Scopes.Ingest).ShouldBeNull("what the key holds is allowed");
    }

    [Fact]
    public async Task AKeyIsIssuedDestroyAloneAndCanBeGivenItLater()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await using var db = harness.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);

        var (alone, _) = await tokens.CreateAsync("remover", [Scopes.Destroy], null);
        var (reader, _) = await tokens.CreateAsync("reader", [Scopes.Search], null);
        (await tokens.SetScopesAsync(reader.Id, [Scopes.Search, Scopes.Destroy])).ShouldBeTrue();

        alone.Scopes.Split(',').ShouldBe([Scopes.Destroy]);
        (await db.Tokens.AsNoTracking().SingleAsync(t => t.Id == reader.Id)).Scopes.Split(',')
            .ShouldBe([Scopes.Search, Scopes.Destroy]);
    }

    // ---- the migration ----------------------------------------------------------------

    private static async Task<(CatalogDbContext Db, string Path)> CatalogAtTheSchemaBeforeItAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dexicon-destroy-{Guid.NewGuid():N}.db");
        var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        await db.GetService<IMigrator>().MigrateAsync(BeforeIt);
        return (db, path);
    }

    private static Task<int> SeedKeyAsync(CatalogDbContext db, string id, string scopes, bool revoked = false) =>
        db.Database.ExecuteSqlRawAsync(
            "INSERT INTO tokens (Id, Name, TokenHash, TokenSalt, Scopes, CreatedUtc, RevokedUtc) "
            + "VALUES ({0}, {0}, zeroblob(32), zeroblob(32), {1}, '2026-10-01 00:00:00', {2})",
            id, scopes, revoked ? "2026-10-02 00:00:00" : null!);

    private static async Task<Dictionary<string, string>> ScopesOfEveryKeyAsync(CatalogDbContext db) =>
        await db.Tokens.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Scopes);

    [Fact]
    public async Task TheMigrationGivesDestroyToEveryKeyHoldingIngestAndToNoOther()
    {
        var (db, path) = await CatalogAtTheSchemaBeforeItAsync();
        try
        {
            await SeedKeyAsync(db, "k-ingest", "search,ingest");
            await SeedKeyAsync(db, "k-first", "ingest");
            await SeedKeyAsync(db, "k-all", "search,ingest,configure,propose");
            await SeedKeyAsync(db, "k-revoked", "search,ingest", revoked: true);
            await SeedKeyAsync(db, "k-read", "search");
            await SeedKeyAsync(db, "k-config", "search,configure");
            await SeedKeyAsync(db, "k-has", "search,ingest,destroy");
            await SeedKeyAsync(db, "k-word", "search,reingest");

            await db.GetService<IMigrator>().MigrateAsync(TheMigration);

            var after = await ScopesOfEveryKeyAsync(db);
            after.ShouldBe(new Dictionary<string, string>
            {
                ["k-ingest"] = "search,ingest,destroy",
                ["k-first"] = "ingest,destroy",
                ["k-all"] = "search,ingest,configure,propose,destroy",
                ["k-revoked"] = "search,ingest,destroy",
                ["k-read"] = "search",
                ["k-config"] = "search,configure",
                ["k-has"] = "search,ingest,destroy",
                ["k-word"] = "search,reingest",
            }, ignoreOrder: true);
        }
        finally
        {
            await db.DisposeAsync();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RunningTheGrantAgainChangesNothing()
    {
        var (db, path) = await CatalogAtTheSchemaBeforeItAsync();
        try
        {
            await SeedKeyAsync(db, "k-ingest", "search,ingest");
            await db.GetService<IMigrator>().MigrateAsync(TheMigration);
            var once = await ScopesOfEveryKeyAsync(db);

            await db.Database.ExecuteSqlRawAsync(GrantDestroyToIngestKeys.GrantSql);

            (await ScopesOfEveryKeyAsync(db)).ShouldBe(once);
            once["k-ingest"].ShouldBe("search,ingest,destroy", "granted once, and not twice");
        }
        finally
        {
            await db.DisposeAsync();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task TheMigrationIsPartOfTheChainAndLeavesTheModelAsTheSnapshotHasIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dexicon-destroy-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);

            await db.Database.MigrateAsync();

            (await db.Database.GetAppliedMigrationsAsync()).ShouldContain(TheMigration);
            (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
            db.Database.HasPendingModelChanges().ShouldBeFalse();
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
