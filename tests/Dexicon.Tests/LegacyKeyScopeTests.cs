using Dexicon.Core.Catalog;
using Dexicon.Core.Catalog.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Keys issued before D-28 still stored <c>admin</c> in their scopes column.
///
/// Never a hole: <c>TokenService.VerifyAsync</c> strips the scope when it builds a
/// principal, so such a key was already refused every admin endpoint. What it was is
/// misleading, because the Access page reads the raw column and so displayed a scope the
/// key could not use. Found by migrating a real catalogue and then asking the running
/// server what that key could actually reach.
///
/// These run the migration's own statement rather than a copy of it, so the test cannot
/// drift away from what ships.
/// </summary>
public sealed class LegacyKeyScopeTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task SeedAsync(string id, string scopes)
    {
        _db.Tokens.Add(new ApiToken
        {
            Id = id,
            Name = id,
            TokenHash = [1],
            TokenSalt = [2],
            Scopes = scopes,
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<string> ScopesOfAsync(string id)
    {
        _db.ChangeTracker.Clear();
        return (await _db.Tokens.AsNoTracking().SingleAsync(t => t.Id == id)).Scopes;
    }

    [Theory]
    // admin in every position it can occupy, plus the spacing a hand-edited row might have.
    [InlineData("search,ingest,admin", "search,ingest")]
    [InlineData("admin,search,ingest", "search,ingest")]
    [InlineData("search,admin,ingest", "search,ingest")]
    [InlineData("search, admin, ingest", "search,ingest")]
    [InlineData("admin", "")]
    // Rows that never had it must come through untouched, including one whose scope merely
    // starts with the same letters.
    [InlineData("search", "search")]
    [InlineData("search,ingest", "search,ingest")]
    [InlineData("administrator", "administrator")]
    public async Task AdminIsStrippedWhereverItAppears(string stored, string expected)
    {
        await SeedAsync("k", stored);

        await _db.Database.ExecuteSqlRawAsync(StripAdminFromExistingKeys.StripAdminSql);

        (await ScopesOfAsync("k")).ShouldBe(expected);
    }

    [Fact]
    public async Task ItLeavesEveryOtherKeyAlone()
    {
        await SeedAsync("legacy", "search,ingest,admin");
        await SeedAsync("modern", "search,ingest");
        await SeedAsync("reader", "search");

        await _db.Database.ExecuteSqlRawAsync(StripAdminFromExistingKeys.StripAdminSql);

        (await ScopesOfAsync("legacy")).ShouldBe("search,ingest");
        (await ScopesOfAsync("modern")).ShouldBe("search,ingest");
        (await ScopesOfAsync("reader")).ShouldBe("search");
    }

    [Fact]
    public async Task ItIsSafeToRunTwice()
    {
        // Migrations run once, but a statement that is not idempotent is a statement that
        // cannot be replayed onto a catalogue restored from a backup mid-way.
        await SeedAsync("k", "search,admin,ingest");

        await _db.Database.ExecuteSqlRawAsync(StripAdminFromExistingKeys.StripAdminSql);
        var once = await ScopesOfAsync("k");
        await _db.Database.ExecuteSqlRawAsync(StripAdminFromExistingKeys.StripAdminSql);

        (await ScopesOfAsync("k")).ShouldBe(once);
    }
}
