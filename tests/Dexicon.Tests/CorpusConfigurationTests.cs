using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Creating and changing corpora and sources, judged in one place whichever caller asks.
///
/// The rules moved here from the API's handlers so that the UI and an agent cannot be held
/// to different ones. These tests are about the rules as the service applies them; the
/// helpers they call have their own tests.
/// </summary>
public sealed class CorpusConfigurationTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "docs");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static async Task<Corpus> CorpusAsync(CatalogDbContext db) =>
        await db.Corpora.SingleAsync(c => c.Id == IndexingHarness.CorpusId);

    [Fact]
    public async Task A_source_added_with_nothing_set_follows_the_corpus_and_is_read_at_once()
    {
        await using var db = _harness.NewContext();
        Directory.CreateDirectory(Path.Combine(_harness.DataPath, "workspace", "extra"));

        var added = await _harness.NewConfiguration(db).AddSourceAsync(await CorpusAsync(db),
            new AddSourceRequest("extra"), default);

        added.Refusal.ShouldBeNull();
        var source = await db.Sources.SingleAsync(s => s.RootPath == "extra");
        (source.UseGitignore, source.MaxFileBytes, source.IncludeGlobs, source.ExcludeGlobs)
            .ShouldBe((null, null, null, null));
        (await db.Jobs.CountAsync(j => j.CorpusId == IndexingHarness.CorpusId && j.Kind == JobKind.Refresh))
            .ShouldBe(1, "adding a folder is asking for it to be read");
    }

    /// <summary>
    /// A cap of zero indexes nothing. Editing a source refused it; adding one and setting a
    /// corpus default accepted it, and both are about to be reachable by agents.
    /// </summary>
    [Fact]
    public async Task A_size_cap_of_zero_is_refused_wherever_one_is_set()
    {
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);
        var corpus = await CorpusAsync(db);

        var add = await config.AddSourceAsync(corpus, new AddSourceRequest("notes/sub", MaxFileBytes: 0), default);
        add.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Sources.AnyAsync(s => s.RootPath == "notes/sub")).ShouldBeFalse("nothing is stored for a refusal");

        var defaults = await config.UpdateCorpusAsync(corpus,
            new UpdateCorpusRequest(Defaults: new CorpusDefaults(null, 0, null, null)), default);
        defaults.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Corpora.AsNoTracking().SingleAsync(c => c.Id == corpus.Id)).DefaultMaxFileBytes.ShouldBeNull();

        var update = await config.UpdateSourceAsync(corpus, IndexingHarness.SourceIdFor(0),
            new UpdateSourceRequest(MaxFileBytes: 0), default);
        update.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
    }

    [Fact]
    public async Task A_source_saved_unchanged_queues_nothing()
    {
        await using var db = _harness.NewContext();

        var updated = await _harness.NewConfiguration(db).UpdateSourceAsync(await CorpusAsync(db),
            IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(), default);

        updated.Refusal.ShouldBeNull();
        updated.Value!.IndexJob.ShouldBeNull();
        (await db.Jobs.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_name_already_taken_is_refused_as_a_conflict()
    {
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("notes"), default);

        created.Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        (await db.Corpora.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_removed_source_leaves_the_index_in_every_set_and_the_other_source_stays()
    {
        await _harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.WriteFileAsync("b.md", IndexingHarness.Prose("beta"), source: 1);
        await _harness.RunIndexAsync(JobKind.Full);
        _harness.Vectors.CountFor("a.md").ShouldBeGreaterThan(0, "the premise: both sources were indexed");

        await using var db = _harness.NewContext();
        var removed = await _harness.NewConfiguration(db).RemoveSourceAsync(await CorpusAsync(db),
            IndexingHarness.SourceIdFor(0), default);

        removed.Refusal.ShouldBeNull();
        _harness.Vectors.CountFor("a.md").ShouldBe(0);
        _harness.Vectors.CountFor("b.md").ShouldBeGreaterThan(0);
        (await db.Files.AnyAsync(f => f.SourceId == IndexingHarness.SourceIdFor(0))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_source_that_is_not_there_is_named_back()
    {
        await using var db = _harness.NewContext();

        var removed = await _harness.NewConfiguration(db).RemoveSourceAsync(await CorpusAsync(db), "nope", default);

        var refusal = removed.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(404);
        refusal.Detail.ShouldContain("'nope'");
    }
}
