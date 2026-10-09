using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// What the indexer relies on from the change tracker. After a save fails because a file's row is gone, the
/// pass stops tracking the file and expects its chunk states to leave the tracker with it, so that the next
/// save does not fail on them again. The states a pass holds are Added (the insert of a document's first
/// states), Unchanged and Modified. A state in the Deleted state is not detached with its file; the
/// reconcile of a folder leaves such states, so the pass stops tracking them itself.
/// </summary>
public sealed class EntityFrameworkDetachTests
{
    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Modified)]
    public async Task StoppingTrackingAFileStopsTrackingItsChunkStates(EntityState kind)
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload, sets: 2);
        await using (var setup = harness.NewContext())
        {
            var documents = harness.NewDocumentService(setup);
            var stored = await documents.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
            await documents.AttachAsync(
                await setup.Corpora.Include(c => c.ChunkSets).SingleAsync(), stored.Sha256, "doc.txt");
            if (kind == EntityState.Added)
                await setup.FileChunkStates.Where(s => s.ChunkSetId == "set-2").ExecuteDeleteAsync();
        }

        await using var db = harness.NewContext();
        var file = await db.Files.SingleAsync();
        if (kind == EntityState.Added)
            db.FileChunkStates.Add(new FileChunkState { FileId = file.Id, ChunkSetId = "set-2", Status = FileStatus.Pending });

        var states = await db.FileChunkStates.ToListAsync();
        if (kind == EntityState.Modified)
            foreach (var state in states) state.Status = FileStatus.Indexed;

        db.ChangeTracker.Entries<FileChunkState>().Count().ShouldBe(2);
        db.ChangeTracker.Entries<FileChunkState>().ShouldContain(e => e.State == kind, "the state under test is tracked");

        db.Entry(file).State = EntityState.Detached;

        db.ChangeTracker.Entries<FileChunkState>().ShouldBeEmpty("their changes are not saved again");
    }

    [Fact]
    public async Task AChunkStateThatIsDeletedStaysTrackedWhenItsFileIsStopped()
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
        db.FileChunkStates.Remove(state);

        db.Entry(file).State = EntityState.Detached;

        db.Entry(state).State.ShouldBe(EntityState.Deleted, "it fails every save after it unless the pass stops tracking it");
    }
}
