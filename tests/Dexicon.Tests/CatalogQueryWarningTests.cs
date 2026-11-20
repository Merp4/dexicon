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
    public async Task AnExportOfACorpusWithSeveralSourcesAndChunkSetsLogsNoWarning()
    {
        var log = new WarningLog();
        await using var harness = await IndexingHarness.StartWithLogsAsync(log, "a", "b");
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await using var db = harness.NewContext();

        var export = await Dexicon.Api.ConfigEndpoints.BuildAsync(db, DateTime.UtcNow, "test", TestContext.Current.CancellationToken);

        export.Corpora.ShouldHaveSingleItem().Sources.Count.ShouldBe(2, "the query that loads the corpus has run, and run once for each source");
        log.Warnings.ShouldBeEmpty();
    }
}
