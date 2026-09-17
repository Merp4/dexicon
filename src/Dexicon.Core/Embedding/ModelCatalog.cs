using Dexicon.Core.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace Dexicon.Core.Embedding;

/// <param name="Dimensions">
/// Known only for models already probed. Probing every listed model would mean loading
/// each one in turn just to render a dropdown, so it stays null until a model is used.
/// </param>
public sealed record AvailableModel(string Name, long SizeBytes, string? Family, int? Dimensions);

/// <param name="Status">The provider's own words, such as "pulling manifest" or "success".</param>
public sealed record ModelPullProgress(string Status, long Completed, long Total)
{
    public int Percent => Total > 0 ? (int)(100 * Completed / Total) : 0;
    public bool Done => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Listing, pulling and deleting models. This is not embedding, and not something every
/// provider can do.
///
/// Deliberately separate from <see cref="IEmbeddingService"/>. You cannot pull a model
/// into OpenAI, and a hosted provider's catalogue is a fixed list rather than a local
/// directory you manage. Folding these onto the embedding interface would have given
/// every provider four methods that three of them answer with "not supported".
/// </summary>
public interface IModelCatalog
{
    /// <summary>Whether this provider's models can be pulled and deleted, not merely listed.</summary>
    bool IsManaged(string provider);

    Task<IReadOnlyList<AvailableModel>> ListAsync(string provider, CancellationToken ct = default);

    IAsyncEnumerable<ModelPullProgress> PullAsync(string provider, string model, CancellationToken ct = default);

    Task DeleteAsync(string provider, string model, CancellationToken ct = default);
}

public sealed class ModelCatalog(
    IEmbeddingGeneratorFactory factory,
    IOptions<DexiconOptions> options,
    IMemoryCache cache,
    ILogger<ModelCatalog> log) : IModelCatalog
{
    private readonly OllamaOptions _ollama = options.Value.Ollama;

    public bool IsManaged(string provider) =>
        factory.Options(provider).Kind == EmbeddingProviderKind.Ollama;

    public async Task<IReadOnlyList<AvailableModel>> ListAsync(string provider, CancellationToken ct = default)
    {
        var configured = factory.Options(provider);

        // A hosted provider cannot be asked what it has; it has everything it offers. The
        // configured list is the answer, and free text still works in the UI.
        if (configured.Kind != EmbeddingProviderKind.Ollama)
            // Family is null, not the provider name. A hosted list is curated, so nothing
            // reads it here, but a field that says "openai" where a model architecture
            // belongs is a trap for whoever reads it next.
            return [.. configured.Models
                .Select(m => new AvailableModel(m, 0, null, KnownDimensions(provider, m)))
                .OrderBy(m => m.Name, StringComparer.Ordinal)];

        try
        {
            using var client = Client(configured);
            var models = await client.ListLocalModelsAsync(ct);

            return [.. models
                .Select(m => new AvailableModel(
                    m.Name, m.Size, m.Details?.Family, KnownDimensions(provider, m.Name)))
                .OrderBy(m => m.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EmbeddingUnavailableException(
                $"Could not list models from provider '{provider}': {ex.Message}", ex);
        }
    }

    public async IAsyncEnumerable<ModelPullProgress> PullAsync(
        string provider, string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var configured = RequireManaged(provider, "pulled");
        log.LogInformation("Pulling {Model} into provider '{Provider}'", model, provider);

        using var client = Client(configured);

        await foreach (var status in client.PullModelAsync(model, ct))
        {
            if (status is null) continue;
            yield return new ModelPullProgress(status.Status ?? "", status.Completed, status.Total);
        }
    }

    public async Task DeleteAsync(string provider, string model, CancellationToken ct = default)
    {
        var configured = RequireManaged(provider, "deleted");

        try
        {
            using var client = Client(configured);
            await client.DeleteModelAsync(model, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EmbeddingUnavailableException(
                $"Could not delete '{model}' from provider '{provider}': {ex.Message}", ex);
        }
    }

    private EmbeddingProviderOptions RequireManaged(string provider, string verb)
    {
        var configured = factory.Options(provider);

        if (configured.Kind != EmbeddingProviderKind.Ollama)
            throw new InvalidOperationException(
                $"Models cannot be {verb} for provider '{provider}': it is {configured.Kind}, which serves a " +
                "fixed catalogue rather than a local model directory. Configure the models you want under " +
                $"Dexicon:Embedding:Providers:{provider}:Models.");

        return configured;
    }

    /// <summary>
    /// A client with NO request timeout, unlike the embedding path which uses the
    /// configured one. A pull is gigabytes over minutes, and the default hundred seconds
    /// would abort it part-way every time on any model worth having. Cancellation still
    /// works; the caller's token is what stops it.
    /// </summary>
    private static OllamaApiClient ClientFor(string endpoint) =>
        new(new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        });

    private OllamaApiClient Client(EmbeddingProviderOptions configured) =>
        ClientFor(configured.Endpoint ?? _ollama.Endpoint);

    /// <summary>Free: the dimensionality of anything already probed is in the shared cache.</summary>
    private int? KnownDimensions(string provider, string model) =>
        cache.TryGetValue($"embed-dims::{provider}::{model}", out int d) ? d : null;
}
