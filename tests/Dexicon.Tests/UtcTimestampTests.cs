using System.Text.Json;
using Dexicon.Core.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Dexicon's time policy, as a test rather than a convention people remember:
///
///   stored UTC, returned UTC, logged UTC. The browser is the ONLY component that
///   converts, because it is the only one that knows whose clock to use.
///
/// The defect this guards: SQLite has no date type, so EF read every DateTime back with
/// <c>Kind = Unspecified</c>. System.Text.Json then serialised it without a `Z`, and
/// <c>new Date("2026-09-16T17:08:11")</c> in a browser parses that as LOCAL time — so
/// every timestamp in the UI was wrong by the viewer's UTC offset, silently, and job
/// times did not line up with the log.
/// </summary>
public sealed class UtcTimestampTests : IAsyncLifetime
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

    [Fact]
    public async Task TimestampsComeBackFromTheDatabaseTaggedAsUtc()
    {
        _db.Tenants.Add(new Tenant { Id = "t", DisplayName = "t", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();   // force a real materialisation, not the tracked instance

        var tenant = await _db.Tenants.SingleAsync();

        tenant.CreatedUtc.Kind.ShouldBe(DateTimeKind.Utc,
            "an Unspecified Kind serialises without a 'Z' and browsers then read it as local time");
    }

    [Fact]
    public async Task NullableTimestampsAreAlsoTaggedUtc()
    {
        _db.Tenants.Add(new Tenant { Id = "t", DisplayName = "t", CreatedUtc = DateTime.UtcNow });
        _db.Corpora.Add(new Corpus
        {
            Id = "c", TenantId = "t", Name = "c",
            EmbeddingModel = "m", CollectionName = "x", BoundaryMode = "none",
            CreatedUtc = DateTime.UtcNow,
            LastIndexedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var corpus = await _db.Corpora.SingleAsync();
        corpus.LastIndexedUtc!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public async Task SerialisedTimestampsCarryAZuluMarker()
    {
        // The property that actually matters to a browser. Asserting on Kind alone would
        // pass while the wire format stayed ambiguous.
        _db.Tenants.Add(new Tenant { Id = "t", DisplayName = "t", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var tenant = await _db.Tenants.SingleAsync();
        var json = JsonSerializer.Serialize(new { tenant.CreatedUtc });

        // An ISO timestamp without a zone is read as LOCAL time by new Date().
        json.ShouldContain("Z\"");
    }

    [Fact]
    public async Task ALocalTimeWrittenByMistakeIsConvertedNotRelabelled()
    {
        // Defence against a future `DateTime.Now`: the converter must CONVERT it, not
        // stamp UTC onto a local value and shift the instant.
        var localNow = DateTime.Now;
        _db.Tenants.Add(new Tenant { Id = "t", DisplayName = "t", CreatedUtc = localNow });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var tenant = await _db.Tenants.SingleAsync();

        tenant.CreatedUtc.Kind.ShouldBe(DateTimeKind.Utc);
        (tenant.CreatedUtc - localNow.ToUniversalTime()).Duration()
            .ShouldBeLessThan(TimeSpan.FromSeconds(1), "the instant must be preserved, not shifted by the offset");
    }

    [Fact]
    public void EveryTimestampPropertyIsNamedUtc()
    {
        // A naming convention that is actually enforced. `CreatedUtc` tells a reader
        // what they are holding; `Created` invites a `DateTime.Now` next to it.
        var offenders = typeof(Tenant).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(Tenant).Namespace && t.IsClass)
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, Prop: p)))
            .Where(x => x.Prop.PropertyType == typeof(DateTime) || x.Prop.PropertyType == typeof(DateTime?))
            .Where(x => !x.Prop.Name.EndsWith("Utc", StringComparison.Ordinal))
            .Select(x => $"{x.Type.Name}.{x.Prop.Name}")
            .ToList();

        offenders.ShouldBeEmpty($"timestamp properties must be named …Utc: {string.Join(", ", offenders)}");
    }
}
