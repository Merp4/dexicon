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

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "docs");
        // Two sets, so a removal that cleaned only the default one would be seen.
        await _harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static async Task<Corpus> CorpusAsync(CatalogDbContext db) =>
        await db.Corpora.SingleAsync(c => c.Id == IndexingHarness.CorpusId);

    [Fact]
    public async Task ASourceAddedWithNothingSetFollowsTheCorpusAndIsReadAtOnce()
    {
        await using var db = _harness.NewContext();
        Directory.CreateDirectory(Path.Combine(_harness.DataPath, "workspace", "extra"));

        var added = await _harness.NewConfiguration(db).AddSourceAsync(await CorpusAsync(db), new AddSourceRequest("extra"), TestContext.Current.CancellationToken);

        added.Refusal.ShouldBeNull();
        var source = await db.Sources.SingleAsync(s => s.RootPath == "extra", cancellationToken: TestContext.Current.CancellationToken);
        (source.UseGitignore, source.MaxFileBytes, source.IncludeGlobs, source.ExcludeGlobs)
            .ShouldBe((null, null, null, null));
        (await db.Jobs.CountAsync(j => j.CorpusId == IndexingHarness.CorpusId && j.Kind == JobKind.Refresh, cancellationToken: TestContext.Current.CancellationToken))
            .ShouldBe(1, "adding a folder is asking for it to be read");
    }

    /// <summary>
    /// A cap of zero indexes nothing. Editing a source refused it; adding one and setting a
    /// corpus default accepted it, and both are about to be reachable by agents.
    /// </summary>
    [Fact]
    public async Task ASizeCapOfZeroIsRefusedWhereverOneIsSet()
    {
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);
        var corpus = await CorpusAsync(db);

        var add = await config.AddSourceAsync(corpus, new AddSourceRequest("notes/sub", MaxFileBytes: 0), TestContext.Current.CancellationToken);
        add.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Sources.AnyAsync(s => s.RootPath == "notes/sub", cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse("nothing is stored for a refusal");

        var defaults = await config.UpdateCorpusAsync(corpus, new UpdateCorpusRequest(Defaults: new CorpusDefaults(null, 0, null, null)), TestContext.Current.CancellationToken);
        defaults.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Corpora.AsNoTracking().SingleAsync(c => c.Id == corpus.Id, cancellationToken: TestContext.Current.CancellationToken)).DefaultMaxFileBytes.ShouldBeNull();

        var update = await config.UpdateSourceAsync(corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(MaxFileBytes: 0), TestContext.Current.CancellationToken);
        update.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
    }

    [Fact]
    public async Task ASourceSavedUnchangedQueuesNothing()
    {
        await using var db = _harness.NewContext();

        var updated = await _harness.NewConfiguration(db).UpdateSourceAsync(await CorpusAsync(db), IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(), TestContext.Current.CancellationToken);

        updated.Refusal.ShouldBeNull();
        updated.Value!.IndexJob.ShouldBeNull();
        (await db.Jobs.AnyAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse();
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
    public async Task ANameAlreadyTakenIsRefusedAsAConflict(string name)
    {
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest(name), TestContext.Current.CancellationToken);

        created.Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        (await db.Corpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    /// <summary>
    /// Case is ignored beyond ASCII, as the resolver ignores it. The name column has no
    /// collation, so the database alone accepts "ångström" beside "Ångström", and a SQL
    /// comparison under NOCASE would too, since it folds ASCII only.
    /// </summary>
    [Fact]
    public async Task ANameDifferingOnlyInTheCaseOfANonAsciiLetterIsTaken()
    {
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);

        (await config.CreateCorpusAsync(new CreateCorpusRequest("Ångström"), TestContext.Current.CancellationToken)).Refusal.ShouldBeNull();
        var second = await config.CreateCorpusAsync(new CreateCorpusRequest("ångström"), TestContext.Current.CancellationToken);

        var refusal = second.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Detail.ShouldContain("already exists");
        refusal.Detail.ShouldNotContain("Ångström", Case.Sensitive, "the caller may not reach the corpus that holds it");
        (await db.Corpora.CountAsync(c => c.Name != "notes", cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task ANameWithAColonIsRefusedBecauseItCouldNotBeAddressed()
    {
        // "notes:x" resolves as the corpus "notes" and its chunk set "x", so a corpus with
        // that name would be listed and never reached.
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("team:notes"), TestContext.Current.CancellationToken);

        created.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Corpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("tab\tbed")]
    [InlineData("esc\u001B[2J")]
    [InlineData("line\u2028separator")]
    [InlineData("paragraph\u2029separator")]
    public async Task ANameWithAControlCharacterIsRefused(string name)
    {
        // Every listing puts a name on a line of its own, and every change is logged with it.
        await using var db = _harness.NewContext();

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest(name), TestContext.Current.CancellationToken);

        created.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Corpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task ANameLongerThanTheModelAllowsIsRefused()
    {
        // The model says 200 and SQLite stores the column as unbounded text, so a long name
        // went in and was then repeated in every listing and log line.
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);

        var refused = await config.CreateCorpusAsync(new CreateCorpusRequest(new string('n', 201)), TestContext.Current.CancellationToken);

        refused.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await db.Corpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
        (await config.CreateCorpusAsync(new CreateCorpusRequest(new string('n', CorpusConfiguration.NameMax)), TestContext.Current.CancellationToken))
            .Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task ANameThatIsTheIdOfAnotherCorpusIsTaken()
    {
        // The resolver matches a name or an id, so this name would reach either corpus.
        await using var db = _harness.NewContext();
        var id = (await db.Corpora.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;

        var created = await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest(id), TestContext.Current.CancellationToken);

        var refusal = created.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Detail.ShouldContain("is the id of a corpus");
        refusal.Detail.ShouldNotContain("'notes'", Case.Sensitive, "the caller may not reach that corpus");
        (await db.Corpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task DefaultsChangedOnACorpusWithNoSourcesQueueNothing()
    {
        // An agent creating a corpus sets its filters before adding a folder; a refresh then
        // would be a job that reads nothing.
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);
        var defaults = new UpdateCorpusRequest(Defaults: new CorpusDefaults(false, null, null, ["**/bin/**"]));
        var empty = (await config.CreateCorpusAsync(new CreateCorpusRequest("empty"), TestContext.Current.CancellationToken)).Value!;

        (await config.UpdateCorpusAsync(empty, defaults, TestContext.Current.CancellationToken)).Value.ShouldBeTrue("the defaults moved");
        (await db.Jobs.AnyAsync(j => j.CorpusId == empty.Id, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse("there is nothing to re-read");

        var withSources = await CorpusAsync(db);
        (await config.UpdateCorpusAsync(withSources, defaults, TestContext.Current.CancellationToken)).Value.ShouldBeTrue();
        (await db.Jobs.AnyAsync(j => j.CorpusId == withSources.Id && j.Kind == JobKind.Refresh, cancellationToken: TestContext.Current.CancellationToken))
            .ShouldBeTrue("a corpus with sources is still refreshed");
    }

    /// <summary>
    /// A refused change leaves the corpus as it was. The corpus is tracked, so a field set
    /// before the refusal would be saved by the next change on the same context.
    /// </summary>
    [Fact]
    public async Task ARefusedCorpusChangeLeavesNothingBehindForTheNextSave()
    {
        await using var db = _harness.NewContext();
        var config = _harness.NewConfiguration(db);
        var corpus = await CorpusAsync(db);

        var refused = await config.UpdateCorpusAsync(corpus, new UpdateCorpusRequest(Description: "rejected", Defaults: new CorpusDefaults(null, 0, null, null)), TestContext.Current.CancellationToken);
        refused.Refusal.ShouldNotBeNull();
        corpus.Description.ShouldBeNull();

        // The next change through the same context, which is what would have saved it.
        (await config.UpdateSourceAsync(corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(UseGitignore: false), TestContext.Current.CancellationToken)).Refusal.ShouldBeNull();

        await using var fresh = _harness.NewContext();
        (await CorpusAsync(fresh)).Description.ShouldBeNull();
    }

    /// <summary>
    /// Removal is scoped to the source and reaches every set. The two sources hold the same
    /// file name, which is the case a delete by path alone would get wrong, and the corpus has
    /// two sets, which is the case a delete in the default set alone would get wrong.
    /// </summary>
    [Fact]
    public async Task ARemovedSourceLeavesTheIndexInEverySetAndTheOtherSourceStays()
    {
        await _harness.WriteFileAsync("same.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.WriteFileAsync("same.md", IndexingHarness.Prose("beta"), source: 1);
        await _harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        string removedSource = IndexingHarness.SourceIdFor(0), keptSource = IndexingHarness.SourceIdFor(1);
        foreach (var set in new[] { "set-1", "set-2" })
        {
            _harness.Vectors.CountFor("same.md", set, removedSource).ShouldBeGreaterThan(0, $"the premise: {set} holds the removed source");
            _harness.Vectors.CountFor("same.md", set, keptSource).ShouldBeGreaterThan(0, $"the premise: {set} holds the kept source");
        }

        await using var db = _harness.NewContext();
        var removed = await _harness.NewConfiguration(db).RemoveSourceAsync(await CorpusAsync(db), removedSource, TestContext.Current.CancellationToken);

        removed.Refusal.ShouldBeNull();
        foreach (var set in new[] { "set-1", "set-2" })
        {
            _harness.Vectors.CountFor("same.md", set, removedSource).ShouldBe(0, $"gone from {set}");
            _harness.Vectors.CountFor("same.md", set, keptSource).ShouldBeGreaterThan(0, $"the other source's file stays in {set}");
        }
        (await db.Files.AnyAsync(f => f.SourceId == removedSource, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await db.Files.AnyAsync(f => f.SourceId == keptSource, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task ASourceThatIsNotThereIsNamedBack()
    {
        await using var db = _harness.NewContext();

        var removed = await _harness.NewConfiguration(db).RemoveSourceAsync(await CorpusAsync(db), "nope", TestContext.Current.CancellationToken);

        var refusal = removed.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(404);
        refusal.Detail.ShouldContain("'nope'");
    }

    [Fact]
    public async Task ARemovedChunkSetTakesItsVectorsAndLeavesTheOtherSetAlone()
    {
        await _harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        _harness.Vectors.CountFor("a.md", "set-2").ShouldBeGreaterThan(0, "the premise: the set to remove holds vectors");

        await using var db = _harness.NewContext();
        var removed = await _harness.NewConfiguration(db).RemoveChunkSetAsync(await CorpusAsync(db), "alt-1", TestContext.Current.CancellationToken);

        removed.Refusal.ShouldBeNull();
        _harness.Vectors.CountFor("a.md", "set-2").ShouldBe(0);
        _harness.Vectors.CountFor("a.md", "set-1").ShouldBeGreaterThan(0, "the default set is untouched");
        (await db.ChunkSets.AnyAsync(s => s.Id == "set-2", cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await db.ChunkSets.AnyAsync(s => s.Id == "set-1", cancellationToken: TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task AChunkSetThatIsNotThereIsNamedBack()
    {
        await using var db = _harness.NewContext();

        var removed = await _harness.NewConfiguration(db).RemoveChunkSetAsync(await CorpusAsync(db), "set-3", TestContext.Current.CancellationToken);

        var refusal = removed.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(404);
        refusal.Detail.ShouldContain("'set-3'");
        (await db.ChunkSets.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(2, "a refusal removes nothing");
    }

    [Fact]
    public async Task TheDefaultChunkSetIsRefusedWhileAnotherExists()
    {
        await using var db = _harness.NewContext();

        var removed = await _harness.NewConfiguration(db).RemoveChunkSetAsync(await CorpusAsync(db), "default", TestContext.Current.CancellationToken);

        var refusal = removed.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Title.ShouldContain("default chunk set");
        (await db.ChunkSets.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(2, "a refusal removes nothing");
    }

    [Fact]
    public async Task TheOnlyChunkSetIsRefusedAndTheCorpusIsNamedAsTheWayOut()
    {
        await using var single = await IndexingHarness.StartAsync("notes");
        await single.SeedCorpusAsync(SourceKind.Workspace, sets: 1);
        await using var db = single.NewContext();

        var removed = await single.NewConfiguration(db).RemoveChunkSetAsync(await CorpusAsync(db), "default", TestContext.Current.CancellationToken);

        var refusal = removed.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Title.ShouldContain("only chunk set");
        refusal.Detail.ShouldContain("Delete the corpus instead");
    }

    [Fact]
    public async Task AChunkSetAJobIsWorkingOnIsRefusedUntilTheJobHasFinished()
    {
        await using (var seed = _harness.NewContext())
        {
            seed.Jobs.Add(new IndexJob
            {
                Id = "job-1",
                CorpusId = IndexingHarness.CorpusId,
                ChunkSetId = "set-2",
                Kind = JobKind.Refresh,
                State = JobState.Queued,
                QueuedUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _harness.NewContext();
        var refused = await _harness.NewConfiguration(db).RemoveChunkSetAsync(await CorpusAsync(db), "alt-1", TestContext.Current.CancellationToken);

        refused.Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        (await db.ChunkSets.AnyAsync(s => s.Id == "set-2", cancellationToken: TestContext.Current.CancellationToken)).ShouldBeTrue("nothing was removed under the job");

        await db.Jobs.ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobState.Succeeded), cancellationToken: TestContext.Current.CancellationToken);
        var removed = await _harness.NewConfiguration(db).RemoveChunkSetAsync(await CorpusAsync(db), "alt-1", TestContext.Current.CancellationToken);

        removed.Refusal.ShouldBeNull();
    }

    /// <summary>
    /// Vectors go first. If the row went first and the delete then failed, the collection would keep
    /// points that nothing in the catalogue can name or clean up; this order leaves the row in place so
    /// the removal can be asked for again.
    /// </summary>
    [Fact]
    public async Task AChunkSetWhoseVectorsCannotBeDeletedKeepsItsRow()
    {
        await _harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        _harness.Vectors.DeletesThrow = true;

        await using var db = _harness.NewContext();
        var corpus = await CorpusAsync(db);
        await Should.ThrowAsync<InvalidOperationException>(
            _harness.NewConfiguration(db).RemoveChunkSetAsync(corpus, "alt-1", TestContext.Current.CancellationToken));

        await using var fresh = _harness.NewContext();
        (await fresh.ChunkSets.AnyAsync(s => s.Id == "set-2", cancellationToken: TestContext.Current.CancellationToken)).ShouldBeTrue("the row stays while its vectors may still exist");
        _harness.Vectors.CountFor("a.md", "set-2").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ACorpusWhoseVectorsCannotBeDeletedKeepsItsRows()
    {
        await _harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        _harness.Vectors.DeletesThrow = true;

        await using var db = _harness.NewContext();
        var corpus = await CorpusAsync(db);
        await Should.ThrowAsync<InvalidOperationException>(
            _harness.NewConfiguration(db).RemoveCorpusAsync(corpus, TestContext.Current.CancellationToken));

        await using var fresh = _harness.NewContext();
        (await fresh.Corpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);
        (await fresh.Files.AnyAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBeTrue();
        _harness.Vectors.CountFor("a.md", "set-1").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ARemovedCorpusTakesItsVectorsInEverySetAndEveryRowUnderIt()
    {
        await _harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        foreach (var set in new[] { "set-1", "set-2" })
            _harness.Vectors.CountFor("a.md", set).ShouldBeGreaterThan(0, $"the premise: {set} holds vectors");

        await using var db = _harness.NewContext();
        var removed = await _harness.NewConfiguration(db).RemoveCorpusAsync(await CorpusAsync(db), TestContext.Current.CancellationToken);

        removed.Refusal.ShouldBeNull();
        foreach (var set in new[] { "set-1", "set-2" })
            _harness.Vectors.CountFor("a.md", set).ShouldBe(0, $"gone from {set}");
        (await db.Corpora.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Sources.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Files.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.FileChunkStates.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.ChunkSets.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Jobs.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(0);
    }
}
