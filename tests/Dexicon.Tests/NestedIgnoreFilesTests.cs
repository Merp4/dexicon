using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// An ignore file in a subdirectory.
///
/// The walk read the one at the source root and no other, so a file a developer excluded
/// two levels down was indexed anyway. Measured against the code this replaces:
///
///   sub/.gitignore contains "secret.txt"  ->  sub/secret.txt IS returned by Walk
///
/// The patterns in such a file say what they mean relative to the file, so they are
/// anchored to its directory as they go in. `secret.txt` in `sub/.gitignore` is
/// `sub/**/secret.txt`: it reaches every depth beneath `sub`, and no sibling of `sub`.
/// </summary>
public sealed class NestedIgnoreFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nested-{Guid.NewGuid():N}");

    public NestedIgnoreFilesTests() => Directory.CreateDirectory(_root);

    private void Write(string relative, string content = "hello")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private List<string> Walk(bool useGitignore = true, string[]? exclude = null, string[]? include = null) =>
        [.. WorkspaceWalker.Walk(_root, useGitignore, include, exclude, 1_000_000)
            .Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public void ASubdirectorysOwnFileIsRead()
    {
        Write("sub/.gitignore", "secret.txt\n");
        Write("sub/secret.txt");
        Write("sub/keep.txt");

        Walk().ShouldBe(["sub/.gitignore", "sub/keep.txt"]);
    }

    [Fact]
    public void ItCannotReachASibling()
    {
        // The whole of the anchoring. Without it `secret.txt` is a root-level pattern that
        // matches at any depth, so one directory's file quietly governs the tree.
        Write("sub/.gitignore", "secret.txt\n");
        Write("sub/secret.txt");
        Write("other/secret.txt");

        Walk().ShouldBe(["other/secret.txt", "sub/.gitignore"]);
    }

    [Fact]
    public void ItReachesEveryDepthBeneathItsOwnDirectory()
    {
        // A pattern with no slash applies at every level, and for a nested file "every
        // level" starts at that file's directory.
        Write("sub/.gitignore", "secret.txt\n");
        Write("sub/deep/deeper/secret.txt");
        Write("sub/deep/keep.txt");

        Walk().ShouldBe(["sub/.gitignore", "sub/deep/keep.txt"]);
    }

    [Fact]
    public void AnAnchoredPatternStopsAtItsOwnDirectory()
    {
        // `/secret.txt` means this directory's, not every one below it.
        Write("sub/.gitignore", "/secret.txt\n");
        Write("sub/secret.txt");
        Write("sub/deep/secret.txt");

        Walk().ShouldBe(["sub/.gitignore", "sub/deep/secret.txt"]);
    }

    [Fact]
    public void ADirectoryOnlyPatternWorksFromWhereItWasWritten()
    {
        Write("sub/.gitignore", "cache/\n");
        Write("sub/cache/a.txt");
        Write("sub/deep/cache/b.txt");
        Write("cache/c.txt");

        Walk().ShouldBe(["cache/c.txt", "sub/.gitignore"]);
    }

    [Fact]
    public void TheDeeperFileOutranksTheShallowerOne()
    {
        // Git's precedence, and the reason a nested file is worth reading at all: a
        // subtree says something more specific than the root did.
        Write(".gitignore", "*.log\n");
        Write("sub/.gitignore", "!keep.log\n");
        Write("sub/keep.log");
        Write("sub/other.log");
        Write("top.log");

        Walk().ShouldBe([".gitignore", "sub/.gitignore", "sub/keep.log"]);
    }

    [Fact]
    public void ADeeperFileCannotBeReachedThroughAPrunedDirectory()
    {
        // Stated rather than left to be found. The decision to prune `vendor` is made with
        // the rules in force at that point, and a file inside it has not been read — it
        // cannot re-include anything because nothing will look.
        //
        // Git decides the same way, and for the same reason: it does not read ignore files
        // in a directory it has excluded. `.dexiconignore` covers the case where someone
        // wants the exception.
        Write(".gitignore", "vendor/\n");
        Write("vendor/.gitignore", "!keep.txt\n");
        Write("vendor/keep.txt");
        Write("src/app.cs");

        Walk().ShouldBe([".gitignore", "src/app.cs"]);
    }

    [Fact]
    public void NorThroughOneTheAlwaysExcludeListRemoved()
    {
        Write("node_modules/.gitignore", "!index.js\n");
        Write("node_modules/pkg/index.js");
        Write("src/app.cs");

        Walk().ShouldBe(["src/app.cs"]);
    }

    [Fact]
    public void ASourcesExcludeGlobsStillOutrankIt()
    {
        // The operator has never seen the repository's nested file. Their globs go on the
        // end of whatever set is in force, so they keep winning over what a tree says
        // about itself.
        Write("sub/.gitignore", "!generated.ts\n");
        Write("sub/generated.ts");
        Write("sub/app.ts");

        Walk(exclude: ["**/generated.ts"]).ShouldBe(["sub/.gitignore", "sub/app.ts"]);
    }

    [Fact]
    public void ASourceThatDoesNotHonourGitignoreDoesNotReadTheNestedOnesEither()
    {
        // One setting, and it says whether git decides what is indexed. Reading the nested
        // files while ignoring the root's would make `use_gitignore: false` mean something
        // nobody could state.
        Write("sub/.gitignore", "secret.txt\n");
        Write("sub/secret.txt");

        Walk(useGitignore: false).ShouldBe(["sub/.gitignore", "sub/secret.txt"]);
    }

    [Fact]
    public void ANestedDexiconignoreIsReadWhateverGitIsDoing()
    {
        // As at the root: `.dexiconignore` is Dexicon's own file and `use_gitignore` is a
        // statement about git.
        Write("sub/.dexiconignore", "generated.ts\n");
        Write("sub/generated.ts");
        Write("sub/app.ts");

        Walk(useGitignore: false).ShouldBe(["sub/.dexiconignore", "sub/app.ts"]);
    }

    [Fact]
    public void AFileThatLinksOutOfTheTreeIsNotRead()
    {
        // `File.ReadAllLines` follows a link, so one pointing out of the tree reads a host
        // file from inside a read-only mount. The same boundary the walk holds while it
        // descends, and that `.git/info/exclude` already gets.
        Write("sub/app.ts");
        Write("sub/secret.txt");

        var outside = Path.Combine(Path.GetTempPath(), $"nested-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "rules"), "secret.txt\n");
        File.CreateSymbolicLink(Path.Combine(_root, "sub", ".gitignore"), Path.Combine(outside, "rules"));

        try
        {
            // The patterns behind it were never applied, and the link is not indexed
            // either. On Linux FileInfo.Length follows a link, so an unchecked one was
            // indexed carrying the host file's content: CI returned `sub/.gitignore`.
            Walk().ShouldBe(["sub/app.ts", "sub/secret.txt"]);

            WorkspaceWalker.Walk(_root, true, null, null, 1_000_000).SkippedFiles
                .ShouldContain(s => s.RelativePath == "sub/.gitignore"
                                 && s.Reason == WorkspaceWalker.LinkNotFollowed);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void AnOrdinaryFileThatLinksOutOfTheTreeIsNotIndexed()
    {
        // Nothing to do with ignore files: the walk checked links on directories and never
        // on files, so a link inside the tree pointing at a host file was sized, sniffed
        // with File.OpenRead and indexed, out of a read-only mount.
        Write("sub/app.ts");

        var outside = Path.Combine(Path.GetTempPath(), $"nested-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "host-secret.txt"), "not in the workspace");
        File.CreateSymbolicLink(Path.Combine(_root, "sub", "notes.txt"),
                                Path.Combine(outside, "host-secret.txt"));

        try { Walk().ShouldBe(["sub/app.ts"]); }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void AnIgnoreFileThatLinksInsideTheTreeIsNotAppliedEither()
    {
        // Git's behaviour, measured with git 2.54: a `.gitignore` that is a symbolic link
        // is not applied ("unable to access '.gitignore': Symbolic link loop"), and what it
        // names is reported as untracked rather than ignored.
        Write("sub/app.ts");
        Write("sub/secret.txt");
        Write("sub/rules", "secret.txt\n");
        File.CreateSymbolicLink(Path.Combine(_root, "sub", ".gitignore"),
                                Path.Combine(_root, "sub", "rules"));

        Walk().ShouldBe(["sub/app.ts", "sub/rules", "sub/secret.txt"]);
    }

    [Fact]
    public void ANestedNegationWithNoSlashKeepsItsWholeSubtreeWalked()
    {
        // `!keep.txt` in sub/.gitignore matches sub/deep/keep.txt, so sub/deep cannot be
        // pruned. Taking the pattern's own text as the deepest certain ancestor gave
        // LiteralPrefix `sub/keep.txt`, which matches no directory at all: sub/deep was
        // pruned and the file the negation exists to re-include was never reached.
        Write(".gitignore", "deep/\n");
        Write("sub/.gitignore", "!keep.txt\n");
        Write("sub/deep/keep.txt");
        Write("sub/deep/other.txt");

        Walk().ShouldContain("sub/deep/keep.txt");
        Walk().ShouldNotContain("sub/deep/other.txt");
    }

    [Fact]
    public void PatternsAreAnchoredWhereTheyWereWritten()
    {
        // The rule-set level, without a filesystem. `MayReincludeBeneath` reads
        // LiteralPrefix to decide whether a directory can be skipped, and a nested
        // negation must not stop the whole tree being pruned the way a root-level
        // `!*.md` does.
        var rules = new IgnoreRuleSet();
        rules.AddPatterns(["!*.md"], "nested", "sub");

        rules.IsIgnored("sub/readme.md", isDirectory: false).ShouldBeFalse();
        rules.MayReincludeBeneath("sub").ShouldBeTrue();
        rules.MayReincludeBeneath("sub/deep").ShouldBeTrue();
        rules.MayReincludeBeneath("other").ShouldBeFalse();
        rules.MayReincludeBeneath("node_modules").ShouldBeFalse();

        // A literal with no slash is the same case, and is the one that was wrong: it too
        // applies at every depth below `sub`, so the whole subtree has to stay walkable.
        var literal = new IgnoreRuleSet();
        literal.AddPatterns(["!keep.txt"], "nested", "sub");

        literal.MayReincludeBeneath("sub").ShouldBeTrue();
        literal.MayReincludeBeneath("sub/deep").ShouldBeTrue();
        literal.MayReincludeBeneath("other").ShouldBeFalse();
    }

    [Fact]
    public void ACopiedSetDoesNotChangeTheOneItCameFrom()
    {
        // Siblings keep the set their parent had; only the subtree that holds the file
        // sees its rules.
        var parent = new IgnoreRuleSet();
        parent.AddPatterns(["*.log"], "root");

        var child = new IgnoreRuleSet(parent);
        child.AddPatterns(["!keep.log"], "nested", "sub");

        parent.Count.ShouldBe(1);
        child.Count.ShouldBe(2);
        parent.IsIgnored("sub/keep.log", isDirectory: false).ShouldBeTrue();
        child.IsIgnored("sub/keep.log", isDirectory: false).ShouldBeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
