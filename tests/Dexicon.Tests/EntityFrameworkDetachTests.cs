using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// What the indexer relies on from the change tracker. After a save fails because a file's row is gone, the
/// pass stops tracking the file and expects its chunk states to leave the tracker with it, so that the next
/// save does not fail on them again.
/// </summary>
public sealed class EntityFrameworkDetachTests
{
    [Fact]
    public async Task StoppingTrackingAFileStopsTrackingItsChunkStatesAndTheirChanges()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using (var setup = harness.NewContext())
        {
            var documents = harness.NewDocumentService(setup);
            var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
            await documents.AttachAsync(await setup.Corpora.SingleAsync(), stored.Sha256, "doc.txt");
        }

        await using var db = harness.NewContext();
        var file = await db.Files.SingleAsync();
        var state = await db.FileChunkStates.SingleAsync();
        state.Status = FileStatus.Indexed;
        db.Entry(state).State.ShouldBe(EntityState.Modified);

        db.Entry(file).State = EntityState.Detached;

        db.Entry(state).State.ShouldBe(EntityState.Detached, "its change is not saved again");
        db.ChangeTracker.Entries<FileChunkState>().ShouldBeEmpty();
    }
}

