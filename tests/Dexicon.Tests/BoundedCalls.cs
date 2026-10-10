namespace Dexicon.Tests;

/// <summary>
/// Runs a call that would never finish, or not for hours, if the guard under test were missing, so that
/// the test fails in bounded time instead of stalling the run. A stack overflow cannot be caught and still
/// ends the test host; a loop or a quadratic parse can be given up on. The abandoned call keeps its thread
/// until the process ends.
/// </summary>
internal static class BoundedCalls
{
    /// <summary>Long enough for a loaded machine to read a few hundred megabytes of XML.</summary>
    public static readonly TimeSpan Generous = TimeSpan.FromSeconds(90);

    public static T Within<T>(TimeSpan limit, Func<T> work)
    {
        var running = Task.Run(work);
        // Not Task.Wait, which throws an AggregateException when the work fails, and the work failing is how most tests end.
        if (Task.WhenAny(running, Task.Delay(limit)).GetAwaiter().GetResult() != running)
            throw new Xunit.Sdk.XunitException(
                $"The call did not finish within {limit.TotalSeconds:N0} s, so the guard that bounds this input is missing.");

        return running.GetAwaiter().GetResult();
    }
}
