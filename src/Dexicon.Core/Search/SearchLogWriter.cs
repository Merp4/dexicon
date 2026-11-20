using System.Diagnostics.CodeAnalysis;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Search;

/// <summary>
/// Writes the rows <see cref="SearchLog"/> queued, and removes the ones past retention.
///
/// One reader drains the queue, because SQLite has one writer and several would only wait on each other inside the
/// catalogue's busy timeout. The daily prune is the only other writer here. A batch is whatever has arrived since the
/// last write, so a burst becomes one transaction and a quiet hour becomes one row each.
///
/// A batch the catalogue refuses is tried once more after <see cref="RetryDelay"/>, since a refusal is usually an index
/// job holding the writer. If that fails too, the batch is dropped, counted and logged at Warning, and the next one is
/// tried. Rows still queued when the host stops are lost, which is the cost of a search never waiting on this.
/// </summary>
public sealed class SearchLogWriter(
    SearchLog log,
    IServiceScopeFactory scopes,
    IOptions<DexiconOptions> options,
    TimeProvider clock,
    ILogger<SearchLogWriter> logger) : BackgroundService
{
    /// <summary>Rows per transaction.</summary>
    internal const int MaxBatch = 100;

    /// <summary>Retention beyond this many days is treated as this many: 100 years, well inside what a date can hold.</summary>
    internal const int MaxRetentionDays = 36_500;

    /// <summary>How long a refused batch waits before its one retry.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "PruneLoopAsync", Justification =
        "Starts the prune loop beside the drain loop and waits for both on the host's stopping token. Nothing it writes depends "
        + "on a later step.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.SearchLog;
        logger.LogInformation(
            "Search log {State}; query text {Text}; kept {Days} days",
            settings.Enabled ? "on" : "off",
            settings.StoreQueryText ? "stored" : "not stored",
            settings.RetentionDays <= 0 ? "without limit" : Math.Min(settings.RetentionDays, MaxRetentionDays));

        try
        {
            await Task.WhenAll(DrainAsync(stoppingToken), PruneLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "WaitToReadAsync", Justification =
        "Writes a batch, then waits for the next one on the host's stopping token. A wait cancelled at shutdown loses "
        + "nothing already written: a batch is one save, and rows still queued at stop are dropped by design.")]
    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "WriteAsync", Justification =
        "The next batch is written on the same stopping token. Cancellation at that point skips only a batch that was "
        + "not written yet.")]
    private async Task DrainAsync(CancellationToken ct)
    {
        var reader = log.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            var batch = new List<SearchLogRow>(MaxBatch);
            while (batch.Count < MaxBatch && reader.TryRead(out var row)) batch.Add(row);

            await WriteAsync(batch, ct);
        }
    }

    /// <summary>Writes one batch, with one retry. A batch that fails twice is dropped: see the class summary.</summary>
    internal async Task WriteAsync(List<SearchLogRow> batch, CancellationToken ct)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await SaveAsync(batch, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
                if (attempt == 1) await Task.Delay(RetryDelay, ct);
            }
        }

        log.CountDropped(batch.Count);
        logger.LogWarning(failure, "Search log could not write {Rows} rows and dropped them ({Dropped} dropped since start)",
            batch.Count, log.Dropped);
    }

    private async Task SaveAsync(List<SearchLogRow> batch, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.SearchLog.AddRange(batch);
        await db.SaveChangesAsync(ct);
    }

    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "WaitForNextTickAsync", Justification =
        "Prunes, then waits a day on the host's stopping token. A wait cancelled at shutdown loses nothing: a delete "
        + "that did not run is repeated tomorrow.")]
    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "PruneAsync", Justification =
        "The next prune runs a day later on the same stopping token; cancelling it skips only that prune.")]
    private async Task PruneLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            await PruneAsync(ct);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Deletes the rows older than the retention window. 0 days, or fewer, keeps every row.</summary>
    internal async Task<int> PruneAsync(CancellationToken ct)
    {
        var days = options.Value.SearchLog.RetentionDays;
        if (days <= 0) return 0;

        try
        {
            var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-Math.Min(days, MaxRetentionDays));
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var removed = await db.SearchLog.Where(r => r.AtUtc < cutoff).ExecuteDeleteAsync(ct);
            if (removed > 0) logger.LogInformation("Search log removed {Rows} rows older than {Days} days", removed, days);
            return removed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Search log could not remove rows older than {Days} days; it will try again tomorrow", days);
            return 0;
        }
    }
}
