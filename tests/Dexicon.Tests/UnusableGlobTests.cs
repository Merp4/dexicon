using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A glob list the walk cannot read is refused where it is sent. A null element, or a character class
/// such as <c>[z-a]</c>, was stored as sent and threw from <see cref="IgnoreRuleSet.AddPatterns"/> on
/// every pass of every source that read the list, so the corpus stopped indexing with the reason in a
/// job and the request answered 200.
/// </summary>
public sealed class UnusableGlobTests
{
    private static readonly string[] NullAmongPatterns = ["**/*.md", null!];

    [Theory]
    [InlineData("[z-a]")]
    [InlineData("docs/[]x")]
    public void TheWalkItselfRefusesAPatternItCannotCompile(string pattern)
    {
        // The reason for the check: this is what each pass of the walk did with the stored value.
        Should.Throw<ArgumentException>(() => new IgnoreRuleSet().AddPatterns([pattern], "source.include"));
    }

    [Fact]
    public void ThePositionOfTheFirstPatternNothingCanReadIsReported()
    {
        SourceFilters.FirstUnusable(["**/*.md", "[z-a]", "[y-b]"]).ShouldBe(1);
        SourceFilters.FirstUnusable(NullAmongPatterns).ShouldBe(1);
    }

    [Fact]
    public void PatternsThatReadAreAcceptedAsAreNoListAndAnEmptyList()
    {
        // The control: patterns the walk takes, including a class, a negation, an anchor and a comment.
        SourceFilters.FirstUnusable(["**/*.md", "[a-c]*.txt", "!drafts/", "/build", "# note", ""]).ShouldBeNull();
        SourceFilters.FirstUnusable([]).ShouldBeNull();
        SourceFilters.FirstUnusable(null).ShouldBeNull();
    }

    /// <summary>A harness with one workspace corpus, and a context on it.</summary>
    private sealed class Seeded : IAsyncDisposable
    {
        private Seeded(IndexingHarness harness, CatalogDbContext db, Corpus corpus)
        {
            Harness = harness;
            Db = db;
            Corpus = corpus;
        }

        public IndexingHarness Harness { get; }
        public CatalogDbContext Db { get; }
        public Corpus Corpus { get; }
        public CorpusConfiguration Config => Harness.NewConfiguration(Db);

        public static async Task<Seeded> StartAsync()
        {
            var harness = await IndexingHarness.StartAsync("notes");
            await harness.SeedCorpusAsync(SourceKind.Workspace);
            var db = harness.NewContext();
            return new Seeded(harness, db, await db.Corpora.SingleAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASourceAddedWithAnUnusableIncludeOrExcludeGlobIsRefusedAndNothingIsSaved()
    {
        await using var s = await Seeded.StartAsync();
        var before = await s.Db.Sources.CountAsync();

        var include = await s.Config.AddSourceAsync(
            s.Corpus, new AddSourceRequest("", IncludeGlobs: ["**/*.md", "[z-a]"]), default);
        var exclude = await s.Config.AddSourceAsync(
            s.Corpus, new AddSourceRequest("", ExcludeGlobs: NullAmongPatterns), default);

        include.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        include.Refusal.Detail.ShouldContain("includeGlobs[1]");
        exclude.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        exclude.Refusal.Detail.ShouldContain("excludeGlobs[1]");
        (await s.Db.Sources.CountAsync()).ShouldBe(before, "a refused source is not saved");
    }

    [Fact]
    public async Task ASourceAddedWithGlobsTheWalkReadsIsAccepted()
    {
        await using var s = await Seeded.StartAsync();

        var added = await s.Config.AddSourceAsync(
            s.Corpus, new AddSourceRequest("", IncludeGlobs: ["**/*.md", "[a-c]*.txt"], ExcludeGlobs: ["!drafts/"]), default);

        added.Refusal.ShouldBeNull();
        added.Value!.Source.IncludeGlobs.ShouldBe(["**/*.md", "[a-c]*.txt"]);
    }

    [Fact]
    public async Task ASourceUpdatedWithAnUnusableGlobIsRefusedAndKeepsItsOwn()
    {
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);
        (await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(IncludeGlobs: ["**/*.md"]), default))
            .Refusal.ShouldBeNull();

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(IncludeGlobs: ["[z-a]"]), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        s.Db.ChangeTracker.Clear();
        SourceFilters.Globs((await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).IncludeGlobs)
            .ShouldBe(["**/*.md"], "the refused list is not saved over the one the source has");
    }

    [Fact]
    public async Task CorpusDefaultsWithAnUnusableGlobAreRefusedAndTheOldOnesKept()
    {
        await using var s = await Seeded.StartAsync();
        (await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(
            Defaults: new CorpusDefaults(null, null, ["**/*.md"], null)), default)).Refusal.ShouldBeNull();

        var outcome = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(
            Defaults: new CorpusDefaults(null, null, ["**/*.md"], ["[z-a]"])), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain("excludeGlobs[0]");
        s.Db.ChangeTracker.Clear();
        var saved = await s.Db.Corpora.AsNoTracking().SingleAsync();
        SourceFilters.Globs(saved.DefaultIncludeGlobs).ShouldBe(["**/*.md"]);
        SourceFilters.Globs(saved.DefaultExcludeGlobs).ShouldBeNull();
    }

    [Fact]
    public async Task ACorpusCreatedWithAnUnusableDefaultGlobIsRefusedAndNotCreated()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var outcome = await harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers"), default,
            defaults: new CorpusDefaults(null, null, NullAmongPatterns, null));

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain("includeGlobs[1]");
        (await db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
    }
}