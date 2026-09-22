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
    public void APathUnderAMissingMountIsAbsentRatherThanRefused()
    {
        // What this covers is the ordinary missing-mount path: the parent is not there, so
        // the enumeration finds no match and the walk stops. Absent, never a refusal.
        //
        // It does NOT cover the mid-walk race the DirectoryNotFoundException catch exists
        // for — a mount going away after its parent has already listed it. The first
        // version of this test claimed to, and passed with that catch removed. Reaching it
        // deterministically needs a filesystem seam this suite does not have, so the race
        // is handled and stated rather than tested.
        var vanishing = Path.Combine(_workspace, "mount");
        Directory.CreateDirectory(Path.Combine(vanishing, "repo"));
        Directory.Delete(vanishing, recursive: true);

        Should.NotThrow(() => WorkspaceDiscovery.Resolve(_workspace, "mount/repo"));
        WorkspaceDiscovery.ResolveExisting(_workspace, "mount/repo").ShouldBeNull();
    }

    [Fact]
    public void ALinkTargetSpelledWithDotDotIsRefused()
    {
        // `..` cuts the other way in a link target than it does in a caller's path.
        //
        // For a caller's path, collapsing it lexically NARROWS: `alias/../secret` becomes
        // `<ws>/secret`, which is inside. For a link TARGET it widens, because the
        // collapse removes the very component that gives the escape away — the OS follows
        // `alias` to the outside first and only then takes the parent.
        //
        // So a target that still spells `..` has not been resolved, and is refused rather
        // than collapsed. There is nothing to lose by it: a target the platform HAS
        // resolved never carries one.
        Directory.CreateDirectory(Path.Combine(_outside, "secret"));
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _outside);
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "link"),
            Path.Combine(_workspace, "alias", "..", "secret"));

        WorkspaceDiscovery.ResolvesInside(
            Path.Combine(_workspace, "alias", "..", "secret"), Path.GetFullPath(_workspace))
            .ShouldBeFalse();
    }

    [Fact]
    public void ALinkBackToTheRootDoesNotMultiplyTheCorpus()
    {
        // Containment cannot catch this: the root IS inside itself. Measured on Linux with
        // `loop -> <workspace>` in a one-file tree, the walk returned
        //
        //   count=41  app.cs | loop/app.cs | loop/loop/app.cs | loop/loop/loop/app.cs
        //
        // stopping only at the platform's own symlink limit. It terminates and multiplies
        // the corpus — the "silently doubles" failure in docs/04, forty-one times over,
        // and forty extra embeddings of every file.
        File.WriteAllText(Path.Combine(_workspace, "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "loop"), _workspace);

        WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldBe(["app.cs"]);
    }

    [Fact]
    public void NorOneBackToADirectoryTheWalkIsAlreadyInside()
    {
        // The subtler shape: the cycle closes on an ordinary directory part way down
        // rather than on the root. Tracking only where LINKS had reached let this one
        // through once before it closed, because `deep` was never recorded — it was a
        // plain directory. Placing every directory by where it physically is stops it
        // where it starts.
        Directory.CreateDirectory(Path.Combine(_workspace, "deep"));
        File.WriteAllText(Path.Combine(_workspace, "deep", "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "deep", "loop"), Path.Combine(_workspace, "deep"));

        WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldBe(["deep/app.cs"]);
    }

    [Fact]
    public void TwoNamesForOneDirectoryIndexItOnce()
    {
        // `alias-a` and `alias-b` are the same directory, and so is `real`. Keying on the
        // name gives three copies of its content; keying on where it physically is gives
        // one, which is the call "one file, one source" already makes for two sources over
        // one tree.
        //
        // `real` is the survivor, not whichever the OS listed first. A file's identity is
        // its relative path, so an order that changed between runs would move every file
        // under it and make the next refresh delete and re-add the lot.
        Directory.CreateDirectory(Path.Combine(_workspace, "real"));
        File.WriteAllText(Path.Combine(_workspace, "real", "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "alias-a"), Path.Combine(_workspace, "real"));
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "alias-b"), Path.Combine(_workspace, "real"));

        WorkspaceWalker.Walk(_workspace, true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldBe(["real/app.cs"]);
    }

    [Fact]
    public void ARootThatIsItselfALinkRecognisesItsOwnRoot()
    {
        // A source created on `inner`, a link to the workspace. Seeding the walk with the
        // link's own spelling meant it did not recognise the directory it had started in
        // when it met it again, and yielded a second copy of everything.
        File.WriteAllText(Path.Combine(_workspace, "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "inner"), _workspace);

        WorkspaceWalker.Walk(Path.Combine(_workspace, "inner"), true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldBe(["app.cs"]);
    }

    [Fact]
    public void ADuplicateUnderALinkedRootIsStillOneCopy()
    {
        // The identity has to hold when the ROOT is a link to somewhere else. Children are
        // spelled `entry/...` while the root is `real`, so a canonicaliser that refused to
        // work outside its root fell back to the lexical path for every one of them and no
        // two names ever matched — the dedupe silently did nothing under such a root.
        Directory.CreateDirectory(Path.Combine(_workspace, "real", "content"));
        File.WriteAllText(Path.Combine(_workspace, "real", "content", "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "real", "alias"), Path.Combine(_workspace, "real", "content"));
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "entry"), Path.Combine(_workspace, "real"));

        WorkspaceWalker.Walk(Path.Combine(_workspace, "entry"), true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldBe(["content/app.cs"]);
    }

    [Fact]
    public void ARootReachedThroughAnAliasedAncestorIsStillPlacedPhysically()
    {
        // `entry -> real`, and the source is `entry/src`. The last component is an
        // ordinary directory, so resolving only that one leaves the root lexical and every
        // link under it reads as outside. The ancestors have to be resolved too.
        Directory.CreateDirectory(Path.Combine(_workspace, "real", "src", "shared"));
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "entry"), Path.Combine(_workspace, "real"));

        WorkspaceDiscovery.Canonical(Path.Combine(_workspace, "entry", "src"))
            .ShouldBe(Path.Combine(Path.GetFullPath(_workspace), "real", "src"));
    }

    [Fact]
    public void AnIgnoreFileUnderAnAliasedRootIsStillRead()
    {
        // The ignore-file containment check was left on the lexical root while the
        // directory and file walk moved to the physical one. A `.gitignore` that is itself
        // a link, under an aliased root, then reads as outside and its rules are dropped —
        // silently, which for an ignore file means indexing what someone excluded.
        //
        // The ignore file is a link on purpose: a plain file is inside by construction and
        // never reaches the containment test at all.
        Directory.CreateDirectory(Path.Combine(_workspace, "real"));
        File.WriteAllText(Path.Combine(_workspace, "real", "rules"), "secret.txt\n");
        File.CreateSymbolicLink(
            Path.Combine(_workspace, "real", ".gitignore"), Path.Combine(_workspace, "real", "rules"));
        File.WriteAllText(Path.Combine(_workspace, "real", "secret.txt"), "excluded");
        File.WriteAllText(Path.Combine(_workspace, "real", "app.cs"), "class A {}");
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "entry"), Path.Combine(_workspace, "real"));

        WorkspaceWalker.Walk(Path.Combine(_workspace, "entry"), true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldNotContain("secret.txt");
    }

    [Fact]
    public void AnInwardLinkUnderALinkedRootIsStillFollowed()
    {
        // The walk tests containment against the physical root, `real`, but on Linux this
        // link's target comes back as `entry/shared`. Unresolved, it reads as outside.
        //
        // `shared` is ignored so the link is the only route to the file. Reachable under
        // both names, it arrives once either way (refused, or followed and deduped), and
        // the test would pass without the fix.
        Directory.CreateDirectory(Path.Combine(_workspace, "real", "shared"));
        File.WriteAllText(Path.Combine(_workspace, "real", "shared", "app.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_workspace, "real", ".gitignore"), "shared/\n");
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "real", "link"),
            Path.Combine(_workspace, "entry", "shared"));
        Directory.CreateSymbolicLink(
            Path.Combine(_workspace, "entry"), Path.Combine(_workspace, "real"));

        WorkspaceWalker.Walk(Path.Combine(_workspace, "entry"), true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath)
            .ShouldContain("link/app.cs");
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
