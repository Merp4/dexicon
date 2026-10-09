using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
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

    public static TheoryData<SourceFilters.GlobReader> EveryReader => new()
    {
        SourceFilters.GlobReader.Walk,
        SourceFilters.GlobReader.Git,
        SourceFilters.GlobReader.WalkAndGit,
    };

    [Theory]
    [MemberData(nameof(EveryReader))]
    public void AListOfExactlyTheCapIsAcceptedAndOneMoreIsRefusedAtTheFirstElementPastIt(SourceFilters.GlobReader reader)
    {
        var atCap = Enumerable.Range(0, SourceFilters.MaxGlobsPerList).Select(i => $"d{i}/").ToList();
        var over = atCap.Append("one-more/").ToList();

        SourceFilters.Check(atCap, reader).ShouldBeNull();
        SourceFilters.Check(over, reader).ShouldBe(
            new SourceFilters.GlobProblem(SourceFilters.MaxGlobsPerList, SourceFilters.GlobProblemKind.TooMany));
        SourceFilters.FirstUnusable(over, reader).ShouldBe(SourceFilters.MaxGlobsPerList);
    }

    [Theory]
    [MemberData(nameof(EveryReader))]
    public void TheCountIsJudgedBeforeAnyElementIsCompiled(SourceFilters.GlobReader reader)
    {
        // Every element is one the walk's parser refuses. A list judged element by element would stop at index 0
        // with an unusable pattern; the count cap reports first, so none of the million was looked at.
        var million = Enumerable.Repeat("[z-a]", 1_000_000).ToList();

        var problem = SourceFilters.Check(million, reader).ShouldNotBeNull();

        problem.Kind.ShouldBe(SourceFilters.GlobProblemKind.TooMany);
        problem.Index.ShouldBe(SourceFilters.MaxGlobsPerList);
    }

    [Theory]
    [MemberData(nameof(EveryReader))]
    public void AnElementOfExactlyTheLengthCapIsAcceptedAndOneMoreCharacterIsRefused(SourceFilters.GlobReader reader)
    {
        var atCap = new string('a', SourceFilters.MaxGlobLength);

        SourceFilters.Check(["docs/", atCap], reader).ShouldBeNull();
        SourceFilters.Check(["docs/", atCap + "a"], reader).ShouldBe(
            new SourceFilters.GlobProblem(1, SourceFilters.GlobProblemKind.TooLong));
    }

    [Fact]
    public void AListAlreadyStoredPastTheCapsIsStillReadAsStored()
    {
        // The caps are judged where a list is saved. A list stored before them is read by the walk unchanged.
        var stored = Enumerable.Range(0, SourceFilters.MaxGlobsPerList + 100).Select(i => $"d{i}/").ToList();
        var corpus = new Corpus { Id = "c", Name = "c", DefaultIncludeGlobs = SourceFilters.Store(stored) };
        var source = new Source { Id = "s", CorpusId = "c", Kind = SourceKind.Workspace, RootPath = "" };

        SourceFilters.Resolve(corpus, source, new IndexingOptions()).IncludeGlobs
            .Count.ShouldBe(SourceFilters.MaxGlobsPerList + 100);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a/..")]
    [InlineData("/../x")]
    [InlineData(@"a\..\b")]
    [InlineData(@"..\x")]
    public void AParentSegmentIsRefusedWhereGitReadsTheListAndLeftToTheWalk(string glob)
    {
        // git log -- ../x: "fatal: ../x: '../x' is outside repository". The walk compiles the element to a regular
        // expression over paths below the root, so it is a pattern that matches nothing and not a failure.
        foreach (var reader in new[] { SourceFilters.GlobReader.Git, SourceFilters.GlobReader.WalkAndGit })
            SourceFilters.Check(["docs/", glob], reader).ShouldBe(
                new SourceFilters.GlobProblem(1, SourceFilters.GlobProblemKind.ParentSegment), reader.ToString());

        SourceFilters.Check(["docs/", glob], SourceFilters.GlobReader.Walk).ShouldBeNull();
    }

    [Theory]
    [InlineData("..x")]
    [InlineData("x..")]
    [InlineData("a..b")]
    [InlineData("...")]
    [InlineData("a/..b/c")]
    [InlineData("a/.../c")]
    public void ADoubleDotInsideAPathSegmentIsNotAParentSegment(string glob)
    {
        foreach (var reader in new[] { SourceFilters.GlobReader.Git, SourceFilters.GlobReader.WalkAndGit })
            SourceFilters.Check([glob], reader).ShouldBeNull(reader.ToString());
    }

    [Fact]
    public void OneLeadingSlashIsAcceptedAndTwoAreRefusedWhereGitReadsTheList()
    {
        foreach (var reader in new[] { SourceFilters.GlobReader.Git, SourceFilters.GlobReader.WalkAndGit })
        {
            SourceFilters.Check(["/docs", "/", "/*.md"], reader).ShouldBeNull(reader.ToString());
            SourceFilters.Check(["docs/", "//docs"], reader).ShouldBe(
                new SourceFilters.GlobProblem(1, SourceFilters.GlobProblemKind.DoubleSlash), reader.ToString());
        }

        SourceFilters.Check(["//docs"], SourceFilters.GlobReader.Walk).ShouldBeNull("the walk reads it as an anchored pattern");
    }

    [Fact]
    public void TheRefusalNamesTheCapAndTheListAndDoesNotEchoTheElement()
    {
        var secret = "LEAKED" + new string('x', SourceFilters.MaxGlobLength);
        var tooMany = Enumerable.Repeat("LEAKED/", SourceFilters.MaxGlobsPerList + 1).ToList();

        var longOne = CorpusEndpoints.UnusableGlobs(["docs/", secret], null).ShouldNotBeNull();
        var many = CorpusEndpoints.UnusableGlobs(null, tooMany).ShouldNotBeNull();
        var parent = CorpusEndpoints.UnusableGlobs(["LEAKED/../x"], null, SourceFilters.GlobReader.Git).ShouldNotBeNull();

        longOne.Status.ShouldBe(400);
        longOne.Detail.ShouldContain("includeGlobs[1]");
        longOne.Detail.ShouldContain($"longer than {SourceFilters.MaxGlobLength} characters");
        many.Status.ShouldBe(400);
        many.Detail.ShouldContain("excludeGlobs");
        many.Detail.ShouldContain($"more than {SourceFilters.MaxGlobsPerList} patterns");
        parent.Detail.ShouldContain("includeGlobs[0]");
        parent.Detail.ShouldContain("'..'");
        foreach (var refusal in new[] { longOne, many, parent })
        {
            refusal.Detail.ShouldNotContain("LEAKED");
            refusal.Detail.ShouldEndWith("Nothing was saved.");
        }
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

    [Theory]
    [InlineData("[z-a]", "does not compile")]
    [InlineData("a\0b", "null character")]
    public async Task ACorpusCreatedWithADefaultIncludeBothReadersRefuseIsNotCreated(string pattern, string reason)
    {
        // The create site reads the default with the same two rules as the update site.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var outcome = await harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("includeGlobs", pattern));

        outcome.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        outcome.Refusal.Detail.ShouldContain(reason);
        (await db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
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
        blank.Refusal.Detail.ShouldContain("null character");
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

    private static string[] OverTheCap() =>
        [.. Enumerable.Range(0, SourceFilters.MaxGlobsPerList + 1).Select(i => $"d{i}/")];

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task AListPastTheCapIsRefusedAtEverySiteThatStoresOne(string field)
    {
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);
        var sources = await s.Db.Sources.CountAsync();
        var over = OverTheCap();

        var added = await s.Config.AddSourceAsync(s.Corpus, field == "includeGlobs"
            ? new AddSourceRequest("", IncludeGlobs: over)
            : new AddSourceRequest("", ExcludeGlobs: over), default);
        var updated = await s.Config.UpdateSourceAsync(s.Corpus, id, field == "includeGlobs"
            ? new UpdateSourceRequest(IncludeGlobs: over)
            : new UpdateSourceRequest(ExcludeGlobs: over), default);
        var corpusUpdated = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith(field, over)), default);
        var corpusCreated = await s.Config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith(field, over));

        foreach (var (site, outcome) in new[]
                 {
                     ("AddSource", added.Refusal), ("UpdateSource", updated.Refusal),
                     ("UpdateCorpus", corpusUpdated.Refusal), ("CreateCorpus", corpusCreated.Refusal),
                 })
        {
            outcome.ShouldNotBeNull(site).Status.ShouldBe(400, site);
            outcome.Title.ShouldBe("Unusable glob", site);
            outcome.Detail.ShouldContain(field, Case.Sensitive, site);
            outcome.Detail.ShouldContain($"more than {SourceFilters.MaxGlobsPerList} patterns", Case.Sensitive, site);
        }

        (await s.Db.Sources.CountAsync()).ShouldBe(sources, "a refused source is not saved");
        (await s.Db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).IncludeGlobs.ShouldBeNull();
        (await s.Db.Corpora.AsNoTracking().SingleAsync()).DefaultIncludeGlobs.ShouldBeNull();
    }

    [Fact]
    public async Task AListAtTheCapIsAcceptedBySourceAndCorpus()
    {
        await using var s = await Seeded.StartAsync();
        var atCap = OverTheCap()[..SourceFilters.MaxGlobsPerList];

        var updated = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(IncludeGlobs: atCap), default);
        var corpus = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("excludeGlobs", atCap)), default);

        updated.Refusal.ShouldBeNull();
        corpus.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task AHistorySourceTakesALeadingSlashIncludeAndRefusesAParentSegment()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        var config = harness.NewConfiguration(db);
        var id = IndexingHarness.SourceIdFor(0);

        var rooted = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["/docs", "/"]), default);
        var parent = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["docs/", "LEAKED/../x"]), default);
        var doubled = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["//docs"]), default);

        rooted.Refusal.ShouldBeNull();
        parent.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        parent.Refusal.Detail.ShouldContain("includeGlobs[1]");
        parent.Refusal.Detail.ShouldContain("'..'");
        parent.Refusal.Detail.ShouldNotContain("LEAKED");
        doubled.Refusal.ShouldNotBeNull().Detail.ShouldContain("'//'");
        db.ChangeTracker.Clear();
        SourceFilters.Globs((await db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).IncludeGlobs)
            .ShouldBe(["/docs", "/"], "the refused lists are not saved over the accepted one");
    }

    [Fact]
    public async Task AFileSourceKeepsAParentSegmentItsWalkMatchesNothingWith()
    {
        await using var s = await Seeded.StartAsync();

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(IncludeGlobs: ["../x"]), default);

        outcome.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task ACorpusDefaultIncludeListIsHeldToGitsRulesBecauseAHistorySourceInheritsIt()
    {
        await using var s = await Seeded.StartAsync();

        var parent = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("includeGlobs", "docs/", "../x")), default);
        var created = await s.Config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("includeGlobs", "a/../b"));
        var rooted = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("includeGlobs", "/build", "/docs")), default);

        parent.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[1]");
        parent.Refusal.Detail.ShouldContain("'..'");
        created.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        (await s.Db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
        rooted.Refusal.ShouldBeNull("a leading slash is valid in a default: the walk anchors it and the git boundary removes it");
    }

    [Fact]
    public async Task ANullEntryInClearIsRefusedAndNothingIsChanged()
    {
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);
        (await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(MaxFileBytes: 4096), default)).Refusal.ShouldBeNull();

        var alone = await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(Clear: [null!]), default);
        var beside = await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(Clear: ["maxFileBytes", null!]), default);

        foreach (var outcome in new[] { alone, beside })
        {
            outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
            outcome.Refusal.Title.ShouldBe("Unknown filter");
            outcome.Refusal.Detail.ShouldContain("null entry");
            outcome.Refusal.Detail.ShouldContain("clear takes field names");
            outcome.Refusal.Detail.ShouldContain("includeGlobs");
        }

        s.Db.ChangeTracker.Clear();
        (await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).MaxFileBytes.ShouldBe(4096, "a refused request clears nothing");
    }

    [Fact]
    public async Task AnUnknownClearNameIsShownOnOneLineAndCutShort()
    {
        await using var s = await Seeded.StartAsync();
        var name = "first\r\nsecond\u001b[2J" + new string('x', 500) + "TAILMARKER";

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(Clear: [name]), default);

        var detail = outcome.Refusal.ShouldNotBeNull().Detail;
        detail.ShouldContain("first");
        detail.ShouldNotContain("\n");
        detail.ShouldNotContain("\r");
        detail.ShouldNotContain("\u001b");
        detail.ShouldNotContain("TAILMARKER");
        detail.Length.ShouldBeLessThan(250);
    }
}
