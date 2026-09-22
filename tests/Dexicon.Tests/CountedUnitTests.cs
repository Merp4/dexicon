using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Mcp;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What a corpus's count is counting, on the agent-facing surface.
///
/// A git-history source's units are commits. A corpus made only of them reporting "201
/// files indexed" contradicts the source summary the same agent can read, and it is the
/// kind of contradiction a model has no way to resolve: it will believe one of them.
/// </summary>
public sealed class CountedUnitTests
{
    private static SourceSummary Source(SourceKind kind) => new(
        Id: "s", Kind: kind.ToString().ToLowerInvariant(), RootPath: "repo",
        UseGitignore: true, MaxFileBytes: 1024, IncludeGlobs: [], ExcludeGlobs: []);

    [Fact]
    public void FilesWhereThereIsNoHistorySource()
    {
        DexiconTools.UnitFor([Source(SourceKind.Workspace)], 2).ShouldBe("files");
        DexiconTools.UnitFor([Source(SourceKind.Upload)], 1).ShouldBe("file");

        // The overwhelmingly common case, and it has to stay exactly as it was.
        DexiconTools.UnitFor([], 5).ShouldBe("files");
    }

    [Fact]
    public void CommitsWhereEverySourceIsHistory()
    {
        DexiconTools.UnitFor([Source(SourceKind.GitHistory)], 201).ShouldBe("commits");
        DexiconTools.UnitFor([Source(SourceKind.GitHistory)], 1).ShouldBe("commit");
    }

    [Fact]
    public void DocumentsWhereItIsBoth()
    {
        // Counting two different things at once, so neither word is true of the total.
        DexiconTools.UnitFor([Source(SourceKind.Workspace), Source(SourceKind.GitHistory)], 9)
            .ShouldBe("documents");
    }
}
