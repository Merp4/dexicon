using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A file the catalogue records as having no chunks must not still have vectors.
///
/// Every route to that row is an early exit that skips the success path's delete, and
/// the reconcile pass at the end of a job only removes files the walk stopped seeing.
/// A file that is still on disk and still walked is therefore nobody's job to clean up,
/// so it kept answering searches with text it no longer contains.
/// </summary>
public sealed class EmptyFileLosesItsVectorsTests
{
    [Fact]
    public async Task AFileWhoseTextDisappears_LosesTheVectorsItHad()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("retrieval"));

        var first = await harness.RunIndexAsync();
        first.State.ShouldBe(JobState.Succeeded);
        harness.Vectors.CountFor("note.md")
            .ShouldBeGreaterThan(0, "the first run must actually index something");

        // Whitespace, not zero bytes: the file is still walked and still extracted, and
        // the extraction is what comes back empty. This is the branch a scanned PDF and
        // an extractor regression both arrive at.
        await harness.WriteFileAsync("note.md", "   \n\n   \n");

        await harness.RunIndexAsync();

        var state = await harness.StateOfAsync("note.md");
        state.Status.ShouldBe(FileStatus.Empty);
        state.ChunkCount.ShouldBe(0);
        harness.Vectors.CountFor("note.md").ShouldBe(0,
            "the catalogue says the file has no chunks, so no chunk of it may still be searchable");
    }

    [Fact]
    public async Task AFileTruncatedToNothing_LosesTheVectorsItHad()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("note.md", IndexingHarness.Prose("chunking"));

        await harness.RunIndexAsync();
        harness.Vectors.CountFor("note.md").ShouldBeGreaterThan(0);

        // Zero bytes is skipped by the walk itself, before extraction, so it reaches the
        // catalogue down a different branch from the one above: Skipped, not Empty. The
        // file is still there, so the reconcile pass never considers it gone either.
        await harness.WriteFileAsync("note.md", "");

        await harness.RunIndexAsync();

        var state = await harness.StateOfAsync("note.md");
        state.Status.ShouldBe(FileStatus.Skipped);
        state.ChunkCount.ShouldBe(0);
        harness.Vectors.CountFor("note.md").ShouldBe(0);
    }

    [Fact]
    public async Task AnUploadedDocumentThatExtractsToNothing_LosesTheVectorsItHad()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Upload);

        await using (var db = harness.NewContext())
        {
            var documents = harness.NewDocumentService(db);

            var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
                .FirstAsync(c => c.Id == IndexingHarness.CorpusId);
            using var bytes = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(IndexingHarness.Prose("embedding")));
            var stored = await documents.StoreAsync(bytes, "paper.md");
            await documents.AttachAsync(corpus, stored.Sha256, "paper.md");
        }

        await harness.RunIndexAsync();
        harness.Vectors.CountFor("paper.md").ShouldBeGreaterThan(0);

        // What an extractor that now yields nothing leaves behind. Written straight to
        // the cache at the CURRENT version, so the read path returns it rather than
        // re-extracting: the bytes are unchanged, and it is the extraction that changed.
        await using (var db = harness.NewContext())
        {
            var text = await db.BlobTexts.FirstAsync();
            text.Text = "";
            text.ExtractedChars = 0;
            await db.SaveChangesAsync();
        }

        await harness.RunIndexAsync();

        var state = await harness.StateOfAsync("paper.md");
        state.Status.ShouldBe(FileStatus.Empty);
        state.ChunkCount.ShouldBe(0);
        harness.Vectors.CountFor("paper.md").ShouldBe(0,
            "uploads have no reconcile pass, so this branch is the only thing that can remove them");
    }
}
