using Dexicon.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Embedding;

/// <summary>
/// Turns text into vectors, for whichever provider and model a chunk set names.
///
/// A thin layer over <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> rather than a
/// replacement for it. What it adds is the behaviour the rest of Dexicon depends on and
/// the abstraction does not provide:
///
///   - batching under <c>MaxConcurrency</c>, with results in INPUT order
///   - jittered retry, because a retry storm turns a slow embedder into an absent one
///   - dimension probing, cached per target and shared across requests
///   - <see cref="EmbeddingUnavailableException"/>, which search catches to degrade to
///     keyword and SAY SO rather than return quietly worse results
/// </summary>
public interface IEmbeddingService
{
    /// <param name="purpose">
    /// Whether this text is being indexed or searched with. Most embedding models are
    /// trained with a task instruction wrapped around the input and retrieve measurably
    /// worse without it, so the framing is applied here rather than at the call sites:
    /// the caller knows which it has, and nothing else does. Passing it as an argument
    /// makes it impossible to forget at one of the two places that embed text.
    /// </param>
    /// <summary>
    /// How many tokens the model actually made of <paramref name="text"/>, or null when
    /// the provider does not say.
    /// </summary>
    /// <remarks>
    /// This is the model's OWN tokenizer, not an estimate and not a tokenizer we ship.
    /// Shipping one means a vocabulary per model, versioned, for models that are pulled at
    /// runtime and may not exist yet, so the only tokenizer that can be right for an
    /// arbitrary model is the one inside it. Ollama returns `prompt_eval_count` on an
    /// embed call; providers that report nothing get null, and callers fall back to an
    /// estimate rather than pretending.
    ///
    /// Far too slow to call per chunk. It exists to CALIBRATE: the ratio is measured once
    /// per model by the probe, and three times per file by TextDensity for a file that is
    /// about to be chunked, after which the chunker counts characters. Three calls against
    /// a file that costs hundreds of embeds is affordable; one per chunk is not.
    /// </remarks>
    Task<int?> CountTokensAsync(EmbeddingTarget target, string text, CancellationToken ct = default);

    /// <param name="source">
    /// What this batch of inputs is, for the log. Every caller that embeds documents does
    /// so a file at a time, so one label describes the whole batch exactly and no guessing
    /// about which input it was is needed.
    ///
    /// It exists because the over-long warning named no file. A run reporting 123 of them
    /// said 123 chunks had their tails dropped and gave no way to find out whose, which is
    /// the difference between a number and a defect someone can act on.
    /// </param>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs,
        string? source = null, CancellationToken ct = default);

    /// <summary>Ask the model its dimensionality. Cached; costs one short embed on a miss.</summary>
    Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default);

    /// <summary>Dimensionality if already known, 0 otherwise. Never makes a call.</summary>
    int KnownDimensions(EmbeddingTarget target);
}

/// <remarks>
/// Not sealed, so <see cref="EmbeddingInputTooLongException"/> can be one of these. Every
/// caller that copes with an embed not happening already catches this type, and a refusal
/// that was a sibling instead reached none of them.
/// </remarks>
public class EmbeddingUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// An input was longer than the model's context, and the provider refused it.
///
/// A KIND of <see cref="EmbeddingUnavailableException"/>, caught ahead of it by anyone who
/// can act on the difference: it is not a fault and not transient, the same input fails the
/// same way every time, and the answer is to divide the text rather than retry it. Only the
/// caller knows how, so the refusal is reported rather than absorbed.
///
/// It is a subtype rather than a sibling because everything that already handled an embed
/// not happening was written against the base type and silently stopped covering the
/// refusal: SearchService fell back to keyword search and instead returned 500 on an
/// over-long query, ModelProbe read the refusal as evidence a model does not truncate
/// silently and instead aborted on exactly the models that behave best, and the indexer
/// skipped the file and instead failed the whole job. None of them had to change.
///
/// It is also the one exact statement about token limits available to this process. Ollama
/// exposes no tokenizer, and a local one cannot be shown to match the model that is loaded,
/// so this is what sizing is built on rather than guarded against. See D-31.
/// </summary>
public sealed class EmbeddingInputTooLongException(string message, Exception? inner = null)
    : EmbeddingUnavailableException(message, inner);

public sealed class EmbeddingService(
    IEmbeddingGeneratorFactory factory,
    IModelProfiles profiles,
    IOptions<DexiconOptions> options,
    Indexing.IndexingLimits limits,
    IMemoryCache cache,
    ILogger<EmbeddingService> log) : IEmbeddingService
{
    private readonly EmbeddingOptions _embedding = options.Value.Embedding;
    private readonly OllamaOptions _ollama = options.Value.Ollama;

    /// <summary>A model's dimensionality does not change; the TTL only covers a restart.</summary>
    private static readonly TimeSpan DimensionsTtl = TimeSpan.FromHours(1);

    private static string DimensionsKey(EmbeddingTarget t) => $"embed-dims::{t.Provider}::{t.Model}";

    public int KnownDimensions(EmbeddingTarget target) =>
        cache.TryGetValue(DimensionsKey(target), out int dims) ? dims : 0;

    public async Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default)
    {
        // In the SHARED cache, not in a field. This service is registered per request, so
        // instance state cannot survive one. /healthz used to do a real embedding
        // round-trip on every fifteen-second poll because of exactly that, which timed out
        // under indexing load and painted the dependency dots red during normal work.
        if (cache.TryGetValue(DimensionsKey(target), out int cached) && cached > 0) return cached;

        // Raw: a dimension probe is a measurement of the model, not a document.
        var vectors = await EmbedAsync(target, EmbedPurpose.Raw, ["dimension probe"], ct: ct);
        var dimensions = vectors[0].Length;

        cache.Set(DimensionsKey(target), dimensions, DimensionsTtl);
        log.LogInformation("{Target} produces {Dimensions}-dimension vectors", target, dimensions);
        return dimensions;
    }

    public async Task<int?> CountTokensAsync(
        EmbeddingTarget target, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var generator = factory.GeneratorFor(target);
        var options = new EmbeddingGenerationOptions { ModelId = target.Model };

        try
        {
            var embeddings = await generator.GenerateAsync([text], options, ct);
            // Deliberately not retried. A token count is a measurement, and a measurement
            // that cannot be taken is absent rather than urgent.
            return (int?)embeddings.Usage?.InputTokenCount;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not count tokens with {Target}", target);
            return null;
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs,
        string? source = null, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(target.Model))
            throw new ArgumentException("An embedding model name is required.", nameof(target));

        // Applied once, here. Both sides of a retrieval have to agree: a document embedded
        // with `search_document:` and a query embedded raw land in a less aligned space,
        // and the result is not an error but a quietly worse ranking.
        if (purpose != EmbedPurpose.Raw)
        {
            var templates = await profiles.ForAsync(target, ct);
            if (!templates.IsRaw)
                inputs = [.. inputs.Select(text => templates.Apply(purpose, text))];
        }

        var generator = factory.GeneratorFor(target);
        var batches = inputs.Chunk(Math.Max(1, _embedding.BatchSize)).ToList();
        var results = new float[batches.Count][][];

        // The endpoint's limit, shared by every caller, not one constructed here per
        // call. Built per call it bounded this batch set and nothing else, so two
        // corpora indexing at once sent twice the configured number and the setting
        // described neither. A corpus indexing alone still gets all of it.
        var gate = limits.EmbeddingFor(target);

        await Task.WhenAll(batches.Select(async (batch, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Indexed, not appended: results must come back in INPUT order, and
                // parallel completion says nothing about order. A chunk paired with
                // its neighbour's vector is an unfalsifiable search-quality bug.
                results[index] = await GenerateAsync(generator, target, batch, source, ct);
            }
            finally { gate.Release(); }
        }));

        var all = new List<float[]>(inputs.Count);
        foreach (var batch in results) all.AddRange(batch);
        return all;
    }

    private async Task<float[][]> GenerateAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingTarget target, string[] batch, string? source, CancellationToken ct)
    {
        // ModelId per call. This is the whole reason a generator is cached per provider
        // instead of per model: a chunk set picks its model at runtime and stores it in
        // the catalogue, so nothing resolved at startup can know about it.
        var generationOptions = Options(target, truncate: false);

        var retryNow = false;

        Exception? last = null;
        for (var attempt = 0; attempt <= _ollama.MaxRetries; attempt++)
        {
            if (attempt > 0 && !retryNow)
            {
                // Jittered backoff. A retry storm against a struggling embedder is how a
                // slow embedding service becomes an unavailable one.
                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) + Random.Shared.Next(0, 250));
                await Task.Delay(delay, ct);
            }

            retryNow = false;

            try
            {
                var embeddings = await generator.GenerateAsync(batch, generationOptions, ct);
                if (embeddings.Count == 0)
                    throw new EmbeddingUnavailableException($"{target} returned no embeddings.");

                return [.. embeddings.Select(e => e.Vector.ToArray())];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // the caller gave up; not a provider failure and not retryable
            }
            catch (Exception ex) when (IsTooLong(ex))
            {
                // The input is longer than the model's context. Ollama would silently
                // shorten it and return a vector for text nobody chose, so we ask it not
                // to; this is that refusal arriving.
                //
                // Reported, not absorbed. This used to re-embed with truncate:true, which
                // stored a vector for the opening of a chunk under the chunk's own id: the
                // tail became unreachable by meaning and nothing downstream could tell.
                // The caller owns the text and can divide it, so the refusal goes to the
                // caller. It is also the only exact statement about token limits available
                // here, and costs about 350 ms flat whatever the input size, so it is
                // cheap enough to be the mechanism rather than the last resort. See D-31.
                // Named, for the same reason the warning it replaces was: a run reporting
                // 123 of these and naming no file gave nobody anything to act on.
                throw new EmbeddingInputTooLongException(
                    $"{target}: {source ?? "an unnamed input"} is longer than the model's "
                    + $"context. Longest of {batch.Length} input(s): "
                    + $"{batch.Max(b => b.Length):N0} chars.",
                    ex);
            }
            catch (Exception ex)
            {
                // Intentionally broad. Each provider SDK throws its own exception types
                // (HttpRequestException, ClientResultException, RequestFailedException)
                // and a catch list goes out of date the moment a provider
                // is added. Cancellation is separated out above; everything else here is
                // "the embedder did not answer", which is one condition with one response.
                last = ex;
                log.LogWarning(ex, "Embedding attempt {Attempt}/{Max} failed for {Target}",
                    attempt + 1, _ollama.MaxRetries + 1, target);
            }
        }

        throw new EmbeddingUnavailableException(
            $"Embedding failed after {_ollama.MaxRetries + 1} attempts against {target}: {last?.Message}", last);
    }

    /// <summary>
    /// Ask the provider to refuse over-long input rather than shorten it.
    ///
    /// Ollama's <c>/api/embed</c> truncates the end of anything past the context window and
    /// returns a vector, with nothing in the response to say it happened
    /// (ollama/ollama#14259). That is the failure this project exists to avoid: the missing
    /// text is reported as indexed, search never matches it, and nothing anywhere is red.
    ///
    /// The key is ignored by providers that do not know it, so this needs no branch on
    /// which one is in use.
    /// </summary>
    private static EmbeddingGenerationOptions Options(EmbeddingTarget target, bool truncate) =>
        new()
        {
            ModelId = target.Model,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["truncate"] = truncate },
        };

    /// <summary>
    /// Whether a provider refused because the input was longer than its context.
    ///
    /// Matched on the message, because the SDKs give no code for it and each provider
    /// words it differently. A miss here costs a retry that fails the same way, not a
    /// wrong answer, which is the right direction for a guess to be wrong in.
    /// </summary>
    private static bool IsTooLong(Exception ex)
    {
        var message = ex.Message;

        return message.Contains("context length", StringComparison.OrdinalIgnoreCase)
            || message.Contains("context window", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum context", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too long", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
                && message.Contains("token", StringComparison.OrdinalIgnoreCase));
    }
}
