using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Qdrant.Client.Grpc;

namespace Dexicon.Tests;

/// <summary>
/// The filter the vector store builds for <c>pathPrefix</c>. The value reaches Qdrant verbatim as a text
/// condition on the keyword-indexed <c>file_path</c>, which Qdrant evaluates as a match on whole words: it
/// splits the value at every character that is not a letter or a digit and matches a path that holds each of
/// them, in any order and anywhere, ignoring case (measured on v1.20.1, see docs/05-search.md). Every
/// description of the argument says so, and this pins the one part of it the repository controls: the
/// condition is a text match on that field and nothing narrows it to a prefix.
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

    private static IEnumerable<FieldCondition> Conditions(SearchQuery query) =>
        QdrantVectorStore.BuildFilter(query).Must.Select(c => c.Field).Where(f => f is not null);

    [Theory]
    [InlineData("src/Auth/")]
    [InlineData("Auth")]
    [InlineData("src/Auth.Tests")]
    public void APathPrefixIsATextConditionOnTheFilePathAsGiven(string value)
    {
        var condition = Conditions(Query(value)).Where(f => f.Key == "file_path").ShouldHaveSingleItem();

        condition.Match.MatchValueCase.ShouldBe(Match.MatchValueOneofCase.Text);
        condition.Match.Text.ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankPathPrefixAddsNoCondition(string? value) =>
        Conditions(Query(value)).Where(f => f.Key == "file_path").ShouldBeEmpty();
}
