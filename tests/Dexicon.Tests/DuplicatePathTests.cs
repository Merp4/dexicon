using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Search;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// One path, two sources of a corpus, two different files.
///
/// A file path is relative to its source root, so "guide" in <c>notes/</c> and "guide" in
/// <c>docs/</c> are different files that the index files under one path. A lookup by path
/// alone cannot tell them apart, and joining the chunks of both reads as one continuous
/// document. <c>get_context</c> read the source with the most chunks and said so; the file
/// endpoint and the file resource joined both and said nothing. These run all three over a
/// real indexing pass, so the chunks, the source ids and the stored documents are the ones
/// an index produces.
///
/// The smaller source is declared first and sorts first by id, so choosing by size and
/// choosing by order give different answers.
/// </summary>
public sealed class DuplicatePathTests : IAsyncLifetime
{
    private const string Warning = "2 sources in this corpus contain a file at that path.";

    private IndexingHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "docs");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static RequestContext Reader() => new()
    {
        Principal = new Principal("k1", "agent", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search }),
    };

    private static string Paragraphs(string word, int count) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"Paragraph {i} about {word}."));

    private static string Page(string word, int count) =>
        "<html><body>"
        + string.Join("", Enumerable.Range(1, count).Select(i => $"<p>Paragraph {i} about {word}.</p>"))
        + "</body></html>";

    /// <summary>
    /// The same name in both sources with different content, the second source's far longer so
    /// it holds the most chunks. Plain text is read from the mount and has no stored document;
    /// HTML is extracted and does.
    /// </summary>
    private async Task IndexBothAsync()
    {
        await _harness.WriteFileAsync("guide.md", Paragraphs("alpha", 12), source: 0);
        await _harness.WriteFileAsync("guide.md", Paragraphs("beta", 400), source: 1);
        await _harness.WriteFileAsync("manual.html", Page("alpha", 12), source: 0);
        await _harness.WriteFileAsync("manual.html", Page("beta", 400), source: 1);
        await _harness.WriteFileAsync("solo.md", Paragraphs("gamma", 12), source: 0);
        await _harness.RunIndexAsync();

        _harness.Vectors.CountFor("guide.md", sourceId: IndexingHarness.SourceIdFor(1))
            .ShouldBeGreaterThan(_harness.Vectors.CountFor("guide.md", sourceId: IndexingHarness.SourceIdFor(0)),
                "the second source has to hold the most chunks for the choice to be by size");
    }

    /// <summary>
    /// Puts a line into the second source's stored document that no chunk holds, so a
    /// read that came from the document can be told from one stitched from chunks.
    /// </summary>
    private async Task MarkTheStoredDocumentAsync(string path)
    {
        await using var db = _harness.NewContext();
        var file = await db.Files.SingleAsync(f => f.RelativePath == path && f.SourceId == IndexingHarness.SourceIdFor(1));
        var sha = (await db.FileChunkStates.SingleAsync(s => s.FileId == file.Id)).SourceSha256;
        var text = await db.FileTexts.SingleAsync(t => t.Sha256 == sha);
        text.Text = "DOCUMENT-ONLY\n" + text.Text;
        await db.SaveChangesAsync();
    }

    private async Task<string> ToolAsync(string path)
    {
        await using var db = _harness.NewContext();
        return await DexiconTools.GetContextAsync(Reader(), new ScopeResolver(db), _harness.Vectors,
            new DocumentReader(db), "notes", path, aroundLine: 1, before: 0, after: 8);
    }

    private async Task<string> ResourceAsync(string path)
    {
        await using var db = _harness.NewContext();
        return await DexiconResources.FileAsync(Reader(), new ScopeResolver(db), _harness.Vectors,
            new DocumentReader(db), "notes", path);
    }

    private async Task<IndexedFileText> EndpointAsync(string path)
    {
        await using var db = _harness.NewContext();
        var result = await CorpusEndpoints.FileAsync("notes", path, null, Reader(), new ScopeResolver(db),
            _harness.Vectors, new DocumentReader(db), default);
        return result.ShouldBeOfType<Ok<IndexedFileText>>().Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task TheToolReadsTheLargestSourceAndWarns()
    {
        await IndexBothAsync();

        var text = await ToolAsync("guide.md");

        text.ShouldContain("about beta");
        text.ShouldNotContain("about alpha");
        text.ShouldContain("! " + Warning);
    }

    [Fact]
    public async Task TheResourceReadsTheLargestSourceAndWarns()
    {
        await IndexBothAsync();

        var text = await ResourceAsync("guide.md");

        text.ShouldContain("about beta");
        text.ShouldNotContain("about alpha");
        text.ShouldContain("! " + Warning);
    }

    [Fact]
    public async Task TheEndpointReadsTheLargestSourceAndWarns()
    {
        await IndexBothAsync();

        var body = await EndpointAsync("guide.md");

        body.Text.ShouldContain("about beta");
        body.Text.ShouldNotContain("about alpha");
        body.Warning.ShouldNotBeNull().ShouldStartWith(Warning);
        body.Store.ShouldBe("chunks");
    }

    [Fact]
    public async Task TheEndpointReadsTheLargestSourcesStoredDocumentAndWarns()
    {
        await IndexBothAsync();
        await MarkTheStoredDocumentAsync("manual.html");

        var body = await EndpointAsync("manual.html");

        body.Store.ShouldBe("file_texts");
        body.Text.ShouldContain("DOCUMENT-ONLY");
        body.Text.ShouldContain("about beta");
        body.Text.ShouldNotContain("about alpha");
        body.Warning.ShouldNotBeNull().ShouldStartWith(Warning);
    }

    [Fact]
    public async Task TheResourceReadsTheLargestSourcesStoredDocumentAndWarns()
    {
        await IndexBothAsync();
        await MarkTheStoredDocumentAsync("manual.html");

        var text = await ResourceAsync("manual.html");

        text.ShouldContain("DOCUMENT-ONLY");
        text.ShouldContain("about beta");
        text.ShouldNotContain("about alpha");
        text.ShouldContain("! " + Warning);
    }

    [Fact]
    public async Task TheToolReadsTheLargestSourcesStoredDocumentAndWarns()
    {
        await IndexBothAsync();
        await MarkTheStoredDocumentAsync("manual.html");

        var text = await ToolAsync("manual.html");

        text.ShouldContain("DOCUMENT-ONLY");
        text.ShouldContain("about beta");
        text.ShouldNotContain("about alpha");
        text.ShouldContain("! " + Warning);
    }

    [Fact]
    public async Task APathOneSourceHoldsIsReadWithoutAWarning()
    {
        // The control: nothing here is shared, so nothing is narrowed and nothing is said.
        await IndexBothAsync();

        var tool = await ToolAsync("solo.md");
        var resource = await ResourceAsync("solo.md");
        var body = await EndpointAsync("solo.md");

        tool.ShouldContain("about gamma");
        resource.ShouldContain("about gamma");
        body.Text.ShouldContain("about gamma");

        tool.ShouldNotContain("sources in this corpus");
        resource.ShouldNotContain("sources in this corpus");
        body.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task TheThreePathsAgreeOnWhichSourceTheyRead()
    {
        await IndexBothAsync();

        var tool = await ToolAsync("guide.md");
        var resource = await ResourceAsync("guide.md");
        var body = await EndpointAsync("guide.md");

        foreach (var text in new[] { tool, resource, body.Text })
            text.ShouldContain("about beta");

        // The sentence is one string, so the three cannot word it differently.
        resource.ShouldContain(body.Warning!);
        tool.ShouldContain(body.Warning!);
    }
}
