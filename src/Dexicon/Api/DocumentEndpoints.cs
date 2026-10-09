using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

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
    private const string ReadsTheNextFileOnTheRequest =
        "The loop reads, stores and attaches one file after another on the request's token. A cancel in it, "
        + "or any other failure once a file is attached, is caught below, which keeps the files already "
        + "attached and queues their indexing without the token. The queuing is not covered by this.";

    /// <summary>The start of the text <see cref="MultipartReader"/> throws when the body ends early.</summary>
    private const string TruncatedBodyMessage = "Unexpected end of Stream";

    /// <summary>
    /// Detach an uploaded document from a corpus (the blob survives; other corpora may still use it).
    /// A method of its own, and the one the route is mapped to, so a test calls the handler that
    /// runs and sees its scope check and its refusals.
    /// </summary>
    internal static async Task<IResult> DetachAsync(
        string nameOrId, string fileId, RequestContext rc, ScopeResolver scopes,
        DocumentService documents, IVectorStoreCleanup cleanup, CancellationToken ct)
    {
        // Its own scope: this removes what ingest added, with no one deciding. D-40.
        if (rc.RequireScope(Scopes.Destroy) is { } denied) return denied;
        var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

        var removed = await cleanup.RemoveAttachmentAsync(corpus, fileId, documents, ct);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Upload into a corpus. A method of its own, and the one the route is mapped to, so a test
    /// calls the handler that runs and sees how it reads the body.
    ///
    /// The body is read with a <see cref="MultipartReader"/> and each file goes straight from the
    /// wire into <see cref="DocumentService.StoreAsync"/>, which holds it to
    /// <see cref="UploadOptions.MaxFileBytes"/> as the bytes arrive. Nothing is spooled to a temp
    /// file first, which on the container's tmpfs would be memory. The request as a whole is held
    /// to <see cref="UploadOptions.MaxRequestBytes"/>.
    /// </summary>
    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "ReadNextSectionAsync",
        Justification = ReadsTheNextFileOnTheRequest)]
    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "StoreAsync",
        Justification = ReadsTheNextFileOnTheRequest)]
    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "AttachAsync",
        Justification = ReadsTheNextFileOnTheRequest)]
    internal static async Task<IResult> UploadAsync(
        string nameOrId, HttpRequest http, RequestContext rc, ScopeResolver scopes,
        DocumentService documents, IndexJobQueue queue, IOptions<DexiconOptions> opts,
        CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;

        if (BoundaryOf(http.ContentType) is not { } boundary)
            return Results.Problem(
                title: "Expected a multipart upload",
                detail: "POST the file as multipart/form-data with a 'files' field.",
                statusCode: 415);

        // The one endpoint that legitimately carries a large body, so the one that opts out of
        // Kestrel's 30 MB default. Without this, DEXICON__UPLOAD__MAXFILEBYTES was unreachable
        // above ~28.6 MB and the caller got a bare 413 rather than the service's own message.
        // The bound that replaces it is UploadOptions.MaxRequestBytes, applied below with a
        // message that names it.
        //
        // Set BEFORE the body is read, which is the only point at which the feature is still
        // writable.
        var bodySize = http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = null;

        var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

        var upload = opts.Value.Upload;
        var bound = upload.MaxRequestBytes;

        // A client that declares its length is refused before a byte of the body is read.
        if (http.ContentLength > bound) return TooLarge(upload);

        var stored = new List<UploadedDocumentResponse>();
        var failures = new List<UploadFailure>();
        var filesSeen = 0;
        var overran = false;
        string? malformed = null;
        ExceptionDispatchInfo? failure = null;

        // Counts what is read, so a body with no declared length (chunked) is refused at the
        // bound too, and so is a declared length that understates the body.
        var body = new BoundedReadStream(http.Body, bound);
        var reader = new MultipartReader(boundary, body)
        {
            HeadersCountLimit = MultipartReader.DefaultHeadersCountLimit,
            HeadersLengthLimit = MultipartReader.DefaultHeadersLengthLimit,
            BodyLengthLimit = bound,
        };

        try
        {
            // ReadNextSectionAsync discards what is left of the section before it, so a file
            // refused for its size still has its remaining bytes read off the wire. They are
            // not kept, and the files after it are reached.
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (section.AsFileSection() is not { } part) continue;   // a form field

                filesSeen++;
                var fileName = part.FileName ?? "";
                try
                {
                    var doc = await documents.StoreAsync(part.FileStream!, fileName, ct);
                    var attachment = await documents.AttachAsync(corpus, doc.Sha256, fileName, ct);

                    stored.Add(new UploadedDocumentResponse(
                        doc.Sha256, fileName, doc.SizeBytes, doc.Title, doc.ExtractedChars,
                        doc.AlreadyExisted, doc.EmptyReason, attachment.Id));
                }
                catch (ArgumentException ex)
                {
                    // One bad file in a batch must not lose the good ones.
                    failures.Add(new UploadFailure(fileName, ex.Message));
                }
            }
        }
        // The caller went away partway through. A file stored before that is attached already, so
        // it is queued below like the rest; with nothing stored there is nothing to finish and the
        // cancellation propagates as before.
        catch (OperationCanceledException) when (ct.IsCancellationRequested && stored.Count > 0)
        {
            // The file that was being attached when the save was cancelled is still tracked as added.
            // The save below is not cancellable, so it would persist that file, which is not in
            // `stored`. Only the files reported above stay.
            documents.DiscardUnsavedChanges();
            failures.Add(new UploadFailure(null, "The connection closed before the upload finished."));
        }
        catch (UploadTooLargeException)
        {
            overran = true;
            failures.Add(new UploadFailure(null, TooLargeMessage(upload)));
        }
        catch (InvalidDataException ex)
        {
            malformed = ex.Message;
            failures.Add(new UploadFailure(null, $"The multipart body could not be read: {ex.Message}"));
        }
        // The body ended before its closing boundary: a client that stopped sending, or a proxy
        // that cut it. The reader reports that as an IOException, as a failing disk would be, so
        // it is the client's fault only when the request stream really ended and the message is
        // the reader's. Any other IOException is the server's and stays a 500.
        catch (IOException ex) when (body.ReachedEnd
                                     && ex.Message.StartsWith(TruncatedBodyMessage, StringComparison.Ordinal))
        {
            malformed = "The body ended before its closing boundary.";
            failures.Add(new UploadFailure(null, $"The multipart body could not be read: {malformed}"));
        }
        // Anything else, with files attached already: the caller still gets the server error, and the
        // attached files are not left with no job until the next scheduled refresh. What the failed step
        // had added is dropped here, as for a cancel, or the save that queues the job would write it. The
        // job is queued after the try and not in this block, where the TokenAfterCommit scan
        // (RequestTokenAfterCommitTests) cannot see that it follows the commits above.
        catch (Exception ex) when (stored.Count > 0)
        {
            documents.DiscardUnsavedChanges();
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        if (failure is not null)
        {
            // A corpus removed while the batch waited has no files left to index, and no job to queue for it.
            if (failure.SourceException is not ScopeResolutionException)
            {
                try { await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: CancellationToken.None); }
                catch (Exception queuing)
                {
                    // The caller is told the first failure. The files wait for the next refresh.
                    http.HttpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("Dexicon.Upload")
                        .LogError(queuing, "Queuing the refresh of corpus {Corpus} after a failed upload batch failed too", corpus.Id);
                }
            }

            failure.Throw();
        }

        if (stored.Count == 0)
        {
            if (overran) return TooLarge(upload);

            if (malformed is not null)
                return Results.Problem(title: "Malformed multipart upload", detail: malformed, statusCode: 400);

            if (filesSeen == 0)
                return Results.Problem(title: "No files in the request", statusCode: 400);

            return Results.Problem(
                title: "No files could be stored",
                detail: string.Join("; ", failures.Select(f => f.File is null ? f.Error : $"{f.File}: {f.Error}")),
                statusCode: 400);
        }

        // Chunking and embedding happen in the indexer, not on the request thread:
        // a 400-page PDF outlasts any sensible HTTP timeout.
        //
        // Not cancellable: the files are attached, and the job is what indexes them. A cancel
        // between the two left documents attached with nothing queued until the next refresh,
        // as in CorpusConfiguration.
        var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: CancellationToken.None);

        return Results.Accepted($"/api/jobs/{job.Id}",
            new UploadResponse(corpus.Name, stored, failures, job.ToSummary()));
    }

    /// <summary>
    /// Attach a stored document to another corpus. A method of its own, and the one the route is
    /// mapped to, so a test calls the handler that runs.
    /// </summary>
    internal static async Task<IResult> AttachAsync(
        string nameOrId, AttachDocumentRequest body, RequestContext rc, ScopeResolver scopes,
        DocumentService documents, CatalogDbContext db, IndexJobQueue queue, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Ingest) is { } denied) return denied;
        var corpus = await scopes.ResolveWritableAsync(rc.RequirePrincipal(), nameOrId, ct);

        // Left out, the name is the one the document was uploaded under. Sent blank it would be stored as
        // the path, and a path is what a listing, a search hit and a detach name the document by.
        if (body.FileName is not null && string.IsNullOrWhiteSpace(body.FileName))
            return Results.Problem(
                title: "fileName is blank",
                detail: "Leave fileName out to keep the name the document was uploaded under.",
                statusCode: 400);

        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == body.Sha256, ct);
        if (blob is null) return Results.Problem(title: "No such document", statusCode: 404);

        // This is the point of the whole design: the same bytes, chunked this
        // corpus's way, without re-uploading or re-extracting anything.
        var name = body.FileName ?? blob.OriginalFileName ?? body.Sha256[..12];
        IndexedFile file;
        try
        {
            file = await documents.AttachAsync(corpus, body.Sha256, name, ct);
        }
        catch (NameTakenException ex)
        {
            return Results.Problem(title: "Name already used", detail: ex.Message, statusCode: 409);
        }

        // Not cancellable, as in UploadAsync: the attachment is saved and the job indexes it.
        var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: CancellationToken.None);

        return Results.Accepted($"/api/jobs/{job.Id}", new DocumentAttached(
            corpus.Name, file.Id, name,
            // Every set, because attaching queues the document into all of them.
            [.. corpus.ChunkSets.Select(s => new AttachedChunking(
                s.Name, s.ChunkSize, s.ChunkOverlap, s.BoundaryMode, s.EmbeddingModel))],
            job.ToSummary()));
    }

    /// <summary>
    /// The multipart boundary, or null when the request is not <c>multipart/form-data</c> or names
    /// none. The length limit is the one the framework applies to a form.
    /// </summary>
    private static string? BoundaryOf(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return null;

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary);
        return StringSegment.IsNullOrEmpty(boundary) || boundary.Length > FormOptions.DefaultMultipartBoundaryLengthLimit
            ? null
            : boundary.Value;
    }

    private static string TooLargeMessage(UploadOptions upload) =>
        $"The upload is over the {upload.MaxRequestBytes:N0} byte request limit: {UploadOptions.BatchFiles} files " +
        $"at the {upload.MaxFileBytes:N0} byte per-file limit (DEXICON__UPLOAD__MAXFILEBYTES) plus form framing. " +
        "Send fewer files in each request.";

    private static IResult TooLarge(UploadOptions upload) =>
        Results.Problem(title: "Upload too large", detail: TooLargeMessage(upload), statusCode: 413);

    private sealed class UploadTooLargeException : Exception;

    /// <summary>Passes reads through and throws <see cref="UploadTooLargeException"/> once more than the limit has been read.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        /// <summary>Whether the request stream has been read to its end.</summary>
        public bool ReachedEnd { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Counted(await inner.ReadAsync(buffer, cancellationToken));

        private int Counted(int read)
        {
            if (read == 0) ReachedEnd = true;
            _read += read;
            if (_read > limit) throw new UploadTooLargeException();
            return read;
        }
    }

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Documents");

        // ── Upload into a corpus ────────────────────────────────────────────
        g.MapPost("/corpora/{nameOrId}/documents", UploadAsync)
            .Produces<UploadResponse>(StatusCodes.Status202Accepted).DisableAntiforgery();

        // ── Attach an already-stored document to another corpus ─────────────
        g.MapPost("/corpora/{nameOrId}/documents/attach", AttachAsync)
            .Produces<DocumentAttached>(StatusCodes.Status202Accepted);

        // ── Detach (the blob survives; other corpora may still use it) ──────
        g.MapDelete("/corpora/{nameOrId}/documents/{fileId}", DetachAsync).Produces(StatusCodes.Status204NoContent);

        // ── The library: every stored document, and where it is attached ────
        g.MapGet("/documents", async (RequestContext rc, ScopeResolver scopes, CatalogDbContext db,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            var visible = await scopes.VisibleAsync(rc.RequirePrincipal(), ct);   // includes ChunkSets
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
            var visible = (await scopes.VisibleAsync(rc.RequirePrincipal(), ct)).Select(c => c.Id).ToList();
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
