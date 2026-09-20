using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Indexing;

/// <summary>
/// What the machine and the endpoints will take, counted once for the whole process.
///
/// Indexing ran one job at a time, so "how much of this may happen at once" and "how
/// much of this may one job do" were the same question and every limit could be a local
/// variable. With several corpora indexing together they come apart: a semaphore
/// constructed inside a method bounds that call and nothing else, so two jobs each took
/// the whole configured budget and the setting described neither of them.
///
/// The limits are per RESOURCE rather than per job, which is what lets a corpus indexing
/// alone use all of a budget while four indexing together share it. They are held as a
/// singleton for the same reason: a scoped instance is a per-caller limit wearing the
/// name of a global one.
/// </summary>
public sealed class IndexingLimits : IDisposable
{
    private readonly Dictionary<string, SemaphoreSlim> _embedding = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _embeddingLock = new();
    private readonly int _embeddingPermits;

    public IndexingLimits(IOptions<DexiconOptions> options)
    {
        var indexing = options.Value.Indexing;
        _embeddingPermits = Math.Max(1, options.Value.Embedding.MaxConcurrency);

        MaxConcurrentCorpora = Math.Max(1, indexing.MaxConcurrentCorpora);
        Extractions = new SemaphoreSlim(Math.Max(1, indexing.MaxConcurrentExtractions));
    }

    /// <summary>
    /// How many index jobs may run at once. A count rather than a semaphore, because it
    /// is spent by starting that many workers: a worker holding a permit while it waits
    /// for one is a worker not reading the queue.
    ///
    /// Two jobs on ONE corpus are excluded by the lease, not by this.
    /// </summary>
    public int MaxConcurrentCorpora { get; }

    /// <summary>How many files may be parsed at once, across every job.</summary>
    public SemaphoreSlim Extractions { get; }

    /// <summary>
    /// Requests in flight against one embedding provider.
    ///
    /// Keyed by provider, because the number describes an endpoint: a local Ollama
    /// admitting four sequences and a hosted deployment with a request quota are
    /// different limits, and a corpus on one should not be throttled by traffic to the
    /// other. Created on first use and kept, since the set of providers is small and
    /// fixed by configuration.
    /// </summary>
    public SemaphoreSlim EmbeddingFor(EmbeddingTarget target)
    {
        lock (_embeddingLock)
        {
            if (_embedding.TryGetValue(target.Provider, out var existing)) return existing;
            var created = new SemaphoreSlim(_embeddingPermits);
            _embedding[target.Provider] = created;
            return created;
        }
    }

    public void Dispose()
    {
        Extractions.Dispose();
        lock (_embeddingLock)
        {
            foreach (var s in _embedding.Values) s.Dispose();
            _embedding.Clear();
        }
    }
}
