using System.Data.Common;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dexicon.Tests;

/// <summary>
/// A corpus has at most one upload source, created by the first document attached to it.
///
/// Nothing in the schema says so, and the source was looked for and then added with no lock between the
/// two. Two first attachments at the same moment, from two requests with a context each, both found none
/// and each added one. Every upload source is indexed on its own, so nothing failed: the corpus listed
/// two, and a document attached under both was chunked, embedded and returned twice.
/// </summary>
public sealed class UploadSourceUniquenessTests
{
    /// <summary>
    /// Holds the lookup of a corpus's upload source until two have asked, or until it has waited
    /// <c>patience</c>. That puts two requests inside the window between looking and adding, which is
    /// otherwise a matter of luck. Opening the window and asserting that both reached it, because a race
    /// test whose gate stopped matching would pass for ever.
    ///
    /// When the second never comes, as when it is queued behind a lock the first holds, the first goes on
    /// after <c>patience</c> and the second then finds what the first added.
    /// </summary>
    internal sealed class HoldTheSourceLookup(TimeSpan patience) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public bool Armed { get; set; }

        /// <summary>How many lookups reached the gate.</summary>
        public int Arrivals => Volatile.Read(ref _arrivals);

        /// <summary>Whether two lookups were held at once.</summary>
        public bool Met => _both.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && IsTheLookup(command))
            {
                if (Interlocked.Increment(ref _arrivals) >= 2) _both.TrySetResult();
                await Task.WhenAny(_both.Task, Task.Delay(patience, cancellationToken));
            }

            return result;
        }

        private static bool IsTheLookup(DbCommand command) =>
            command.CommandText.Contains("FROM \"sources\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"Kind\"", StringComparison.Ordinal)
            && command.CommandText.Contains("LIMIT 1", StringComparison.Ordinal);
    }

    private static Task<Source?> LookUpTheUploadSourceAsync(CatalogDbContext db) =>
        db.Sources.FirstOrDefaultAsync(s => s.CorpusId == IndexingHarness.CorpusId && s.Kind == SourceKind.Upload);

    [Fact]
    public async Task TheGateHoldsTwoReadersOfTheUploadSourceAtOnce()
    {
        // The control for the test below: with nothing in the way, the gate does put two lookups inside
        // the window, and both find no source.
        var gate = new HoldTheSourceLookup(TimeSpan.FromSeconds(10));
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        gate.Armed = true;

        var found = await Task.WhenAll(LookUpTheUploadSourceAsync(first), LookUpTheUploadSourceAsync(second));

        gate.Met.ShouldBeTrue("the gate has to have held both lookups at once");
        found.ShouldAllBe(s => s == null);
    }

    [Fact]
    public async Task TwoFirstAttachmentsToOneCorpusAtTheSameTimeCreateOneUploadSource()
    {
        var gate = new HoldTheSourceLookup(TimeSpan.FromMilliseconds(750));
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var firstDocuments = harness.NewDocumentService(first);
        var secondDocuments = harness.NewDocumentService(second);
        var one = await firstDocuments.StoreAsync(new MemoryStream("the first document"u8.ToArray()), "one.txt");
        var two = await secondDocuments.StoreAsync(new MemoryStream("the second document"u8.ToArray()), "two.txt");
        var firstCorpus = await first.Corpora.SingleAsync();
        var secondCorpus = await second.Corpora.SingleAsync();
        gate.Armed = true;

        await Task.WhenAll(
            firstDocuments.AttachAsync(firstCorpus, one.Sha256, "one.txt"),
            secondDocuments.AttachAsync(secondCorpus, two.Sha256, "two.txt"));

        gate.Arrivals.ShouldBe(2, "both attachments have to have looked for the source");
        await using var check = harness.NewContext();
        var uploads = await check.Sources.Where(s => s.Kind == SourceKind.Upload).ToListAsync();
        uploads.Count.ShouldBe(1, "a corpus has one upload source");
        (await check.Files.CountAsync(f => f.SourceId == uploads[0].Id)).ShouldBe(2, "both documents are attached to it");
    }

    [Fact]
    public async Task TwoAttachmentsOfOneDocumentAtTheSameTimeAreOneAttachment()
    {
        // The same lookup-then-add held for the document: "one blob, one attachment per corpus" is a
        // check made before the insert, and two requests could both pass it.
        var gate = new HoldTheSourceLookup(TimeSpan.FromMilliseconds(750));
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var firstDocuments = harness.NewDocumentService(first);
        var secondDocuments = harness.NewDocumentService(second);
        var stored = await firstDocuments.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var firstCorpus = await first.Corpora.SingleAsync();
        var secondCorpus = await second.Corpora.SingleAsync();
        gate.Armed = true;

        await Task.WhenAll(
            firstDocuments.AttachAsync(firstCorpus, stored.Sha256, "doc.txt"),
            secondDocuments.AttachAsync(secondCorpus, stored.Sha256, "renamed.txt"));

        gate.Arrivals.ShouldBe(2, "both attachments have to have looked for the source");
        await using var check = harness.NewContext();
        (await check.Files.CountAsync(f => f.BlobSha256 == stored.Sha256)).ShouldBe(1, "one attachment of one blob per corpus");
    }
}
