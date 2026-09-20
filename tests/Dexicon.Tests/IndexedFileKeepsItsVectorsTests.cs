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
    public async Task TheCheckIsScopedToOneSetAndOneSource()
    {
        // The count comes back per (set, source). If it were read across sets, a second
        // set's copy of a file would make the first look healthy when it is not.
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("scoping"));
        await harness.RunIndexAsync();

        await using var db = harness.NewContext();
        var perSet = await db.FileChunkStates.Where(s => s.ChunkCount > 0).ToListAsync();
        perSet.Count.ShouldBe(2, "one row per set");
        perSet.Select(s => s.ChunkSetId).Distinct().Count().ShouldBe(2);
    }
}
