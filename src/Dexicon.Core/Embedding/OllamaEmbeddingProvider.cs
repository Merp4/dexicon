using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dexicon.Core.Configuration;
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

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);

    /// <summary>Probe the model and learn its dimensionality. Called at startup and before a rebuild.</summary>
    Task<int> ProbeDimensionsAsync(string model, CancellationToken ct = default);
}

public sealed class EmbeddingUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _embedding;
    private readonly OllamaOptions _ollama;
    private readonly ILogger<OllamaEmbeddingProvider> _log;
    private int _dimensions;

    public OllamaEmbeddingProvider(
        HttpClient http,
        IOptions<DexiconOptions> options,
        ILogger<OllamaEmbeddingProvider> log)
    {
        _http = http;
        _embedding = options.Value.Embedding;
        _ollama = options.Value.Ollama;
        _log = log;
        _http.BaseAddress = new Uri(_ollama.Endpoint.TrimEnd('/') + "/");
        _http.Timeout = _ollama.Timeout;
    }

    public string Model => _embedding.Model;
    public int Dimensions => _dimensions;

    public async Task<int> ProbeDimensionsAsync(string model, CancellationToken ct = default)
    {
        var vectors = await PostEmbedAsync(model, ["dimension probe"], ct);
        _dimensions = vectors[0].Length;
        _log.LogInformation("Embedding model {Model} produces {Dimensions}-dimension vectors", model, _dimensions);
        return _dimensions;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return [];

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
                results[index] = [.. await PostEmbedAsync(_embedding.Model, batch, ct)];
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
}
