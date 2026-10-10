namespace Dexicon.Tests;

public sealed class GatesTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    private sealed class Owner : IDisposable
    {
        public int Disposed;

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    [Fact]
    public async Task AGateThatIsNeverReachedFailsAndNamesItself()
    {
        var never = new TaskCompletionSource().Task;

        var thrown = await Should.ThrowAsync<TimeoutException>(
            () => Gates.ReachedAsync(never, "the held lookup", Short));

        thrown.Message.ShouldContain("the held lookup");
    }

    [Fact]
    public async Task ATaskThatDoesNotFinishFailsAndNamesItself()
    {
        var never = new TaskCompletionSource<int>().Task;

        var thrown = await Should.ThrowAsync<TimeoutException>(() => never.FinishesAsync("the removal", Short));

        thrown.Message.ShouldContain("the removal");
    }

    [Fact]
    public async Task AGateThatIsReachedAndATaskThatFinishesPassTheirResultOn()
    {
        await Gates.ReachedAsync(Task.CompletedTask, "a gate");
        (await Task.FromResult(7).FinishesAsync("a task")).ShouldBe(7);
    }

    [Fact]
    public async Task AResultThatArrivesAfterTheFailureIsDisposed()
    {
        var late = new TaskCompletionSource<Owner>();
        var owner = new Owner();

        await Should.ThrowAsync<TimeoutException>(() => late.Task.FinishesAsync("taking the lock", Short));
        late.SetResult(owner);

        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref owner.Disposed) == 0 && DateTime.UtcNow < until) await Task.Delay(10);
        owner.Disposed.ShouldBe(1, "what was taken late is given back");
    }
}
