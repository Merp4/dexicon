using Dexicon.Core.Catalog;

namespace Dexicon.Tests;

/// <summary>
/// A job's counters have to add up to its total.
///
/// Every file a pass records increments exactly one of done, skipped or failed, so
/// <c>FilesTotal</c> must count every file the pass will record: the ones the source owns
/// AND the ones the walk excluded, which get a row and a skip of their own. Counting only
/// the owned ones reported 27,031 skipped against a total of 27,011 on a real corpus, and
/// any progress reading <c>(done + skipped + failed) / total</c> past 1.0.
/// </summary>
public sealed class JobCounterTests
{
    /// <summary>
    /// Three that index, and two the walk throws out for different reasons. Zero bytes
    /// and a NUL byte are the two exclusions reachable without touching configuration.
    /// </summary>
    private static async Task<IndexingHarness> WithMixedFilesAsync(int sets = 1)
    {
        var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: sets);

        await harness.WriteFileAsync("one.md", IndexingHarness.Prose("indexing"));
        await harness.WriteFileAsync("two.md", IndexingHarness.Prose("chunking"));
        await harness.WriteFileAsync("three.md", IndexingHarness.Prose("embedding"));

        await harness.WriteFileAsync("empty.md", "");
        await File.WriteAllBytesAsync(Path.Combine(harness.SourceDirectory, "blob.txt"),
            [0x44, 0x00, 0x41, 0x54, 0x41]);

        return harness;
    }

    private static void ShouldAddUp(IndexJob job, int expectedTotal)
    {
        var processed = job.FilesDone + job.FilesSkipped + job.FilesFailed;
        job.FilesTotal.ShouldBe(expectedTotal);
        processed.ShouldBe(job.FilesTotal,
            $"done {job.FilesDone} + skipped {job.FilesSkipped} + failed {job.FilesFailed} "
            + $"must equal total {job.FilesTotal}");
    }

    [Fact]
    public async Task ExcludedFilesAreInTheTotal_NotOnlyInTheSkippedCount()
    {
        await using var harness = await WithMixedFilesAsync();

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Succeeded);
        job.FilesDone.ShouldBe(3);
        job.FilesSkipped.ShouldBe(2, "the two the walk excluded");
        ShouldAddUp(job, expectedTotal: 5);
    }

    [Fact]
    public async Task OnASecondPassWhereNothingChanged_TheCountersStillAddUp()
    {
        await using var harness = await WithMixedFilesAsync();
        await harness.RunIndexAsync();

        // The shape the defect was found in: every file increments FilesSkipped, three
        // because they are unchanged and two because the walk threw them out.
        var job = await harness.RunIndexAsync();

        job.FilesDone.ShouldBe(0);
        job.FilesSkipped.ShouldBe(5);
        ShouldAddUp(job, expectedTotal: 5);
    }

    [Fact]
    public async Task TheCountersAddUpAcrossTwoChunkSets()
    {
        // A job covers every set and each set walks the tree again, which is why the
        // total accumulates rather than being assigned. A fix that held for one set and
        // not for two would reintroduce the defect the `+=` already exists for.
        await using var harness = await WithMixedFilesAsync(sets: 2);

        var job = await harness.RunIndexAsync();

        job.FilesDone.ShouldBe(6, "three files, once per set");
        job.FilesSkipped.ShouldBe(4);
        ShouldAddUp(job, expectedTotal: 10);
    }

    [Fact]
    public async Task WithNestedSources_AnExcludedFileIsCountedByEachButStillAddsUp()
    {
        // Shadowing applies to the walk's owned files and not to its skipped ones, so a
        // file an exclusion catches under a nested source is reported by every source
        // above it. That double-counts it in the total, which is worth knowing, and the
        // thing this line has to guarantee is that it double-counts it in BOTH terms.
        await using var harness = await IndexingHarness.StartAsync("outer", "outer/inner");
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        await harness.WriteFileAsync("kept.md", IndexingHarness.Prose("outer"), source: 0);
        await harness.WriteFileAsync("empty.md", "", source: 1);

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Succeeded);
        job.FilesSkipped.ShouldBe(2, "the nested empty file is skipped once per source that saw it");
        ShouldAddUp(job, expectedTotal: 3);
    }

    [Fact]
    public async Task ProgressNeverReadsPastOne()
    {
        // What the UI computes, asserted here so the invariant is stated in the terms the
        // defect was reported in rather than only in the counters it comes from.
        await using var harness = await WithMixedFilesAsync();
        await harness.RunIndexAsync();
        var job = await harness.RunIndexAsync();

        var processed = job.FilesDone + job.FilesSkipped + job.FilesFailed;
        var fraction = (double)processed / job.FilesTotal;
        fraction.ShouldBeLessThanOrEqualTo(1.0);
    }
}
