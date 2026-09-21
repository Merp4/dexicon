using System.Data.Common;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A source added while a job was queued is walked by that job.
///
/// `EnqueueAsync` coalesces a refresh onto a job that is still `Queued` and answers the
/// caller with it, which is a promise that this pass covers what they asked for. The
/// corpus and its sources were read before the lease was taken and the state was set, so
/// a source added in that window was never walked, and the request that added it was
/// reported as covered by a pass that could not have seen it.
///
/// Nothing can coalesce once the row says `Running`, so reading the sources after that
/// is what makes the promise true.
/// </summary>
public sealed class SourceAddedWhileQueuedTests
{
    /// <summary>
    /// Adds a second source as the lease's conditional UPDATE runs, which is inside the
    /// window between the corpus read and the state becoming Running.
    ///
    /// Opening the window rather than hoping to land in it. `Fired` is asserted, because
    /// a race test that quietly stops racing passes for ever.
    /// </summary>
    private sealed class AddSourceWhenClaimed(string secondRoot) : DbCommandInterceptor
    {
        /// <summary>Set once the harness exists, since it chooses where the catalogue lives.</summary>
        public string DataPath { get; set; } = "";

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired
                && command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("HeldBy", StringComparison.Ordinal))
            {
                Fired = true;

                using var conn = new SqliteConnection(
                    $"Data Source={Path.Combine(DataPath, "catalog.db")}");
                conn.Open();
                using var insert = conn.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO Sources (Id, CorpusId, Kind, RootPath, CreatedUtc)
                    VALUES ('source-late', 'corpus-1', 0, $root, $now)
                    """;
                insert.Parameters.AddWithValue("$root", secondRoot);
                insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ASourceAddedAfterTheCorpusWasReadIsStillWalked()
    {
        var watcher = new AddSourceWhenClaimed("later");

        await using var harness = await IndexingHarness.StartAsync(watcher, "notes", "later");
        watcher.DataPath = harness.DataPath;
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        // The second source's row is removed again, so the pass starts with one source
        // and the interceptor puts the other one back mid-flight. Its files stay on disk.
        await using (var db = harness.NewContext())
        {
            db.Sources.Remove(await db.Sources.FindAsync(IndexingHarness.SourceIdFor(1))
                              ?? throw new InvalidOperationException("seed is wrong"));
            await db.SaveChangesAsync();
        }

        await harness.WriteFileAsync("early.md", IndexingHarness.Prose("early"));
        await harness.WriteFileAsync("late.md", IndexingHarness.Prose("late"), source: 1);

        var job = await harness.RunIndexAsync();

        watcher.Fired.ShouldBeTrue("the interceptor has to have opened the window");
        job.State.ShouldBe(JobState.Succeeded);

        harness.Vectors.CountFor("early.md").ShouldBeGreaterThan(0);
        harness.Vectors.CountFor("late.md").ShouldBeGreaterThan(0,
            "a source added while the job was queued is covered by that job, "
            + "because the caller who added it was told this job covers them");
    }
}
