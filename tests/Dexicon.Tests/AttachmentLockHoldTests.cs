using Dexicon.Core.Documents;

namespace Dexicon.Tests;

/// <summary>The lock that attachments take, as another writer of the catalogue takes it.</summary>
[Collection(nameof(AttachmentLockCollection))]
public sealed class AttachmentLockHoldTests
{
    [Fact]
    public async Task DisposingAHoldTwiceDoesNotReleaseTheLockOfTheNextHolder()
    {
        var first = await DocumentService.HoldAttachmentsAsync().FinishesAsync("taking the lock");
        first.Dispose();
        var second = await DocumentService.HoldAttachmentsAsync().FinishesAsync("taking the lock after a release");
        Task<IDisposable> third;
        try
        {
            first.Dispose();
            third = DocumentService.HoldAttachmentsAsync();
        }
        catch
        {
            second.Dispose();
            throw;
        }

        try
        {
            (await Task.WhenAny(third, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBeSameAs(third,
                "the second dispose of the first hold released the lock the second hold took");
        }
        finally
        {
            second.Dispose();

            // Whatever took the lock gives it back, so a failure here does not hang the tests after it.
            _ = third.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
                TaskScheduler.Default);
        }

        using var granted = await third.FinishesAsync("taking the lock once the second hold was released");
    }

    [Fact]
    public async Task ACancelledWaitForTheLockLeavesTheLockWithItsHolder()
    {
        using var holder = await DocumentService.HoldAttachmentsAsync().FinishesAsync("taking the lock");
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(
            async () => (await DocumentService.HoldAttachmentsAsync(giveUp.Token)).Dispose());
    }
}
