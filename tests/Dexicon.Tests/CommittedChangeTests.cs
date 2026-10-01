using System.Data.Common;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dexicon.Tests;

/// <summary>
/// A change that is saved is not left without what makes it take effect.
///
/// The configuration is committed first and its job queued second, and a cancel between the
/// two left the filters changed with no refresh. Sending the same values again reads as no
/// change, so nothing queued one either. And a corpus a key creates is mapped to it in the
/// same save, so a failure between the two cannot leave one the key can neither reach nor
/// create again.
/// </summary>
public sealed class CommittedChangeTests
{
    /// <summary>
    /// Cancels the caller's token on the first command after a write to a table, which is
    /// inside the window between the save and the next step. Opening the window rather than
    /// hoping to land in it, and `Fired` is asserted, because a race test that quietly stops
    /// racing passes for ever.
    ///
    /// Armed by the test just before the call under test. Left running from the start it fired
    /// on the seed's own writes, before the token existed, and every assertion held: the first
    /// version of these tests passed with the fix reverted.
    ///
    /// The command then honours the token it was given, as a driver that supports cancellation
    /// does: SQLite's does not, so without it a cancel in this window is invisible here.
    /// </summary>
    private sealed class CancelAfterWriteTo(string table) : DbCommandInterceptor
    {
        private bool _written;

        public CancellationTokenSource? Cts { get; set; }

        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        private void Observe(DbCommand command, CancellationToken token)
        {
            if (!Armed) return;

            if (_written)
            {
                if (!Fired) Cts?.Cancel();
                Fired = true;
                token.ThrowIfCancellationRequested();
                return;
            }

            var text = command.CommandText;
            if ((text.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                 || text.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
                && text.Contains(table, StringComparison.OrdinalIgnoreCase))
                _written = true;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, cancellationToken);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command, cancellationToken);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Defaults_that_are_saved_still_queue_their_refresh_when_the_caller_is_cancelled()
    {
        var watcher = new CancelAfterWriteTo("corpora");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        watcher.Armed = true;

        var outcome = await harness.NewConfiguration(db).UpdateCorpusAsync(corpus,
            new UpdateCorpusRequest(Defaults: new CorpusDefaults(null, null, null, ["**/bin/**"])), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        outcome.Refusal.ShouldBeNull();
        outcome.Value.ShouldBeTrue();
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(1, "the saved change has its job");
    }

    [Fact]
    public async Task A_source_that_is_saved_still_queues_its_refresh_when_the_caller_is_cancelled()
    {
        var watcher = new CancelAfterWriteTo("sources");
        await using var harness = await IndexingHarness.StartAsync(watcher, "notes", "docs");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        Directory.CreateDirectory(Path.Combine(harness.DataPath, "workspace", "extra"));
        using var cts = new CancellationTokenSource();
        watcher.Cts = cts;
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        watcher.Armed = true;

        var outcome = await harness.NewConfiguration(db).AddSourceAsync(corpus, new AddSourceRequest("extra"), cts.Token);

        watcher.Fired.ShouldBeTrue("the window has to have been opened");
        outcome.Refusal.ShouldBeNull();
        (await db.Sources.CountAsync(s => s.RootPath == "extra")).ShouldBe(1);
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(1, "the saved source has its job");
    }

    [Fact]
    public async Task A_corpus_made_for_a_mapped_key_joins_its_mapping_and_one_made_for_an_unmapped_key_does_not()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var (mapped, _) = await tokens.CreateAsync("mapped", [Scopes.Configure], null);
        var (unmapped, _) = await tokens.CreateAsync("unmapped", [Scopes.Configure], null);
        db.TokenCorpora.Add(new TokenCorpus { TokenId = mapped.Id, CorpusId = IndexingHarness.CorpusId });
        await db.SaveChangesAsync();
        var config = harness.NewConfiguration(db);

        var forMapped = (await config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, mapped.Id)).Value!;
        await config.CreateCorpusAsync(new CreateCorpusRequest("books"), default, unmapped.Id);
        await config.CreateCorpusAsync(new CreateCorpusRequest("misc"), default);

        (await db.TokenCorpora.Where(tc => tc.TokenId == mapped.Id).Select(tc => tc.CorpusId).ToListAsync())
            .ShouldBe([IndexingHarness.CorpusId, forMapped.Id], ignoreOrder: true);
        (await db.TokenCorpora.CountAsync(tc => tc.TokenId == unmapped.Id)).ShouldBe(0, "no mapping means every corpus");
    }
}
