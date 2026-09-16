using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dexicon.Core.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Embedding;

/// <summary>
/// Turns text into dense vectors. The seam that keeps a different provider a
/// registration rather than a rewrite — nothing above this knows about Ollama.
/// </summary>
public interface IEmbeddingProvider
{
    string Model { get; }

    /// <summary>Dimensionality, discovered on first use and cached. 0 until then.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Embed with the configured default model. For anything that belongs to a chunk set,
    /// use the overload that names the model.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);

    /// <summary>
    /// Embed with a NAMED model.
    ///
    /// The model is a per-call argument rather than something baked into a client at
    /// registration, because chunk sets choose their model at runtime, in the UI, and
    /// store it in the catalogue. Anything resolved from configuration at startup —
    /// keyed DI included — cannot see a set created after the process began, and would
    /// either fail to resolve or quietly serve a different model than the one asked for.
    /// Ollama takes the model in the request body, so there is nothing to bind anyway.
    ///
    /// The bug this closes: EmbedAsync used to read the globally configured model and
    /// ignore its caller, so a set pinned to mxbai-embed-large filled an mxbai collection
    /// with nomic vectors. Nothing errors; the results are simply wrong.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs,
        CancellationToken ct = default);

    /// <summary>Probe the model and learn its dimensionality. Called at startup and before a rebuild.</summary>
    Task<int> ProbeDimensionsAsync(string model, CancellationToken ct = default);

    /// <summary>
    /// The models this Ollama has actually pulled. Used by the UI so choosing a model is
    /// a list rather than a typing exercise — a typo previously surfaced as a 503 at
    /// corpus creation with no hint of what the legal values were.
    /// </summary>
    Task<IReadOnlyList<AvailableModel>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Pull a model, reporting progress as it downloads. Models run to gigabytes, so this
    /// streams rather than blocking a request for several minutes with nothing to show.
    /// </summary>
    IAsyncEnumerable<ModelPullProgress> PullModelAsync(string model, CancellationToken ct = default);

    /// <summary>Remove a pulled model from the Ollama instance.</summary>
    Task DeleteModelAsync(string model, CancellationToken ct = default);
}

public sealed class EmbeddingUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <param name="Dimensions">
/// Known only for models this instance has already probed. Probing every listed model to
/// fill it in would mean an embedding round-trip per model on every page load, so it is
/// left null and resolved when a model is actually chosen.
/// </param>
public sealed record AvailableModel(string Name, long SizeBytes, string? Family, int? Dimensions);

/// <param name="Status">Ollama's own words — "pulling manifest", "verifying sha256digest", "success".</param>
public sealed record ModelPullProgress(string Status, long Completed, long Total)
{
    public int Percent => Total > 0 ? (int)(100 * Completed / Total) : 0;
    public bool Done => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);
}

public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _embedding;
    private readonly OllamaOptions _ollama;
    private readonly ILogger<OllamaEmbeddingProvider> _log;
    private readonly IMemoryCache _cache;
    private int _dimensions;

    /// <summary>A model's dimensionality does not change; the TTL only guards a restart of Ollama.</summary>
    private static readonly TimeSpan DimensionsTtl = TimeSpan.FromHours(1);

    public OllamaEmbeddingProvider(
        HttpClient http,
        IOptions<DexiconOptions> options,
        IMemoryCache cache,
        ILogger<OllamaEmbeddingProvider> log)
    {
        _http = http;
        _cache = cache;
        _embedding = options.Value.Embedding;
        _ollama = options.Value.Ollama;
        _log = log;
        _http.BaseAddress = new Uri(_ollama.Endpoint.TrimEnd('/') + "/");
        _http.Timeout = _ollama.Timeout;
    }

    public string Model => _embedding.Model;

    /// <summary>Last known dimensionality, from the shared cache — 0 if never probed.</summary>
    public int Dimensions =>
        _dimensions > 0 ? _dimensions
        : _cache.TryGetValue(DimensionsKey(_embedding.Model), out int cached) ? cached
        : 0;

    public async Task<int> ProbeDimensionsAsync(string model, CancellationToken ct = default)
    {
        // Cached in the shared memory cache, not in this instance.
        //
        // The provider is registered with AddHttpClient, so it is created PER REQUEST:
        // instance state cannot cache anything across calls. /healthz therefore did a
        // real embedding round-trip on every poll — wasteful always, and actively
        // misleading while indexing saturates Ollama, because the poll would time out
        // and the UI would paint both dependency dots red during normal work.
        //
        // Keyed by model: a model change must re-probe, since dimensions are the whole
        // point of the call.
        if (_cache.TryGetValue(DimensionsKey(model), out int cached) && cached > 0)
        {
            _dimensions = cached;
            return cached;
        }

        var vectors = await PostEmbedAsync(model, ["dimension probe"], ct);
        _dimensions = vectors[0].Length;

        _cache.Set(DimensionsKey(model), _dimensions, DimensionsTtl);
        _log.LogInformation("Embedding model {Model} produces {Dimensions}-dimension vectors", model, _dimensions);
        return _dimensions;
    }

    private static string DimensionsKey(string model) => $"embed-dims::{model}";

    public async IAsyncEnumerable<ModelPullProgress> PullModelAsync(
        string model, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // No timeout. _http carries the configured Ollama timeout, which is sized for an
        // embedding call; a multi-gigabyte download is a different order of magnitude and
        // would be cancelled part-way through every time.
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new PullRequest(model, Stream: true)),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // NDJSON: one status object per line, not a JSON array.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;

            PullStatus? status;
            try { status = JsonSerializer.Deserialize<PullStatus>(line); }
            catch (JsonException) { continue; }   // a partial line is not a failure

            if (status?.Status is null) continue;
            if (status.Error is { Length: > 0 } error)
                throw new EmbeddingUnavailableException($"Ollama could not pull '{model}': {error}");

            yield return new ModelPullProgress(status.Status, status.Completed, status.Total);
        }
    }

    public async Task DeleteModelAsync(string model, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "api/delete")
        {
            Content = JsonContent.Create(new DeleteRequest(model)),
        };

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new EmbeddingUnavailableException(
            $"Ollama returned {(int)response.StatusCode} deleting '{model}': {Truncate(body, 300)}");
    }

    public async Task<IReadOnlyList<AvailableModel>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = await _http.GetFromJsonAsync<TagsResponse>("api/tags", ct);
            if (payload?.Models is null) return [];

            return [.. payload.Models
                .Select(m => new AvailableModel(
                    m.Name,
                    m.Size,
                    m.Details?.Family,
                    // Free: the dimensionality of anything already probed is in the cache.
                    _cache.TryGetValue(DimensionsKey(m.Name), out int d) ? d : null))
                .OrderBy(m => m.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new EmbeddingUnavailableException(
                $"Could not list models from {_ollama.Endpoint}: {ex.Message}", ex);
        }
    }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
        EmbedAsync(_embedding.Model, inputs, ct);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs,
        CancellationToken ct = default)
    {
        if (inputs.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("An embedding model name is required.", nameof(model));

        var batches = inputs.Chunk(_embedding.BatchSize).ToList();
        var results = new float[batches.Count][][];

        // MaxConcurrency is honoured HERE. It was configured, documented in
        // .env.example and passed by compose while nothing read it — the batches ran
        // strictly sequentially, so raising it changed nothing at all. Measured on a
        // 437-page PDF: ~32 chunks per 18 s single-threaded on CPU Ollama.
        var gate = new SemaphoreSlim(Math.Max(1, _embedding.MaxConcurrency));

        await Task.WhenAll(batches.Select(async (batch, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Indexed, not appended: results must come back in input order, and
                // parallel completion says nothing about order.
                results[index] = [.. await PostEmbedAsync(model, batch, ct)];
            }
            finally { gate.Release(); }
        }));

        gate.Dispose();

        var all = new List<float[]>(inputs.Count);
        foreach (var batch in results) all.AddRange(batch);

        if (_dimensions == 0 && all.Count > 0) _dimensions = all[0].Length;
        return all;
    }

    private async Task<IReadOnlyList<float[]>> PostEmbedAsync(string model, string[] inputs, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= _ollama.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Jittered backoff. A retry storm against a struggling Ollama is how a
                // slow embedding service becomes an unavailable one.
                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) + Random.Shared.Next(0, 250));
                await Task.Delay(delay, ct);
            }

            try
            {
                using var resp = await _http.PostAsJsonAsync("api/embed", new EmbedRequest(model, inputs), ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    throw new EmbeddingUnavailableException(
                        $"Ollama returned {(int)resp.StatusCode} for model '{model}': {Truncate(body, 300)}");
                }

                var payload = await resp.Content.ReadFromJsonAsync<EmbedResponse>(ct);
                if (payload?.Embeddings is null || payload.Embeddings.Count == 0)
                    throw new EmbeddingUnavailableException($"Ollama returned no embeddings for model '{model}'.");

                return payload.Embeddings;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or EmbeddingUnavailableException
                                       or JsonException)
            {
                last = ex;
                _log.LogWarning(ex, "Embedding attempt {Attempt}/{Max} failed for model {Model}",
                    attempt + 1, _ollama.MaxRetries + 1, model);
            }
        }

        throw new EmbeddingUnavailableException(
            $"Embedding failed after {_ollama.MaxRetries + 1} attempts against {_ollama.Endpoint}: {last?.Message}", last);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] List<float[]> Embeddings);

    private sealed record PullRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record DeleteRequest(
        [property: JsonPropertyName("model")] string Model);

    private sealed record PullStatus(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("completed")] long Completed,
        [property: JsonPropertyName("total")] long Total,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record TagsResponse(
        [property: JsonPropertyName("models")] List<TagModel>? Models);

    private sealed record TagModel(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("details")] TagDetails? Details);

    private sealed record TagDetails(
        [property: JsonPropertyName("family")] string? Family);
}
