using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Qdrant.Client.Grpc;

namespace Dexicon.Tests;

/// <summary>
/// The filter the vector store builds for a search, with and without a phrase.
///
/// The store builds it once and puts it on both prefetches and on the final query, so a condition
/// added here narrows the dense candidates, the sparse candidates and the fused result alike.
/// A phrase is a <c>match.phrase</c> condition on the chunk's stored text: Qdrant 1.20 documents that
/// without a full-text index it tokenises the text with its word tokenizer and lowercases it, which
/// is why nothing has to be added to the index.
/// </summary>
public sealed class PhraseFilterTests
{
    private static SearchQuery Query(string? phrase) => new()
    {
        Text = "tokens",
        CorpusIds = ["c1"],
        ChunkSetIds = ["s1"],
        CollectionName = "col",
        Phrase = phrase,
    };

    private static IEnumerable<FieldCondition> Conditions(SearchQuery query) =>
        QdrantVectorStore.BuildFilter(query).Must.Select(c => c.Field).Where(f => f is not null);

    [Fact]
    public void APhraseIsAPhraseConditionOnTheChunkText()
    {
        var condition = Conditions(Query("refresh token")).Where(f => f.Key == "content").ShouldHaveSingleItem();

        condition.Match.MatchValueCase.ShouldBe(Match.MatchValueOneofCase.Phrase);
        condition.Match.Phrase.ShouldBe("refresh token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoPhraseAddsNothingToTheFilter(string? phrase)
    {
        // The scope conditions are the same ones a search has always sent: the kind, the corpora and the sets.
        Conditions(Query(phrase)).Select(f => f.Key).ShouldBe(["kind", "corpus_id", "chunk_set_id"]);
    }

    [Fact]
    public void APhraseLeavesTheScopeConditionsInPlace()
    {
        // The phrase is one more condition, after the scope. A filter that replaced the scope would search
        // corpora the key does not reach.
        Conditions(Query("refresh token")).Select(f => f.Key)
            .ShouldBe(["kind", "corpus_id", "chunk_set_id", "content"]);
    }
}
