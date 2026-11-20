using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// Reading a configuration file into an instance (D-54): a dry run changes nothing, an apply adds and updates through the rules
/// the screens are judged by, nothing is deleted, and each corpus or source stands on its own.
/// </summary>
public sealed class ConfigImportTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private IndexingHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await IndexingHarness.StartAsync("notes", "history", "extra");

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private static ExportedFilters Unset => new(null, null, null, null);

    private static ExportedChunkSet Set(string name = "default", bool isDefault = true, int size = 256, int overlap = 32, string mode = "language-aware") =>
        new(name, null, "ollama", IndexingHarness.Model, size, overlap, mode, null, false, false, false, isDefault);

    private static ExportedSource Source(string root, ExportedFilters? filters = null, string kind = "workspace", GitHistoryOptions? git = null) =>
        new(kind, root, filters ?? Unset, git);

    private static ExportedCorpus Corpus(string name, ExportedSource[]? sources = null, ExportedChunkSet[]? sets = null,
        string? description = null, ExportedFilters? defaults = null) =>
        new(name, description, defaults ?? Unset, sources ?? [], sets ?? [Set()]);

    private static ConfigExport File(params ExportedCorpus[] corpora) =>
        new(ConfigEndpoints.FormatName, ConfigEndpoints.FormatVersion, Now, "test", corpora, [], [], []);

    private async Task<ConfigImportResult> RunAsync(ConfigExport file, bool apply, IndexingHarness? on = null)
    {
        var harness = on ?? _harness;
        await using var db = harness.NewContext();
        var importer = new ConfigImporter(db, harness.NewConfiguration(db), NullLogger<ConfigImporter>.Instance);
        return await importer.RunAsync(file, apply, TestContext.Current.CancellationToken);
    }

    private static string Outcomes(ConfigImportResult r, string kind) =>
        string.Join(",", r.Items.Where(i => i.Kind == kind).Select(i => $"{i.Name}={i.Outcome}"));

    private async Task<Corpus> StoredAsync(string name, IndexingHarness? on = null)
    {
        await using var db = (on ?? _harness).NewContext();
        return await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets).AsNoTracking()
            .SingleAsync(c => c.Name == name, TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync()
    {
        await using var db = _harness.NewContext();
        return await db.Corpora.CountAsync(TestContext.Current.CancellationToken)
               + await db.Sources.CountAsync(TestContext.Current.CancellationToken)
               + await db.ChunkSets.CountAsync(TestContext.Current.CancellationToken)
               + await db.Jobs.CountAsync(TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------------------------------------- dry run

    [Fact]
    public async Task ADryRunSaysWhatWouldBeMadeAndChangesNothing()
    {
        var before = await CountAsync();

        var result = await RunAsync(File(Corpus("Papers", [Source("notes")], [Set(), Set("beta", isDefault: false)])), apply: false);

        result.Applied.ShouldBeFalse();
        Outcomes(result, "corpus").ShouldBe("Papers=create");
        Outcomes(result, "source").ShouldBe("Papers: workspace notes=create");
        Outcomes(result, "chunk set").ShouldBe("Papers: beta=not imported,Papers: default=create");
        result.Items.Single(i => i.Kind == "corpus").Detail.ShouldNotBeNull().ShouldContain("Would be made");
        (await CountAsync()).ShouldBe(before, "no corpus, source, chunk set or job was written");
    }

    [Fact]
    public async Task WithoutApplyTheRequestIsADryRun()
    {
        var before = await CountAsync();
        await using var db = _harness.NewContext();
        var importer = new ConfigImporter(db, _harness.NewConfiguration(db), NullLogger<ConfigImporter>.Instance);

        var result = await ConfigImportEndpoints.ImportAsync(File(Corpus("Papers")), apply: null, Admin(), importer, TestContext.Current.CancellationToken);

        ((Ok<ConfigImportResult>)result).Value.ShouldNotBeNull().Applied.ShouldBeFalse();
        (await CountAsync()).ShouldBe(before);
    }

    // ---------------------------------------------------------------------------------------------------- apply

    [Fact]
    public async Task AnApplyMakesTheCorpusWithItsDefaultSetItsFiltersAndItsSources()
    {
        var file = File(Corpus(
            "Papers",
            [
                Source("notes", new ExportedFilters(["**/*.md"], ["old/**"], UseGitignore: false, MaxFileBytes: 5000)),
                Source("history"),
            ],
            [Set(size: 512, overlap: 64, mode: "blank-line")],
            description: "Conference papers",
            defaults: new ExportedFilters(["**/*.txt"], [], UseGitignore: true, MaxFileBytes: 9000)));

        var result = await RunAsync(file, apply: true);

        result.Applied.ShouldBeTrue();
        Outcomes(result, "corpus").ShouldBe("Papers=create");
        Outcomes(result, "source").ShouldBe("Papers: workspace notes=create,Papers: workspace history=create", "in the order of the file");
        var stored = await StoredAsync("Papers");
        stored.Description.ShouldBe("Conference papers");
        SourceFilters.Globs(stored.DefaultIncludeGlobs).ShouldBe(["**/*.txt"]);
        SourceFilters.Globs(stored.DefaultExcludeGlobs).ShouldNotBeNull().ShouldBeEmpty();
        (stored.DefaultUseGitignore, stored.DefaultMaxFileBytes).ShouldBe((true, 9000));

        var set = stored.ChunkSets.ShouldHaveSingleItem();
        (set.Name, set.IsDefault, set.ChunkSize, set.ChunkOverlap, set.BoundaryMode, set.EmbeddingProvider, set.EmbeddingModel)
            .ShouldBe(("default", true, 512, 64, "blank-line", "ollama", IndexingHarness.Model));

        var notes = stored.Sources.Single(s => s.RootPath == "notes");
        SourceFilters.Globs(notes.IncludeGlobs).ShouldBe(["**/*.md"]);
        SourceFilters.Globs(notes.ExcludeGlobs).ShouldBe(["old/**"]);
        (notes.UseGitignore, notes.MaxFileBytes).ShouldBe((false, 5000));
        SourceFilters.Globs(stored.Sources.Single(s => s.RootPath == "history").IncludeGlobs).ShouldBeNull("a source that sets none inherits");
    }

    [Fact]
    public async Task ApplyingTheSameFileAgainChangesNothing()
    {
        var file = File(Corpus("Papers", [Source("notes", new ExportedFilters(["**/*.md"], [], null, 5000))], description: "d",
            defaults: new ExportedFilters(null, ["bin/**"], null, null)));
        await RunAsync(file, apply: true);
        var before = await CountAsync();

        var again = await RunAsync(file, apply: true);

        Outcomes(again, "corpus").ShouldBe("Papers=unchanged");
        Outcomes(again, "source").ShouldBe("Papers: workspace notes=unchanged");
        Outcomes(again, "chunk set").ShouldBe("Papers: default=unchanged");
        (await CountAsync()).ShouldBe(before, "nothing was made, and no refresh was queued for a change that was not one");
    }

    [Fact]
    public async Task ACorpusIsMatchedByNameIgnoringCase()
    {
        await RunAsync(File(Corpus("Papers")), apply: true);

        var again = await RunAsync(File(Corpus("PAPERS")), apply: true);

        Outcomes(again, "corpus").ShouldBe("Papers=unchanged", "named as the corpus here is named");
        await using var db = _harness.NewContext();
        (await db.Corpora.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task AnExistingCorpusAndItsSourcesAreUpdatedAndANewSourceIsAdded()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(["**/*.md"], null, true, 100))],
            description: "old", defaults: new ExportedFilters(null, null, null, 1000))), apply: true);

        var plan = await RunAsync(File(Corpus("Papers",
            [
                Source("notes", new ExportedFilters(null, ["tmp/**"], null, null)),
                Source("history"),
            ],
            description: "new", defaults: new ExportedFilters(["**/*.rst"], null, null, 2000))), apply: false);
        var result = await RunAsync(File(Corpus("Papers",
            [
                Source("notes", new ExportedFilters(null, ["tmp/**"], null, null)),
                Source("history"),
            ],
            description: "new", defaults: new ExportedFilters(["**/*.rst"], null, null, 2000))), apply: true);

        Outcomes(plan, "corpus").ShouldBe("Papers=update");
        Outcomes(result, "corpus").ShouldBe("Papers=update");
        Outcomes(result, "source").ShouldBe("Papers: workspace notes=update,Papers: workspace history=create");
        var stored = await StoredAsync("Papers");
        stored.Description.ShouldBe("new");
        SourceFilters.Globs(stored.DefaultIncludeGlobs).ShouldBe(["**/*.rst"]);
        stored.DefaultMaxFileBytes.ShouldBe(2000);

        // The file leaves these unset, so the source follows the corpus again: cleared, not left as it was.
        var notes = stored.Sources.Single(s => s.RootPath == "notes");
        notes.IncludeGlobs.ShouldBeNull();
        notes.UseGitignore.ShouldBeNull();
        notes.MaxFileBytes.ShouldBeNull();
        SourceFilters.Globs(notes.ExcludeGlobs).ShouldBe(["tmp/**"]);
        stored.Sources.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnEmptyListAndAnUnsetListAreDifferentAndEachIsKept()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(null, [], null, null))])), apply: true);
        (await StoredAsync("Papers")).Sources.Single().ExcludeGlobs.ShouldBe("[]", "an empty list is a decision to exclude nothing");

        var result = await RunAsync(File(Corpus("Papers", [Source("notes", Unset)])), apply: true);

        Outcomes(result, "source").ShouldBe("Papers: workspace notes=update");
        (await StoredAsync("Papers")).Sources.Single().ExcludeGlobs.ShouldBeNull("unset inherits again");

        var back = await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(null, [], null, null))])), apply: true);
        Outcomes(back, "source").ShouldBe("Papers: workspace notes=update");
        (await StoredAsync("Papers")).Sources.Single().ExcludeGlobs.ShouldBe("[]");
    }

    [Fact]
    public async Task ASourceThatDiffersOnlyInItsGitignoreSettingIsUpdated()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(null, null, UseGitignore: true, null))])), apply: true);

        var result = await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(null, null, UseGitignore: false, null))])), apply: true);

        Outcomes(result, "source").ShouldBe("Papers: workspace notes=update");
        (await StoredAsync("Papers")).Sources.Single().UseGitignore.ShouldBe(false);
    }

    [Fact]
    public async Task ASourceThatDiffersOnlyInItsSizeCapIsUpdated()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(null, null, null, MaxFileBytes: 100))])), apply: true);

        var result = await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(null, null, null, MaxFileBytes: 200))])), apply: true);

        Outcomes(result, "source").ShouldBe("Papers: workspace notes=update");
        (await StoredAsync("Papers")).Sources.Single().MaxFileBytes.ShouldBe(200);
    }

    [Fact]
    public async Task AChunkSetHereThatDiffersFromTheFilesIsReportedAndLeftAlone()
    {
        await RunAsync(File(Corpus("Papers", sets: [Set(size: 256)])), apply: true);

        var bigger = await RunAsync(File(Corpus("Papers", sets: [Set(size: 512)])), apply: true);
        var overlap = await RunAsync(File(Corpus("Papers", sets: [Set(size: 256, overlap: 16)])), apply: true);
        var mode = await RunAsync(File(Corpus("Papers", sets: [Set(size: 256, mode: "blank-line")])), apply: true);

        foreach (var result in new[] { bigger, overlap, mode })
        {
            var item = result.Items.Single(i => i.Kind == "chunk set");
            item.Outcome.ShouldBe("not imported");
            item.Detail.ShouldNotBeNull().ShouldContain("differs from the set here");
        }

        var stored = (await StoredAsync("Papers")).ChunkSets.ShouldHaveSingleItem();
        (stored.ChunkSize, stored.ChunkOverlap, stored.BoundaryMode).ShouldBe((256, 32, "language-aware"), "import changed nothing about the set");
    }

    [Fact]
    public async Task AFileWithAFilterBlockLeftOutIsReadAsOneThatSetsNone()
    {
        var bare = new ExportedCorpus("Bare", null, null!, [new ExportedSource("workspace", "notes", null!, null)], [Set()]);

        var result = await RunAsync(File(bare), apply: true);

        Outcomes(result, "corpus").ShouldBe("Bare=create");
        Outcomes(result, "source").ShouldBe("Bare: workspace notes=create");
    }

    [Fact]
    public async Task ADescriptionTheFileLeavesOutIsNotCleared()
    {
        await RunAsync(File(Corpus("Papers", description: "keep me")), apply: true);

        var result = await RunAsync(File(Corpus("Papers", description: null)), apply: true);

        Outcomes(result, "corpus").ShouldBe("Papers=unchanged");
        result.Items.Single(i => i.Kind == "corpus").Detail.ShouldNotBeNull().ShouldContain("the one here is kept");
        (await StoredAsync("Papers")).Description.ShouldBe("keep me");
    }

    // ---------------------------------------------------------------------------------------- what it never does

    [Fact]
    public async Task NothingHereThatTheFileDoesNotNameIsRemovedAndEachIsSaidToBeExtra()
    {
        await RunAsync(File(Corpus("Kept", [Source("notes"), Source("extra")]), Corpus("Stray", [Source("history")])), apply: true);

        var result = await RunAsync(File(Corpus("Kept", [Source("notes")])), apply: true);

        Outcomes(result, "corpus").ShouldBe("Kept=unchanged,Stray=extra");
        Outcomes(result, "source").ShouldBe("Kept: workspace notes=unchanged,Kept: workspace extra=extra", "what the file names, then what it does not");
        await using var db = _harness.NewContext();
        (await db.Corpora.Select(c => c.Name).OrderBy(n => n).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(["Kept", "Stray"]);
        (await db.Sources.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
    }

    [Fact]
    public async Task ChunkSetsBeyondTheDefaultKeysAndProfilesAreReportedNotImportedAndNothingIsMadeOfThem()
    {
        var file = File(Corpus("Papers", sets: [Set(), Set("beta", isDefault: false, size: 128)])) with
        {
            ModelProfiles = [new ExportedModelProfile("ollama", "m", "d: {text}", "q: {text}", null)],
            Keys = [new ExportedKey("agent", [Scopes.Search], AllCorpora: true, [], null)],
        };

        var result = await RunAsync(file, apply: true);

        Outcomes(result, "chunk set").ShouldBe("Papers: beta=not imported,Papers: default=create");
        Outcomes(result, "model profile").ShouldBe("ollama/m=not imported");
        Outcomes(result, "key").ShouldBe("agent=not imported");
        await using var db = _harness.NewContext();
        (await db.ChunkSets.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.ModelProfiles.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Tokens.CountAsync(t => t.Name == "agent", TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task ADefaultSetThatCarriesWhatACorpusIsNotMadeWithSaysWhichAreLeftOut()
    {
        var set = Set() with { UnitAware = true, HeadingContext = true, CustomBoundaryPattern = "^## " };

        var result = await RunAsync(File(Corpus("Papers", sets: [set])), apply: true);

        var item = result.Items.Single(i => i.Kind == "chunk set");
        item.Outcome.ShouldBe("not imported");
        item.Detail.ShouldNotBeNull().ShouldContain("without the boundary pattern, unitAware, headingContext");
        (await StoredAsync("Papers")).ChunkSets.ShouldHaveSingleItem().UnitAware.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------------------- one at a time

    [Fact]
    public async Task ACorpusTheRulesRefuseIsReportedAndTheOthersAreStillMade()
    {
        var file = File(
            Corpus("First", [Source("notes")]),
            Corpus("Bad:Name", [Source("history")]),
            Corpus("Third", [Source("extra")]));

        var result = await RunAsync(file, apply: true);

        Outcomes(result, "corpus").ShouldBe("First=create,Bad:Name=refused,Third=create");
        result.Items.Single(i => i.Outcome == "refused").Detail.ShouldNotBeNull().ShouldContain("Colon separates corpus from chunk set", Case.Sensitive,
            "the refusal carries its reason and not only its title");
        await using var db = _harness.NewContext();
        (await db.Corpora.Select(c => c.Name).OrderBy(n => n).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(["First", "Third"]);
        Outcomes(result, "source").ShouldNotContain("Bad:Name", Case.Sensitive, "the sources of a corpus that was not made are not tried");
    }

    [Fact]
    public async Task ASourceTheRulesRefuseIsReportedAndTheCorpusAndItsOtherSourcesStand()
    {
        var file = File(Corpus("Papers",
        [
            Source("notes", new ExportedFilters(["[z-a]"], null, null, null)),
            Source("history"),
            Source("extra", kind: "githistory", git: new GitHistoryOptions { Ref = "main" }),
        ]));

        var result = await RunAsync(file, apply: true);

        Outcomes(result, "source").ShouldBe("Papers: workspace notes=refused,Papers: workspace history=create,Papers: githistory extra=refused");
        result.Items.Single(i => i.Name == "Papers: workspace notes").Outcome.ShouldBe("refused");
        result.Items.Single(i => i.Name == "Papers: githistory extra").Outcome.ShouldBe("refused");
        result.Items.Single(i => i.Name == "Papers: workspace history").Outcome.ShouldBe("create");
        (await StoredAsync("Papers")).Sources.Select(s => s.RootPath).ShouldBe(["history"]);
    }

    [Fact]
    public async Task AnErrorThatIsNotARuleIsReportedAsFailedAndTheNextCorpusIsStillMade()
    {
        var calls = 0;
        _harness.Vectors.OnEnsureCollection = () =>
        {
            if (calls++ == 0) throw new IOException("the vector store is away");
        };

        var result = await RunAsync(File(Corpus("First", [Source("notes")]), Corpus("Second")), apply: true);

        Outcomes(result, "corpus").ShouldBe("First=failed,Second=create");
        result.Items.Single(i => i.Outcome == "failed").Detail.ShouldNotBeNull().ShouldContain("An error stopped this corpus");
        Outcomes(result, "source").ShouldBe("", "the sources of a corpus that failed are not tried");
        await using var db = _harness.NewContext();
        (await db.Corpora.AnyAsync(c => c.Name == "Second", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task AFailedSaveIsNotSavedAgainWithTheNextCorpus()
    {
        await using var harness = await IndexingHarness.StartAsync(new RefusesToSave("First"), "notes");

        var result = await RunAsync(File(Corpus("First"), Corpus("Second")), apply: true, on: harness);

        Outcomes(result, "corpus").ShouldBe("First=failed,Second=create", "the corpus that failed was dropped from the context and did not fail the next");
        await using var db = harness.NewContext();
        (await db.Corpora.Select(c => c.Name).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(["Second"]);
    }

    [Fact]
    public async Task AFailedUpdateDoesNotLeaveTheNextCorpusReadFromAStaleList()
    {
        await using var failing = await IndexingHarness.StartAsync(new RefusesToSave("First", EntityState.Modified), "notes");
        await RunAsync(File(Corpus("First", description: "old"), Corpus("Second", description: "old")), apply: true, on: failing);

        var result = await RunAsync(File(Corpus("First", description: "new"), Corpus("Second", description: "new")), apply: true, on: failing);

        Outcomes(result, "corpus").ShouldBe("First=failed,Second=update");
        (await StoredAsync("Second", failing)).Description.ShouldBe("new", "the update was made to a corpus the context tracks, so it was saved");
        (await StoredAsync("First", failing)).Description.ShouldBe("old");
    }

    /// <summary>Fails every save while a corpus of that name is waiting in that state, which is also true of a later save if the failed corpus is still tracked.</summary>
    private sealed class RefusesToSave(string name, EntityState state = EntityState.Added) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Corpus>().Any(e => e.State == state && e.Entity.Name == name))
                throw new InvalidOperationException("the catalogue refused the save");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task AFileThatSetsNoDefaultFiltersClearsTheOnesHereAndNamesWhichDiffer()
    {
        await RunAsync(File(Corpus("Papers", defaults: new ExportedFilters(null, ["a/**"], UseGitignore: true, MaxFileBytes: 100))), apply: true);

        var plan = await RunAsync(File(Corpus("Papers")), apply: false);

        var item = plan.Items.Single(i => i.Kind == "corpus");
        item.Outcome.ShouldBe("update");
        item.Detail.ShouldNotBeNull().ShouldContain("(excludeGlobs, useGitignore, maxFileBytes)");
        item.Detail.ShouldContain("removes the files it now excludes from the index");

        var applied = await RunAsync(File(Corpus("Papers")), apply: true);

        applied.Items.Single(i => i.Kind == "corpus").Outcome.ShouldBe("update");
        var stored = await StoredAsync("Papers");
        (stored.DefaultIncludeGlobs, stored.DefaultExcludeGlobs, stored.DefaultUseGitignore, stored.DefaultMaxFileBytes).ShouldBe((null, null, null, null));
        Outcomes(await RunAsync(File(Corpus("Papers")), apply: true), "corpus").ShouldBe("Papers=unchanged", "the clearing took, and an import does not report it again");
    }

    [Fact]
    public async Task ASourceFilterChangeSaysThatANarrowerFilterRemovesIndexedFiles()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);
        var narrower = File(Corpus("Papers", [Source("notes", new ExportedFilters(null, ["old/**"], null, null))]));

        var plan = await RunAsync(narrower, apply: false);
        var applied = await RunAsync(narrower, apply: true);

        plan.Items.Single(i => i.Kind == "source").Detail.ShouldNotBeNull().ShouldContain("removes the files it now excludes from the index");
        applied.Items.Single(i => i.Kind == "source").Detail.ShouldNotBeNull().ShouldContain("removes the files it now excludes from the index");
    }

    [Fact]
    public async Task ADryRunSaysTheDefaultChunkSetWouldBeMadeAndAnApplySaysItWas()
    {
        var file = File(Corpus("Papers"));

        (await RunAsync(file, apply: false)).Items.Single(i => i.Kind == "chunk set").Detail.ShouldBe("Would be made with the corpus.");
        (await RunAsync(file, apply: true)).Items.Single(i => i.Kind == "chunk set").Detail.ShouldBe("Made with the corpus.");
    }

    [Fact]
    public async Task AHistorySourceIsAddedWithItsOptionsAndAChangeToThemIsAnUpdate()
    {
        var repo = Path.Combine(_harness.Settings.Value.Indexing.WorkspaceRoot, "history");
        StashFixture.Init(repo);
        StashFixture.Commit(repo, "a.txt", "one", "first");
        ExportedSource History(GitHistoryOptions git) => Source("history", kind: "githistory", git: git);

        var added = await RunAsync(File(Corpus("Papers", [History(new GitHistoryOptions { Ref = "main" })])), apply: true);

        Outcomes(added, "source").ShouldBe("Papers: githistory history=create");
        var stored = (await StoredAsync("Papers")).Sources.Single();
        (stored.Kind, GitHistoryOptions.FromJson(stored.GitOptions).Ref).ShouldBe((SourceKind.GitHistory, "main"));

        var plan = await RunAsync(File(Corpus("Papers", [History(new GitHistoryOptions { Ref = "main", IncludeStat = false })])), apply: false);
        var changed = await RunAsync(File(Corpus("Papers", [History(new GitHistoryOptions { Ref = "main", IncludeStat = false })])), apply: true);
        var again = await RunAsync(File(Corpus("Papers", [History(new GitHistoryOptions { Ref = "main", IncludeStat = false })])), apply: true);
        var withoutOptions = await RunAsync(File(Corpus("Papers", [History(null!)])), apply: true);

        Outcomes(plan, "source").ShouldBe("Papers: githistory history=update");
        Outcomes(changed, "source").ShouldBe("Papers: githistory history=update");
        GitHistoryOptions.FromJson((await StoredAsync("Papers")).Sources.Single().GitOptions).IncludeStat.ShouldBeFalse();
        Outcomes(again, "source").ShouldBe("Papers: githistory history=unchanged");
        Outcomes(withoutOptions, "source").ShouldBe("Papers: githistory history=unchanged", "a file that leaves the options out keeps the ones here");
        GitHistoryOptions.FromJson((await StoredAsync("Papers")).Sources.Single().GitOptions).IncludeStat.ShouldBeFalse();
    }

    [Fact]
    public async Task ADryRunOfAnExistingCorpusSaysWhichSourcesWouldBeChangedAndAddedAndWritesNothing()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);
        var before = await CountAsync();
        var file = File(Corpus("Papers", [Source("notes", new ExportedFilters(null, ["old/**"], null, null)), Source("history")]));

        var plan = await RunAsync(file, apply: false);

        Outcomes(plan, "source").ShouldBe("Papers: workspace notes=update,Papers: workspace history=create");
        plan.Items.Single(i => i.Name.EndsWith("notes", StringComparison.Ordinal) && i.Kind == "source").Detail.ShouldNotBeNull()
            .ShouldStartWith("Would change its filters or options, and queue a refresh.");
        plan.Items.Single(i => i.Name.EndsWith("history", StringComparison.Ordinal) && i.Kind == "source").Detail.ShouldBe("Would be added, and a refresh queued.");
        (await CountAsync()).ShouldBe(before);
        (await StoredAsync("Papers")).Sources.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheDetailOfEachOutcomeSaysWhatHappenedOrWouldHappen()
    {
        var file = File(Corpus("Papers", [Source("notes")]));
        var born = $"with a chunk set named 'default' from the file's 'default' (ollama/{IndexingHarness.Model}, 256 tokens, overlap 32, language-aware)";

        var plan = await RunAsync(file, apply: false);
        var made = await RunAsync(file, apply: true);

        plan.Items.Single(i => i.Kind == "corpus").Detail.ShouldBe($"Would be made {born}. Applying probes the embedding model first.");
        made.Items.Single(i => i.Kind == "corpus").Detail.ShouldBe($"Made {born}.");
        plan.Items.Single(i => i.Kind == "source").Detail.ShouldBe("Would be added, and a refresh queued.");
        made.Items.Single(i => i.Kind == "source").Detail.ShouldBe("Added, and a refresh queued.");

        var narrower = File(Corpus("Papers", [Source("notes", new ExportedFilters(null, ["old/**"], null, null))], description: "Now described"));
        var changed = await RunAsync(narrower, apply: true);
        changed.Items.Single(i => i.Kind == "corpus").Detail.ShouldBe("Changed the description.");
        changed.Items.Single(i => i.Kind == "source").Detail.ShouldBe(
            "Changed its filters or options, and a refresh was queued. A narrower filter removes the files it now excludes from the index at the refresh.");
        Outcomes(await RunAsync(File(Corpus("Papers", [Source("notes")], sets: [Set("other", isDefault: false)])), apply: true), "chunk set")
            .ShouldBe("Papers: other=not imported");
    }

    [Fact]
    public async Task ASourceUpdateThatTheRulesRefuseIsReportedRefusedAndNotAsUpdated()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);

        var result = await RunAsync(File(Corpus("Papers", [Source("notes", new ExportedFilters(null, null, null, MaxFileBytes: 0))])), apply: true);

        var item = result.Items.Single(i => i.Kind == "source");
        item.Outcome.ShouldBe("refused");
        item.Detail.ShouldNotBeNull().ShouldContain("Invalid size cap");
        (await StoredAsync("Papers")).Sources.Single().MaxFileBytes.ShouldBeNull();
    }

    [Fact]
    public async Task TheAnswerCountsItsItemsAndListsWhatIsOnlyHereInNameOrder()
    {
        await RunAsync(File(Corpus("Alpha"), Corpus("Zed", [Source("history"), Source("extra")]), Corpus("Mid"), Corpus("Aaa")), apply: true);
        var file = File(Corpus("Alpha"), Corpus("Beta"), Corpus("Zed")) with
        {
            ModelProfiles = [new ExportedModelProfile("ollama", "m", "d", "q", null)],
            Keys = [new ExportedKey("k", [Scopes.Search], true, [], null)],
        };

        var result = await RunAsync(file, apply: false);

        result.Counts.ShouldBe(new Dictionary<string, int>
        {
            ["create"] = 2,
            ["extra"] = 4,
            ["not imported"] = 2,
            ["unchanged"] = 4,
        }, "Beta and its set; Aaa, Mid and Zed's two sources; the profile and the key; Alpha, Zed and their sets");
        result.Items.Where(i => i.Outcome == "extra").Select(i => i.Name).ShouldBe(
            ["Zed: workspace extra", "Zed: workspace history", "Aaa", "Mid"], "the sources of a corpus in the file, then corpora, each in name order");
    }

    [Fact]
    public async Task WithNoDefaultSetTheFirstByNameIsTheOneAMadeCorpusGetsAndTheRestAreNotImported()
    {
        var result = await RunAsync(File(Corpus("Papers", sets: [Set("b", isDefault: false), Set("a", isDefault: false)])), apply: true);

        Outcomes(result, "chunk set").ShouldBe("Papers: a=create,Papers: b=not imported");
        result.Items.Single(i => i.Kind == "corpus").Detail.ShouldNotBeNull().ShouldContain("from the file's 'a'");
    }

    [Theory]
    [InlineData("unitAware", "not imported")]
    [InlineData("sentenceAware", "not imported")]
    [InlineData("headingContext", "not imported")]
    [InlineData("customBoundaryPattern", "not imported")]
    [InlineData("provider", "not imported")]
    [InlineData("model", "not imported")]
    [InlineData("providerCase", "unchanged")]
    [InlineData("modelCase", "unchanged")]
    [InlineData("nameCase", "unchanged")]
    public async Task AChunkSetHereIsComparedOnEveryFieldTheFileCarries(string field, string expected)
    {
        await RunAsync(File(Corpus("Papers")), apply: true);
        var wanted = field switch
        {
            "unitAware" => Set() with { UnitAware = true },
            "sentenceAware" => Set() with { SentenceAware = true },
            "headingContext" => Set() with { HeadingContext = true },
            "customBoundaryPattern" => Set() with { CustomBoundaryPattern = "^x" },
            "provider" => Set() with { EmbeddingProvider = "other" },
            "model" => Set() with { EmbeddingModel = "other-model" },
            "providerCase" => Set() with { EmbeddingProvider = "OLLAMA" },
            "modelCase" => Set() with { EmbeddingModel = IndexingHarness.Model.ToUpperInvariant() },
            _ => Set() with { Name = "DEFAULT" },
        };

        var result = await RunAsync(File(Corpus("Papers", sets: [wanted])), apply: true);

        result.Items.Single(i => i.Kind == "chunk set").Outcome.ShouldBe(expected);
    }

    [Fact]
    public async Task AnUploadSourceIsNeitherMatchedNorReportedAsOnlyHere()
    {
        await RunAsync(File(Corpus("Papers")), apply: true);
        await using (var db = _harness.NewContext())
        {
            var corpusId = (await db.Corpora.SingleAsync(TestContext.Current.CancellationToken)).Id;
            db.Sources.Add(new Source { Id = Ulid.NewUlid().ToString(), CorpusId = corpusId, Kind = SourceKind.Upload, RootPath = string.Empty, CreatedUtc = Now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var none = await RunAsync(File(Corpus("Papers")), apply: false);
        var root = await RunAsync(File(Corpus("Papers", [Source("")])), apply: false);

        Outcomes(none, "source").ShouldBe("", "an upload source is not the file's to name");
        Outcomes(root, "source").ShouldBe("Papers: workspace =create", "the workspace root is not the upload source that has the same empty root");
    }

    [Fact]
    public async Task AHistorySourceAndAFileSourceOverOneFolderAreTwoSources()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);

        var result = await RunAsync(File(Corpus("Papers",
            [Source("notes"), Source("notes", kind: "githistory", git: new GitHistoryOptions { Ref = "main" })])), apply: true);

        Outcomes(result, "source").ShouldBe("Papers: workspace notes=unchanged,Papers: githistory notes=refused",
            "the history source is not taken for the file source on the same folder");
        result.Items.Single(i => i.Name == "Papers: githistory notes").Detail.ShouldNotBeNull()
            .ShouldNotContain("Not a git-history source", Case.Sensitive, "it was added as a new source and refused as one, and not matched to the file source and refused as a change to it");
    }

    [Theory]
    [InlineData("notes/")]
    [InlineData("notes/.")]
    [InlineData("./notes")]
    [InlineData("x/../notes")]
    [InlineData("notes//")]
    public async Task ASourceRootIsMatchedBySpellingsOfOneFolder(string root)
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);

        var result = await RunAsync(File(Corpus("Papers", [Source(root)])), apply: true);

        result.Items.Single(i => i.Kind == "source").Outcome.ShouldBe("unchanged");
        (await StoredAsync("Papers")).Sources.Count.ShouldBe(1, "a second spelling was not added as a second source over the folder");
    }

    [Fact]
    public async Task ASourceRootIsComparedAsTheFilesystemComparesIt()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);
        var sameFolder = WorkspaceDiscovery.PathComparer.Equals("notes", "NOTES");

        var result = await RunAsync(File(Corpus("Papers", [Source("NOTES")])), apply: true);

        result.Items.Single(i => i.Kind == "source" && i.Name.EndsWith("NOTES", StringComparison.Ordinal)).Outcome
            .ShouldBe(sameFolder ? "unchanged" : "create");
        (await StoredAsync("Papers")).Sources.Count.ShouldBe(sameFolder ? 1 : 2);
    }

    [Fact]
    public async Task ARootWithALeadingSlashIsNotTheFolderWithoutOne()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);

        var result = await RunAsync(File(Corpus("Papers", [Source("/notes")])), apply: true);

        result.Items.Single(i => i.Kind == "source" && i.Name.EndsWith("/notes", StringComparison.Ordinal)).Outcome
            .ShouldBe("refused", "it names a place outside the workspace, which the add refuses");
        (await StoredAsync("Papers")).Sources.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnEmbeddingServiceThatIsAwayRefusesTheCorpusWithItsReasonAndMakesNothing()
    {
        _harness.Embedder = new EmbedderThatIsAway();
        var before = await CountAsync();

        var result = await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);

        var item = result.Items.Single(i => i.Kind == "corpus");
        item.Outcome.ShouldBe("refused");
        item.Detail.ShouldNotBeNull().ShouldContain("Embedding model unavailable");
        (await CountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task AFileWithNoChunkSetForANewCorpusIsRefusedForThatCorpusOnly()
    {
        var result = await RunAsync(File(Corpus("NoSet", sets: []), Corpus("Fine")), apply: true);

        Outcomes(result, "corpus").ShouldBe("NoSet=refused,Fine=create");
    }

    // ----------------------------------------------------------------------------------------------- the route

    private static RequestContext Admin() => new()
    {
        Principal = new Principal("admin", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin }),
    };

    private async Task<IResult> CallAsync(ConfigExport file, RequestContext rc, bool? apply = null)
    {
        await using var db = _harness.NewContext();
        var importer = new ConfigImporter(db, _harness.NewConfiguration(db), NullLogger<ConfigImporter>.Instance);
        return await ConfigImportEndpoints.ImportAsync(file, apply, rc, importer, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AKeyWithEveryScopeButAdminIsRefused()
    {
        var rc = new RequestContext
        {
            Principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal)
            {
                Scopes.Search, Scopes.Ingest, Scopes.Configure, Scopes.Propose, Scopes.Destroy,
            }),
        };
        var before = await CountAsync();

        ((IStatusCodeHttpResult)await CallAsync(File(Corpus("Papers")), rc, apply: true)).StatusCode.ShouldBe(403);

        (await CountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task AFileThatIsNotAConfigurationFileOfThisVersionIsRefused()
    {
        var wrongFormat = File(Corpus("Papers")) with { Format = "something-else" };
        var wrongVersion = File(Corpus("Papers")) with { Version = 2 };

        ((IStatusCodeHttpResult)await CallAsync(wrongFormat, Admin(), apply: true)).StatusCode.ShouldBe(400);
        ((IStatusCodeHttpResult)await CallAsync(wrongVersion, Admin(), apply: true)).StatusCode.ShouldBe(400);
        (await CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ACorpusNamedTwiceOrNotAtAllRefusesTheWholeFileBeforeAnythingIsMade()
    {
        ((IStatusCodeHttpResult)await CallAsync(File(Corpus("Papers"), Corpus("papers")), Admin(), apply: true)).StatusCode.ShouldBe(400);
        ((IStatusCodeHttpResult)await CallAsync(File(Corpus("Fine"), Corpus(" ")), Admin(), apply: true)).StatusCode.ShouldBe(400);
        (await CountAsync()).ShouldBe(0);
    }

    private async Task<string> RefusedAsync(ConfigExport file)
    {
        var result = await CallAsync(file, Admin(), apply: true);
        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(400);
        (await CountAsync()).ShouldBe(0, "nothing was made before the file was refused");
        return ((ProblemHttpResult)result).ProblemDetails.Title!;
    }

    [Fact]
    public async Task ANullEntryInAnyListRefusesTheWholeFile()
    {
        (await RefusedAsync(File(new ExportedCorpus[] { Corpus("Fine"), null! }))).ShouldBe("The file has an empty entry");
        (await RefusedAsync(File(Corpus("Fine", sources: [Source("notes"), null!])))).ShouldBe("The file has an empty entry");
        (await RefusedAsync(File(Corpus("Fine", sets: [Set(), null!])))).ShouldBe("The file has an empty entry");
        (await RefusedAsync(File(Corpus("Fine")) with { Keys = [null!] })).ShouldBe("The file has an empty entry");
        (await RefusedAsync(File(Corpus("Fine")) with { ModelProfiles = [null!] })).ShouldBe("The file has an empty entry");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("upload")]
    [InlineData("bogus")]
    public async Task ASourceWithAKindThatIsNeitherFilesNorHistoryRefusesTheWholeFile(string? kind)
    {
        var source = new ExportedSource(kind!, "notes", Unset, null);

        (await RefusedAsync(File(Corpus("Papers", [source])))).ShouldBe("A source has a kind this version does not read");
    }

    [Fact]
    public async Task ASourceWithNoRootPathRefusesTheWholeFileAndAnEmptyOneIsTheWorkspaceRoot()
    {
        (await RefusedAsync(File(Corpus("Papers", [new ExportedSource("workspace", null, Unset, null)])))).ShouldBe("A source has no root path");

        var root = await RunAsync(File(Corpus("Papers", [Source("")])), apply: true);

        Outcomes(root, "source").ShouldBe("Papers: workspace =create");
    }

    [Fact]
    public async Task OneFolderListedTwiceUnderDifferentSpellingsRefusesTheWholeFile()
    {
        (await RefusedAsync(File(Corpus("Papers", [Source("notes"), Source("./notes/")])))).ShouldBe("A source is listed twice");
        (await RefusedAsync(File(Corpus("Papers", [Source("notes"), Source("x/../notes")])))).ShouldBe("A source is listed twice");
    }

    [Fact]
    public async Task AWorkspaceSourceAndAHistorySourceOnOneFolderAreNotListedTwice()
    {
        var result = await CallAsync(File(Corpus("Papers", [Source("notes"), Source("notes", kind: "githistory", git: new GitHistoryOptions { Ref = "main" })])), Admin());

        result.ShouldBeOfType<Ok<ConfigImportResult>>();
    }

    [Fact]
    public async Task AKindIsReadIgnoringCaseAndATrimmedNameMatchesTheCorpusHere()
    {
        await RunAsync(File(Corpus("Papers", [Source("notes")])), apply: true);

        var result = await RunAsync(File(Corpus("  papers ", [Source("notes", kind: "Workspace")])), apply: true);

        Outcomes(result, "corpus").ShouldBe("Papers=unchanged");
        Outcomes(result, "source").ShouldBe("papers: workspace notes=unchanged");
        (await StoredAsync("Papers")).Sources.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACorpusNamedTwiceBySurroundingSpaceRefusesTheWholeFile()
    {
        (await RefusedAsync(File(Corpus("Papers"), Corpus(" papers ")))).ShouldBe("A corpus is named twice");
    }

    [Fact]
    public async Task AFileWithListsLeftOutIsReadAsOneWithNoneOfThem()
    {
        var bare = new ExportedCorpus("Bare", null, null!, null!, null!);

        var result = await CallAsync(File(bare), Admin(), apply: false);

        var plan = ((Ok<ConfigImportResult>)result).Value.ShouldNotBeNull();
        plan.Items.Single().Outcome.ShouldBe("refused", "it has no chunk set, which is the one thing a new corpus needs");
    }

    // -------------------------------------------------------------------------------------------- round trip

    [Fact]
    public async Task AnExportImportedIntoAnEmptyInstanceExportsAsTheSameCorporaAndSources()
    {
        await RunAsync(File(
            Corpus("Papers", [Source("notes", new ExportedFilters(["**/*.md"], [], false, 5000)), Source("history")],
                [Set(size: 512, overlap: 64, mode: "blank-line")], "Conference papers", new ExportedFilters(["**/*.txt"], null, true, 9000)),
            Corpus("Notes", [Source("extra")])), apply: true);

        string Corpora(ConfigExport e) => JsonSerializer.Serialize(e.Corpora, ConfigEndpoints.FileJson);
        ConfigExport exported;
        await using (var db = _harness.NewContext())
            exported = await ConfigEndpoints.BuildAsync(db, Now, "a", TestContext.Current.CancellationToken);

        await using var second = await IndexingHarness.StartAsync("notes", "history", "extra");
        var imported = await RunAsync(exported, apply: true, on: second);
        imported.Items.Where(i => i.Kind is "corpus" or "source").ShouldAllBe(i => i.Outcome == "create");
        ConfigExport again;
        await using (var db = second.NewContext())
            again = await ConfigEndpoints.BuildAsync(db, Now, "a", TestContext.Current.CancellationToken);

        Corpora(again).ShouldBe(Corpora(exported));
    }

    private sealed class EmbedderThatIsAway : IEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default) =>
            throw new EmbeddingUnavailableException("the provider is away");

        public Task<int?> CountTokensAsync(EmbeddingTarget target, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default) =>
            throw new EmbeddingUnavailableException("the provider is away");

        public int KnownDimensions(EmbeddingTarget target) => 768;
    }
}

/// <summary>The route through the running application: admin only, and a hand-written body binds as it is read.</summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class ConfigImportHostTests : IDisposable
{
    private const string Password = "a-test-password-for-this-run";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-configimport-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _console = Console.Out;

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private WebApplicationFactory<Program> Start()
    {
        Directory.CreateDirectory(_data);
        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", Password);
        return new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        Console.SetOut(_console);
        Serilog.Log.CloseAndFlush();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={new StorageOptions { DataPath = _data }.CatalogPath}"));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static async Task<string> AdminTokenAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/session", new { password = Password }, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("token").GetString()!;
    }

    private static StringContent Json(string text) => new(text, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task AnAnonymousCallerAndAKeyWithEveryScopeButAdminAreRefusedAndTheAdministratorGetsAPlan()
    {
        await using var factory = Start();
        const string empty = "{\"format\":\"dexicon-config\",\"version\":1,\"corpora\":[]}";
        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsync("/api/config/import", Json(empty), TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(System.Net.HttpStatusCode.Unauthorized);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync(client));
        var created = await client.PostAsJsonAsync("/api/tokens", new { name = "everything-but-admin", scopes = Scopes.Issuable }, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("secret").GetString();
        using var asKey = factory.CreateClient();
        asKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        (await asKey.PostAsync("/api/config/import", Json(empty), TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(System.Net.HttpStatusCode.Forbidden);

        var plan = await client.PostAsync("/api/config/import", Json(empty), TestContext.Current.CancellationToken);
        plan.EnsureSuccessStatusCode();
        var root = await plan.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        root.GetProperty("applied").GetBoolean().ShouldBeFalse("no apply parameter is a dry run");
    }

    [Fact]
    public async Task AHandWrittenBodyBindsAsItIsReadAndAMalformedOneIsRefusedWithAReason()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync(client));

        // Properties left out: no defaults block, no sources, no chunk sets.
        var sparse = await client.PostAsync("/api/config/import",
            Json("{\"format\":\"dexicon-config\",\"version\":1,\"corpora\":[{\"name\":\"Papers\"}]}"), TestContext.Current.CancellationToken);
        sparse.EnsureSuccessStatusCode();
        var items = (await sparse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("outcome").GetString().ShouldBe("refused");

        var empty = await client.PostAsync("/api/config/import",
            Json("{\"format\":\"dexicon-config\",\"version\":1,\"corpora\":[null]}"), TestContext.Current.CancellationToken);
        empty.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await empty.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("The file has an empty entry");

        var applied = await client.PostAsync("/api/config/import?apply=true",
            Json("{\"format\":\"dexicon-config\",\"version\":1,\"corpora\":[]}"), TestContext.Current.CancellationToken);
        (await applied.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("applied").GetBoolean().ShouldBeTrue();
    }
}
