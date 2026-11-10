using Dexicon.Core.Embedding;

namespace Dexicon.Tests;

/// <summary>
/// Every model in Ollama's embedding category, and what Dexicon does with it.
///
/// Task framing is not optional and not visible: send raw text to a model that was trained
/// with `search_document:` and nothing fails; retrieval is worse, and worse by a
/// different amount per model, which also makes comparing two models meaningless. So the
/// failure mode this guards is silent by construction, and the only defence is checking
/// the list against the library rather than against memory.
///
/// The list was read off ollama.com/search?c=embedding on 2026-11-10: the whole category,
/// fourteen entries, thirteen of them embedding models and one a decision model (see
/// <see cref="DecisionModelsInTheCategory"/>). The previous reading, 2026-09-17, held twelve;
/// the additions are `embeddinggemma-2` and `laya`. It is a snapshot and will go stale: a model added
/// tomorrow gets raw framing and a UI that says so, which is the designed fallback. What
/// this test pins is that nothing in the list was covered by ACCIDENT, and that a future
/// edit cannot quietly drop one.
/// </summary>
public class ModelProfileCoverageTests
{
    /// <param name="Pulls">Downloads on 2026-11-10, so the list stays ordered by what people use.</param>
    public record Model(string Name, string Pulls, bool NameSaysEmbed, string? Family);

    public static readonly Model[] OllamaEmbeddingLibrary =
    [
        new("nomic-embed-text", "96.9M", true, "nomic-bert"),
        new("mxbai-embed-large", "16.8M", true, "bert"),
        new("bge-m3", "8.3M", false, "bert"),
        new("qwen3-embedding", "4.5M", true, null),
        new("all-minilm", "3.9M", false, "bert"),
        new("snowflake-arctic-embed", "3.2M", true, "bert"),
        new("embeddinggemma", "2.8M", true, null),
        new("nomic-embed-text-v2-moe", "1.3M", true, "nomic-bert"),
        new("paraphrase-multilingual", "964.3K", false, "bert"),
        new("snowflake-arctic-embed2", "496.9K", true, "bert"),
        new("granite-embedding", "424.1K", true, null),
        new("bge-large", "304.8K", false, "bert"),
        new("embeddinggemma-2", "147.2K", true, null),
    ];

    /// <summary>
    /// Listed in the category and not an embedding model. `laya` is a 421M decision model fine-tuned
    /// from ModernBERT-large, served through Ollama's decision API (questions with criteria, answers
    /// per question) and not through `/api/embed`. Giving it a framing would suggest it can index a
    /// corpus, so it has none and is exempt from the rule the other entries are held to.
    ///
    /// The listing filter does not exclude it: its family is `modern-bert`, which contains "bert".
    /// That is the filter's known limit (D-45), not something this list asserts.
    /// </summary>
    public static readonly Model[] DecisionModelsInTheCategory =
    [
        new("laya", "24.5K", false, "modern-bert"),
    ];

    public static TheoryData<string> EveryModel()
    {
        var data = new TheoryData<string>();
        foreach (var m in OllamaEmbeddingLibrary) data.Add(m.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void EveryModelInTheLibraryHasADeliberateFraming(string model)
    {
        // Not "has a template": being embedded raw is a legitimate answer for a symmetric
        // sentence-transformers model. What must not happen is a model falling through to
        // raw because nobody looked, which is indistinguishable at runtime from raw because
        // someone checked the card and decided.
        var profile = new ModelProfiles(null!, null!).Suggest(model);

        profile.ShouldNotBeNull($"'{model}' is in Ollama's embedding library with no built-in profile. " +
                                "Read its model card and add one — raw is fine, silence is not.");
        profile.Origin.ShouldBe(TemplateOrigin.BuiltIn);
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void ATaggedNameResolvesToTheSameFraming(string model)
    {
        // Ollama serves "bge-large:latest" and "bge-large:335m"; a corpus stores whichever
        // was typed. Framing that depended on the tag would differ between the corpus and
        // the query against it.
        var profiles = new ModelProfiles(null!, null!);

        profiles.Suggest($"{model}:latest").ShouldBe(profiles.Suggest(model));
        profiles.Suggest($"{model}:335m").ShouldBe(profiles.Suggest(model));
    }

    [Fact]
    public void TheTwoArcticGenerationsAreFramedDifferently()
    {
        // v1 wants "Represent this sentence…", v2 wants "query: ". They differ by one
        // character in the model name, and using either prefix on the other generation is
        // wrong in a way no error reports. v2 shipped as raw until a real check caught it.
        var profiles = new ModelProfiles(null!, null!);

        var v1 = profiles.Suggest("snowflake-arctic-embed").ShouldNotBeNull();
        var v2 = profiles.Suggest("snowflake-arctic-embed2").ShouldNotBeNull();

        v1.Query.ShouldContain("Represent this sentence");
        v2.Query.ShouldStartWith("query: ");
        v1.Query.ShouldNotBe(v2.Query);

        // Both frame the query only. Prefixing documents too would embed the instruction
        // into every chunk in the corpus.
        v1.Document.ShouldBe("{text}");
        v2.Document.ShouldBe("{text}");
    }

    [Fact]
    public void ADecisionModelInTheCategoryIsGivenNoFraming()
    {
        // The exemption is as load-bearing as the rule. Without it the next reader either adds
        // a framing for a model that cannot index anything, or deletes the coverage rule to
        // make the list pass.
        var profiles = new ModelProfiles(null!, null!);

        foreach (var model in DecisionModelsInTheCategory)
            profiles.Suggest(model.Name).ShouldBeNull(
                $"'{model.Name}' is a decision model, not an embedder; a framing would imply it can index text.");
    }

    [Fact]
    public void TheTwoEmbeddingGemmaGenerationsShareFramingUnderSeparateKeys()
    {
        // Ollama names the second generation `embeddinggemma-2`, hyphenated, so `Family` leaves it
        // alone and it needs its own key. The card's Document and SearchQuery prompts are the first
        // generation's strings; if they ever diverge, this is where the difference gets decided.
        var profiles = new ModelProfiles(null!, null!);

        var v1 = profiles.Suggest("embeddinggemma").ShouldNotBeNull();
        var v2 = profiles.Suggest("embeddinggemma-2").ShouldNotBeNull();

        v2.Document.ShouldBe("title: none | text: {text}");
        v2.Query.ShouldBe("task: search result | query: {text}");
        v2.ShouldBe(v1);
        profiles.Suggest("embeddinggemma-2:270m").ShouldBe(v2);
        profiles.Suggest("embeddinggemma-2:q8_0").ShouldBe(v2);
    }

    [Fact]
    public void AModelNobodyHasHeardOfGetsNoFramingRatherThanAGuess()
    {
        // The fallback that makes runtime-added models safe: an unknown model is embedded
        // raw and the UI says so, instead of inheriting a neighbour's prefix by name match.
        new ModelProfiles(null!, null!).Suggest("some-new-embedder-2027").ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void EveryModelInTheLibrarySurvivesTheListingFilter(string model)
    {
        // A model with a perfect profile that never appears in the picker is not usable.
        // Four of the thirteen carry no "embed" in their name, so this asserts the family
        // check is doing the work it is relied on for.
        var entry = Array.Find(OllamaEmbeddingLibrary, m => m.Name == model)!;

        Dexicon.Api.ModelNames.LooksLikeAnEmbeddingModel($"{model}:latest", entry.Family)
            .ShouldBeTrue($"'{model}' would be filtered out of the model picker.");
    }

    [Fact]
    public void AChatModelIsStillFilteredOut()
    {
        // The filter has to keep earning its place: offering a chat model turns a bad pick
        // into a 503 at index time.
        Dexicon.Api.ModelNames.LooksLikeAnEmbeddingModel("qwen3.5:4b", "qwen3").ShouldBeFalse();
        Dexicon.Api.ModelNames.LooksLikeAnEmbeddingModel("llama3:8b", "llama").ShouldBeFalse();
    }
}
