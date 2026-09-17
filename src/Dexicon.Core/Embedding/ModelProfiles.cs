using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dexicon.Core.Embedding;

/// <summary>What a piece of text is FOR. The only thing the caller knows that the model cannot infer.</summary>
public enum EmbedPurpose
{
    /// <summary>Text being indexed.</summary>
    Document = 0,

    /// <summary>Text being searched with.</summary>
    Query = 1,

    /// <summary>Neither — measuring the model itself. Never templated.</summary>
    Raw = 2,
}

/// <param name="Document">Template for indexed text. Must contain <c>{text}</c>.</param>
/// <param name="Query">Template for search queries. Must contain <c>{text}</c>.</param>
/// <param name="Origin">Where this came from, so the UI can say whether it is saved or assumed.</param>
public sealed record ModelTemplates(string Document, string Query, TemplateOrigin Origin)
{
    /// <summary>The no-op: text through unchanged.</summary>
    public static readonly ModelTemplates Raw = new("{text}", "{text}", TemplateOrigin.None);

    public bool IsRaw => Document == "{text}" && Query == "{text}";

    public string Apply(EmbedPurpose purpose, string text) => purpose switch
    {
        EmbedPurpose.Document => Document.Replace("{text}", text, StringComparison.Ordinal),
        EmbedPurpose.Query => Query.Replace("{text}", text, StringComparison.Ordinal),
        _ => text,
    };

    /// <summary>Stable identity for the chunking fingerprint.</summary>
    public string Fingerprint => IsRaw ? "raw" : $"{Document}␟{Query}";
}

public enum TemplateOrigin
{
    /// <summary>No templates: the model is embedded raw.</summary>
    None = 0,

    /// <summary>A built-in suggestion for a recognised model. Correct out of the box, overridable.</summary>
    BuiltIn = 1,

    /// <summary>A row someone saved. Always wins.</summary>
    Configured = 2,
}

/// <summary>
/// Task templates per embedding model.
///
/// Most embedding models are trained with a task instruction wrapped around the input, and
/// they are NOT optional: nomic-embed-text wants <c>search_document:</c> on indexed text
/// and <c>search_query:</c> on queries, EmbeddingGemma wants
/// <c>title: none | text: …</c> and <c>task: search result | query: …</c>. Send raw text
/// instead and nothing fails — retrieval is simply worse, and worse by different amounts
/// per model, which also makes any comparison between two models meaningless.
///
/// A TEMPLATE rather than a prefix because gemma's document form wraps the text rather
/// than preceding it. One field covers both, and whatever the next model wants.
///
/// The resolution order is the whole design, and it exists because models are added at
/// RUNTIME through the UI:
///
///   1. a saved row for this (provider, model) — always wins
///   2. a built-in suggestion for a recognised name — correct out of the box
///   3. nothing — the text goes through unchanged
///
/// Built-ins are a fallback, not the mechanism. Hard-coding <c>if (model.StartsWith("nomic"))</c>
/// would mean every model pulled after this code was written gets silently wrong framing,
/// which would quietly undo the point of being able to add models at runtime. Anything
/// built-in can be overridden by saving a row, and the UI says which of the three applies
/// so "embedded raw" is visible rather than assumed.
///
/// The table below covers every model in Ollama's embedding category as of 2026-09-17 —
/// all twelve of them, not a selection. That is a snapshot, not a contract: a model added
/// to the library tomorrow gets raw framing and a UI that says so, which is the designed
/// behaviour and the reason this is a fallback. `ModelProfileCoverageTests` pins the list
/// so a future reader can see what was checked and when.
/// </summary>
public interface IModelProfiles
{
    Task<ModelTemplates> ForAsync(EmbeddingTarget target, CancellationToken ct = default);

    /// <summary>The built-in suggestion for a name, or null. Used to pre-fill the editor.</summary>
    ModelTemplates? Suggest(string model);
}

public sealed class ModelProfiles(CatalogDbContext db, IMemoryCache cache) : IModelProfiles
{
    /// <summary>Short: a profile edit must take effect without waiting out a cache.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Known models, from their own model cards. Seeds the editor and acts as the
    /// out-of-the-box default; a saved row overrides any of it.
    ///
    /// Keyed on the family name — the part before any tag — because `nomic-embed-text`,
    /// `nomic-embed-text:latest` and `nomic-embed-text:v1.5` all want the same framing.
    /// That is the ONLY inference made from a model's name anywhere in this file.
    /// </summary>
    private static readonly Dictionary<string, ModelTemplates> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        // nomic: https://huggingface.co/nomic-ai/nomic-embed-text-v1.5 — "required, not optional"
        ["nomic-embed-text"] = new("search_document: {text}", "search_query: {text}", TemplateOrigin.BuiltIn),
        ["nomic-embed-text-v2-moe"] = new("search_document: {text}", "search_query: {text}", TemplateOrigin.BuiltIn),

        // EmbeddingGemma: https://ai.google.dev/gemma/docs/embeddinggemma — the document form
        // wraps rather than prefixes, which is why these are templates.
        ["embeddinggemma"] = new("title: none | text: {text}", "task: search result | query: {text}", TemplateOrigin.BuiltIn),

        // Instruction-aware; the instruction belongs on the QUERY side only.
        ["qwen3-embedding"] = new(
            "{text}",
            "Instruct: Given a search query, retrieve relevant passages\nQuery: {text}",
            TemplateOrigin.BuiltIn),

        // mxbai asks for a query instruction and nothing on documents.
        ["mxbai-embed-large"] = new(
            "{text}",
            "Represent this sentence for searching relevant passages: {text}",
            TemplateOrigin.BuiltIn),

        // Arctic Embed asks for a query prefix, and the two generations ask for a
        // DIFFERENT one. v2's card: "For optimal retrieval quality, use the CLS token to
        // embed each text portion and use the query prefix below (just on the query)."
        //   v1: https://huggingface.co/Snowflake/snowflake-arctic-embed-m
        //   v2: https://huggingface.co/Snowflake/snowflake-arctic-embed-l-v2.0
        // v2 was listed here as raw, on the belief that Arctic was trained without
        // prefixes. It is not, and raw framing costs recall silently — which is the exact
        // failure this whole file exists to prevent.
        ["snowflake-arctic-embed"] = new(
            "{text}",
            "Represent this sentence for searching relevant passages: {text}",
            TemplateOrigin.BuiltIn),
        ["snowflake-arctic-embed2"] = new("{text}", "query: {text}", TemplateOrigin.BuiltIn),

        // BGE v1.5 made instructions optional — "you can generate embedding without
        // instruction in all cases for convenience" — but the same card continues: "For a
        // retrieval task that uses short queries to find long related documents, it is
        // recommended to add instructions for these short queries." That is precisely what
        // Dexicon does, so the instruction is on. Override by saving a row if your corpus
        // is the other shape. https://huggingface.co/BAAI/bge-large-en-v1.5
        ["bge-large"] = new(
            "{text}",
            "Represent this sentence for searching relevant passages: {text}",
            TemplateOrigin.BuiltIn),

        // BGE-M3 genuinely takes no instruction, unlike its v1.5 siblings.
        ["bge-m3"] = ModelTemplates.Raw with { Origin = TemplateOrigin.BuiltIn },

        // Symmetric sentence-transformers models: trained on sentence pairs with no task
        // prefix at all, so a prefix would be noise in the embedding rather than framing.
        // Listed explicitly so "no template" reads as a decision rather than an omission.
        ["all-minilm"] = ModelTemplates.Raw with { Origin = TemplateOrigin.BuiltIn },
        ["paraphrase-multilingual"] = ModelTemplates.Raw with { Origin = TemplateOrigin.BuiltIn },

        // IBM Granite embedding: bi-encoder, no task instructions in the model card.
        ["granite-embedding"] = ModelTemplates.Raw with { Origin = TemplateOrigin.BuiltIn },
    };

    public async Task<ModelTemplates> ForAsync(EmbeddingTarget target, CancellationToken ct = default)
    {
        var key = $"model-templates::{target.Provider}::{target.Model}";
        if (cache.TryGetValue(key, out ModelTemplates? hit) && hit is not null) return hit;

        var saved = await db.ModelProfiles.AsNoTracking().FirstOrDefaultAsync(
            p => p.Provider == target.Provider && p.Model == target.Model, ct);

        var resolved = saved is not null
            ? new ModelTemplates(saved.DocumentTemplate, saved.QueryTemplate, TemplateOrigin.Configured)
            : Suggest(target.Model) ?? ModelTemplates.Raw;

        cache.Set(key, resolved, Ttl);
        return resolved;
    }

    public ModelTemplates? Suggest(string model) =>
        BuiltIn.TryGetValue(Family(model), out var t) ? t : null;

    /// <summary>The name without its tag: `nomic-embed-text:v1.5` -> `nomic-embed-text`.</summary>
    internal static string Family(string model)
    {
        var colon = model.IndexOf(':');
        return colon < 0 ? model : model[..colon];
    }

    /// <summary>Every model this build ships a suggestion for, for the UI to offer.</summary>
    public static IReadOnlyCollection<string> KnownModels => BuiltIn.Keys;
}
