using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dexicon.Tests;

/// <summary>
/// Holds a lookup until two have asked, or until it has waited <c>patience</c>. That puts two requests inside
/// the window between looking and adding, which is otherwise a matter of luck. A race test asserts that both
/// reached the window, because one whose gate stopped matching would pass for ever.
///
/// When the second never comes, as when it is queued behind a lock the first holds, the first goes on after
/// <c>patience</c> and the second then finds what the first added.
/// </summary>
/// <param name="patience">How long the first lookup waits for a second.</param>
/// <param name="isTheLookup">Whether a command's SQL is the lookup to hold.</param>
internal sealed class HoldTheLookup(TimeSpan patience, Func<string, bool> isTheLookup) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrivals;
    private int _releasedTogether;

    public bool Armed { get; set; }

    /// <summary>How many lookups reached the gate.</summary>
    public int Arrivals => Volatile.Read(ref _arrivals);

    /// <summary>
    /// Whether two lookups were held at once: both were still waiting when the second arrived. A lookup
    /// that gave up after <c>patience</c> and went on before the second came does not count.
    /// </summary>
    public bool Met => Volatile.Read(ref _releasedTogether) >= 2;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (Armed && isTheLookup(command.CommandText))
        {
            if (Interlocked.Increment(ref _arrivals) >= 2) _both.TrySetResult();
            await Task.WhenAny(_both.Task, Task.Delay(patience, cancellationToken));
            if (_both.Task.IsCompletedSuccessfully) Interlocked.Increment(ref _releasedTogether);
        }

        return result;
    }
}
