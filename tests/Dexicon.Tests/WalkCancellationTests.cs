using System.Text.RegularExpressions;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The token of a caller reaches the walk it started. The walk polls it for each directory and every 256 files
/// (<see cref="WorkspaceWalker.Walk"/>), so a caller that passes <c>default</c> or <c>CancellationToken.None</c> has a
/// walk of a large tree that keeps running after its job was cancelled or its lease was lost. The analyzer that
/// requires a token to be forwarded (CA2016) accepts an explicit <c>None</c>, so the call sites are read here.
/// </summary>
public sealed partial class WalkCancellationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    // The calls whose last argument is the token, with the file each is in.
    private static readonly (string File, string Call)[] Calls =
    [
        ("src/Dexicon.Core/Indexing/CorpusIndexer.cs", "WorkspaceDiscovery.Walk("),
        ("src/Dexicon.Core/Indexing/CorpusSweeper.cs", "WorkspaceDiscovery.Walk("),
        ("src/Dexicon.Core/Indexing/WorkspaceDiscovery.cs", "WorkspaceWalker.Walk("),
        ("src/Dexicon.Core/Indexing/SourceCoverage.cs", "WorkspaceWalker.Walk("),
        ("src/Dexicon/Api/CorpusEndpoints.cs", "SourceCoverage.Find("),
        ("src/Dexicon/Mcp/DexiconTools.cs", "SourceCoverage.Find("),
    ];

    /// <summary>The text of the call's argument list, up to its closing parenthesis.</summary>
    private static string ArgumentsOf(string source, string call)
    {
        var start = source.IndexOf(call, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var depth = 1;
        var at = start + call.Length;
        while (at < source.Length && depth > 0)
        {
            if (source[at] == '(') depth++;
            else if (source[at] == ')') depth--;
            at++;
        }

        return source[(start + call.Length)..(at - 1)];
    }

    [Fact]
    public void EveryCallThatStartsAWalkGivesItTheCallersToken()
    {
        var root = RepoRoot();
        var read = 0;

        foreach (var (file, call) in Calls)
        {
            var arguments = ArgumentsOf(File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))), call);
            arguments.ShouldNotBeEmpty($"{call} was not found in {file}, so this test reads nothing there");
            read++;

            // `ct` as the last argument, named or not, and neither a default nor None anywhere in the call.
            TheLastArgument().IsMatch(arguments).ShouldBeTrue($"{call} in {file} ends with `{arguments.Trim()}`");
            arguments.ShouldNotContain("default", Case.Sensitive, $"{call} in {file}");
            arguments.ShouldNotContain("CancellationToken.None", Case.Sensitive, $"{call} in {file}");
        }

        read.ShouldBe(Calls.Length);
    }

    [GeneratedRegex(@"(?:ct: )?\bct\s*$")]
    private static partial Regex TheLastArgument();

    [Fact]
    public void ACoverageCheckStopsOnACancelledToken()
    {
        var root = Directory.CreateTempSubdirectory("dexicon-cancel-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "books", "a"));
            Directory.CreateDirectory(Path.Combine(root, "books", "b"));
            File.WriteAllText(Path.Combine(root, "books", "loose.md"), "text");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Should.Throw<OperationCanceledException>(() => SourceCoverage.Find(
                root,
                [new SourceCoverage.SourceRoot("books/a", 262_144), new SourceCoverage.SourceRoot("books/b", 262_144)],
                ct: cts.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
