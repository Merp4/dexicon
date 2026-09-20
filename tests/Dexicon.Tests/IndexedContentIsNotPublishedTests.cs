using System.Text.RegularExpressions;

namespace Dexicon.Tests;

/// <summary>
/// What Dexicon indexes does not belong in what Dexicon publishes.
///
/// This repository is public; the libraries it is pointed at are not. Illustrative
/// examples had been taken from a real private shelf, so comments, docs and fixtures
/// named a publisher, a third-party repository and a real book title — none of which
/// carried any of the points they were making, and any of which reads as an association
/// the project has not made.
///
/// The rule is in CONTRIBUTING.md. This is the part that holds it, because a rule in a
/// document is a rule nobody runs.
///
/// It is names, not numbers. "a 1,834-document corpus" and "an intact 84 MB PDF at
/// 13.4s" are measurements worth keeping and say nothing about whose shelf they came
/// from.
///
/// This file is the one place the forbidden strings may appear, and it excludes itself
/// below. Any tool that rewrites them across the tree has to exclude it too, or it
/// inverts the rule into forbidding the replacements.
/// </summary>
public sealed class IndexedContentIsNotPublishedTests
{
    /// <summary>
    /// What must not reappear. Deliberately specific: a general "looks like a book
    /// title" rule would fire on prose and be turned off within a week.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] Forbidden =
    [
        ("a publisher's shorthand", new Regex(@"(?<![A-Za-z0-9])o" + "rly(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("a real book title", new Regex("Logic For " + "Dummies", RegexOptions.IgnoreCase)),
        ("a third-party repository", new Regex(@"(?<![A-Za-z0-9])t" + "pn(-pdfs)?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)),
        ("another project of the author's", new Regex("mcp" + "toolbox", RegexOptions.IgnoreCase)),
        ("a path from a real mount", new Regex("AI" + "Books", RegexOptions.IgnoreCase)),
    ];

    private static readonly string[] Extensions =
        [".cs", ".md", ".ts", ".tsx", ".py", ".json", ".yml", ".yaml", ".props", ".sh", ".ps1", ".example"];

    private static readonly string[] SkipDirectories =
        ["obj", "bin", "node_modules", ".git", "dist", "generated", ".claude", "TestResults"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static List<string> TrackedTextFiles(string root) =>
    [
        .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => SkipDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)))
    ];

    [Fact]
    public void NoIndexedContentNamesAnywhereInTheRepository()
    {
        var root = RepoRoot();
        var files = TrackedTextFiles(root);

        // The scan asserts its own reach before it reports a clean result. The first
        // version of this sweep ran from a worktree living under a `.claude` directory
        // and tested every part of the ABSOLUTE path against the skip list, so it read
        // nothing and reported zero occurrences — which is what real success looks like.
        files.Count.ShouldBeGreaterThan(100,
            "the filter is wrong, not the tree: a scan that reads nothing always passes");

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.EndsWith(nameof(IndexedContentIsNotPublishedTests) + ".cs", StringComparison.Ordinal))
                continue;   // the patterns themselves

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (var (name, pattern) in Forbidden)
                    if (pattern.IsMatch(lines[i]))
                        offenders.Add($"{relative}:{i + 1} — {name}");
        }

        offenders.ShouldBeEmpty(
            "this repository is public and the content it indexes is not. Use an invented "
            + "name; keep the measurement, drop what identifies the library. See "
            + $"CONTRIBUTING.md.\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// The near-misses that exist on purpose. `poorly` is ordinary English and
    /// `manualsx` is a fixture proving one path is not a prefix of another; a substring
    /// rule that caught either would be turned off rather than fixed.
    /// </summary>
    [Fact]
    public void OrdinaryWordsContainingTheseFragmentsAreLeftAlone()
    {
        var publisher = Forbidden.Single(f => f.Name == "a publisher's shorthand").Pattern;

        publisher.IsMatch("poorly written").ShouldBeFalse();
        publisher.IsMatch("manualsx is not inside manuals").ShouldBeFalse();
        publisher.IsMatch("o" + "rly/AI").ShouldBeTrue();
        publisher.IsMatch("books/o" + "rly").ShouldBeTrue();
    }
}
