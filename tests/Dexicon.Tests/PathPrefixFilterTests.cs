using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Qdrant.Client.Grpc;

namespace Dexicon.Tests;

/// <summary>
/// The filter the vector store builds for <c>pathPrefix</c>, which means "files under this path": a chunk matches
/// when its <c>path_prefixes</c> (the folders above its file, and the file) holds the value, normalised. A chunk
/// written before that payload existed has none, and is matched the way every chunk was, by the words of the
/// value on <c>file_path</c>, until the backfill reaches it.
/// </summary>
public sealed class PathPrefixFilterTests
{
    private static SearchQuery Query(string? pathPrefix) => new()
    {
        Text = "tokens",
        CorpusIds = ["c1"],
        ChunkSetIds = ["s1"],
        CollectionName = "col",
        PathPrefix = pathPrefix,
    };

    private static IEnumerable<Condition> PathConditions(SearchQuery query) =>
        QdrantVectorStore.BuildFilter(query).Must.Where(c => c.Filter is not null);

    [Theory]
    [InlineData("src/Auth/", "src/auth")]
    [InlineData("Auth", "auth")]
    [InlineData("./src//Auth.Tests", "src/auth.tests")]
    [InlineData("src\\Auth\\a.cs", "src/auth/a.cs")]
    [InlineData("  src/Auth  ", "src/auth")]
    public void APathPrefixIsAKeywordConditionOnTheFoldersOfTheFileWithTheValueNormalised(string value, string expected)
    {
        var either = PathConditions(Query(value)).ShouldHaveSingleItem().Filter;

        either.Should.Count.ShouldBe(2);
        var anchored = either.Should[0].Field;
        anchored.Key.ShouldBe("path_prefixes");
        anchored.Match.MatchValueCase.ShouldBe(Match.MatchValueOneofCase.Keyword);
        anchored.Match.Keyword.ShouldBe(expected);
    }

    [Fact]
    public void AChunkWithNoPathPrefixesIsMatchedByTheWordsOfTheValueAsGiven()
    {
        var either = PathConditions(Query("  src/Auth/ ")).ShouldHaveSingleItem().Filter;

        var legacy = either.Should[1].Filter;
        legacy.Must.Count.ShouldBe(2);
        legacy.Must[0].IsEmpty.Key.ShouldBe("path_prefixes");
        var words = legacy.Must[1].Field;
        words.Key.ShouldBe("file_path");
        words.Match.MatchValueCase.ShouldBe(Match.MatchValueOneofCase.Text);
        words.Match.Text.ShouldBe("src/Auth/");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/")]
    [InlineData("./")]
    [InlineData("\\")]
    public void AValueThatNamesNoPathAddsNoCondition(string? value) =>
        PathConditions(Query(value)).ShouldBeEmpty();
}
