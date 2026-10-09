using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A detach and an attachment of the same document at the same moment. An attachment that has found the
/// file saves its rename or replacement against that row, and a detach that deleted the row between the
/// two made the save fail. The detach takes the lock attaching holds, so the second of the two waits for
/// the first.
/// </summary>
public sealed class DetachingWhileAttachingTests
{
    private static bool IsAFileLookup(string sql) =>
        sql.Contains("FROM \"files\"", StringComparison.Ordinal)
        && sql.Contains("LIMIT 1", StringComparison.Ordinal);

    [Fact]
    public async Task TheGateHoldsAnAttachmentLookupAndADetachLookupAtOnce()
    {
        // The control for the test below: with nothing in the way, the gate does put the two shapes of
        // lookup inside the window together.
        var gate = new HoldTheLookup(TimeSpan.FromSeconds(10), IsAFileLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        gate.Armed = true;

        await Task.WhenAll(
            first.Files.FirstOrDefaultAsync(f => f.SourceId == "s" && f.BlobSha256 == "abc"),
            second.Files.Include(f => f.Source).FirstOrDefaultAsync(f => f.Id == "x" && f.Source!.CorpusId == "c"));

        gate.Met.ShouldBeTrue("the gate has to have held both lookups at once");
    }

    [Fact]
    public async Task ADetachWaitsForAnAttachmentOfTheSameDocumentAndNeitherFails()
    {
        var gate = new HoldTheLookup(TimeSpan.FromMilliseconds(750), IsAFileLookup);
        await using var harness = await IndexingHarness.StartAsync(gate, "notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        await using var first = harness.NewContext();
        await using var second = harness.NewContext();
        var attaching = harness.NewDocumentService(first);
        var detaching = harness.NewDocumentService(second);
        var stored = await attaching.StoreAsync(new MemoryStream("one document"u8.ToArray()), "doc.txt");
        var corpus = await first.Corpora.SingleAsync();
        var attached = await attaching.AttachAsync(corpus, stored.Sha256, "doc.txt");
        gate.Armed = true;

        // Attached under another name is the rename an attachment saves against the row it found.
        await Task.WhenAll(
            attaching.AttachAsync(corpus, stored.Sha256, "renamed.txt"),
            detaching.DetachAsync(IndexingHarness.CorpusId, attached.Id));

        gate.Arrivals.ShouldBeGreaterThanOrEqualTo(2, "both have to have looked for the file");
        gate.Met.ShouldBeFalse("the second of the two was held back until the first had finished");
    }
}
