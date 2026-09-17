using Dexicon.Core.Embedding;

namespace Dexicon.Tests;

/// <summary>
/// Every model in Ollama's embedding category, and what Dexicon does with it.
///
/// Task framing is not optional and not visible: send raw text to a model that was trained
/// with `search_document:` and nothing fails — retrieval is simply worse, and worse by a
/// different amount per model, which also makes comparing two models meaningless. So the
/// failure mode this guards is silent by construction, and the only defence is checking
/// the list against the library rather than against memory.
///
/// The list was read off ollama.com/search?c=embedding on 2026-09-17 — the whole category,
/// twelve models, not a selection. It is a snapshot and will go stale: a model added
/// tomorrow gets raw framing and a UI that says so, which is the designed fallback. What
/// this test pins is that nothing in the list was covered by ACCIDENT, and that a future
/// edit cannot quietly drop one.
/// </summary>
public class ModelProfileCoverageTests
{
    /// <param name="Pulls">Downloads on 2026-09-17, so the list stays ordered by what people use.</param>
    public record Model(string Name, string Pulls, bool NameSaysEmbed, string? Family);

    public static readonly Model[] OllamaEmbeddingLibrary =
    [
        new("nomic-embed-text", "86M", true, "nomic-bert"),
        new("mxbai-embed-large", "14.7M", true, "bert"),
        new("bge-m3", "6.7M", false, "bert"),
        new("qwen3-embedding", "4M", true, null),
        new("all-minilm", "3.6M", false, "bert"),
        new("snowflake-arctic-embed", "3.1M", true, "bert"),
        new("embeddinggemma", "2.1M", true, null),
        new("paraphrase-multilingual", "941.2K", false, "bert"),
        new("nomic-embed-text-v2-moe", "906.6K", true, "nomic-bert"),
        new("snowflake-arctic-embed2", "452.4K", true, "bert"),
        new("granite-embedding", "369.1K", true, null),
        new("bge-large", "285.6K", false, "bert"),
    ];

    public static TheoryData<string> EveryModel()
    {
        var data = new TheoryData<string>();
        foreach (var m in OllamaEmbeddingLibrary) data.Add(m.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void Every_model_in_the_library_has_a_deliberate_framing(string model)
    {
        // Not "has a template" — being embedded raw is a legitimate answer for a symmetric
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
    public void A_tagged_name_resolves_to_the_same_framing(string model)
    {
        // Ollama serves "bge-large:latest" and "bge-large:335m"; a corpus stores whichever
        // was typed. Framing that depended on the tag would differ between the corpus and
        // the query against it.
        var profiles = new ModelProfiles(null!, null!);

        profiles.Suggest($"{model}:latest").ShouldBe(profiles.Suggest(model));
        profiles.Suggest($"{model}:335m").ShouldBe(profiles.Suggest(model));
    }

    [Fact]
    public void The_two_Arctic_generations_are_framed_differently()
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
    public void A_model_nobody_has_heard_of_gets_no_framing_rather_than_a_guess()
    {
        // The fallback that makes runtime-added models safe: an unknown model is embedded
        // raw and the UI says so, instead of inheriting a neighbour's prefix by name match.
        new ModelProfiles(null!, null!).Suggest("some-new-embedder-2027").ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void Every_model_in_the_library_survives_the_listing_filter(string model)
    {
        // A model with a perfect profile that never appears in the picker is not usable.
        // Four of the twelve carry no "embed" in their name, so this asserts the family
        // check is doing the work it is relied on for.
        var entry = Array.Find(OllamaEmbeddingLibrary, m => m.Name == model)!;

        Dexicon.Api.ModelNames.LooksLikeAnEmbeddingModel($"{model}:latest", entry.Family)
            .ShouldBeTrue($"'{model}' would be filtered out of the model picker.");
    }

    [Fact]
    public void A_chat_model_is_still_filtered_out()
    {
        // The filter has to keep earning its place: offering a chat model turns a bad pick
        // into a 503 at index time.
        Dexicon.Api.ModelNames.LooksLikeAnEmbeddingModel("qwen3.5:4b", "qwen3").ShouldBeFalse();
        Dexicon.Api.ModelNames.LooksLikeAnEmbeddingModel("llama3:8b", "llama").ShouldBeFalse();
    }
}
