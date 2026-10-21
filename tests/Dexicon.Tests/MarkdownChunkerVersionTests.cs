using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A change to what the chunker makes of Markdown re-chunks Markdown files and no others. Bumping
/// <see cref="CodeChunker.Version"/> would re-embed every file of every corpus to correct a payload field
/// on the Markdown ones, since the key of every file carries that version.
/// </summary>
public sealed class MarkdownChunkerVersionTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string Note = "# Alpha\n\nFirst paragraph.\n\n## Beta\n\nSecond paragraph.\n";

    [Theory]
    [InlineData("notes.md")]
    [InlineData("docs/README.markdown")]
    public void MarkdownFilesHaveAChunkerVersionOfTheirOwn(string path) =>
        CodeChunker.VersionFor(path).ShouldNotBe(CodeChunker.VersionFor("notes.txt"));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("Program.cs")]
    [InlineData("book.pdf")]
    [InlineData("page.html")]
    [InlineData("commits/2026-10-01-0123456789ab")]
    [InlineData("")]
    public void OtherFilesKeepTheVersionEveryFileHadBefore(string path) =>
        CodeChunker.VersionFor(path).ShouldBe(CodeChunker.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task TheKeyOfAMarkdownFileDiffersFromTheKeyOfTheSameBytesUnderAnotherName()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await using var db = harness.NewContext();
        var set = await db.ChunkSets.SingleAsync();

        var asMarkdown = CorpusIndexer.ChunkingFingerprint(set, "blob", ModelTemplates.Raw, "a.md");
        var asText = CorpusIndexer.ChunkingFingerprint(set, "blob", ModelTemplates.Raw, "a.txt");

        asMarkdown.ShouldNotBe(asText);
        asText.ShouldBe(CorpusIndexer.ChunkingFingerprint(set, "blob", ModelTemplates.Raw, "b.cs"),
            "two formats other than Markdown share one key");
    }

    [Fact]
    public async Task AMarkdownFileIndexedUnderTheEarlierVersionIsChunkedAgainOnceAndTheRestAreNot()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);

        // The same bytes under two names with the same time. A text file's key is what the earlier version
        // wrote for either, since the two names differ only in the extension.
        await WriteAsync(harness, "note.md", Note);
        await WriteAsync(harness, "note.txt", Note);
        var first = await harness.RunIndexAsync();
        first.FilesDone.ShouldBe(2);

        var markdown = await harness.StateOfAsync("note.md");
        var text = await harness.StateOfAsync("note.txt");
        markdown.ContentHash.ShouldNotBe(text.ContentHash);
        markdown.SettledFor.ShouldNotBe(text.SettledFor);
        (await SectionsOf(harness, "note.md")).ShouldBe(["Alpha"]);

        // Put the Markdown file's record back as the earlier version left it.
        await using (var db = harness.NewContext())
        {
            var fileId = await db.Files.Where(f => f.RelativePath == "note.md").Select(f => f.Id).SingleAsync();
            await db.FileChunkStates.Where(s => s.FileId == fileId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ContentHash, text.ContentHash)
                .SetProperty(s => s.SettledFor, text.SettledFor));
        }

        var upgraded = await harness.RunIndexAsync();

        upgraded.FilesDone.ShouldBe(1, "the Markdown file");
        upgraded.FilesSkipped.ShouldBe(1, "the text file, which is what it was");
        (await harness.StateOfAsync("note.md")).ContentHash.ShouldBe(markdown.ContentHash);
        (await harness.StateOfAsync("note.txt")).ContentHash.ShouldBe(text.ContentHash);
        (await SectionsOf(harness, "note.md")).ShouldBe(["Alpha"]);

        var settled = await harness.RunIndexAsync();

        settled.FilesDone.ShouldBe(0, "the Markdown file is settled under its new key");
        settled.FilesSkipped.ShouldBe(2);
    }

    [Fact]
    public async Task AnUploadedMarkdownDocumentIsChunkedAgainWhenItsStateHoldsTheEarlierKey()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Upload);
        string sha;
        await using (var setup = harness.NewContext())
        {
            var documents = harness.NewDocumentService(setup);
            var stored = await documents.StoreAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Note)), "doc.md");
            sha = stored.Sha256;
            await documents.AttachAsync(await setup.Corpora.SingleAsync(), sha, "doc.md");
        }

        (await harness.RunIndexAsync()).FilesDone.ShouldBe(1);

        await using (var db = harness.NewContext())
        {
            var set = await db.ChunkSets.AsNoTracking().FirstAsync();
            var state = await db.FileChunkStates.AsNoTracking().SingleAsync();
            var earlier = CorpusIndexer.ChunkingFingerprint(set, sha, ModelTemplates.Raw, "doc.txt");
            state.ContentHash.ShouldBe(CorpusIndexer.ChunkingFingerprint(set, sha, ModelTemplates.Raw, "doc.md"));
            state.ContentHash.ShouldNotBe(earlier);

            await db.FileChunkStates.ExecuteUpdateAsync(u => u.SetProperty(s => s.ContentHash, earlier));
        }

        (await harness.RunIndexAsync()).FilesDone.ShouldBe(1, "the key held is the one the earlier version wrote");
        (await harness.RunIndexAsync()).FilesDone.ShouldBe(0, "and it is the new key now");
    }

    private static async Task WriteAsync(IndexingHarness harness, string name, string text)
    {
        await harness.WriteFileAsync(name, text);
        File.SetLastWriteTimeUtc(Path.Combine(harness.SourceDirectory, name), Stamp);
    }

    private static async Task<IReadOnlyList<string?>> SectionsOf(IndexingHarness harness, string path) =>
        [.. (await harness.Vectors.GetFileChunksAsync(IndexingHarness.Collection, "set-1", path)).Select(h => h.Section)];
}
