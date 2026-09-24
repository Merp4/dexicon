using Dexicon.Core.Indexing;
using Dexicon.Mcp;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A file outside every source root is not skipped, not failed and not counted: it is
/// absent, and absence has no row anywhere. The case these tests are built from is real. A
/// library had sources <c>manuals/AI</c>, <c>manuals/dotnet</c> and eight more siblings, and one
/// book sitting directly in <c>manuals/</c>. A keyword search on that book's exact title
/// returned four other books, which reads like a ranking result rather than a gap.
///
/// The reported figure has to stay quiet on the ordinary case or it will be ignored, so
/// most of these assert about what is NOT reported.
/// </summary>
public sealed class SourceCoverageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dexicon-coverage-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Write(string relative, string content = "text")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private IReadOnlyList<SourceCoverage.Gap> Find(params string[] sourceRoots) =>
        SourceCoverage.Find(_root, sourceRoots.Select(r => new SourceCoverage.SourceRoot(r, 262_144)));

    [Fact]
    public void AFileLooseAmongTheIndexedSiblingsIsReported()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/An Invented Handbook.txt");

        var gaps = Find("books/manuals/AI", "books/manuals/dotnet");

        gaps.Count.ShouldBe(1);
        gaps[0].DirectoryRelativePath.ShouldBe("books/manuals");
        gaps[0].Files.ShouldBe(["An Invented Handbook.txt"]);
    }

    [Fact]
    public void ADirectoryWithOnlyOneSourceUnderItSaysNothingAboutThatDirectory()
    {
        // The repository indexes docs/ and does not index its own README. One source is
        // not evidence that the parent was meant to be covered, and reporting it here
        // would put a false line on every corpus that indexes a single folder.
        Write("docs/guide.md");
        Write("README.md");

        Find("docs").ShouldBeEmpty();
    }

    [Fact]
    public void TheRuleIsPerDirectoryAndNotPerCorpus()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/loose-a.md");
        Write("papers/2024/two.md");
        Write("papers/loose-b.md");

        // Two sources in the corpus, one under each parent. Counting sources across the
        // corpus rather than per directory would report both of these on no evidence.
        Find("books/manuals/AI", "papers/2024").ShouldBeEmpty();
    }

    [Fact]
    public void ADirectoryThatIsItselfASourceIsNotAGap()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/loose.md");

        // manuals is indexed in its own right, so loose.md is already covered. The two
        // deeper sources are redundant, not a gap.
        Find("books/manuals", "books/manuals/AI", "books/manuals/dotnet").ShouldBeEmpty();
    }

    [Fact]
    public void AnAncestorSourceCoversTheDirectoryToo()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/loose.md");

        Find("books", "books/manuals/AI", "books/manuals/dotnet").ShouldBeEmpty();
    }

    [Fact]
    public void OnlyTheDirectoryItselfIsLookedAtAndNotWhatIsBelowIt()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/loose.md");
        // Under a source, so already indexed. A recursive walk would report both of these
        // and bury the one file that matters.
        Write("books/manuals/AI/deep.md");
        Write("books/manuals/AI/nested/deeper.md");

        var gaps = Find("books/manuals/AI", "books/manuals/dotnet");

        gaps.Count.ShouldBe(1);
        gaps[0].Files.ShouldBe(["loose.md"]);
    }

    [Fact]
    public void AFileNoSourceWouldHaveIndexedIsNotReported()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/cover.png");                  // always-exclude
        Write("books/manuals/empty.md", string.Empty);     // empty file
        Write("books/manuals/blob.md", "text\0more");      // binary sniff

        // Reporting a file that would not be indexed even with a source on it turns the
        // check into a list of everything on disk.
        var gaps = Find("books/manuals/AI", "books/manuals/dotnet");

        gaps.ShouldBeEmpty();
    }

    [Fact]
    public void AGitignoreInTheDirectoryIsObeyed()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/.gitignore", "notes.md\n");
        Write("books/manuals/notes.md");

        var gaps = Find("books/manuals/AI", "books/manuals/dotnet");

        // notes.md is gone. The ignore file itself stays, because a source over this
        // directory would have indexed it: the check reports what would have been indexed,
        // not what is interesting.
        gaps.Count.ShouldBe(1);
        gaps[0].Files.ShouldBe([".gitignore"]);
    }

    [Fact]
    public void ALocalGitExcludeInTheDirectoryIsObeyedToo()
    {
        // The check reports what would have been indexed, and that now includes git's
        // per-clone ignore file. Without this it would name a worktree's files as a gap
        // and tell the user to add a source over them.
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/.git/info/exclude", "notes.md\n");
        Write("books/manuals/notes.md");

        Find("books/manuals/AI", "books/manuals/dotnet").ShouldBeEmpty();
    }

    [Fact]
    public void ADocumentOverTheCodeCapIsStillReported()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/big.pdf", new string('x', 300_000));

        // A PDF gets the document cap, not the 256 KB code cap. Applying the code cap here
        // would silently drop the exact kind of file this check exists for.
        var gaps = Find("books/manuals/AI", "books/manuals/dotnet");

        gaps.Count.ShouldBe(1);
        gaps[0].Files.ShouldBe(["big.pdf"]);
    }

    [Fact]
    public void SourcesUnderDifferentParentsAreJudgedSeparately()
    {
        Write("books/manuals/AI/one.md");
        Write("books/manuals/dotnet/two.md");
        Write("books/manuals/loose-a.md");
        Write("papers/2024/three.md");
        Write("papers/2025/four.md");
        Write("papers/loose-b.md");

        var gaps = Find("books/manuals/AI", "books/manuals/dotnet", "papers/2024", "papers/2025");

        gaps.Select(g => g.DirectoryRelativePath).ShouldBe(["books/manuals", "papers"]);
        gaps[0].Files.ShouldBe(["loose-a.md"]);
        gaps[1].Files.ShouldBe(["loose-b.md"]);
    }

    [Fact]
    public void AnEmptyOrAbsentRootPathIsNotTreatedAsAPath()
    {
        Write("loose.md");
        Write("docs/guide.md");
        Write("src/code.md");

        // An upload source has no root path at all, and a workspace source pointed at the
        // root has the empty one. Neither should be read as a directory named "".
        SourceCoverage.Find(_root, [
            new SourceCoverage.SourceRoot(null, 262_144),
            new SourceCoverage.SourceRoot(null, 262_144),
        ]).ShouldBeEmpty();

        // The root itself as a source covers everything below it.
        SourceCoverage.Find(_root, [
            new SourceCoverage.SourceRoot("", 262_144),
            new SourceCoverage.SourceRoot("docs", 262_144),
            new SourceCoverage.SourceRoot("src", 262_144),
        ]).ShouldBeEmpty();
    }

    [Fact]
    public void ASourceRootThatNoLongerExistsDoesNotThrow()
    {
        Write("books/manuals/AI/one.md");

        // books/manuals/dotnet was removed from disk but its row is still in the catalogue.
        Find("books/manuals/AI", "books/manuals/dotnet").ShouldBeEmpty();
    }

    [Fact]
    public void ACleanCorpusAddsNothingToIndexStatus()
    {
        // index_status is read on every turn an agent checks its bearings. A section that
        // appears when there is nothing to say is a section that gets skipped when there is.
        DexiconTools.RenderCoverage([]).ShouldBeEmpty();
    }

    [Fact]
    public void TheReportNamesTheDirectoryTheFilesAndWhatToDo()
    {
        var text = DexiconTools.RenderCoverage([
            new SourceCoverage.Gap("books/manuals", ["An Invented Handbook.pdf"]),
        ]);

        text.ShouldContain("NOT INDEXED");
        text.ShouldContain("books/manuals");
        text.ShouldContain("An Invented Handbook.pdf");
        // Without this an agent reads the line as a statistic rather than as something to fix.
        text.ShouldContain("Add a source on books/manuals");
    }

    [Fact]
    public void ALongListIsTruncatedRatherThanFillingTheAgentsContext()
    {
        var files = Enumerable.Range(1, 12).Select(n => $"book-{n:00}.pdf").ToList();

        var text = DexiconTools.RenderCoverage([new SourceCoverage.Gap("books/manuals", files)]);

        text.ShouldContain("12 file(s)");
        text.ShouldContain("book-05.pdf");
        text.ShouldNotContain("book-06.pdf");
        text.ShouldContain("and 7 more");
    }

    [Fact]
    public void TheWorkspaceRootIsNamedRatherThanLeftBlank()
    {
        var text = DexiconTools.RenderCoverage([new SourceCoverage.Gap("", ["README.md"])]);

        text.ShouldContain("the workspace root");
        text.ShouldNotContain("in  are");
    }
}
