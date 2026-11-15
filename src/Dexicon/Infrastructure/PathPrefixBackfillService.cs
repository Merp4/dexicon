using Dexicon.Core.Vectors;
using Microsoft.Extensions.Hosting;

namespace Dexicon.Infrastructure;

/// <summary>
/// Gives the chunks that were written before the <c>path_prefixes</c> payload existed their value, once at start,
/// so that a <c>pathPrefix</c> filter means the same for every chunk. Until a chunk has it, the filter matches that
/// chunk by the words of the value, as it always did, and nothing is lost in the meantime.
///
/// Qdrant is often not up yet when the host starts, so a failed attempt waits a minute and tries again. This service
/// logs the retries at debug level and the last failure as a warning; when Qdrant is up and refuses to create the
/// index, the store also logs a warning on every attempt. A start with nothing to do costs one index request and one
/// empty scroll per collection.
/// </summary>
internal sealed class PathPrefixBackfillService(IVectorStore vectors, ILogger<PathPrefixBackfillService> log) : BackgroundService
{
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    internal const int Attempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                var updated = await vectors.BackfillPathPrefixesAsync(stoppingToken);
                if (updated > 0)
                    log.LogInformation("Set the path prefixes of {Points} point(s) written before they were stored", updated);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt == Attempts)
                {
                    log.LogWarning(ex, "Could not set the path prefixes of older points after {Attempts} attempts; a pathPrefix filter "
                                       + "matches them by the words of its value until a later start finishes it", Attempts);
                    return;
                }

                log.LogDebug(ex, "The path prefixes of older points were not set (attempt {Attempt} of {Attempts}); trying again in {Delay}",
                    attempt, Attempts, RetryDelay);

                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
