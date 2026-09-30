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
        // Two sets, so a removal that cleaned only the default one would be seen.
        await _harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
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

    /// <summary>
    /// A name is judged as it is stored and resolved: trimmed, and without regard to case.
    /// Judged as sent, " notes " passed and then failed the unique index, a 500 where a 409
    /// was promised.
    /// </summary>
    [Theory]
    [InlineData("notes")]
    [InlineData(" notes ")]
    [InlineData("NOTES")]
    public async Task A_name_already_taken_is_refused_as_a_conflict(string name)
    {
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest(name), default);

        created.Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        (await db.Corpora.CountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// Case is ignored beyond ASCII, as the resolver ignores it. The name column has no
    /// collation, so the database alone accepts "ångström" beside "Ångström", and a SQL
    /// comparison under NOCASE would too, since it folds ASCII only.
    /// </summary>
    [Fact]
    public async Task A_name_differing_only_in_the_case_of_a_non_ascii_letter_is_taken()
    {
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);

        (await config.CreateCorpusAsync(new CreateCorpusRequest("Ångström"), default)).Refusal.ShouldBeNull();
        var second = await config.CreateCorpusAsync(new CreateCorpusRequest("ångström"), default);

        var refusal = second.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Detail.ShouldContain("'Ångström'", Case.Sensitive, "the refusal names the corpus that holds it");
        (await db.Corpora.CountAsync(c => c.Name != "notes")).ShouldBe(1);
    }

    [Fact]
    public async Task A_name_with_a_colon_is_refused_because_it_could_not_be_addressed()
    {
        // "notes:x" resolves as the corpus "notes" and its chunk set "x", so a corpus with
        // that name would be listed and never reached.
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("team:notes"), default);

        created.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Corpora.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_name_that_is_another_corpus_s_id_is_taken()
    {
        // The resolver matches a name or an id, so this name would reach either corpus.
        await using var db = _harness.NewContext();
        var id = (await db.Corpora.SingleAsync()).Id;

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest(id), default);

        var refusal = created.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Detail.ShouldContain("'notes'");
        (await db.Corpora.CountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// A refused change leaves the corpus as it was. The corpus is tracked, so a field set
    /// before the refusal would be saved by the next change on the same context.
    /// </summary>
    [Fact]
    public async Task A_refused_corpus_change_leaves_nothing_behind_for_the_next_save()
    {
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);
        var corpus = await CorpusAsync(db);

        var refused = await config.UpdateCorpusAsync(corpus,
            new UpdateCorpusRequest(Description: "rejected", Defaults: new CorpusDefaults(null, 0, null, null)), default);
        refused.Refusal.ShouldNotBeNull();
        corpus.Description.ShouldBeNull();

        // The next change through the same context, which is what would have saved it.
        (await config.UpdateSourceAsync(corpus, IndexingHarness.SourceIdFor(0),
            new UpdateSourceRequest(UseGitignore: false), default)).Refusal.ShouldBeNull();

        await using var fresh = _harness.NewContext();
        (await CorpusAsync(fresh)).Description.ShouldBeNull();
    }

    /// <summary>
    /// Removal is scoped to the source and reaches every set. The two sources hold the same
    /// file name, which is the case a delete by path alone would get wrong, and the corpus has
    /// two sets, which is the case a delete in the default set alone would get wrong.
    /// </summary>
    [Fact]
    public async Task A_removed_source_leaves_the_index_in_every_set_and_the_other_source_stays()
    {
        await _harness.WriteFileAsync("same.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.WriteFileAsync("same.md", IndexingHarness.Prose("beta"), source: 1);
        await _harness.RunIndexAsync(JobKind.Full);

        string removedSource = IndexingHarness.SourceIdFor(0), keptSource = IndexingHarness.SourceIdFor(1);
        foreach (var set in new[] { "set-1", "set-2" })
        {
            _harness.Vectors.CountFor("same.md", set, removedSource).ShouldBeGreaterThan(0, $"the premise: {set} holds the removed source");
            _harness.Vectors.CountFor("same.md", set, keptSource).ShouldBeGreaterThan(0, $"the premise: {set} holds the kept source");
        }

        await using var db = _harness.NewContext();
        var removed = await _harness.NewConfiguration(db).RemoveSourceAsync(await CorpusAsync(db), removedSource, default);

        removed.Refusal.ShouldBeNull();
        foreach (var set in new[] { "set-1", "set-2" })
        {
            _harness.Vectors.CountFor("same.md", set, removedSource).ShouldBe(0, $"gone from {set}");
            _harness.Vectors.CountFor("same.md", set, keptSource).ShouldBeGreaterThan(0, $"the other source's file stays in {set}");
        }
        (await db.Files.AnyAsync(f => f.SourceId == removedSource)).ShouldBeFalse();
        (await db.Files.AnyAsync(f => f.SourceId == keptSource)).ShouldBeTrue();
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
