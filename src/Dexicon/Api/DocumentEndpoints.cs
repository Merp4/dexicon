using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

public sealed record AttachDocumentRequest(string Sha256, string? FileName = null);

public sealed record UploadedDocumentResponse(
    string Sha256, string FileName, long SizeBytes, string? Title,
    int ExtractedChars, bool Deduplicated, string? Warning, string FileId);

public sealed record LibraryDocument(
    string Sha256,
    string? OriginalFileName,
    long SizeBytes,
    string? MediaType,
    string? Title,
    int ExtractedChars,
    string? EmptyReason,
    DateTime CreatedUtc,
    IReadOnlyList<LibraryAttachment> Attachments);

/// <summary>Where a document is attached, and how THAT corpus chunks it.</summary>
public sealed record LibraryAttachment(
    string CorpusId, string CorpusName, string FileId, string FileName,
    string Status, int ChunkCount, int ChunkSize, int ChunkOverlap, string BoundaryMode);

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Documents");

        // ── Upload into a corpus ────────────────────────────────────────────
        g.MapPost("/corpora/{nameOrId}/documents", async (
            string nameOrId, HttpRequest http, RequestContext rc, ScopeResolver scopes,
            DocumentService documents, IndexJobQueue queue, IOptions<DexiconOptions> opts,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;

            if (!http.HasFormContentType)
                return Results.Problem(
                    title: "Expected a multipart upload",
                    detail: "POST the file as multipart/form-data with a 'files' field.",
                    statusCode: 415);

            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);
            var form = await http.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return Results.Problem(title: "No files in the request", statusCode: 400);

            var stored = new List<UploadedDocumentResponse>();
            var failures = new List<UploadFailure>();

            foreach (var file in form.Files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    var doc = await documents.StoreAsync(stream, file.FileName, ct);
                    var attachment = await documents.AttachAsync(corpus, doc.Sha256, file.FileName, ct);

                    stored.Add(new UploadedDocumentResponse(
                        doc.Sha256, file.FileName, doc.SizeBytes, doc.Title, doc.ExtractedChars,
                        doc.AlreadyExisted, doc.EmptyReason, attachment.Id));
                }
                catch (ArgumentException ex)
                {
                    // One bad file in a batch must not lose the good ones.
                    failures.Add(new UploadFailure(file.FileName, ex.Message));
                }
            }

            if (stored.Count == 0)
                return Results.Problem(
                    title: "No files could be stored",
                    detail: string.Join("; ", failures.Select(f => $"{f.File}: {f.Error}")),
                    statusCode: 400);

            // Chunking and embedding happen in the indexer, not on the request thread:
            // a 400-page PDF outlasts any sensible HTTP timeout.
            var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct);

            return Results.Accepted($"/api/jobs/{job.Id}",
                new UploadResponse(corpus.Name, stored, failures, job.ToSummary()));
        }).Produces<UploadResponse>().DisableAntiforgery();

        // ── Attach an already-stored document to another corpus ─────────────
        g.MapPost("/corpora/{nameOrId}/documents/attach", async (
            string nameOrId, AttachDocumentRequest body, RequestContext rc, ScopeResolver scopes,
            DocumentService documents, CatalogDbContext db, IndexJobQueue queue, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);

            var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == body.Sha256, ct);
            if (blob is null) return Results.Problem(title: "No such document", statusCode: 404);

            // This is the point of the whole design: the same bytes, chunked this
            // corpus's way, without re-uploading or re-extracting anything.
            var name = body.FileName ?? blob.OriginalFileName ?? body.Sha256[..12];
            var file = await documents.AttachAsync(corpus, body.Sha256, name, ct);
            var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct);

            return Results.Accepted($"/api/jobs/{job.Id}", new DocumentAttached(
                corpus.Name, file.Id, name,
                // Every set, because attaching queues the document into all of them.
                [.. corpus.ChunkSets.Select(s => new AttachedChunking(
                    s.Name, s.ChunkSize, s.ChunkOverlap, s.BoundaryMode, s.EmbeddingModel))],
                job.ToSummary()));
        }).Produces<DocumentAttached>();

        // ── Detach (the blob survives; other corpora may still use it) ──────
        g.MapDelete("/corpora/{nameOrId}/documents/{fileId}", async (
            string nameOrId, string fileId, RequestContext rc, ScopeResolver scopes,
            DocumentService documents, IVectorStoreCleanup cleanup, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
            var corpus = await scopes.ResolveWritableAsync(rc.RequireTenant(), nameOrId, ct);

            var removed = await cleanup.RemoveAttachmentAsync(corpus, fileId, documents, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // ── The library: every stored document, and where it is attached ────
        g.MapGet("/documents", async (RequestContext rc, ScopeResolver scopes, CatalogDbContext db,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            var visible = await scopes.VisibleAsync(rc.RequireTenant(), ct);   // includes ChunkSets
            var corpusById = visible.ToDictionary(c => c.Id, StringComparer.Ordinal);
            var corpusIds = corpusById.Keys.ToList();

            var sources = await db.Sources
                .Where(s => corpusIds.Contains(s.CorpusId) && s.Kind == SourceKind.Upload)
                .ToListAsync(ct);
            var sourceToCorpus = sources.ToDictionary(s => s.Id, s => s.CorpusId, StringComparer.Ordinal);
            var sourceIds = sources.Select(s => s.Id).ToList();

            var attachments = await db.Files
                .Where(f => sourceIds.Contains(f.SourceId) && f.BlobSha256 != null)
                .ToListAsync(ct);

            // The library lists each attachment as its corpus's DEFAULT set sees it, which
            // is the chunking a plain search would actually reach.
            var attachmentIds = attachments.Select(a => a.Id).ToList();
            var statesByFile = (await db.FileChunkStates
                    .Where(s => attachmentIds.Contains(s.FileId))
                    .ToListAsync(ct))
                .ToDictionary(s => (s.FileId, s.ChunkSetId), s => s);

            var shas = attachments.Select(a => a.BlobSha256!).Distinct().ToList();
            var blobs = await db.Blobs.Where(b => shas.Contains(b.Sha256)).ToListAsync(ct);
            var texts = await db.BlobTexts.Where(t => shas.Contains(t.Sha256)).ToListAsync(ct);
            var textBySha = texts.ToDictionary(t => t.Sha256, StringComparer.Ordinal);

            var library = blobs.Select(b =>
            {
                textBySha.TryGetValue(b.Sha256, out var text);
                var mine = attachments
                    .Where(a => a.BlobSha256 == b.Sha256)
                    .Select(a =>
                    {
                        var corpus = corpusById[sourceToCorpus[a.SourceId]];
                        var set = corpus.ChunkSets.FirstOrDefault(s => s.IsDefault)
                                  ?? corpus.ChunkSets.FirstOrDefault();
                        var state = statesByFile.TryGetValue((a.Id, set?.Id ?? ""), out var st) ? st : null;

                        return new LibraryAttachment(
                            corpus.Id, corpus.Name, a.Id, a.RelativePath,
                            (state?.Status ?? FileStatus.Pending).ToString().ToLowerInvariant(),
                            state?.ChunkCount ?? 0,
                            set?.ChunkSize ?? 0, set?.ChunkOverlap ?? 0, set?.BoundaryMode ?? "-");
                    })
                    .OrderBy(a => a.CorpusName, StringComparer.Ordinal)
                    .ToList();

                return new LibraryDocument(
                    b.Sha256, b.OriginalFileName, b.SizeBytes, b.MediaType,
                    text?.Title, text?.ExtractedChars ?? 0, text?.EmptyReason,
                    b.CreatedUtc, mine);
            })
            .OrderByDescending(d => d.CreatedUtc)
            .ToList();

            return Results.Ok(library);
        }).Produces<IReadOnlyList<LibraryDocument>>();

        // ── Raw text, so "what did the extractor actually see?" is answerable ─
        g.MapGet("/documents/{sha256}/text", async (string sha256, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            // Authorisation is by attachment: you can read the text of a document only
            // if it is attached to a corpus you can see. A bare hash grants nothing.
            var visible = (await scopes.VisibleAsync(rc.RequireTenant(), ct)).Select(c => c.Id).ToList();
            var attached = await db.Files
                .Include(f => f.Source)
                .AnyAsync(f => f.BlobSha256 == sha256 && visible.Contains(f.Source!.CorpusId), ct);

            if (!attached) return Results.NotFound();

            var text = await db.BlobTexts.AsNoTracking().FirstOrDefaultAsync(t => t.Sha256 == sha256, ct);
            if (text is null) return Results.NotFound();

            return Results.Ok(new ExtractedTextResponse(
                text.Sha256, text.Title, text.Extractor, text.ExtractedChars,
                text.ExtractedUtc, text.EmptyReason,
                text.Text.Length > 20_000 ? text.Text[..20_000] + "\n…(truncated)" : text.Text));
        }).Produces<ExtractedTextResponse>();
    }
}

/// <summary>
/// Removing an attachment has to delete its vectors too, and the vector store is a
/// Core concern rather than an endpoint's. Small seam so the endpoint stays readable.
/// </summary>
public interface IVectorStoreCleanup
{
    Task<bool> RemoveAttachmentAsync(Corpus corpus, string fileId, DocumentService documents, CancellationToken ct);
}
