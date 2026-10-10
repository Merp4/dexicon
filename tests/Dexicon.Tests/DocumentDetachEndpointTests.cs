using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dexicon.Tests;

/// <summary>
/// The handler the detach route is mapped to, called with real services and a vector store that
/// holds points. What it must do is the authorization boundary of the <c>destroy</c> scope (D-40),
/// so it is held here and not left to a manual run: reverting its check to <c>ingest</c> fails the
/// first two tests, and dropping the upload filter fails the last.
/// </summary>
public sealed class DocumentDetachEndpointTests
{
    private static RequestContext As(params string[] scopes) => new()
    {
        Principal = new Principal("k", "agent", scopes.ToHashSet(StringComparer.Ordinal)),
    };

    private static Task<IResult> DetachAsync(IndexingHarness harness, CatalogDbContext db, RequestContext rc, string fileId) =>
        DocumentEndpoints.DetachAsync(
            "notes", fileId, rc, new ScopeResolver(db), harness.NewDocumentService(db),
            new VectorStoreCleanup(db, harness.Vectors, NullLogger<VectorStoreCleanup>.Instance), default);

    /// <summary>An uploaded document, attached and indexed. Returns the attachment's id.</summary>
    private static async Task<string> UploadedAndIndexedAsync(IndexingHarness harness, CatalogDbContext db)
    {
        await harness.SeedCorpusAsync(SourceKind.Upload);
        var documents = harness.NewDocumentService(db);
        var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
            .FirstAsync(c => c.Id == IndexingHarness.CorpusId);
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(IndexingHarness.Prose("detach")));
        var stored = await documents.StoreAsync(bytes, "paper.md");
        var file = await documents.AttachAsync(corpus, stored.Sha256, "paper.md");
        await harness.RunIndexAsync();
        harness.Vectors.CountFor("paper.md").ShouldBeGreaterThan(0, "the premise");
        return file.Id;
    }

    [Fact]
    public async Task AKeyHoldingIngestAloneIsRefusedAndTheDocumentStaysAttachedAndIndexed()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await using var db = harness.NewContext();
        var fileId = await UploadedAndIndexedAsync(harness, db);

        var result = await DetachAsync(harness, db, As(Scopes.Search, Scopes.Ingest), fileId);

        var refused = result.ShouldBeOfType<ProblemHttpResult>();
        refused.StatusCode.ShouldBe(403);
        refused.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("needs 'destroy'");
        (await db.Files.AnyAsync(f => f.Id == fileId)).ShouldBeTrue();
        harness.Vectors.CountFor("paper.md").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task AKeyHoldingDestroyDetachesTheDocumentFromTheCorpusAndKeepsTheBlob()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await using var db = harness.NewContext();
        var fileId = await UploadedAndIndexedAsync(harness, db);

        var result = await DetachAsync(harness, db, As(Scopes.Search, Scopes.Destroy), fileId);

        result.ShouldBeOfType<NoContent>();
        await using var fresh = harness.NewContext();
        (await fresh.Files.AnyAsync(f => f.Id == fileId)).ShouldBeFalse("detached from the corpus");
        harness.Vectors.CountFor("paper.md").ShouldBe(0, "its vectors go with it");
        (await fresh.Blobs.AnyAsync()).ShouldBeTrue("another corpus may still hold the document");
    }

    [Fact]
    public async Task TheAdministratorDetachesWithoutNamingDestroy()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await using var db = harness.NewContext();
        var fileId = await UploadedAndIndexedAsync(harness, db);

        (await DetachAsync(harness, db, As(Scopes.Admin), fileId)).ShouldBeOfType<NoContent>();
    }

    [Fact]
    public async Task AKeyHoldingDestroyIsAllowedInAndToldWhenThereIsNothingToDetach()
    {
        await using var harness = await IndexingHarness.StartAsync();
        await using var db = harness.NewContext();
        await UploadedAndIndexedAsync(harness, db);

        (await DetachAsync(harness, db, As(Scopes.Destroy), "no-such-file")).ShouldBeOfType<NotFound>();
    }

    [Fact]
    public async Task AFileASourceReadFromAFolderIsNotDetachedAndKeepsItsVectors()
    {
        // The endpoint takes a file id, and ids of every file in a corpus are listed to a key that can
        // search. A workspace file is the source's: it goes when the source does.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        await harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"), source: 0);
        await harness.RunIndexAsync(JobKind.Full);
        await using var db = harness.NewContext();
        var fileId = await db.Files.Where(f => f.RelativePath == "a.md").Select(f => f.Id).SingleAsync();
        harness.Vectors.CountFor("a.md").ShouldBeGreaterThan(0, "the premise");

        var result = await DetachAsync(harness, db, As(Scopes.Search, Scopes.Destroy), fileId);

        result.ShouldBeOfType<NotFound>();
        await using var fresh = harness.NewContext();
        (await fresh.Files.AnyAsync(f => f.Id == fileId)).ShouldBeTrue("the source still lists it");
        harness.Vectors.CountFor("a.md").ShouldBeGreaterThan(0, "and its vectors are untouched");
    }
}
