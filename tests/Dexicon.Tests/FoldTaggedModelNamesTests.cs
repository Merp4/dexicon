using Dexicon.Core.Catalog;
using Dexicon.Core.Catalog.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dexicon.Tests;

/// <summary>
/// The saved framings and measurements stored under <c>model:latest</c> move to <c>model</c>.
///
/// Both tables are keyed on (provider, model), so a model stored under both spellings is two rows
/// and renaming one onto the other would break the key. The migration runs through the migrator the
/// app uses, from the schema before it, over rows written the way the old code wrote them.
/// </summary>
public sealed class FoldTaggedModelNamesTests
{
    private const string BeforeIt = "20261008100000_GrantDestroyToIngestKeys";
    private const string TheMigration = "20261008200000_FoldTaggedModelNames";

    private static async Task<(CatalogDbContext Db, string Path)> CatalogAtTheSchemaBeforeItAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dexicon-fold-{Guid.NewGuid():N}.db");
        var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        await db.GetService<IMigrator>().MigrateAsync(BeforeIt);
        return (db, path);
    }

    private static Task<int> SeedProfileAsync(CatalogDbContext db, string provider, string model, string marker, string updated) =>
        db.Database.ExecuteSqlRawAsync(
            "INSERT INTO model_profiles (Provider, Model, DocumentTemplate, QueryTemplate, CreatedUtc, UpdatedUtc) "
            + "VALUES ({0}, {1}, '{{text}}', {2}, '2026-10-01 00:00:00', {3})",
            provider, model, marker, updated);

    private static Task<int> SeedMeasurementAsync(CatalogDbContext db, string provider, string model, int marker, string measured) =>
        db.Database.ExecuteSqlRawAsync(
            "INSERT INTO model_measurements (Provider, Model, Dimensions, TruncatesSilently, RecommendedChunkChars, "
            + "RecommendedChunkTokens, ContextTokens, MeasuredUtc) VALUES ({0}, {1}, 768, 0, 1000, 256, {2}, {3})",
            provider, model, marker, measured);

    private static async Task<Dictionary<string, string>> ProfilesAsync(CatalogDbContext db) =>
        await db.ModelProfiles.AsNoTracking().ToDictionaryAsync(p => $"{p.Provider}/{p.Model}", p => p.QueryTemplate);

    private static async Task<Dictionary<string, int?>> MeasurementsAsync(CatalogDbContext db) =>
        await db.ModelMeasurements.AsNoTracking().ToDictionaryAsync(m => $"{m.Provider}/{m.Model}", m => m.ContextTokens);

    private static async Task SeedProfilesAsync(CatalogDbContext db)
    {
        await SeedProfileAsync(db, "ollama", "lone:latest", "lone-tagged", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "newer", "bare-older", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "newer:latest", "tagged-newer", "2026-10-03 00:00:00");
        await SeedProfileAsync(db, "ollama", "older", "bare-newer", "2026-10-03 00:00:00");
        await SeedProfileAsync(db, "ollama", "older:latest", "tagged-older", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "tie", "bare-tie", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "tie:latest", "tagged-tie", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "Upper:LATEST", "upper-tagged", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "twice:latest", "twice-lower", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "twice:LATEST", "twice-upper", "2026-10-03 00:00:00");
        await SeedProfileAsync(db, "ollama", "versioned:v1.5", "versioned", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "versioned", "versioned-bare", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "ollama", "untouched", "untouched", "2026-10-02 00:00:00");
        await SeedProfileAsync(db, "openai", "newer", "other-provider", "2026-10-09 00:00:00");
        await SeedProfileAsync(db, "openai", "lone:latest", "other-provider-tagged", "2026-10-02 00:00:00");
    }

    [Fact]
    public async Task TheMigrationFoldsTaggedFramingsIntoTheBareNameAndKeepsTheNewestOfAPair()
    {
        var (db, path) = await CatalogAtTheSchemaBeforeItAsync();
        try
        {
            await SeedProfilesAsync(db);

            await db.GetService<IMigrator>().MigrateAsync(TheMigration);

            (await ProfilesAsync(db)).ShouldBe(new Dictionary<string, string>
            {
                ["ollama/lone"] = "lone-tagged",
                ["ollama/newer"] = "tagged-newer",
                ["ollama/older"] = "bare-newer",
                ["ollama/tie"] = "bare-tie",
                ["ollama/Upper"] = "upper-tagged",
                ["ollama/twice"] = "twice-upper",
                ["ollama/versioned:v1.5"] = "versioned",
                ["ollama/versioned"] = "versioned-bare",
                ["ollama/untouched"] = "untouched",
                ["openai/newer"] = "other-provider",
                ["openai/lone"] = "other-provider-tagged",
            }, ignoreOrder: true);
        }
        finally
        {
            await db.DisposeAsync();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task TheMigrationFoldsTaggedMeasurementsAndKeepsTheNewestOfAPair()
    {
        var (db, path) = await CatalogAtTheSchemaBeforeItAsync();
        try
        {
            await SeedMeasurementAsync(db, "ollama", "lone:latest", 1, "2026-10-02 00:00:00");
            await SeedMeasurementAsync(db, "ollama", "newer", 2, "2026-10-02 00:00:00");
            await SeedMeasurementAsync(db, "ollama", "newer:latest", 3, "2026-10-03 00:00:00");
            await SeedMeasurementAsync(db, "ollama", "older", 4, "2026-10-03 00:00:00");
            await SeedMeasurementAsync(db, "ollama", "older:latest", 5, "2026-10-02 00:00:00");
            await SeedMeasurementAsync(db, "ollama", "versioned:v1.5", 6, "2026-10-02 00:00:00");
            await SeedMeasurementAsync(db, "openai", "lone:latest", 7, "2026-10-02 00:00:00");

            await db.GetService<IMigrator>().MigrateAsync(TheMigration);

            (await MeasurementsAsync(db)).ShouldBe(new Dictionary<string, int?>
            {
                ["ollama/lone"] = 1,
                ["ollama/newer"] = 3,
                ["ollama/older"] = 4,
                ["ollama/versioned:v1.5"] = 6,
                ["openai/lone"] = 7,
            }, ignoreOrder: true);
        }
        finally
        {
            await db.DisposeAsync();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RunningTheFoldAgainChangesNothing()
    {
        var (db, path) = await CatalogAtTheSchemaBeforeItAsync();
        try
        {
            await SeedProfilesAsync(db);
            await SeedMeasurementAsync(db, "ollama", "newer", 2, "2026-10-02 00:00:00");
            await SeedMeasurementAsync(db, "ollama", "newer:latest", 3, "2026-10-03 00:00:00");
            await db.GetService<IMigrator>().MigrateAsync(TheMigration);
            var profiles = await ProfilesAsync(db);
            var measurements = await MeasurementsAsync(db);

            await db.Database.ExecuteSqlRawAsync(FoldTaggedModelNames.FoldSql);

            (await ProfilesAsync(db)).ShouldBe(profiles, ignoreOrder: true);
            (await MeasurementsAsync(db)).ShouldBe(measurements, ignoreOrder: true);
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
        var path = Path.Combine(Path.GetTempPath(), $"dexicon-fold-{Guid.NewGuid():N}.db");
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
