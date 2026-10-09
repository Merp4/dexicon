using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dexicon.Tests;

/// <summary>
/// Holds the first lookup that matches until the test lets it go, so the test can run another request while
/// the first is inside the window between looking and saving. <see cref="Reached"/> completes once the
/// lookup is held. A test releases it in a <c>finally</c>: a lookup nobody releases is given up after
/// <see cref="Patience"/> so a failed test does not hang the run.
/// </summary>
/// <param name="isTheLookup">Whether a command's SQL is the lookup to hold.</param>
internal sealed class HoldOneLookup(Func<string, bool> isTheLookup) : DbCommandInterceptor
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _taken;

    public bool Armed { get; set; }

    /// <summary>Completes when a lookup is held.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the held lookup go on.</summary>
    public void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (Armed && isTheLookup(command.CommandText) && Interlocked.Exchange(ref _taken, 1) == 0)
        {
            _reached.TrySetResult();
            try { await _release.Task.WaitAsync(Patience, cancellationToken); }
            catch (TimeoutException) { /* a test that never released it */ }
        }

        return result;
    }
}
