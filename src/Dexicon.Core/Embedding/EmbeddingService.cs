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
    /// worse without it, so the framing is applied HERE rather than at the call sites —
    /// the caller knows which it has, and nothing else does. Passing it as an argument
    /// makes it impossible to forget at one of the two places that embed text.
    /// </param>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs,
        CancellationToken ct = default);

    /// <summary>Ask the model its dimensionality. Cached; costs one short embed on a miss.</summary>
    Task<int> ProbeDimensionsAsync(EmbeddingTarget target, CancellationToken ct = default);

    /// <summary>Dimensionality if already known, 0 otherwise. Never makes a call.</summary>
    int KnownDimensions(EmbeddingTarget target);
}

public sealed class EmbeddingUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class EmbeddingService(
    IEmbeddingGeneratorFactory factory,
    IModelProfiles profiles,
    IOptions<DexiconOptions> options,
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
        // instance state cannot survive one — /healthz used to do a real embedding
        // round-trip on every fifteen-second poll because of exactly that, which timed out
        // under indexing load and painted the dependency dots red during normal work.
        if (cache.TryGetValue(DimensionsKey(target), out int cached) && cached > 0) return cached;

        // Raw: a dimension probe is a measurement of the model, not a document.
        var vectors = await EmbedAsync(target, EmbedPurpose.Raw, ["dimension probe"], ct);
        var dimensions = vectors[0].Length;

        cache.Set(DimensionsKey(target), dimensions, DimensionsTtl);
        log.LogInformation("{Target} produces {Dimensions}-dimension vectors", target, dimensions);
        return dimensions;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        EmbeddingTarget target, EmbedPurpose purpose, IReadOnlyList<string> inputs,
        CancellationToken ct = default)
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

        var gate = new SemaphoreSlim(Math.Max(1, _embedding.MaxConcurrency));
        try
        {
            await Task.WhenAll(batches.Select(async (batch, index) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    // Indexed, not appended: results must come back in INPUT order, and
                    // parallel completion says nothing about order. A chunk paired with
                    // its neighbour's vector is an unfalsifiable search-quality bug.
                    results[index] = await GenerateAsync(generator, target, batch, ct);
                }
                finally { gate.Release(); }
            }));
        }
        finally { gate.Dispose(); }

        var all = new List<float[]>(inputs.Count);
        foreach (var batch in results) all.AddRange(batch);
        return all;
    }

    private async Task<float[][]> GenerateAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingTarget target, string[] batch, CancellationToken ct)
    {
        // ModelId per call. This is the whole reason a generator is cached per provider
        // instead of per model: a chunk set picks its model at runtime and stores it in
        // the catalogue, so nothing resolved at startup can know about it.
        var generationOptions = new EmbeddingGenerationOptions { ModelId = target.Model };

        Exception? last = null;
        for (var attempt = 0; attempt <= _ollama.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Jittered backoff. A retry storm against a struggling embedder is how a
                // slow embedding service becomes an unavailable one.
                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) + Random.Shared.Next(0, 250));
                await Task.Delay(delay, ct);
            }

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
            catch (Exception ex)
            {
                // Deliberately broad. Each provider SDK throws its own exception types —
                // HttpRequestException, ClientResultException, RequestFailedException —
                // and a catch list is a list that goes out of date the moment a provider
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
}
