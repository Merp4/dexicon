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
/// One reader, because SQLite has one writer and several would only queue behind each other inside the
/// catalogue's busy timeout. A batch is whatever has arrived since the last write, so a burst becomes one
/// transaction and a quiet hour becomes one row each.
///
/// A batch the catalogue refuses is dropped, counted and logged at Warning, and the next one is tried: a
/// catalogue locked for the thirty seconds of its busy timeout by an index job costs those rows and nothing
/// else. Rows still queued when the host stops are lost, which is the cost of a search never waiting on this.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.SearchLog;
        logger.LogInformation(
            "Search log {State}; query text {Text}; kept {Days} days",
            settings.Enabled ? "on" : "off",
            settings.StoreQueryText ? "stored" : "not stored",
            settings.RetentionDays == 0 ? "without limit" : settings.RetentionDays);

        try
        {
            await Task.WhenAll(DrainAsync(stoppingToken), PruneLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

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

    /// <summary>Writes one batch. A failure drops it: see the class summary.</summary>
    internal async Task WriteAsync(List<SearchLogRow> batch, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            db.SearchLog.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.CountDropped(batch.Count);
            logger.LogWarning(ex, "Search log could not write {Rows} rows and dropped them ({Dropped} dropped since start)",
                batch.Count, log.Dropped);
        }
    }

    private async Task PruneLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            await PruneAsync(ct);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Deletes the rows older than the retention window. 0 days keeps every row.</summary>
    internal async Task<int> PruneAsync(CancellationToken ct)
    {
        var days = options.Value.SearchLog.RetentionDays;
        if (days <= 0) return 0;

        try
        {
            var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-days);
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
