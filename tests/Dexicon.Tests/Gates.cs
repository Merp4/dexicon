namespace Dexicon.Tests;

/// <summary>
/// Waits for the gates and tasks of a concurrency test with a limit, so a gate that is never reached fails
/// the test and names itself, and does not hang the run.
/// </summary>
internal static class Gates
{
    /// <summary>
    /// Long enough for a loaded runner to finish work that takes milliseconds on a free one. The tests
    /// wait this long only for something that has to happen, so a pass never waits it out.
    /// </summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Completes when the gate does, and throws naming <paramref name="what"/> when it does not.</summary>
    public static async Task ReachedAsync(Task gate, string what, TimeSpan? patience = null)
    {
        var limit = patience ?? Patience;
        if (await Task.WhenAny(gate, Task.Delay(limit)) != gate)
            throw new TimeoutException($"{what} was not reached within {limit.TotalSeconds:N1} s.");

        await gate;
    }

    /// <summary>
    /// The task's result, or a failure naming <paramref name="what"/> when it has not finished in time. A
    /// result that arrives after the failure and is disposable is disposed, so that a lock taken late is not
    /// held for ever by a test that has already failed.
    /// </summary>
    public static async Task<T> FinishesAsync<T>(this Task<T> task, string what, TimeSpan? patience = null)
    {
        var limit = patience ?? Patience;
        if (await Task.WhenAny(task, Task.Delay(limit)) != task)
        {
            _ = task.ContinueWith(
                late => { if (late.IsCompletedSuccessfully && late.Result is IDisposable owner) owner.Dispose(); },
                TaskScheduler.Default);
            throw new TimeoutException($"{what} did not finish within {limit.TotalSeconds:N1} s.");
        }

        return await task;
    }

    /// <summary>Waits for the task, or fails naming <paramref name="what"/> when it has not finished in time.</summary>
    public static async Task FinishesAsync(this Task task, string what, TimeSpan? patience = null)
    {
        var limit = patience ?? Patience;
        if (await Task.WhenAny(task, Task.Delay(limit)) != task)
            throw new TimeoutException($"{what} did not finish within {limit.TotalSeconds:N1} s.");

        await task;
    }
}
/// <summary>
/// Test classes that hold the attachment lock for a stretch of time on purpose. A collection with parallelism
/// off runs alone, so no other class waits on that lock, or has it held for it, while a gate is open.
/// </summary>
[CollectionDefinition(nameof(AttachmentLockCollection), DisableParallelization = true)]
public sealed class AttachmentLockCollection;
