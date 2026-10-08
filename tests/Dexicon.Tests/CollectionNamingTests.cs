using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Vectors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// Which collection a chunk set lands in.
///
/// A collection IS a vector space. Two names for one model mean two collections holding
/// vectors that belong together, neither aware of the other, and the multitenancy design
/// rests on corpus_id partitioning ONE collection per (provider, model, dimensions).
///
/// Ollama lists `embeddinggemma:latest`; a configuration file says `embeddinggemma`. The
/// UI's model picker offers the provider's tagged names, so a corpus created there would
/// not have shared a collection with one created from the configured default. This
/// repository's own Qdrant still holds both `dexicon__ollama__embeddinggemma-latest__768`
/// and `dexicon__ollama__embeddinggemma__768` from before that was noticed.
/// </summary>
public class CollectionNamingTests
{
    private static QdrantVectorStore Store() =>
        new(Options.Create(new DexiconOptions()), NullLogger<QdrantVectorStore>.Instance);

    private static string Name(string provider, string model, int dimensions = 768) =>
        Store().CollectionNameFor(new EmbeddingTarget(provider, model), dimensions);

    [Fact]
    public void ATaggedLatestLandsInTheSameCollectionAsTheUntaggedName()
    {
        Name("ollama", "embeddinggemma:latest").ShouldBe(Name("ollama", "embeddinggemma"));
    }

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("mxbai-embed-large")]
    [InlineData("bge-m3")]
    public void TheSameHoldsForEveryModelAProviderMightTag(string model)
    {
        Name("ollama", $"{model}:latest").ShouldBe(Name("ollama", model));
    }

    [Fact]
    public void ARealVersionTagIsADifferentModelAndKeepsItsOwnCollection()
    {
        // The line this must not cross. `:latest` is an alias; `:v1.5` and `:0.6b` are
        // different weights producing different vectors, and merging them would corrupt
        // the space quietly.
        Name("ollama", "nomic-embed-text:v1.5").ShouldNotBe(Name("ollama", "nomic-embed-text"));
        Name("ollama", "qwen3-embedding:0.6b").ShouldNotBe(Name("ollama", "qwen3-embedding"));
    }

    [Fact]
    public void TwoProvidersServingOneModelNameStayApart()
    {
        // Pre-existing behaviour worth keeping: the same name from OpenAI and from Ollama
        // is not the same vector space.
        Name("ollama", "embeddinggemma").ShouldNotBe(Name("openai", "embeddinggemma"));
    }

    [Fact]
    public void DifferentDimensionsStayApart()
    {
        Name("ollama", "bge-m3", 1024).ShouldNotBe(Name("ollama", "bge-m3", 768));
    }

    [Fact]
    public void TheNameIsStillALegalQdrantCollectionName()
    {
        var name = Name("ollama", "embeddinggemma:latest");

        name.ShouldBe("dexicon__ollama__embeddinggemma__768");
        name.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');
    }

    [Fact]
    public void CaseDoesNotCreateASecondCollection()
    {
        Name("ollama", "EmbeddingGemma:LATEST").ShouldBe(Name("ollama", "embeddinggemma"));
    }
}
