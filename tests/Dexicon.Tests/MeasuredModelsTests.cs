using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A probe taken from the Models screen, read back by the indexer.
///
/// The screen probes <c>embeddinggemma:latest</c>; a chunk set records <c>embeddinggemma</c>. The
/// measurement was stored under the name it was requested with and the indexer looked it up by the
/// set's name, so a set never got the context limit its model had been measured to have.
/// </summary>
public sealed class MeasuredModelsTests : IAsyncLifetime
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

    private async Task StoreAsync(string model, int contextTokens, string provider = "ollama")
    {
        var row = await MeasuredModels.RowForAsync(_db, new EmbeddingTarget(provider, model));
        row.Dimensions = 768;
        row.ContextTokens = contextTokens;
        row.MeasuredUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task AMeasurementTakenUnderTheTaggedNameIsFoundForASetRecordedWithoutIt()
    {
        await StoreAsync("embeddinggemma:latest", 2048);

        var found = await MeasuredModels.ForAsync(_db, new EmbeddingTarget("ollama", "embeddinggemma"));

        found.ShouldNotBeNull().ContextTokens.ShouldBe(2048);
    }

    [Fact]
    public async Task AMeasurementTakenUnderTheBareNameIsFoundForASetRecordedWithTheTag()
    {
        await StoreAsync("embeddinggemma", 2048);

        var found = await MeasuredModels.ForAsync(_db, new EmbeddingTarget("ollama", "embeddinggemma:latest"));

        found.ShouldNotBeNull().ContextTokens.ShouldBe(2048);
    }

    [Fact]
    public async Task ProbingBothSpellingsWritesOneRowAndTheLaterProbeWins()
    {
        await StoreAsync("embeddinggemma", 2048);
        await StoreAsync("embeddinggemma:latest", 1024);

        (await _db.ModelMeasurements.Select(m => m.Model).ToListAsync()).ShouldBe(["embeddinggemma"]);
        (await MeasuredModels.ForAsync(_db, new EmbeddingTarget("ollama", "embeddinggemma")))
            .ShouldNotBeNull().ContextTokens.ShouldBe(1024);
    }

    [Fact]
    public async Task AnotherTagOrAnotherProviderIsNotTheSameMeasurement()
    {
        // Controls: the lookup must not widen into "any row that starts with the name".
        await StoreAsync("embeddinggemma:latest", 2048);

        (await MeasuredModels.ForAsync(_db, new EmbeddingTarget("ollama", "embeddinggemma:v2"))).ShouldBeNull();
        (await MeasuredModels.ForAsync(_db, new EmbeddingTarget("openai", "embeddinggemma"))).ShouldBeNull();
        (await MeasuredModels.ForAsync(_db, new EmbeddingTarget("ollama", "nomic-embed-text"))).ShouldBeNull();
    }
}
