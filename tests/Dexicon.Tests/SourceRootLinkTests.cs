using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A source root that is a link out of the workspace.
///
/// `Path.GetFullPath` canonicalises separators and dots and resolves no links, so
/// `workspace/link` passes a boundary check on the text and then IS whatever it points at.
/// Measured against the code this replaces, with a source rooted at such a link:
///
///   Resolve          accepted -> workspace\link
///   Walk files       [host-secret.txt]      &lt;- from outside the workspace, indexed
///   ResolveExisting  refused
///
/// The walk already refuses an outward link on every directory it descends into
/// (GitignoreFilter.EnumerateFilesSafely) and `ResolveExisting` already refuses one on a
/// git-history source's path. The root of a workspace source reached neither.
///
/// It is refused rather than skipped: the operator named this one path, and answering
/// "there is nothing there" about a directory that plainly exists sends them looking at
/// the mount instead of at the link.
/// </summary>
public sealed class SourceRootLinkTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("dexicon-ws-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("dexicon-outside-").FullName;

    [Fact]
    public void ARootThatLinksOutOfTheWorkspaceIsRefused()
    {
        File.WriteAllText(Path.Combine(_outside, "host-secret.txt"), "not in the workspace");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "link"), _outside);

        var refused = Should.Throw<UnauthorizedAccessException>(
            () => WorkspaceDiscovery.Resolve(_workspace, "link"));

        refused.Message.ShouldContain("link");
        refused.Message.ShouldContain(_outside);
    }

    [Fact]
    public void SoIsOneHalfWayAlongThePath()
    {
        // Resolving only the last component would miss this, and a source two levels down
        // is the ordinary shape: `repos/link/src`.
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "link"), _outside);
        Directory.CreateDirectory(Path.Combine(_outside, "src"));

        Should.Throw<UnauthorizedAccessException>(
            () => WorkspaceDiscovery.Resolve(_workspace, "link/src"));
    }

    [Fact]
    public void ALinkThatStaysInsideIsFine()
    {
        // Inward links are allowed, and a repository laid out with one must stay indexable.
        Directory.CreateDirectory(Path.Combine(_workspace, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "inward"), Path.Combine(_workspace, "real"));

        WorkspaceDiscovery.Resolve(_workspace, "inward")
            .ShouldBe(Path.Combine(Path.GetFullPath(_workspace), "inward"));
    }

    [Fact]
    public void APathThatDoesNotExistIsNotRefused()
    {
        // The mount being away is an operational condition, and a source created before
        // its mount is attached is ordinary. Refusing here would report one as the other.
        WorkspaceDiscovery.Resolve(_workspace, "not/here/yet")
            .ShouldBe(Path.GetFullPath(Path.Combine(_workspace, "not", "here", "yet")));
    }

    [Fact]
    public void TheSpellingIsTheOneThatWasAsked()
    {
        // Resolve refuses; it does not rewrite. Returning the followed path would change
        // what every caller stores and compares. ResolveExisting is the one that hands
        // back the filesystem's own spelling.
        Directory.CreateDirectory(Path.Combine(_workspace, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "inward"), Path.Combine(_workspace, "real"));

        WorkspaceDiscovery.Resolve(_workspace, "inward")
            .ShouldNotBe(Path.Combine(Path.GetFullPath(_workspace), "real"));

        WorkspaceDiscovery.ResolveExisting(_workspace, "inward")
            .ShouldBe(Path.Combine(Path.GetFullPath(_workspace), "real"));
    }

    [Fact]
    public void AnAbsentDirectoryIsStillAbsentRatherThanRefused()
    {
        // The distinction ResolveExisting exists to make, and the refactor must not lose
        // it: null for "not there", an exception for "not allowed".
        WorkspaceDiscovery.ResolveExisting(_workspace, "not/here/yet").ShouldBeNull();
    }

    [Fact]
    public void ALinkWithNothingAtTheOtherEndIsAbsent()
    {
        // A dangling link RESOLVES — measured, `ResolveLinkTarget(returnFinalTarget: true)`
        // hands back the target it names whether or not anything is there. Following one
        // inside the root therefore set `current` to a directory that does not exist, and
        // this method is documented to answer null for exactly that.
        var gone = Path.Combine(_workspace, "never-created");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "dangling"), gone);

        WorkspaceDiscovery.ResolveExisting(_workspace, "dangling").ShouldBeNull();
    }

    [Fact]
    public void AndNothingThrowsOutOfTheSegmentAfterIt()
    {
        // With a segment to go, the walk called Directory.EnumerateDirectories on that
        // absent path. "The mount is away" and "this resolves outside the workspace" are
        // the two answers this is allowed to give; an IO exception out of the middle is
        // neither.
        var gone = Path.Combine(_workspace, "never-created");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "dangling"), gone);

        WorkspaceDiscovery.ResolveExisting(_workspace, "dangling/src").ShouldBeNull();
    }

    [Fact]
    public void ADanglingLinkAimedOutOfTheWorkspaceNeverYieldsAPath()
    {
        // Safe on both platforms, and not by the same route, so this asserts the property
        // rather than the mechanism.
        //
        // Windows lists a dangling directory link — the reparse point carries the
        // directory attribute — so the containment check sees it and refuses. Linux does
        // not list it as a directory at all, so the walk never finds it. Measured: CI
        // failed on the first version of this, which asserted the Windows route.
        //
        // Either way nothing hands back a usable path, and if the target is created later
        // the link becomes an ordinary directory and the check applies.
        var gone = Path.Combine(_outside, "never-created");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "outward"), gone);

        string? reached;
        try { reached = WorkspaceDiscovery.ResolveExisting(_workspace, "outward"); }
        catch (UnauthorizedAccessException) { return; }

        reached.ShouldBeNull();
    }

    [Fact]
    public void ALinkThatPointsAtItselfIsRefusedRatherThanThrowingIO()
    {
        // `returnFinalTarget` follows the chain and throws IOException on a cycle rather
        // than answering null. Callers translate UnauthorizedAccessException and not that,
        // so it would have reached source creation as a 500 or aborted a sweep.
        var a = Path.Combine(_workspace, "a");
        var b = Path.Combine(_workspace, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        // Same disjunction as the dangling case, for the same platform reason: what
        // matters is that no IOException escapes and no path comes back.
        string? reached;
        try { reached = WorkspaceDiscovery.ResolveExisting(_workspace, "a"); }
        catch (UnauthorizedAccessException) { return; }

        reached.ShouldBeNull();
    }

    [Fact]
    public void ALinkWhoseTargetPassesThroughAnotherLinkIsRefused()
    {
        // `alias` leaves the workspace and `link` points at `alias/src`, so the target's
        // own text stays inside and only its parent gives it away.
        //
        // `ResolveLinkTarget(returnFinalTarget: true)` canonicalises the parent components
        // too, which is the property this depends on and the reason it is pinned here.
        // Measured on this pair:
        //
        //   LinkTarget    <workspace>\alias\src     <- the literal text, inside
        //   ResolveFinal  <outside>\src             <- what the check actually sees
        Directory.CreateDirectory(Path.Combine(_outside, "src"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _outside);
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "link"), Path.Combine(_workspace, "alias", "src"));

        Should.Throw<UnauthorizedAccessException>(
            () => WorkspaceDiscovery.Resolve(_workspace, "link"));
    }

    [Fact]
    public void ADotDotAfterALinkStaysInsideRatherThanFollowingIt()
    {
        // `Path.GetFullPath` collapses `..` lexically, before any segment is looked at, so
        // `alias/../secret` is `<workspace>/secret`. The OS would instead resolve `alias`
        // to the outside, take its parent, and land somewhere else entirely.
        //
        // The divergence is deliberate and it only ever narrows: the path this returns is
        // inside the root by construction. Resolving links first and applying `..` after
        // — which is what matching the OS would mean — lets `alias/..` reach outside the
        // workspace, which is the escape this collapse exists to prevent.
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _outside);
        Directory.CreateDirectory(Path.Combine(_workspace, "secret"));

        WorkspaceDiscovery.Resolve(_workspace, "alias/../secret")
            .ShouldBe(Path.Combine(Path.GetFullPath(_workspace), "secret"));
    }

    [Fact]
    public void TheWalkDoesNotFollowOneEither()
    {
        // The resolver protects the source ROOT. This is a link two levels inside a
        // perfectly ordinary root, which is where the content actually leaves: the walk
        // had its own copy of the boundary test, and the copy asked the easier question.
        //
        // Measured on Linux before the two were made one routine:
        //
        //   WALK: keep/app.cs | link/host-secret.txt
        //
        // The file is from outside the workspace, read out of a read-only mount.
        Directory.CreateDirectory(Path.Combine(_outside, "src"));
        File.WriteAllText(Path.Combine(_outside, "src", "host-secret.txt"), "not in the workspace");

        Directory.CreateDirectory(Path.Combine(_workspace, "keep"));
        File.WriteAllText(Path.Combine(_workspace, "keep", "app.cs"), "class A {}");

        Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _outside);
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "link"), Path.Combine(_workspace, "alias", "src"));

        WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldBe(["keep/app.cs"]);
    }





    [Fact]
    public void AFileLinkThroughAnAliasedParentIsNotIndexed()
    {
        // The shape that needed the recursive walk, applied to a file: the target's own
        // text is inside the workspace and only its parent gives it away.
        Directory.CreateDirectory(Path.Combine(_outside, "src"));
        File.WriteAllText(Path.Combine(_outside, "src", "secret.txt"), "not in the workspace");
        File.WriteAllText(Path.Combine(_workspace, "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _outside);
        File.CreateSymbolicLink(
            Path.Combine(_workspace, "link.txt"),
            Path.Combine(_workspace, "alias", "src", "secret.txt"));

        var walked = WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000);

        walked.Files.Select(f => f.RelativePath).ShouldBe(["app.cs"]);
        walked.SkippedFiles.ShouldContain(s => s.RelativePath == "link.txt");
    }





    [Fact]
    public void CoverageDoesNotWalkThroughOneEither()
    {
        // The one path that reaches a directory NOBODY created a source on: coverage
        // derives the shared parent of two sources and walks it. So `link` gets walked
        // while no source names it, and the walk reads files to sniff them for NUL bytes.
        //
        // It has to go through the same resolver rather than carry a copy of the rule,
        // which is how the two come to disagree.
        File.WriteAllText(Path.Combine(_outside, "host-secret.txt"), "not in the workspace");
        Directory.CreateDirectory(Path.Combine(_outside, "a"));
        Directory.CreateDirectory(Path.Combine(_outside, "b"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "link"), _outside);

        var gaps = SourceCoverage.Find(_workspace, [
            new SourceCoverage.SourceRoot("link/a", 262_144),
            new SourceCoverage.SourceRoot("link/b", 262_144),
        ]);

        gaps.ShouldBeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_outside, recursive: true); } catch (IOException) { }
    }
}
