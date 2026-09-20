using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// The workspace root is a boundary, and a boundary is a directory, not a string prefix.
///
/// The check used to be <c>combined.StartsWith(root)</c>, which refuses `../etc/passwd`
/// and accepts `../workspaces-secret`. That is the shape of the bug worth a permanent
/// test: the escape it missed never traversed anywhere, it only needed a sibling whose
/// name begins with the root's. Every case below fails against the old check or passes
/// against both; the siblings are the interesting cases.
///
/// Paths are built with Path.Combine and Path.GetFullPath rather than written as literals
/// so the cases mean the same thing on the Windows they are usually written on and the
/// Linux they always run on in CI.
/// </summary>
public sealed class WorkspaceContainmentTests
{
    private static string Root => Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "dexicon-workspace-containment"));

    private static string Sibling(string suffix) => Root + suffix;

    [Fact]
    public void TheRootIsInsideItself()
    {
        // The workspace picker's opening call resolves to the root with no relative path.
        // Refusing it would make the mount unbrowsable.
        CorpusIndexer.IsInside(Root, Root).ShouldBeTrue();
    }

    [Fact]
    public void ATrailingSeparatorOnTheRootChangesNothing()
    {
        CorpusIndexer.IsInside(Root + Path.DirectorySeparatorChar, Root).ShouldBeTrue();
        CorpusIndexer.IsInside(Root, Root + Path.DirectorySeparatorChar).ShouldBeTrue();
    }

    [Fact]
    public void AChildIsInside()
    {
        CorpusIndexer.IsInside(Path.Combine(Root, "repo"), Root).ShouldBeTrue();
        CorpusIndexer.IsInside(Path.Combine(Root, "repo", "src", "Auth"), Root).ShouldBeTrue();
    }

    [Theory]
    [InlineData("-secret")]
    [InlineData(".bak")]
    [InlineData("2")]
    public void ASiblingSharingTheRootsNamePrefixIsNotInside(string suffix)
    {
        // The whole point. `/workspaces-secret` starts with `/workspaces` and is a
        // different directory; the old prefix check let every one of these through.
        CorpusIndexer.IsInside(Sibling(suffix), Root).ShouldBeFalse();
        CorpusIndexer.IsInside(Path.Combine(Sibling(suffix), "deeper"), Root).ShouldBeFalse();
    }

    [Fact]
    public void AnUnrelatedPathIsNotInside()
    {
        // Caught by the old check too. Here so that tightening the boundary later cannot
        // quietly lose the case that already worked.
        CorpusIndexer.IsInside(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere")), Root)
            .ShouldBeFalse();
    }

    [Fact]
    public void ResolveWorkspacePath_RefusesASiblingReachedByTraversal()
    {
        var indexer = IndexerRootedAt(Root);

        // Resolves to `<root>-secret`, which is exactly what the prefix check accepted.
        Should.Throw<UnauthorizedAccessException>(
            () => indexer.ResolveWorkspacePath("../dexicon-workspace-containment-secret"));
    }

    [Fact]
    public void ResolveWorkspacePath_RefusesAnAbsolutePathOutsideTheRoot()
    {
        var indexer = IndexerRootedAt(Root);

        // Path.Combine discards the root when the second argument is absolute, so this
        // never touches the root at all, so it has to be refused on the way out.
        Should.Throw<UnauthorizedAccessException>(
            () => indexer.ResolveWorkspacePath(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere"))));
    }

    [Fact]
    public void ResolveWorkspacePath_AllowsAChild()
    {
        var indexer = IndexerRootedAt(Root);

        indexer.ResolveWorkspacePath("repo/src").ShouldBe(Path.Combine(Root, "repo", "src"));
        indexer.ResolveWorkspacePath(null).ShouldBe(Root);
    }

    /// <summary>
    /// ResolveWorkspacePath reads the options and nothing else: no database, no vector
    /// store, no embedder, so the rest is left null rather than mocked. If that stops
    /// being true this throws a NullReferenceException, which is the right way to find out.
    /// </summary>
    private static CorpusIndexer IndexerRootedAt(string root) =>
        new(null!, null!, null!, null!, null!, null!, null!,
            Options.Create(new DexiconOptions { Indexing = new IndexingOptions { WorkspaceRoot = root } }),
            null!);
}
