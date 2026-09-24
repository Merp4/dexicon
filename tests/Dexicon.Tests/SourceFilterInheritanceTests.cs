using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Which filters a source is actually walked with.
///
/// Three layers, narrowest first: the source's own value, the corpus default, then the
/// deployment's configuration. The middle one exists because ten folders under one parent
/// carried ten copies of the same two globs, set one at a time at creation and editable
/// never.
///
/// The distinction these tests exist to protect is null against empty. Null is "I have no
/// opinion"; an empty array is "none, whatever the corpus says". Collapse them and an
/// override becomes impossible to express, which is the bug that makes a corpus default
/// dangerous rather than useful.
/// </summary>
public sealed class SourceFilterInheritanceTests
{
    private static readonly IndexingOptions Configured = new() { MaxFileBytes = 262_144 };

    private static Corpus Corpus(
        bool? useGitignore = null, int? maxFileBytes = null,
        string? include = null, string? exclude = null) =>
        new()
        {
            Id = "c",
            Name = "books",
            CreatedUtc = DateTime.UtcNow,
            DefaultUseGitignore = useGitignore,
            DefaultMaxFileBytes = maxFileBytes,
            DefaultIncludeGlobs = include,
            DefaultExcludeGlobs = exclude,
        };

    private static Source Source(
        bool? useGitignore = null, int? maxFileBytes = null,
        string? include = null, string? exclude = null) =>
        new()
        {
            Id = "s",
            CorpusId = "c",
            Kind = SourceKind.Workspace,
            RootPath = "books/manuals/AI",
            CreatedUtc = DateTime.UtcNow,
            UseGitignore = useGitignore,
            MaxFileBytes = maxFileBytes,
            IncludeGlobs = include,
            ExcludeGlobs = exclude,
        };

    [Fact]
    public void ASourceWithNoOpinionTakesTheConfiguredValue()
    {
        var e = SourceFilters.Resolve(Corpus(), Source(), Configured);

        e.UseGitignore.ShouldBeTrue();
        e.MaxFileBytes.ShouldBe(262_144);
        e.IncludeGlobs.ShouldBeEmpty();
        e.ExcludeGlobs.ShouldBeEmpty();
    }

    [Fact]
    public void TheCorpusDefaultSitsBetweenTheSourceAndTheConfiguration()
    {
        var corpus = Corpus(useGitignore: false, maxFileBytes: 64 * 1024 * 1024,
            exclude: """["**/*.pdf"]""");

        var e = SourceFilters.Resolve(corpus, Source(), Configured);

        e.UseGitignore.ShouldBeFalse();
        e.MaxFileBytes.ShouldBe(64 * 1024 * 1024);
        e.ExcludeGlobs.ShouldBe(["**/*.pdf"]);
    }

    [Fact]
    public void ASourceOverridesTheCorpus()
    {
        var corpus = Corpus(useGitignore: false, maxFileBytes: 64 * 1024 * 1024,
            exclude: """["**/*.pdf"]""");
        var source = Source(useGitignore: true, maxFileBytes: 1024,
            exclude: """["**/*.epub"]""");

        var e = SourceFilters.Resolve(corpus, source, Configured);

        e.UseGitignore.ShouldBeTrue();
        e.MaxFileBytes.ShouldBe(1024);
        e.ExcludeGlobs.ShouldBe(["**/*.epub"]);
    }

    [Fact]
    public void EachFieldResolvesOnItsOwn()
    {
        // Setting one value must not pin the other three. A source that only wants a
        // bigger cap should still follow the corpus on globs.
        var corpus = Corpus(useGitignore: false, exclude: """["**/*.pdf"]""");
        var source = Source(maxFileBytes: 99);

        var e = SourceFilters.Resolve(corpus, source, Configured);

        e.MaxFileBytes.ShouldBe(99);
        e.UseGitignore.ShouldBeFalse();
        e.ExcludeGlobs.ShouldBe(["**/*.pdf"]);
    }

    [Fact]
    public void AnEmptyArrayOnTheSourceIsAnOverrideAndNotAnAbsentOne()
    {
        // The whole reason null and empty are stored differently. This source wants no
        // exclusions at all, on a corpus that excludes PDFs; if empty meant "inherit", it
        // could not say so, and would silently keep excluding them.
        var corpus = Corpus(exclude: """["**/*.pdf"]""");
        var source = Source(exclude: "[]");

        SourceFilters.Resolve(corpus, source, Configured).ExcludeGlobs.ShouldBeEmpty();
    }

    [Fact]
    public void FalseAndZeroAreValuesRatherThanAbsences()
    {
        // `false` is the interesting one: a source that turns .gitignore off under a
        // corpus that has it on must not be read as having no opinion.
        var corpus = Corpus(useGitignore: true);
        var source = Source(useGitignore: false);

        SourceFilters.Resolve(corpus, source, Configured).UseGitignore.ShouldBeFalse();
    }

    [Fact]
    public void AnUnreadableGlobColumnFallsBackRatherThanThrowing()
    {
        // A corrupt column should not stop a corpus indexing. It reads as unset, so the
        // corpus default applies and the UI shows an inherited value.
        var corpus = Corpus(exclude: """["**/*.pdf"]""");
        var source = Source(exclude: "{not json");

        SourceFilters.Resolve(corpus, source, Configured).ExcludeGlobs.ShouldBe(["**/*.pdf"]);
    }

    [Fact]
    public void OriginSaysWhichLayerAnswered()
    {
        // What lets the UI label a value "inherited" rather than showing a number the
        // reader will assume they typed.
        SourceFilters.OriginOf(1024, 64).ShouldBe(SourceFilters.Origin.Source);
        SourceFilters.OriginOf(null, 64).ShouldBe(SourceFilters.Origin.Corpus);
        SourceFilters.OriginOf(null, null).ShouldBe(SourceFilters.Origin.Configured);

        // An empty list is a value, so it stops the search at the source.
        SourceFilters.OriginOf(new List<string>(), new List<string> { "x" })
            .ShouldBe(SourceFilters.Origin.Source);
    }

    [Fact]
    public void StoreAndGlobsRoundTripTheThreeStates()
    {
        SourceFilters.Store(null).ShouldBeNull();
        SourceFilters.Globs(SourceFilters.Store([])).ShouldBeEmpty();
        SourceFilters.Globs(SourceFilters.Store(["a", "b"])).ShouldBe(["a", "b"]);

        // The one that matters: an unset column stays distinguishable from an empty one
        // after a round trip through storage.
        SourceFilters.Globs(null).ShouldBeNull();
        SourceFilters.Globs("[]").ShouldNotBeNull();
    }
}

/// <summary>
/// Changing a source's filters after it was added.
///
/// They used to be write-once: set when the folder was added and unreachable afterwards,
/// so changing one meant deleting the source, which drops its files from every chunk set,
/// and re-embedding the folder from scratch. Nobody iterates on a glob at that price.
///
/// Three cases per field, and only two of them are obvious: omitted leaves it alone, a
/// value sets it, and a name in `clear` returns it to the corpus default. The third exists
/// because JSON cannot distinguish an absent property from an explicit null once it is
/// bound to a nullable, and inferring a clear from null would make every partial update an
/// accidental reset of everything it did not mention.
/// </summary>
public sealed class SourceFilterUpdateTests
{
    private static Source Source() => new()
    {
        Id = "s",
        CorpusId = "c",
        Kind = SourceKind.Workspace,
        RootPath = "books/manuals/AI",
        CreatedUtc = DateTime.UtcNow,
        UseGitignore = false,
        MaxFileBytes = 64 * 1024 * 1024,
        IncludeGlobs = """["**/*.epub"]""",
        ExcludeGlobs = """["**/*.pdf"]""",
    };

    /// <summary>
    /// A history source is walked by `git log`, so only the include globs mean anything
    /// there — they become pathspecs. The other three are read by nothing on that path,
    /// and storing them answers 200 to a request that was not honoured.
    ///
    /// The UI hides these fields for a history source, which is what is offered rather
    /// than what is enforced. This is the rule where the request is handled.
    /// </summary>
    [Fact]
    public void FileOnlySettingsAreNotForAHistorySource()
    {
        CorpusEndpoints.FileOnlySettingsFor(SourceKind.GitHistory, useGitignore: true, null, null)
            .ShouldBe("useGitignore");
        CorpusEndpoints.FileOnlySettingsFor(SourceKind.GitHistory, null, maxFileBytes: 1024, null)
            .ShouldBe("maxFileBytes");
        CorpusEndpoints.FileOnlySettingsFor(SourceKind.GitHistory, null, null, excludeGlobs: ["**/*.pdf"])
            .ShouldBe("excludeGlobs");

        CorpusEndpoints.FileOnlySettingsFor(SourceKind.GitHistory, true, 1024, ["**/*.pdf"])
            .ShouldBe("useGitignore, maxFileBytes, excludeGlobs", "all three are named, not just the first");

        // Include globs are the exception, and the reason this is not simply "no filters
        // on a history source".
        CorpusEndpoints.FileOnlySettingsFor(SourceKind.GitHistory, null, null, null).ShouldBeNull();

        // And a file source takes all of them, which is what it is for.
        CorpusEndpoints.FileOnlySettingsFor(SourceKind.Workspace, true, 1024, ["**/*.pdf"]).ShouldBeNull();
    }

    /// <summary>
    /// History settings git cannot be asked with are refused where they arrive. They were
    /// stored as sent and found on the next pass, as the source being unavailable with
    /// the reason in a job, so an editor saving a typo was told it had succeeded.
    /// </summary>
    [Fact]
    public void UnusableHistorySettingsAreRefused()
    {
        Refusal(new GitHistoryOptions { Ref = "main..other" }).ShouldContain("not a usable ref");
        Refusal(new GitHistoryOptions { Ref = "-rf" }).ShouldContain("not a usable ref");
        Refusal(new GitHistoryOptions { Ref = " " }).ShouldContain("not a usable ref");
        Refusal(new GitHistoryOptions { MaxCommits = 0 }).ShouldContain("maxCommits is 0");
        Refusal(new GitHistoryOptions { MaxDiffBytes = -1 }).ShouldContain("maxDiffBytes");
        Refusal(new GitHistoryOptions { KeepIndexed = true }).ShouldContain("keepIndexed applies only with maxCommits");

        CorpusEndpoints.UnusableHistorySettings(new GitHistoryOptions { Ref = "origin/main", MaxCommits = 50 })
            .ShouldBeNull();
        CorpusEndpoints.UnusableHistorySettings(new GitHistoryOptions { MaxCommits = 50, KeepIndexed = true })
            .ShouldBeNull();
        CorpusEndpoints.UnusableHistorySettings(null).ShouldBeNull("absent settings are the defaults");
    }

    private static string Refusal(GitHistoryOptions git)
    {
        var problem = CorpusEndpoints.UnusableHistorySettings(git)
            .ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>();
        problem.StatusCode.ShouldBe(400);
        return problem.ProblemDetails.Detail.ShouldNotBeNull();
    }

    /// <summary>
    /// A name `clear` does not understand is a typo, and the caller wants to know.
    ///
    /// Unknown names were dropped on the floor and the request answered 200, so
    /// `clear: ["maxfilebytes"]` left the setting in place and reported success — and so
    /// would a field renamed one day, on every caller still sending the old name.
    /// </summary>
    [Fact]
    public void AClearNameThatIsNotAFilterIsNamedBack()
    {
        CorpusEndpoints.UnknownClearName(["maxFileBytes", "includeGlobs"]).ShouldBeNull();
        CorpusEndpoints.UnknownClearName(null).ShouldBeNull();
        CorpusEndpoints.UnknownClearName([]).ShouldBeNull();

        // How ApplyFilters compares them, so how this must.
        CorpusEndpoints.UnknownClearName(["MAXFILEBYTES"]).ShouldBeNull();

        CorpusEndpoints.UnknownClearName(["maxFileBytes", "nonsense"]).ShouldBe("nonsense");
        CorpusEndpoints.UnknownClearName(["git"]).ShouldBe("git", "history settings are not a filter");
    }

    [Fact]
    public void AnOmittedFieldIsLeftAlone()
    {
        var s = Source();

        var changed = CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest(MaxFileBytes: 1024));

        changed.ShouldBeTrue();
        s.MaxFileBytes.ShouldBe(1024);
        // Everything the request did not mention survives it.
        s.UseGitignore.ShouldBe(false);
        s.IncludeGlobs.ShouldBe("""["**/*.epub"]""");
        s.ExcludeGlobs.ShouldBe("""["**/*.pdf"]""");
    }

    [Fact]
    public void ANamedFieldReturnsToInheriting()
    {
        var s = Source();

        CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest(Clear: ["excludeGlobs", "maxFileBytes"]));

        s.ExcludeGlobs.ShouldBeNull();
        s.MaxFileBytes.ShouldBeNull();
        s.IncludeGlobs.ShouldBe("""["**/*.epub"]""");
        s.UseGitignore.ShouldBe(false);
    }

    [Fact]
    public void ClearingWinsOverAValueSentAlongsideIt()
    {
        // A form that sends every field it rendered, plus a clear for the one the reader
        // reset, must not have the rendered value win. Otherwise "reset to inherited"
        // silently re-pins the number that was on screen.
        var s = Source();

        CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest(MaxFileBytes: 4096, Clear: ["maxFileBytes"]));

        s.MaxFileBytes.ShouldBeNull();
    }

    [Fact]
    public void AnEmptyGlobListIsStoredAsAnOverrideRatherThanAsAnAbsence()
    {
        // "No exclusions, whatever the corpus says." Stored as null it would silently
        // inherit the corpus's exclusions instead.
        var s = Source();

        CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest(ExcludeGlobs: []));

        s.ExcludeGlobs.ShouldBe("[]");
        SourceFilters.Globs(s.ExcludeGlobs).ShouldBeEmpty();
    }

    [Fact]
    public void AnUnchangedRequestReportsNoChange()
    {
        // What stops a form submitted without edits re-walking a library.
        var s = Source();

        CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest(
            UseGitignore: false,
            MaxFileBytes: 64 * 1024 * 1024,
            IncludeGlobs: ["**/*.epub"],
            ExcludeGlobs: ["**/*.pdf"])).ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyRequestChangesNothing()
    {
        var s = Source();

        CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest()).ShouldBeFalse();
        s.MaxFileBytes.ShouldBe(64 * 1024 * 1024);
    }

    [Fact]
    public void FalseIsAValueAndNotAnOmission()
    {
        // `body.UseGitignore is { } g` on a bool? must accept false. Treating it as absent
        // would make "turn .gitignore off" impossible to send.
        var s = Source();
        s.UseGitignore = true;

        CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest(UseGitignore: false)).ShouldBeTrue();
        s.UseGitignore.ShouldBe(false);
    }

    [Fact]
    public void ClearNamesAreMatchedWithoutRegardToCase()
    {
        var s = Source();

        CorpusEndpoints.ApplyFilters(s, new UpdateSourceRequest(Clear: ["ExcludeGlobs"]));

        s.ExcludeGlobs.ShouldBeNull();
    }
}
