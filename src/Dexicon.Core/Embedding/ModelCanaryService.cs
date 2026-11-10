using System.Diagnostics.CodeAnalysis;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Embedding;

/// <summary>
/// Checks every chunk set's model canary (<see cref="ModelCanary"/>) once a few hours, so a model that
/// changed under a set is reported without waiting for someone to index into it.
///
/// A timer of its own, not a call from the health report. <c>/healthz</c> is polled every fifteen
/// seconds per open tab, and an embedding call on that path once timed out under indexing load and
/// painted the dependency dots red. The report reads the column this writes.
/// </summary>
[SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", Justification =
    "Checks each set in turn until the host stops. A check is one update statement and nothing depends on a "
    + "second one, so stopping between sets leaves each as its last check wrote it, and a set not reached is "
    + "checked at the next tick.")]
public sealed class ModelCanaryService(
    IServiceScopeFactory scopes,
    ILogger<ModelCanaryService> log,
    TimeSpan? firstCheckAfter = null,
    TimeSpan? every = null) : BackgroundService
{
    /// <summary>
    /// Waited before the first check, so startup (the dependency probes, a cold embedding model loading
    /// into memory) finishes first. A set with no recorded canary gets one from this check, and the
    /// sooner that happens after an upgrade the less a model change can go unseen. The constructor
    /// argument is for tests; the host supplies none.
    /// </summary>
    internal static readonly TimeSpan DefaultFirstCheckAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Between checks. A model is replaced by a person at a keyboard, and the pass that follows checks
    /// again before it writes, so the delay bounds how long a change sits unreported on an idle server.
    /// </summary>
    internal static readonly TimeSpan DefaultEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(firstCheckAfter ?? DefaultFirstCheckAfter, stoppingToken);

            using var timer = new PeriodicTimer(every ?? DefaultEvery);
            do
            {
                try { await RunTickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Model canary check failed; will try again in {Hours} hours",
                        (every ?? DefaultEvery).TotalHours);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// One check of every set. A model that does not answer is not asked again in the same tick: a
    /// failed embed retries with backoff, and several sets on one missing model would repeat it.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        var sets = await db.ChunkSets.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);
        var silent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var set in sets)
        {
            var model = $"{set.EmbeddingProvider}/{EmbeddingTarget.Canonical(set.EmbeddingModel)}";
            if (silent.Contains(model)) continue;

            try
            {
                var outcome = await ModelCanary.CheckAsync(db, embedder, set, log, ct);
                if (outcome == CanaryOutcome.Unavailable) silent.Add(model);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A provider that cannot be built, or a catalogue error, is one set's failure. The other sets
                // are still checked, and this model is not asked again in the tick.
                log.LogWarning(ex, "Chunk set {Set} of corpus {CorpusId}: the model check failed",
                    set.Name, set.CorpusId);
                silent.Add(model);
            }
        }
    }
}
