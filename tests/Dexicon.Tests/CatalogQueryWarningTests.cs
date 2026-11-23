using Dexicon.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace Dexicon.Tests;

/// <summary>
/// Entity Framework Core warns when one query loads two collections of the same row, because the
/// single join it builds repeats every row of the one collection for each row of the other. A pass
/// loads a corpus with its sources and its chunk sets, which is such a query, and the warning was
/// logged on the first scheduled refresh after every start. A warning that is always there hides
/// the one that is not.
/// </summary>
public sealed class CatalogQueryWarningTests
{
    private sealed class WarningLog : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Category(this, categoryName);

        public void Dispose() { }

        private sealed class Category(WarningLog owner, string name) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    lock (owner.Warnings) owner.Warnings.Add($"{name}: {formatter(state, exception)}");
            }
        }
    }

    [Fact]
    public async Task AnIndexPassOverACorpusWithSeveralChunkSetsLogsNoWarning()
    {
        var log = new WarningLog();
        await using var harness = await IndexingHarness.StartWithLogsAsync(log);
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("alpha"));

        var job = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Succeeded);
        job.FilesDone.ShouldBeGreaterThan(0, "the pass read the file, so the query that loads the corpus has run");
        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadingAFileBackByItsPathLogsNoWarning()
    {
        // get_context and the reason for an empty result look a file up by its path, taking two rows to tell one
        // file from a path that two sources share. Without an order, Entity Framework Core warned that the rows
        // a row limit returns are unpredictable, once per process on the first get_context.
        var log = new WarningLog();
        await using var harness = await IndexingHarness.StartWithLogsAsync(log);
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("alpha"));
        (await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken)).State.ShouldBe(JobState.Succeeded);

        await using var db = harness.NewContext();
        var reader = new Dexicon.Core.Documents.DocumentReader(db);
        var status = await reader.StatusAtAsync(IndexingHarness.CorpusId, "set-1", "note.md", TestContext.Current.CancellationToken);
        await reader.ForAsync(IndexingHarness.CorpusId, "set-1", "note.md", sourceId: null, TestContext.Current.CancellationToken);

        status.ShouldNotBeNull("the path names one file, so the lookup found it").Status.ShouldBe(FileStatus.Indexed);
        log.Warnings.ShouldBeEmpty();
    }
}
