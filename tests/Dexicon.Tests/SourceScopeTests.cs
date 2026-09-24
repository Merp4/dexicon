using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Which source owns a file when two of them reach it.
///
/// A source covers its whole tree, so adding one above an existing source makes every file
/// beneath reachable twice. File identity is (source, relative path), so the same file
/// under two sources is two rows, two chunkings and two sets of vectors: the corpus
/// silently doubles, a search returns the same passage twice, and the second copy is paid
/// for in embedding time. Nothing fails and no count says which files were affected.
///
/// Found on a real library. The coverage notice said to add a source on `books/manuals` to
/// pick up one loose book; `books/manuals` sits above ten sources, so the refresh that
/// followed would have indexed ninety-five books a second time.
/// </summary>
public sealed class SourceScopeTests
{
    private static Source S(string id, string? root, SourceKind kind = SourceKind.Workspace) =>
        new() { Id = id, CorpusId = "c", Kind = kind, RootPath = root, CreatedUtc = DateTime.UtcNow };

    private static IReadOnlyList<string> Shadowed(Source source, params Source[] all) =>
        SourceScope.ShadowedPrefixes(all, source);

    [Fact]
    public void ADeeperSourceOwnsItsOwnSubtree()
    {
        var manuals = S("2", "books/manuals");
        var ai = S("1", "books/manuals/AI");

        var shadowed = Shadowed(manuals, manuals, ai);

        shadowed.ShouldBe(["AI"]);
        SourceScope.IsShadowed("AI/one.pdf", shadowed).ShouldBeTrue();
        // The loose file the parent source was added for.
        SourceScope.IsShadowed("An Invented Handbook.pdf", shadowed).ShouldBeFalse();
    }

    [Fact]
    public void TheDeeperSourceItselfYieldsNothing()
    {
        var manuals = S("2", "books/manuals");
        var ai = S("1", "books/manuals/AI");

        // Asked the other way round, the specific source keeps everything: it is the most
        // specific thing covering its own tree.
        Shadowed(ai, manuals, ai).ShouldBeEmpty();
    }

    [Fact]
    public void EveryDeeperSourceIsAccountedForAndNotJustTheFirst()
    {
        var manuals = S("9", "books/manuals");
        var subs = new[] { "AI", "Agile", "Architecture", "DB", "dotnet" }
            .Select((n, i) => S(i.ToString(), $"books/manuals/{n}")).ToArray();

        var shadowed = Shadowed(manuals, [manuals, .. subs]);

        shadowed.Count.ShouldBe(5);
        foreach (var n in new[] { "AI", "Agile", "Architecture", "DB", "dotnet" })
            SourceScope.IsShadowed($"{n}/book.pdf", shadowed).ShouldBeTrue();
    }

    [Fact]
    public void ASiblingDoesNotShadowAnything()
    {
        var ai = S("1", "books/manuals/AI");
        var db = S("2", "books/manuals/DB");

        Shadowed(ai, ai, db).ShouldBeEmpty();
        Shadowed(db, ai, db).ShouldBeEmpty();
    }

    [Fact]
    public void AFolderThatMerelySharesAPrefixIsNotInsideIt()
    {
        // `books/manualsx` is not under `books/manuals`, however much of the string they share.
        // The same boundary test the workspace root uses, for the same reason.
        var manuals = S("2", "books/manuals");
        var manualsx = S("1", "books/manualsx");

        Shadowed(manuals, manuals, manualsx).ShouldBeEmpty();
        SourceScope.IsShadowed("manualsx-notes.md", Shadowed(manuals, manuals, manualsx)).ShouldBeFalse();
    }

    [Fact]
    public void ASourceOnTheWorkspaceRootStillYieldsToDeeperOnes()
    {
        var root = S("2", "");
        var docs = S("1", "docs");

        var shadowed = Shadowed(root, root, docs);

        shadowed.ShouldBe(["docs"]);
        SourceScope.IsShadowed("docs/guide.md", shadowed).ShouldBeTrue();
        SourceScope.IsShadowed("README.md", shadowed).ShouldBeFalse();
    }

    [Fact]
    public void TwoSourcesOnTheSameRootLeaveExactlyOneOwner()
    {
        // Duplicated by an API caller: the UI warns, the API allows it. Both indexing is
        // the bug this whole type exists for; neither indexing is worse.
        var first = S("1", "books/manuals/AI");
        var second = S("2", "books/manuals/AI");

        Shadowed(first, first, second).ShouldBeEmpty();
        Shadowed(second, first, second).ShouldBe([string.Empty]);

        SourceScope.IsShadowed("anything.pdf", Shadowed(second, first, second)).ShouldBeTrue();
        SourceScope.IsShadowed("anything.pdf", Shadowed(first, first, second)).ShouldBeFalse();
    }

    [Fact]
    public void TheOwnerOfADuplicatedRootDoesNotDependOnRowOrder()
    {
        var first = S("1", "books/manuals/AI");
        var second = S("2", "books/manuals/AI");

        // Same answer whichever order the rows came back in.
        Shadowed(first, second, first).ShouldBeEmpty();
        Shadowed(second, second, first).ShouldBe([string.Empty]);
    }

    [Fact]
    public void AnUploadSourceNeitherShadowsNorIsShadowed()
    {
        var manuals = S("2", "books/manuals");
        var upload = S("1", null, SourceKind.Upload);

        Shadowed(manuals, manuals, upload).ShouldBeEmpty();
        Shadowed(upload, manuals, upload).ShouldBeEmpty();
    }

    [Fact]
    public void AnUploadSourceIsIgnoredOnItsKindAndNotOnlyOnItsMissingPath()
    {
        // Upload files are content-addressed blobs, not paths in the workspace tree, so
        // they can neither shadow a folder nor be shadowed by one. Today they carry no
        // root path and the path check alone would be enough; the kind check is what keeps
        // that true if one ever does.
        var manuals = S("2", "books/manuals");
        var upload = S("1", "books/manuals/AI", SourceKind.Upload);

        Shadowed(manuals, manuals, upload).ShouldBeEmpty();
        Shadowed(upload, manuals, upload).ShouldBeEmpty();
    }

    [Fact]
    public void ThreeLevelsResolveToTheMostSpecific()
    {
        var books = S("3", "books");
        var manuals = S("2", "books/manuals");
        var ai = S("1", "books/manuals/AI");

        // `books` yields its whole manuals subtree to `books/manuals`, which in turn yields AI.
        // Each source keeps only what nothing deeper claims.
        Shadowed(books, books, manuals, ai).ShouldBe(["manuals", "manuals/AI"]);
        Shadowed(manuals, books, manuals, ai).ShouldBe(["AI"]);
        Shadowed(ai, books, manuals, ai).ShouldBeEmpty();

        SourceScope.IsShadowed("manuals/AI/x.pdf", Shadowed(books, books, manuals, ai)).ShouldBeTrue();
        SourceScope.IsShadowed("loose-at-books.pdf", Shadowed(books, books, manuals, ai)).ShouldBeFalse();
    }
}
