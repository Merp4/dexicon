using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dexicon.Tests;

/// <summary>
/// What an upload's chunk state is stamped with. For text that is current and reads back whole it is the
/// value every state holds from 0.6.8, so an upgrade chunks nothing again. For older or damaged text the
/// stamp names the version and length that were chunked, so it never equals the stamp of the text that
/// replaces it.
/// </summary>
public sealed class UploadFingerprintTests
{
    private const string Sha = "0123456789abcdef";

    private static BlobText Row(int version, string text, int? writtenChars = null) => new()
    {
        Sha256 = Sha,
        Text = text,
        ExtractedChars = writtenChars ?? text.Length,
        ExtractorVersion = version,
        Extractor = "PlainText",
    };

    [Fact]
    public void TheKeyOfCurrentWholeTextIsTheHashAlone()
    {
        CorpusIndexer.TextKey(Row(ExtractorVersions.Current, "whole text")).ShouldBe(Sha);
    }

    [Fact]
    public void TheKeyOfTextWrittenByALaterBuildIsTheHashAlone()
    {
        // A rollback reads a row a newer build wrote, and does not extract it again.
        CorpusIndexer.TextKey(Row(ExtractorVersions.Current + 1, "whole text")).ShouldBe(Sha);
    }

    [Fact]
    public void TheKeyOfStaleTextDiffersFromTheHashAndNamesTheVersion()
    {
        var older = Row(ExtractorVersions.Current - 1, "whole text");
        var oldest = Row(ExtractorVersions.Current - 2, "whole text");

        CorpusIndexer.TextKey(older).ShouldNotBe(Sha);
        CorpusIndexer.TextKey(older).ShouldNotBe(CorpusIndexer.TextKey(oldest), "two stale versions of the same length");
    }

    [Fact]
    public void TheKeyOfStaleTextNamesTheLengthToo()
    {
        var shorter = Row(ExtractorVersions.Current - 1, "short");
        var longer = Row(ExtractorVersions.Current - 1, "a longer text");

        CorpusIndexer.TextKey(shorter).ShouldNotBe(CorpusIndexer.TextKey(longer), "one version, two lengths");
    }

    [Fact]
    public void TheKeyOfDamagedTextDiffersFromTheHashWhateverItsVersion()
    {
        var damaged = Row(ExtractorVersions.Current, "cut", writtenChars: 300);

        CorpusIndexer.TextKey(damaged).ShouldNotBe(Sha);
        CorpusIndexer.TextKey(damaged).ShouldNotBe(
            CorpusIndexer.TextKey(Row(ExtractorVersions.Current, "cut text", writtenChars: 300)),
            "one version, two lengths of cut text");
    }

    [Fact]
    public async Task AStateStampedWithThePlainFingerprintIsSkippedForCurrentWholeText()
    {
        // The value 0.6.8 wrote: the fingerprint built on the hash itself. Nothing is chunked again after
        // the upgrade when the text is current.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        string sha;
        await using (var setup = harness.NewContext())
        {
            var documents = harness.NewDocumentService(setup);
            var stored = await documents.StoreAsync(new MemoryStream("some current text to index"u8.ToArray()), "doc.txt");
            sha = stored.Sha256;
            await documents.AttachAsync(await setup.Corpora.SingleAsync(), sha, "doc.txt");
        }

        var first = await harness.RunIndexAsync();

        await using (var check = harness.NewContext())
        {
            var set = await check.ChunkSets.FirstAsync();
            var state = await check.FileChunkStates.AsNoTracking().SingleAsync();
            var row = await check.BlobTexts.AsNoTracking().SingleAsync();
            CorpusIndexer.TextKey(row).ShouldBe(row.Sha256);
            state.ContentHash.ShouldBe(CorpusIndexer.ChunkingFingerprint(set, sha, ModelTemplates.Raw),
                "the stamp of a current row is the fingerprint built on the hash alone");
        }

        var again = await harness.RunIndexAsync();

        first.FilesDone.ShouldBe(1);
        again.FilesDone.ShouldBe(0, "nothing changed");
        again.FilesSkipped.ShouldBe(1);
    }
}
