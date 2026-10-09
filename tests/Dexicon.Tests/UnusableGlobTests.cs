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

    [Fact]
    public void AHistorySourcesIncludeListIsOnlyHeldToNotBeingNullOrEmpty()
    {
        // Git reads these as pathspecs, which the walk's parser never sees: a class it cannot compile is left to git,
        // an empty pathspec is one git rejects, and a pathspec of spaces is one git accepts.
        SourceFilters.FirstUnusable(["docs/", "[z-a]"], SourceFilters.GlobReader.Git).ShouldBeNull();
        SourceFilters.FirstUnusable(["docs/", "  "], SourceFilters.GlobReader.Git).ShouldBeNull();
        SourceFilters.FirstUnusable(["docs/", ""], SourceFilters.GlobReader.Git).ShouldBe(1);
        SourceFilters.FirstUnusable(["docs/", "a\0b"], SourceFilters.GlobReader.Git).ShouldBe(1);
        SourceFilters.FirstUnusable(NullAmongPatterns, SourceFilters.GlobReader.Git).ShouldBe(1);
    }

    [Fact]
    public void ACorpusDefaultIncludeListIsHeldToBothReaders()
    {
        // Sources of both kinds inherit it: a file source compiles it, a history source hands it to git.
        SourceFilters.FirstUnusable(["docs/", "**/*.md"], SourceFilters.GlobReader.WalkAndGit).ShouldBeNull();
        SourceFilters.FirstUnusable(["docs/", ""], SourceFilters.GlobReader.WalkAndGit).ShouldBe(1);
        SourceFilters.FirstUnusable(["docs/", "[z-a]"], SourceFilters.GlobReader.WalkAndGit).ShouldBe(1);
        SourceFilters.FirstUnusable(NullAmongPatterns, SourceFilters.GlobReader.WalkAndGit).ShouldBe(1);
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
        include.Refusal.Detail.ShouldContain("include[1] for the configure tools");
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
    public async Task AHistorySourceIsAddedWithTheGitRuleForItsIncludeList()
    {
        // The glob check comes before the check that the folder is a repository, so which refusal comes back says which rule ran.
        await using var s = await Seeded.StartAsync();

        var empty = await s.Config.AddSourceAsync(s.Corpus, new AddSourceRequest("", GitHistory: true, IncludeGlobs: [""]), default);
        var oddClass = await s.Config.AddSourceAsync(s.Corpus, new AddSourceRequest("", GitHistory: true, IncludeGlobs: ["[z-a]"]), default);

        empty.Refusal.ShouldNotBeNull().Title.ShouldBe("Unusable glob");
        (oddClass.Refusal?.Title).ShouldNotBe("Unusable glob", "git reads the list as pathspecs, so the walk's parser does not judge it");
    }

    [Theory]
    [InlineData("INCLUDEGLOBS")]
    [InlineData("includeGlobs")]
    public async Task AListIsClearedWhateverTheCaseOfItsName(string name)
    {
        await using var s = await Seeded.StartAsync();

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0),
            new UpdateSourceRequest(IncludeGlobs: ["[z-a]"], Clear: [name]), default);

        outcome.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task ClearingTheExcludeListLeavesTheIncludeListJudged()
    {
        // Each list is skipped for its own name only.
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);

        var excludeCleared = await s.Config.UpdateSourceAsync(s.Corpus, id,
            new UpdateSourceRequest(ExcludeGlobs: ["[z-a]"], Clear: ["excludeGlobs"]), default);
        var includeStillJudged = await s.Config.UpdateSourceAsync(s.Corpus, id,
            new UpdateSourceRequest(IncludeGlobs: ["[z-a]"], Clear: ["excludeGlobs"]), default);

        excludeCleared.Refusal.ShouldBeNull();
        includeStillJudged.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
    }

    [Fact]
    public async Task AnEmptyDefaultIncludePatternIsRefusedBecauseAHistorySourceWouldInheritIt()
    {
        await using var s = await Seeded.StartAsync();

        var update = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("includeGlobs", "")), default);
        var create = await s.Config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("includeGlobs", ""));

        update.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        create.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        (await s.Db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
    }

    private static CorpusDefaults DefaultsWith(string field, params string[] globs) => field == "includeGlobs"
        ? new CorpusDefaults(null, null, globs, null)
        : new CorpusDefaults(null, null, null, globs);

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task ASourceUpdatedWithAnUnusableGlobIsRefusedAndKeepsItsOwn(string field)
    {
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);
        (await s.Config.UpdateSourceAsync(s.Corpus, id, field == "includeGlobs"
            ? new UpdateSourceRequest(IncludeGlobs: ["**/*.md"])
            : new UpdateSourceRequest(ExcludeGlobs: ["**/*.md"]), default)).Refusal.ShouldBeNull();

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, id, field == "includeGlobs"
            ? new UpdateSourceRequest(IncludeGlobs: ["[z-a]"])
            : new UpdateSourceRequest(ExcludeGlobs: ["[z-a]"]), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain($"{field}[0]");
        s.Db.ChangeTracker.Clear();
        var saved = await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id);
        SourceFilters.Globs(field == "includeGlobs" ? saved.IncludeGlobs : saved.ExcludeGlobs)
            .ShouldBe(["**/*.md"], "the refused list is not saved over the one the source has");
    }

    [Fact]
    public async Task AListTheRequestClearsIsNotJudged()
    {
        // ApplyFilters stores nothing for a field named in clear, so a pattern sent beside it is not stored.
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, id,
            new UpdateSourceRequest(IncludeGlobs: ["[z-a]"], Clear: ["includeGlobs"]), default);

        outcome.Refusal.ShouldBeNull();
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).IncludeGlobs.ShouldBeNull();
    }

    [Fact]
    public async Task AHistorySourceTakesAnIncludePathspecTheWalksParserWouldRefuse()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        var config = harness.NewConfiguration(db);
        var id = IndexingHarness.SourceIdFor(0);

        (await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["[z-a]"]), default))
            .Refusal.ShouldBeNull();
        var blank = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["docs/", ""]), default);

        blank.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        blank.Refusal.Detail.ShouldContain("includeGlobs[1]");
        blank.Refusal.Detail.ShouldContain("pathspec");
    }

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task CorpusDefaultsWithAnUnusableGlobAreRefusedAndTheOldOnesKept(string field)
    {
        await using var s = await Seeded.StartAsync();
        (await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith(field, "**/*.md")), default))
            .Refusal.ShouldBeNull();

        var outcome = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith(field, "[z-a]")), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain($"{field}[0]");
        s.Db.ChangeTracker.Clear();
        var saved = await s.Db.Corpora.AsNoTracking().SingleAsync();
        SourceFilters.Globs(field == "includeGlobs" ? saved.DefaultIncludeGlobs : saved.DefaultExcludeGlobs).ShouldBe(["**/*.md"]);
    }

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task ACorpusCreatedWithAnUnusableDefaultGlobIsRefusedAndNotCreated(string field)
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var outcome = await harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers"), default, defaults: DefaultsWith(field, "**/*.md", null!));

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain($"{field}[1]");
        (await db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
    }
}
