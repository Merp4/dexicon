using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A job keeps one error string, and every reason a pass degraded has to be in it. Each
/// source that could not be reached wrote its reason there and the next one replaced it,
/// and an embedding failure at the end of the pass replaced whatever was left.
/// </summary>
public sealed class EveryReasonAJobDegradedTests
{
    [Fact]
    public async Task AnUnreachableSourceIsNotHiddenByAnEmbeddingFailure()
    {
        await using var harness = await IndexingHarness.StartAsync("gone", "here");
        await harness.WriteFileAsync("ok.txt", IndexingHarness.Prose("fine"), source: 1);
        await harness.WriteFileAsync("bad.txt", IndexingHarness.Prose("unembeddable"), source: 1);
        Directory.Delete(harness.SourceDirectories[0]);
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        harness.Embedder = new FailsOn("unembeddable");

        var job = await harness.RunIndexAsync();

        job.State.ShouldBe(JobState.Degraded);
        job.Error.ShouldNotBeNull();
        job.Error.ShouldContain("'gone' is not available");
        job.Error.ShouldContain("could not be embedded");

        // The source that needs someone to look at a mount outranks files that are retried
        // on their own next run.
        await using var db = harness.NewContext();
        (await db.Corpora.FirstAsync()).State.ShouldBe(CorpusState.Unavailable);
    }

    [Fact]
    public async Task TwoUnreachableSourcesAreBothReported()
    {
        await using var harness = await IndexingHarness.StartAsync("first", "second");
        Directory.Delete(harness.SourceDirectories[0]);
        Directory.Delete(harness.SourceDirectories[1]);
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        var job = await harness.RunIndexAsync();

        job.Error.ShouldNotBeNull();
        job.Error.ShouldContain("'first' is not available");
        job.Error.ShouldContain("'second' is not available");
    }

    [Fact]
    public async Task AReasonIsReportedOnceHoweverManySetsMetIt()
    {
        // Each chunk set is its own pass over the sources, so the same source is found
        // missing once per set.
        await using var harness = await IndexingHarness.StartAsync("gone");
        Directory.Delete(harness.SourceDirectory);
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);

        var job = await harness.RunIndexAsync();

        job.Error.ShouldNotBeNull();
        job.Error.Split("'gone' is not available").Length.ShouldBe(2);
    }

    private sealed class FailsOn(string marker) : IEmbeddingService
    {
        private readonly IndexingHarness.FixedEmbedder _inner = new();

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default) =>
            inputs.Any(i => i.Contains(marker, StringComparison.Ordinal))
                ? throw new EmbeddingUnavailableException($"refused input containing '{marker}'")
                : _inner.EmbedAsync(target, purpose, inputs, source, ct);

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            _inner.CountTokensAsync(t, text, ct);
        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            _inner.ProbeDimensionsAsync(t, ct);
        public int KnownDimensions(EmbeddingTarget t) => _inner.KnownDimensions(t);
    }
}
