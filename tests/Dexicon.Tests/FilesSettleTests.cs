using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Core;

namespace Dexicon.Tests;

/// <summary>
/// A file a pass has finished with is not opened on the next one while its size and modified
/// time and the settings it was finished under are what they were.
///
/// Every pass used to read every byte of every file, hash it, load its cached text and compare
/// a fingerprint, to learn that nothing had changed. Measured from the job history of a live
/// instance: 58 s for a corpus of 242 books, 67 s for 6,566 files of code and 94 s for 1,834
/// papers, each tick, which is about 3.6 minutes of reading every ten. A file whose read failed
/// the same way every time was opened and logged every tick as well.
///
/// Each case here shows one rule. The size and time are what stand in for the bytes, so a file
/// rewritten to a different content of the same size and the same time is not noticed: a test
/// states that, because it is the price of not reading, and a full pass is how to pay it back.
/// </summary>
public sealed class FilesSettleTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<(IndexingHarness Harness, CountingEmbedder Embedder)> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var embedder = new CountingEmbedder();
        harness.Embedder = embedder;
        return (harness, embedder);
    }

    /// <summary>Written, then stamped, so a test decides what the walk will see.</summary>
    private static async Task WriteAsync(IndexingHarness harness, string name, string text, DateTime? at = null)
    {
        await harness.WriteFileAsync(name, text);
        File.SetLastWriteTimeUtc(Path.Combine(harness.SourceDirectory, name), at ?? Stamp);
    }

    [Fact]
    public async Task A_file_that_has_not_changed_is_not_opened_again()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));

        var first = await harness.RunIndexAsync();
        first.FilesDone.ShouldBe(1);
        var embedded = embedder.Inputs;
        embedded.ShouldBeGreaterThan(0);

        // Different bytes, the same length and the same time. Had the file been opened this
        // would be re-chunked and re-embedded; that it is not is how the test sees the read
        // was skipped, and it is also the price of the shortcut.
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("omega"));

        var second = await harness.RunIndexAsync();

        second.FilesSkipped.ShouldBe(1);
        second.FilesDone.ShouldBe(0);
        embedder.Inputs.ShouldBe(embedded, "nothing was embedded, so the file was not read");
    }

    [Fact]
    public async Task A_file_whose_time_changed_but_not_its_text_is_read_and_not_embedded_again()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));
        await harness.RunIndexAsync();
        var embedded = embedder.Inputs;
        var settled = (await harness.StateOfAsync("note.md")).SettledFor;
        settled.ShouldNotBeNull();

        // Touched. The key no longer matches, so the file is read, its text is found to be what
        // was indexed, and the key is rewritten for the time it has now.
        File.SetLastWriteTimeUtc(Path.Combine(harness.SourceDirectory, "note.md"), Stamp.AddHours(1));
        var second = await harness.RunIndexAsync();

        second.FilesSkipped.ShouldBe(1);
        embedder.Inputs.ShouldBe(embedded, "the text was the same, so nothing is embedded again");
        var renewed = (await harness.StateOfAsync("note.md")).SettledFor;
        renewed.ShouldNotBeNull().ShouldNotBe(settled);

        // And the next pass leaves it unopened on the new time, shown the same way as above.
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("omega"), Stamp.AddHours(1));
        await harness.RunIndexAsync();
        embedder.Inputs.ShouldBe(embedded);
    }

    [Fact]
    public async Task A_file_that_changed_is_indexed_again()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));
        await harness.RunIndexAsync();
        var embedded = embedder.Inputs;

        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha") + "\n\nA new paragraph.",
            Stamp.AddMinutes(5));
        var second = await harness.RunIndexAsync();

        second.FilesDone.ShouldBe(1);
        embedder.Inputs.ShouldBeGreaterThan(embedded);
    }

    [Fact]
    public async Task A_changed_chunking_setting_indexes_a_settled_file_again()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));
        await harness.RunIndexAsync();
        var embedded = embedder.Inputs;

        await using (var db = harness.NewContext())
            await db.ChunkSets.ExecuteUpdateAsync(u => u.SetProperty(s => s.ChunkSize, 64));

        var second = await harness.RunIndexAsync();

        second.FilesDone.ShouldBe(1, "the file did not change, but what is made of it did");
        embedder.Inputs.ShouldBeGreaterThan(embedded);
    }

    [Fact]
    public async Task A_full_pass_reads_every_file_whatever_it_is_settled_for()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));
        await harness.RunIndexAsync();
        var embedded = embedder.Inputs;

        var full = await harness.RunIndexAsync(JobKind.Full);

        full.FilesDone.ShouldBe(1);
        embedder.Inputs.ShouldBeGreaterThan(embedded);
    }

    [Fact]
    public async Task A_file_that_failed_for_a_reason_that_may_pass_is_tried_again()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));

        embedder.Down = true;
        var first = await harness.RunIndexAsync();
        first.FilesFailed.ShouldBe(1);
        var failed = await harness.StateOfAsync("note.md");
        failed.Status.ShouldBe(FileStatus.Failed);
        failed.SettledFor.ShouldBeNull("a service that was down says nothing about the file");

        embedder.Down = false;
        var second = await harness.RunIndexAsync();

        second.FilesDone.ShouldBe(1);
        (await harness.StateOfAsync("note.md")).Status.ShouldBe(FileStatus.Indexed);
    }

    [Fact]
    public async Task A_settled_file_that_fails_on_a_later_pass_is_not_left_settled()
    {
        // The pass that touches a row unsettles it. A full pass reprocesses a file that was
        // settled, the embedding service is down, and the row must not keep the key from
        // before: the next ordinary pass would skip a file whose last attempt failed.
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));
        await harness.RunIndexAsync();
        (await harness.StateOfAsync("note.md")).SettledFor.ShouldNotBeNull();

        embedder.Down = true;
        var full = await harness.RunIndexAsync(JobKind.Full);
        full.FilesFailed.ShouldBe(1);
        var failed = await harness.StateOfAsync("note.md");
        failed.Status.ShouldBe(FileStatus.Failed);
        failed.SettledFor.ShouldBeNull();

        embedder.Down = false;
        var next = await harness.RunIndexAsync();

        next.FilesDone.ShouldBe(1);
        (await harness.StateOfAsync("note.md")).Status.ShouldBe(FileStatus.Indexed);
    }

    [Fact]
    public async Task A_file_that_failed_to_read_and_would_again_is_left_alone_until_it_changes()
    {
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        var logs = new RecordingLoggerFactory();
        var path = Path.Combine(harness.SourceDirectory, "report.pdf");

        // No trailer, which is what a truncated download is: the same on every read.
        await File.WriteAllBytesAsync(path, Truncated(40_000));
        File.SetLastWriteTimeUtc(path, Stamp);

        var first = await harness.RunIndexAsync(log: logs.CreateLogger<CorpusIndexer>());
        first.FilesFailed.ShouldBe(1);
        var state = await harness.StateOfAsync("report.pdf");
        state.Status.ShouldBe(FileStatus.Failed);
        state.ContentHash.ShouldBeNull("a failure is never recorded as indexed");
        state.SettledFor.ShouldNotBeNull();
        Said(logs, "Extraction failed for").ShouldBe(1);

        var second = await harness.RunIndexAsync(log: logs.CreateLogger<CorpusIndexer>());

        second.FilesFailed.ShouldBe(0, "it was not opened, so it did not fail again");
        second.FilesSkipped.ShouldBe(1);
        Said(logs, "Extraction failed for").ShouldBe(1, "and the log does not say it again");
        (await harness.StateOfAsync("report.pdf")).Status.ShouldBe(FileStatus.Failed, "the row still says why");

        // Replaced by another truncated file, which is a change and is looked at.
        await File.WriteAllBytesAsync(path, Truncated(41_000));
        File.SetLastWriteTimeUtc(path, Stamp.AddMinutes(5));
        var third = await harness.RunIndexAsync(log: logs.CreateLogger<CorpusIndexer>());

        third.FilesFailed.ShouldBe(1);
        Said(logs, "Extraction failed for").ShouldBe(2);
    }

    [Fact]
    public async Task A_file_with_nothing_to_index_is_settled_too()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "blank.md", "   \n\n   \n");

        await harness.RunIndexAsync();

        var state = await harness.StateOfAsync("blank.md");
        state.Status.ShouldBe(FileStatus.Empty);
        state.SettledFor.ShouldNotBeNull();

        // Rewritten to something with text in it, of the same length and at the same time. A
        // pass that opened the file would index it; this one leaves it as it was.
        await WriteAsync(harness, "blank.md", "ab cd ef\n");

        var second = await harness.RunIndexAsync();

        second.FilesSkipped.ShouldBe(1);
        embedder.Inputs.ShouldBe(0, "the file was not opened, so the text now in it was not seen");
        (await harness.StateOfAsync("blank.md")).Status.ShouldBe(FileStatus.Empty);
    }

    [Fact]
    public async Task A_row_indexed_before_keys_existed_is_read_once_and_settled()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await WriteAsync(harness, "note.md", IndexingHarness.Prose("alpha"));
        await harness.RunIndexAsync();
        var embedded = embedder.Inputs;

        // What the catalogue held after the upgrade: indexed, with no key.
        await using (var db = harness.NewContext())
            await db.FileChunkStates.ExecuteUpdateAsync(u => u.SetProperty(s => s.SettledFor, (string?)null));

        var second = await harness.RunIndexAsync();

        second.FilesSkipped.ShouldBe(1);
        embedder.Inputs.ShouldBe(embedded, "read and compared, and found unchanged");
        (await harness.StateOfAsync("note.md")).SettledFor.ShouldNotBeNull("so the pass after this one skips the read");
    }

    [Theory]
    [InlineData(typeof(PdfDocumentFormatException), true)]
    [InlineData(typeof(InvalidDataException), true)]
    [InlineData(typeof(System.Xml.XmlException), true)]
    [InlineData(typeof(IOException), false)]
    [InlineData(typeof(UnauthorizedAccessException), false)]
    [InlineData(typeof(OutOfMemoryException), false)]
    [InlineData(typeof(ArgumentException), false)]
    public void A_failure_repeats_on_the_same_bytes_only_when_the_file_is_what_failed(Type cause, bool repeats)
    {
        var inner = (Exception)Activator.CreateInstance(cause, "cause")!;

        CorpusIndexer.RepeatsOnTheSameBytes(new ExtractionFailedException("could not be read", inner))
            .ShouldBe(repeats);
    }

    [Fact]
    public void A_failure_with_no_cause_of_its_own_repeats_and_a_timeout_does_not()
    {
        CorpusIndexer.RepeatsOnTheSameBytes(new ExtractionFailedException("has no trailer")).ShouldBeTrue();
        CorpusIndexer.RepeatsOnTheSameBytes(new ExtractionTimeoutException("too slow")).ShouldBeFalse();
    }

    private static int Said(RecordingLoggerFactory logs, string text)
    {
        lock (logs.Lines) return logs.Lines.Count(l => l.Contains(text));
    }

    private static byte[] Truncated(int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        Array.Fill(bytes, (byte)'x', 9, sizeBytes - 9);
        return bytes;
    }

    /// <summary>Counts what is embedded, and refuses when told the service is down.</summary>
    private sealed class CountingEmbedder : IEmbeddingService
    {
        private readonly IndexingHarness.FixedEmbedder _inner = new();
        private int _inputs;

        public bool Down { get; set; }
        public int Inputs => Volatile.Read(ref _inputs);

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            if (Down) throw new EmbeddingUnavailableException("the embedding service is down");

            Interlocked.Add(ref _inputs, inputs.Count);
            return _inner.EmbedAsync(target, purpose, inputs, source, ct);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            _inner.CountTokensAsync(t, text, ct);
        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            _inner.ProbeDimensionsAsync(t, ct);
        public int KnownDimensions(EmbeddingTarget t) => _inner.KnownDimensions(t);
    }
}
