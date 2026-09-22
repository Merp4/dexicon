using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// `.git/info/exclude`, git's per-clone ignore file.
///
/// It holds what a working copy excludes without the repository saying so, which is where
/// anything that adds directories to someone's checkout puts them — `git worktree`, and
/// the editors and agents that create worktrees inside the repository. The walk read
/// `.gitignore` and not this, so those directories were indexed.
///
/// Reported against a checkout with four worktrees: 22,004 files walked to 5,463 tracked
/// ones, and search returning the same document at two older commits. That is worse than
/// noise. A hit from a stale copy carries a real path and a real line and says something
/// that stopped being true.
/// </summary>
public sealed class LocalGitExcludesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gitexclude-{Guid.NewGuid():N}");

    public LocalGitExcludesTests() => Directory.CreateDirectory(_root);

    private void Write(string relative, string content = "hello")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private List<string> Walk(bool useGitignore = true) =>
        [.. WorkspaceWalker.Walk(_root, useGitignore, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public void AWorktreeExcludedOnlyLocallyIsNotIndexed()
    {
        Write("src/app.cs");
        Write(".claude/worktrees/feature/src/app.cs", "an older copy of the same file");
        Write(".git/info/exclude", "**/.claude/worktrees/\n");

        Walk().ShouldBe(["src/app.cs"]);
    }

    [Fact]
    public void ACheckoutWithNoLocalExcludesIsUnaffected()
    {
        // The common case: the file is absent, or it is the stock one git writes, which is
        // nothing but comments.
        Write("src/app.cs");
        Write(".git/info/exclude", "# git ls-files --others --exclude-from=.git/info/exclude\n# *.[oa]\n");

        Walk().ShouldBe(["src/app.cs"]);
    }

    [Fact]
    public void GitignoreOutranksIt()
    {
        // Git's precedence: a pattern in .gitignore beats one in info/exclude, so a
        // negation there re-includes what the local file dropped. Reversing the two reads
        // as a detail until someone's !generated/api.ts stops working.
        Write("generated/api.ts");
        Write(".git/info/exclude", "generated/\n");
        Write(".gitignore", "!generated/\n");

        Walk().ShouldContain("generated/api.ts");
    }

    [Fact]
    public void ASourceThatDoesNotHonourGitignoreDoesNotHonourThisEither()
    {
        // One setting, and it says whether git decides what is indexed. Reading half of
        // git's answer when it is off would make `useGitignore: false` mean something
        // nobody could state.
        Write("src/app.cs");
        Write(".claude/worktrees/feature/src/app.cs");
        Write(".git/info/exclude", "**/.claude/worktrees/\n");

        Walk(useGitignore: false)
            .ShouldBe([".claude/worktrees/feature/src/app.cs", "src/app.cs"]);
    }

    [Fact]
    public void ALinkedWorktreesGitFileIsNotFollowed()
    {
        // In a linked worktree `.git` is a file pointing at a gitdir outside the tree.
        // Following it would read a file the source root does not contain, which is the
        // boundary the walk holds everywhere else. The pointer is left alone and the walk
        // does not fail over it.
        //
        // It is also not indexed. `.git/` in the always-exclude list matches directories
        // only, so before this the pointer was a candidate like any other text file — and
        // what it holds is `gitdir: <absolute host path>`, which is the one thing a
        // payload must never carry (docs/03).
        Write("src/app.cs");
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: ../../.git/worktrees/feature\n");

        Walk().ShouldBe(["src/app.cs"]);
    }

    [Fact]
    public void ASubmodulesGitFileIsNotIndexedEither()
    {
        // Same file, one level down, where the any-depth match is what catches it.
        Write("src/app.cs");
        Write("vendor/lib/README.md");
        File.WriteAllText(Path.Combine(_root, "vendor", "lib", ".git"),
            "gitdir: ../../.git/modules/vendor/lib\n");

        Walk().ShouldBe(["src/app.cs", "vendor/lib/README.md"]);
    }

    [Fact]
    public void AnExcludeFileThatIsALinkOutOfTheRootIsNotRead()
    {
        // `File.Exists` and `File.ReadAllLines` both follow a link, so a workspace that
        // carries one has the walk read a host file from inside a read-only mount. That is
        // the boundary EnumerateFilesSafely holds while it descends, and this reads a path
        // it never descended to.
        //
        // The patterns being ignorable rather than returnable is not the point: the read
        // is the boundary crossing, and a file that says `*` would empty the index.
        Write("src/app.cs");
        var outside = Path.Combine(Path.GetTempPath(), $"gitexclude-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "exclude"), "src/\n");

        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        File.CreateSymbolicLink(Path.Combine(_root, ".git", "info", "exclude"),
                                Path.Combine(outside, "exclude"));

        try { Walk().ShouldBe(["src/app.cs"]); }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void AGitDirectoryThatIsALinkOutOfTheRootIsNotRead()
    {
        Write("src/app.cs");
        var outside = Path.Combine(Path.GetTempPath(), $"gitexclude-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(outside, "info"));
        File.WriteAllText(Path.Combine(outside, "info", "exclude"), "src/\n");

        Directory.CreateSymbolicLink(Path.Combine(_root, ".git"), outside);

        try { Walk().ShouldBe(["src/app.cs"]); }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void ASourceRootedBelowTheRepositoryReadsNeitherFile()
    {
        // A source at `repo/docs` has no `.git` of its own, so the repository's local
        // excludes are not read for it — exactly as its `.gitignore` is not. Stated
        // because a rule with no written boundary gets one invented at the first hard
        // case.
        Write("repo/.git/info/exclude", "docs/notes.md\n");
        Write("repo/.gitignore", "docs/notes.md\n");
        Write("repo/docs/notes.md");

        var docs = Path.Combine(_root, "repo", "docs");
        var files = WorkspaceWalker.Walk(docs, useGitignore: true, null, null, 1_000_000)
            .Files.Select(f => f.RelativePath).ToList();

        files.ShouldBe(["notes.md"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
