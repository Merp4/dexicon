using System.ClientModel;
using System.Collections.Concurrent;
using Azure.AI.OpenAI;
using Dexicon.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace Dexicon.Core.Embedding;

/// <summary>Which backend and which model. What a chunk set records, in one value.</summary>
public readonly record struct EmbeddingTarget(string Provider, string Model)
{
    public override string ToString() => $"{Provider}/{Model}";

    /// <summary>
    /// The model without a redundant <c>:latest</c>.
    ///
    /// Ollama lists `embeddinggemma:latest`; a configuration file says `embeddinggemma`.
    /// They are the same model and the same vectors, and anything that treats them as two
    /// names splits one model in half. Only `:latest` goes — `:v1.5` and `:0.6b` are
    /// genuinely different models with genuinely different vectors.
    /// </summary>
    public string CanonicalModel =>
        Model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? Model[..^7] : Model;
}

/// <summary>Raised when a chunk set names a provider this deployment has not configured.</summary>
public sealed class UnknownEmbeddingProviderException(string provider, IEnumerable<string> configured)
    : Exception(
        $"No embedding provider named '{provider}' is configured. " +
        $"Configured: {string.Join(", ", configured.DefaultIfEmpty("(none)"))}. " +
        "Add it under Dexicon:Embedding:Providers, or point the chunk set at one that exists.");

/// <summary>
/// Produces an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> for a target, caching
/// one per PROVIDER rather than per model.
///
/// Per provider, not per model, because the model is a per-call argument
/// (<see cref="EmbeddingGenerationOptions.ModelId"/>) and a chunk set chooses it at
/// runtime, in the UI. Anything that binds a model at registration — keyed DI included —
/// cannot see a set created after the process started, and would either fail to resolve
/// or quietly serve a different model than the one asked for. A client is a connection
/// and a credential; a model is an argument.
/// </summary>
public interface IEmbeddingGeneratorFactory
{
    IEmbeddingGenerator<string, Embedding<float>> GeneratorFor(EmbeddingTarget target);

    /// <summary>Names of every configured provider, for error messages and the UI.</summary>
    IReadOnlyCollection<string> ProviderNames { get; }

    EmbeddingProviderOptions Options(string provider);
}

public sealed class EmbeddingGeneratorFactory : IEmbeddingGeneratorFactory, IDisposable
{
    private readonly EmbeddingOptions _embedding;
    private readonly OllamaOptions _ollama;
    private readonly ILogger<EmbeddingGeneratorFactory> _log;

    // Keyed by provider name. Generators are thread-safe and hold a connection, so one
    // each is right; building one per call would open a socket per batch.
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Owned here, so the configured Ollama timeout actually applies. OllamaApiClient built
    // from a Uri makes its own HttpClient with the default 100 seconds, which quietly
    // orphaned DEXICON__OLLAMA__TIMEOUT — a setting that exists because embedding a batch
    // on CPU Ollama was measured at ~18 seconds and a slow machine needs longer.
    private readonly ConcurrentDictionary<string, HttpClient> _httpClients =
        new(StringComparer.OrdinalIgnoreCase);

    public EmbeddingGeneratorFactory(
        IOptions<DexiconOptions> options,
        ILogger<EmbeddingGeneratorFactory> log)
    {
        _embedding = options.Value.Embedding;
        _ollama = options.Value.Ollama;
        _log = log;
    }

    public IReadOnlyCollection<string> ProviderNames => _embedding.Providers.Keys;

    public EmbeddingProviderOptions Options(string provider) =>
        _embedding.Providers.TryGetValue(provider, out var o)
            ? o
            : throw new UnknownEmbeddingProviderException(provider, _embedding.Providers.Keys);

    public IEmbeddingGenerator<string, Embedding<float>> GeneratorFor(EmbeddingTarget target) =>
        _cache.GetOrAdd(target.Provider, name => Build(name, Options(name)));

    private IEmbeddingGenerator<string, Embedding<float>> Build(string name, EmbeddingProviderOptions options)
    {
        _log.LogInformation("Creating {Kind} embedding client for provider '{Provider}'", options.Kind, name);

        return options.Kind switch
        {
            EmbeddingProviderKind.Ollama =>
                // The endpoint falls back to the top-level Ollama setting, which the
                // compose stack already supplies, so "ollama" needs no configuration.
                new OllamaApiClient(HttpClientFor(name, options.Endpoint ?? _ollama.Endpoint)),

            EmbeddingProviderKind.OpenAI =>
                new OpenAIClient(new ApiKeyCredential(RequireKey(name, options)))
                    .GetEmbeddingClient(DefaultModelFor(options))
                    .AsIEmbeddingGenerator(),

            EmbeddingProviderKind.AzureOpenAI =>
                new AzureOpenAIClient(
                        new Uri(options.Endpoint
                                ?? throw new InvalidOperationException(
                                    $"Provider '{name}' is Azure OpenAI and needs an Endpoint.")),
                        new ApiKeyCredential(RequireKey(name, options)))
                    .GetEmbeddingClient(DefaultModelFor(options))
                    .AsIEmbeddingGenerator(),

            _ => throw new InvalidOperationException($"Unsupported embedding provider kind '{options.Kind}'."),
        };
    }

    /// <summary>
    /// OpenAI's client wants a deployment/model at construction even though every call
    /// overrides it with <c>ModelId</c>. Any configured model will do as the placeholder;
    /// a set naming something else still gets what it asked for.
    /// </summary>
    private HttpClient HttpClientFor(string provider, string endpoint) =>
        _httpClients.GetOrAdd(provider, _ => new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = _ollama.Timeout,
        });

    private static string DefaultModelFor(EmbeddingProviderOptions options) =>
        options.Models.FirstOrDefault() ?? "text-embedding-3-small";

    private static string RequireKey(string name, EmbeddingProviderOptions options)
    {
        var key = options.ApiKey;

        if (string.IsNullOrWhiteSpace(key) && options.ApiKeyEnvVar is { Length: > 0 } variable)
            key = Environment.GetEnvironmentVariable(variable);

        if (string.IsNullOrWhiteSpace(key))
            throw new EmbeddingUnavailableException(
                $"Provider '{name}' has no API key. Set the environment variable " +
                $"'{options.ApiKeyEnvVar ?? "(none configured)"}', or supply Dexicon:Embedding:Providers:" +
                $"{name}:ApiKey from a secret store. The key is never read from the catalogue.");

        return key;
    }

    public void Dispose()
    {
        foreach (var generator in _cache.Values) generator.Dispose();
        _cache.Clear();

        foreach (var client in _httpClients.Values) client.Dispose();
        _httpClients.Clear();
    }
}
