using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A file recorded as indexed must have the chunks it claims.
///
/// The success path deletes a file's vectors before embedding the new ones, and writes
/// the row only after. A pass that dies in between — a restart, a lost lease, a cancel —
/// left the row reading Indexed, with a hash and a chunk count, over vectors that were
/// already gone. The hash still matched, so every later refresh short-circuited the file:
/// it was unsearchable and no refresh would ever repair it.
///
/// Found on a real corpus: three files short by 13,016 points, two of them reporting
/// thousands of chunks and holding none.
/// </summary>
public sealed class IndexedFileKeepsItsVectorsTests
{
    private static async Task<IndexingHarness> IndexedAsync()
    {
        var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("retrieval"));
        await harness.RunIndexAsync();
        harness.Vectors.CountFor("note.md").ShouldBeGreaterThan(0);
        return harness;
    }

    [Fact]
    public async Task VectorsLostBehindTheCatalogue_AreNoticedAndReindexed()
    {
        await using var harness = await IndexedAsync();
        var before = harness.Vectors.CountFor("note.md");

        // Exactly what an interrupted pass leaves: the points gone, the row untouched
        // and still carrying a hash that matches the file on disk.
        harness.Vectors.DropSilently("note.md");
        (await harness.StateOfAsync("note.md")).ContentHash.ShouldNotBeNull();

        await harness.RunIndexAsync();

        harness.Vectors.CountFor("note.md").ShouldBe(before,
            "the pass compares the recorded count against the index and re-indexes the difference");
        var state = await harness.StateOfAsync("note.md");
        state.Status.ShouldBe(FileStatus.Indexed);
        state.ChunkCount.ShouldBe(before);
    }

    [Fact]
    public async Task APartialLoss_IsNoticedToo()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        // Long enough to chunk into several pieces, so some can go missing.
        await harness.WriteFileAsync("long.md",
            string.Join("\n\n", Enumerable.Range(1, 400).Select(i => $"Paragraph {i} of the document.")));
        await harness.RunIndexAsync();

        var before = harness.Vectors.CountFor("long.md");
        before.ShouldBeGreaterThan(1, "this test needs a file of more than one chunk");

        harness.Vectors.DropOne("long.md");
        harness.Vectors.CountFor("long.md").ShouldBe(before - 1);

        await harness.RunIndexAsync();

        harness.Vectors.CountFor("long.md").ShouldBe(before);
    }

    [Fact]
    public async Task ASurplusIsADisagreementToo_AndIsRepairedTheSameWay()
    {
        // The check compares, it does not subtract. A file holding MORE points than its
        // row records - a stale chunk left at a high index - is the same disagreement,
        // and the pass cannot tell which of the two records is the wrong one.
        await using var harness = await IndexedAsync();
        var recorded = harness.Vectors.CountFor("note.md");

        harness.Vectors.AddStraySilently("note.md");
        harness.Vectors.CountFor("note.md").ShouldBe(recorded + 1);

        var job = await harness.RunIndexAsync();

        job.FilesDone.ShouldBe(1, "a surplus is re-indexed, not ignored");
        harness.Vectors.CountFor("note.md").ShouldBe(recorded,
            "re-indexing deletes the file's points first, so the stray goes with them");
    }

    [Fact]
    public async Task AnIncompleteCountIsNotTreatedAsAnEmptyOne()
    {
        // The dangerous failure. A facet at its cap reports nothing for every file past
        // the cutoff, which is the same shape as a file that holds no points. Acting on
        // it would send a whole corpus back through the embedding model.
        await using var harness = await IndexedAsync();
        var before = harness.Vectors.CountFor("note.md");

        harness.Vectors.CountsAreIncomplete = true;
        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Succeeded);
        job.FilesDone.ShouldBe(0, "nothing may be re-indexed on the strength of an incomplete answer");
        harness.Vectors.CountFor("note.md").ShouldBe(before);
        (await harness.StateOfAsync("note.md")).ContentHash.ShouldNotBeNull();
    }

    [Fact]
    public async Task AFailedCountReadDoesNotFailTheJob_OrReindexAnything()
    {
        // An outage is not a report that the index is empty.
        await using var harness = await IndexedAsync();
        var before = harness.Vectors.CountFor("note.md");

        harness.Vectors.CountsThrow = true;
        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Succeeded);
        job.FilesDone.ShouldBe(0);
        harness.Vectors.CountFor("note.md").ShouldBe(before);
    }

    [Fact]
    public async Task APassInterruptedAtTheDelete_LeavesNoRowClaimingAHash()
    {
        // The write-ahead claim, read back from the catalogue.
        //
        // The interruption has to be one no catch block handles, or the test proves
        // nothing: an ordinary exception is turned into a Failed row that nulls the
        // hash on its way past, so it passes with or without the claim. Cancellation
        // is the one the per-file catches deliberately let through, and it is what a
        // restart and a lost lease both arrive as.
        await using var harness = await IndexedAsync();
        var hashBefore = (await harness.StateOfAsync("note.md")).ContentHash;
        hashBefore.ShouldNotBeNull();

        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("something else"));
        harness.Vectors.AbandonNextDelete = true;

        // It escapes every per-file catch and ends the job, which is the point: no
        // handler runs for this file, so nothing tidies its row on the way out.
        var abandoned = await harness.RunIndexAsync();
        abandoned.State.ShouldBe(JobState.Failed);

        var state = await harness.StateOfAsync("note.md");
        state.ContentHash.ShouldBeNull(
            "a row still carrying its hash here is skipped by every later refresh, for good");

        // And it is recoverable: the next pass indexes it rather than short-circuiting.
        await harness.RunIndexAsync();

        var after = await harness.StateOfAsync("note.md");
        after.Status.ShouldBe(FileStatus.Indexed);
        after.ContentHash.ShouldNotBeNull();
        harness.Vectors.CountFor("note.md").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task OneSetLosingItsVectors_DoesNotDragTheOtherThroughTheModel()
    {
        // The count is read per set. If it were read across them, the other set's copy
        // of the same file would make this one look healthy and the loss would stand.
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("scoping"));
        await harness.RunIndexAsync();

        var inSetOne = harness.Vectors.CountFor("note.md", chunkSetId: "set-1");
        var inSetTwo = harness.Vectors.CountFor("note.md", chunkSetId: "set-2");
        inSetOne.ShouldBeGreaterThan(0);
        inSetTwo.ShouldBeGreaterThan(0);

        harness.Vectors.DropSilently("note.md", chunkSetId: "set-1");

        var job = await harness.RunIndexAsync();

        harness.Vectors.CountFor("note.md", chunkSetId: "set-1").ShouldBe(inSetOne, "the set that lost them");
        harness.Vectors.CountFor("note.md", chunkSetId: "set-2").ShouldBe(inSetTwo, "the set that did not");
        job.FilesDone.ShouldBe(1, "only the set that was short may be re-embedded");
    }

    [Fact]
    public async Task OneSourceLosingItsVectors_LeavesAnotherSourcesSamePathAlone()
    {
        // Two sources of one corpus can hold the same relative path, and the count is
        // read per source for exactly that reason. Read across sources, the healthy
        // copy would cover for the missing one.
        await using var harness = await IndexingHarness.StartAsync("first", "second");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("first source"), source: 0);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("second source"), source: 1);
        await harness.RunIndexAsync();

        var one = IndexingHarness.SourceIdFor(0);
        var two = IndexingHarness.SourceIdFor(1);
        var inOne = harness.Vectors.CountFor("note.md", sourceId: one);
        var inTwo = harness.Vectors.CountFor("note.md", sourceId: two);
        inOne.ShouldBeGreaterThan(0);
        inTwo.ShouldBeGreaterThan(0);

        harness.Vectors.DropSilently("note.md", sourceId: one);

        var job = await harness.RunIndexAsync();

        harness.Vectors.CountFor("note.md", sourceId: one).ShouldBe(inOne);
        harness.Vectors.CountFor("note.md", sourceId: two).ShouldBe(inTwo);
        job.FilesDone.ShouldBe(1, "only the source that was short may be re-embedded");
    }

    [Fact]
    public async Task AnUploadedDocumentThatLosesItsVectors_IsNoticedToo()
    {
        // Uploads reach the same skip check with a matching fingerprint, and have no
        // reconcile pass behind them, so the comparison has to run on that path as well.
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Upload);

        await using (var db = harness.NewContext())
        {
            var documents = harness.NewDocumentService(db);
            var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
                .FirstAsync(c => c.Id == IndexingHarness.CorpusId);
            using var bytes = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(IndexingHarness.Prose("uploads")));
            var stored = await documents.StoreAsync(bytes, "paper.md");
            await documents.AttachAsync(corpus, stored.Sha256, "paper.md");
        }

        await harness.RunIndexAsync();
        var before = harness.Vectors.CountFor("paper.md");
        before.ShouldBeGreaterThan(0);

        harness.Vectors.DropSilently("paper.md");
        await harness.RunIndexAsync();

        harness.Vectors.CountFor("paper.md").ShouldBe(before);
    }
}
