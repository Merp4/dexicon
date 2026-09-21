using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The size the walk measured survives whatever happens to the file afterwards.
///
/// It used to be written on the success and empty branches only, so a file that failed
/// to read kept the row's default of zero. On a live index the eight failures in one
/// corpus of ~1,800 documents were among its largest files, and the Files list showed
/// every one of them as empty while the failure's own detail quoted the real size. The
/// walk knows the size before the read is attempted, so nothing about the read can take
/// it away.
/// </summary>
public sealed class AFailedFileKeepsItsSizeTests
{
    /// <summary>
    /// A PDF with no trailer, which is what a truncated download is. The extractor
    /// rejects it without reading it, so this is a real read failure rather than a file
    /// that merely produced no text: those two take different branches and only one of
    /// them used to record a size.
    /// </summary>
    private static byte[] TruncatedPdf(int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        Array.Fill(bytes, (byte)'x', 9, sizeBytes - 9);
        return bytes;
    }

    [Fact]
    public async Task AFileWhoseReadFails_StillRecordsTheSizeTheWalkMeasured()
    {
        const int Size = 40_000;

        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await File.WriteAllBytesAsync(
            Path.Combine(harness.SourceDirectory, "report.pdf"), TruncatedPdf(Size));

        var job = await harness.RunIndexAsync();
        job.FilesFailed.ShouldBe(1, "the file must actually have failed to read");

        var state = await harness.StateOfAsync("report.pdf");
        state.Status.ShouldBe(FileStatus.Failed);

        await using var db = harness.NewContext();
        var file = await db.Files.SingleAsync(f => f.RelativePath == "report.pdf");
        file.SizeBytes.ShouldBe(Size,
            "the walk measured the file before the read was attempted");
    }

    /// <summary>
    /// The skip the size itself causes. Reporting it as zero bytes contradicts the
    /// reason beside it, which names the cap and the measurement that exceeded it.
    /// </summary>
    [Fact]
    public async Task AFileSkippedForBeingTooLarge_RecordsHowLargeItWas()
    {
        // Over the 262,144-byte cap that applies to everything but a document format.
        const int Size = 300_000;

        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await File.WriteAllTextAsync(
            Path.Combine(harness.SourceDirectory, "dump.txt"), new string('x', Size));

        await harness.RunIndexAsync();

        var state = await harness.StateOfAsync("dump.txt");
        state.Status.ShouldBe(FileStatus.Skipped);
        state.StatusDetail.ShouldNotBeNull().ShouldContain("size cap");

        await using var db = harness.NewContext();
        var file = await db.Files.SingleAsync(f => f.RelativePath == "dump.txt");
        file.SizeBytes.ShouldBe(Size);
    }
}
