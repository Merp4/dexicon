using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Links are not followed: a source path through one is refused, and the walk lists one
/// as skipped rather than entering or reading it (WorkspaceWalker.IsLink).
///
/// `Path.GetFullPath` canonicalises separators and dots and resolves no links, so
/// `workspace/link` passes a boundary check on the text and then IS whatever it points at.
/// Measured with a source rooted at such a link, before any link check existed:
///
///   Resolve          accepted -> workspace\link
///   Walk files       [host-secret.txt]      &lt;- from outside the workspace, indexed
///
/// A source path is refused rather than skipped: the operator named this one path, and
/// answering "there is nothing there" about a directory that plainly exists sends them
/// looking at the mount instead of at the link.
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
        // Checking only the last component would miss this, and a source two levels down
        // is the ordinary shape: `repos/link/src`.
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "link"), _outside);
        Directory.CreateDirectory(Path.Combine(_outside, "src"));

        Should.Throw<UnauthorizedAccessException>(
            () => WorkspaceDiscovery.Resolve(_workspace, "link/src"));
    }

    [Fact]
    public void SoIsALinkThatStaysInside()
    {
        // The directory it points at can be named instead, and is then an ordinary path.
        Directory.CreateDirectory(Path.Combine(_workspace, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "inward"), Path.Combine(_workspace, "real"));

        Should.Throw<UnauthorizedAccessException>(() => WorkspaceDiscovery.Resolve(_workspace, "inward"));
        Should.Throw<UnauthorizedAccessException>(() => WorkspaceDiscovery.ResolveExisting(_workspace, "inward"));
        WorkspaceDiscovery.ResolveExisting(_workspace, "real")
            .ShouldBe(Path.Combine(Path.GetFullPath(_workspace), "real"));
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
    public void AnAbsentDirectoryIsStillAbsentRatherThanRefused()
    {
        // The distinction ResolveExisting exists to make: null for "not there", an
        // exception for "not allowed".
        WorkspaceDiscovery.ResolveExisting(_workspace, "not/here/yet").ShouldBeNull();
    }

    [Fact]
    public void ADanglingLinkNeverYieldsAPath()
    {
        // Windows lists a dangling directory link, since the reparse point carries the
        // directory attribute, so it is found and refused. Linux does not list it as a
        // directory at all, so the segment is absent. Either answer is safe, and CI failed
        // on an earlier test that asserted the Windows one.
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "dangling"), Path.Combine(_workspace, "never-created"));
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "outward"), Path.Combine(_outside, "never-created"));

        NeverYieldsAPath("dangling");
        NeverYieldsAPath("dangling/src");
        NeverYieldsAPath("outward");
    }

    [Fact]
    public void NorDoesALinkThatPointsAtItself()
    {
        // Callers translate UnauthorizedAccessException and nothing else, so an
        // IOException from a cycle would reach source creation as a 500.
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "a"), Path.Combine(_workspace, "b"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "b"), Path.Combine(_workspace, "a"));

        NeverYieldsAPath("a");
    }

    private void NeverYieldsAPath(string relative)
    {
        string? reached;
        try { reached = WorkspaceDiscovery.ResolveExisting(_workspace, relative); }
        catch (UnauthorizedAccessException) { return; }

        reached.ShouldBeNull();
    }

    [Fact]
    public void ADotDotAfterALinkInACallersPathStaysInside()
    {
        // `Path.GetFullPath` collapses `..` lexically, before any segment is looked at, so
        // `alias/../secret` is `<workspace>/secret` and `alias` is never consulted. The OS
        // would resolve `alias` first and take the parent of wherever it led. The
        // divergence only narrows: what comes back is inside by construction.
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _outside);
        Directory.CreateDirectory(Path.Combine(_workspace, "secret"));

        WorkspaceDiscovery.Resolve(_workspace, "alias/../secret")
            .ShouldBe(Path.Combine(Path.GetFullPath(_workspace), "secret"));
    }

    [Fact]
    public void TheWalkEntersNoDirectoryLinkAndListsEach()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "keep"));
        File.WriteAllText(Path.Combine(_workspace, "keep", "app.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_outside, "host-secret.txt"), "not in the workspace");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "inward"), Path.Combine(_workspace, "keep"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "outward"), _outside);

        var walked = WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000);

        walked.Files.Select(f => f.RelativePath).ShouldBe(["keep/app.cs"]);
        walked.SkippedFiles
            .Where(s => s.Reason == WorkspaceWalker.LinkNotFollowed)
            .Select(s => s.RelativePath).Order(StringComparer.Ordinal)
            .ShouldBe(["inward", "outward"]);
    }

    [Fact]
    public void OrAFileLink()
    {
        File.WriteAllText(Path.Combine(_workspace, "app.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "not in the workspace");
        File.CreateSymbolicLink(Path.Combine(_workspace, "alias.cs"), Path.Combine(_workspace, "app.cs"));
        File.CreateSymbolicLink(Path.Combine(_workspace, "link.txt"), Path.Combine(_outside, "secret.txt"));

        var walked = WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000);

        walked.Files.Select(f => f.RelativePath).ShouldBe(["app.cs"]);
        walked.SkippedFiles
            .Where(s => s.Reason == WorkspaceWalker.LinkNotFollowed)
            .Select(s => s.RelativePath).Order(StringComparer.Ordinal)
            .ShouldBe(["alias.cs", "link.txt"]);
    }

    [Fact]
    public void ADirectoryLinkTheRulesIgnoreIsNotListed()
    {
        // As absent as any other ignored entry.
        Directory.CreateDirectory(Path.Combine(_workspace, "real"));
        File.WriteAllText(Path.Combine(_workspace, ".gitignore"), "vendor/\n");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "vendor"), Path.Combine(_workspace, "real"));

        WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000)
            .SkippedFiles.ShouldNotContain(s => s.RelativePath == "vendor");
    }

    [Fact]
    public void ADotDotAfterADirectoryLinkReachesNothing()
    {
        // POSIX takes `..` from wherever `alias` led; `ResolveLinkTarget(...).FullName`
        // collapses it in the text, so this target read as `workspace/hostdir`, absent and
        // therefore inside. Measured on Linux when links were followed: the walk returned
        // `link/secret.txt`, and a source rooted at `link` returned the same file.
        Directory.CreateDirectory(Path.Combine(_outside, "hostdir"));
        File.WriteAllText(Path.Combine(_outside, "hostdir", "secret.txt"), "not in the workspace");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), Path.Combine(_outside, "hostdir"));
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "link"), Path.Combine(_workspace, "alias", "..", "hostdir"));

        WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000)
            .Files.ShouldBeEmpty();

        Should.Throw<UnauthorizedAccessException>(() => WorkspaceDiscovery.Resolve(_workspace, "link"));
    }

    [Fact]
    public void ALinkBackToTheRootIsNotWalkedAgain()
    {
        // Measured on Linux when links were followed, `loop -> <workspace>` in a one-file
        // tree gave 41 copies: `app.cs`, `loop/app.cs`, `loop/loop/app.cs` and on until the
        // platform's own link limit stopped it.
        File.WriteAllText(Path.Combine(_workspace, "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "loop"), _workspace);

        WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath).ShouldBe(["app.cs"]);
    }

    [Fact]
    public void APathUnderAMissingMountIsAbsentRatherThanRefused()
    {
        // What this covers is the ordinary missing-mount path: the parent is not there, so
        // the enumeration finds no match and the walk stops. Absent, never a refusal.
        //
        // It does NOT cover the mid-walk race the DirectoryNotFoundException catch exists
        // for — a mount going away after its parent has already listed it. Reaching it
        // deterministically needs a filesystem seam this suite does not have, so the race
        // is handled and stated rather than tested.
        var vanishing = Path.Combine(_workspace, "mount");
        Directory.CreateDirectory(Path.Combine(vanishing, "repo"));
        Directory.Delete(vanishing, recursive: true);

        Should.NotThrow(() => WorkspaceDiscovery.Resolve(_workspace, "mount/repo"));
        WorkspaceDiscovery.ResolveExisting(_workspace, "mount/repo").ShouldBeNull();
    }

    [Fact]
    public void CoverageDoesNotWalkThroughOneEither()
    {
        // The one path that reaches a directory NOBODY created a source on: coverage
        // derives the shared parent of two sources and walks it. So `link` gets walked
        // while no source names it, and the walk reads files to sniff them for NUL bytes.
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
