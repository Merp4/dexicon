using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Documents;

public sealed record StoredDocument(
    string Sha256,
    long SizeBytes,
    string FileName,
    string? Title,
    int ExtractedChars,
    bool AlreadyExisted,
    string? EmptyReason);

/// <summary>
/// Uploaded documents, stored once and chunked many times.
///
/// The separation that matters: BYTES are content-addressed and EXTRACTION is cached
/// against them, because both are deterministic and extraction is expensive. CHUNKING
/// is a property of the corpus, because it is cheap and it is the thing people actually
/// want to vary.
///
/// That gives three things for free:
///   - uploading the same PDF twice stores one blob and extracts once (two uploads at the same moment
///     each extract, and the one saved second keeps the other's text)
///   - attaching one document to two corpora with different chunk sizes produces two
///     independent chunk sets without re-opening the file
///   - changing a corpus's chunk settings re-chunks and re-embeds from cached text
///
/// See docs/04-ingestion.md.
/// </summary>
public sealed class DocumentService(
    CatalogDbContext db,
    IOptions<DexiconOptions> options,
    ILogger<DocumentService> log)
{
    private readonly StorageOptions _storage = options.Value.Storage;
    private readonly UploadOptions _upload = options.Value.Upload;
    private readonly IndexingOptions _indexing = options.Value.Indexing;

    /// <summary>
    /// Held from looking for a corpus's upload source and for the document's existing attachment to the
    /// save that adds them, so two attachments cannot both find none, and by a detach for its lookup and
    /// delete, so an attachment does not save against a row a detach has removed. A corpus has one upload source and
    /// holds a blob once (the same blob may be attached to any number of corpora), and no unique index
    /// says so: both were checks made before the insert, and two requests could pass them together and
    /// each add one. Dexicon is one process owning its catalogue (D-01), so one lock is enough, and a
    /// second process writing the file is not covered, as for <c>CorpusConfiguration.Naming</c>, and neither
    /// are the writers of file rows that do not take it: removing a source or a corpus, and the indexer's
    /// reconcile. One lock for all corpora, because an attachment is a few queries and a save.
    /// </summary>
    private static readonly SemaphoreSlim Attaching = new(1, 1);

    /// <summary>The longest file name stored, in UTF-16 characters.</summary>
    public const int MaxFileNameLength = 260;

    /// <summary>
    /// Why <paramref name="fileName"/> cannot be stored as a file's path, or null when it can: it is
    /// blank, longer than <see cref="MaxFileNameLength"/>, or holds a control character (a line break
    /// among them, including U+2028 and U+2029) or a bidirectional override. A listing, a search hit and a log line show the name.
    /// The text does not repeat the name, because the upload endpoint reports it to whoever sent the
    /// file and the attach endpoint puts it in a problem detail.
    /// </summary>
    public static string? FileNameProblem(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "A file name is required.";
        if (fileName.Length > MaxFileNameLength)
            return $"A file name is limited to {MaxFileNameLength} characters.";
        if (fileName.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029' || IsBidirectionalOverride(c)))
            return "A file name cannot hold a control character, such as a line break, or a bidirectional override.";

        return null;
    }

    /// <summary>
    /// The embedding and override characters U+202A to U+202E and the isolates U+2066 to U+2069, which
    /// reorder the text around them and so can make a name read as another in a listing. Other format
    /// characters stay allowed: U+200C and U+200D are part of Persian and of emoji sequences.
    /// </summary>
    private static bool IsBidirectionalOverride(char c) => c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069';

    /// <summary>Where a blob's bytes live: /data/blobs/ab/abcdef…, with two hex chars of fan-out.</summary>
    public string PathFor(string sha256) =>
        Path.Combine(_storage.BlobRoot, sha256[..2], sha256);

    /// <summary>
    /// Store bytes, extract text once, and return what happened. Does NOT attach the
    /// document to anything; attachment is a separate, per-corpus operation.
    /// </summary>
    public async Task<StoredDocument> StoreAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        // Before the bytes are read: a refused name costs the upload nothing. No paramName, as below.
        if (FileNameProblem(fileName) is { } problem) throw new ArgumentException(problem);

        // Buffer to a temp file rather than memory: a 200 MB upload should not be a
        // 200 MB allocation, and the hash is only known after the whole stream is read.
        Directory.CreateDirectory(_storage.BlobRoot);
        var temp = Path.Combine(_storage.BlobRoot, $".incoming-{Guid.NewGuid():N}");

        string sha;
        long size;
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
            {
                size = await CopyCappedAsync(content, fs, _upload.MaxFileBytes, fileName, ct);
            }

            // No paramName on either: the upload endpoint lists these messages for whoever sent the file.
            if (size == 0) throw new ArgumentException($"'{fileName}' is empty.");

            await using (var fs = File.OpenRead(temp))
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));

            var final = PathFor(sha);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);

            // The move does not replace a file. Bytes that are stored already, from an earlier upload or one
            // that finished between this one's hash and its move, are the file this upload would have
            // written, so its copy is dropped. One path for both, so an upload of stored bytes reaches it.
            try
            {
                File.Move(temp, final);
            }
            catch (IOException) when (File.Exists(final))
            {
                File.Delete(temp);
            }
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        if (await ExistingAsync(sha, fileName, ct) is { } existing)
        {
            log.LogInformation("Upload '{File}' is an existing blob {Sha}; stored once, extraction reused",
                fileName, sha[..12]);
            return existing;
        }

        var blob = new Blob
        {
            Sha256 = sha,
            SizeBytes = size,
            MediaType = MediaTypeFor(fileName),
            OriginalFileName = fileName,
            CreatedUtc = DateTime.UtcNow,
        };

        // Extracted before either row is added to the context. A timeout, or another failure that is not
        // a verdict on the bytes, leaves nothing tracked, so the save of the next file's attachment, or
        // the one that queues the job, cannot write a blob that has no text. No document record is
        // created: the bytes stay in the blob store, which a concurrent upload of the same bytes may
        // be relying on, and a later upload of them extracts again.
        BlobText text;
        try
        {
            text = await ExtractAsync(sha, fileName, ct);
        }
        catch (ExtractionFailedException ex)
        {
            // The same bytes were uploaded at the same moment and the other upload saved its blob while
            // this one was extracting. This upload reports that blob and is not told to send the file again.
            // Not cancellable, as for the duplicate key below: the read only decides the reply. A lookup
            // that fails (a busy catalogue) is logged and the extraction failure stays the answer, so a
            // file's failure does not become the batch's.
            StoredDocument? saved = null;
            try
            {
                saved = await ExistingAsync(sha, fileName, CancellationToken.None);
            }
            catch (Exception lookup)
            {
                log.LogWarning(lookup,
                    "Looking for a blob of {Sha} saved by another upload failed after the extraction of uploaded '{File}' failed",
                    sha[..12], fileName);
            }

            if (saved is null)
            {
                log.LogWarning(ex, "Extraction of uploaded '{File}' {Outcome}; no document record was created",
                    fileName,
                    ex is ExtractionTimeoutException
                        ? $"did not finish within {_indexing.ExtractionTimeoutSeconds} s"
                        : "failed for a reason that is not a verdict on the file");
                throw;
            }

            log.LogInformation("Upload '{File}' of {Sha} was saved by another upload while this one failed to extract it",
                fileName, sha[..12]);
            return saved;
        }
        db.Blobs.Add(blob);
        db.BlobTexts.Add(text);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsDuplicateKey())
        {
            // The same bytes were uploaded at the same moment, and the other upload saved its blob
            // while this one was extracting: both had looked, found none, and gone on. The blob is
            // there and holds the same bytes, so this upload reports it as it would one that
            // arrived later, and the extraction it did is dropped. If no blob with this hash is
            // there, the duplicate was something else and stays an error, as does any other failure.
            db.Entry(blob).State = EntityState.Detached;
            db.Entry(text).State = EntityState.Detached;

            // Not cancellable: the other upload's blob is committed, and this read only decides the reply.
            if (await ExistingAsync(sha, fileName, CancellationToken.None) is not { } winner) throw;
            log.LogInformation("Upload '{File}' of {Sha} was saved by another upload first; its extraction is dropped",
                fileName, sha[..12]);
            return winner;
        }

        log.LogInformation("Stored '{File}' as {Sha} ({Size:N0} bytes, {Chars:N0} chars extracted)",
            fileName, sha[..12], size, text.ExtractedChars);

        return new StoredDocument(sha, size, fileName, text.Title, text.ExtractedChars,
            AlreadyExisted: false, text.EmptyReason);
    }

    /// <summary>The stored document for bytes already in the library, or null when they are not.</summary>
    private async Task<StoredDocument?> ExistingAsync(string sha, string fileName, CancellationToken ct)
    {
        var existing = await db.Blobs.Include(b => b.Text).FirstOrDefaultAsync(b => b.Sha256 == sha, ct);
        if (existing is null) return null;

        return new StoredDocument(sha, existing.SizeBytes, fileName, existing.Text?.Title,
            existing.Text?.ExtractedChars ?? 0, AlreadyExisted: true, existing.Text?.EmptyReason);
    }

    /// <summary>
    /// Copy to the destination and refuse anything over the cap WITHOUT reading past it.
    ///
    /// The check used to run on the finished file, which meant a 2 GB upload was written
    /// to /data in full and hashed before being told it was too big: the refusal was
    /// correct and the disk had already paid for it. Stopping one byte over the cap makes
    /// the limit a limit rather than a verdict.
    ///
    /// The message cannot name the file's real size for the same reason, since the rest of
    /// the stream is never read, so it names the cap and the setting that changes it, which
    /// is the actionable part.
    /// </summary>
    private static async Task<long> CopyCappedAsync(
        Stream source, Stream destination, long cap, string fileName, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > cap)
                    // No paramName: the endpoint reports this message to whoever uploaded
                    // the file, and "(Parameter 'source')" is the name of an argument they
                    // cannot see and did not pass.
                    throw new ArgumentException(
                        $"'{fileName}' is over the {cap:N0} byte upload limit " +
                        "(DEXICON__UPLOAD__MAXFILEBYTES).");

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The extractor for a file name, <see cref="ExtractorRegistry.For"/> unless a test replaces it:
    /// the registered extractors read a file in a few milliseconds and cannot be made slow.
    /// </summary>
    internal Func<string, ITextExtractor?> ExtractorFor { get; init; } = ExtractorRegistry.For;

    private async Task<BlobText> ExtractAsync(string sha, string fileName, CancellationToken ct)
    {
        var extractor = ExtractorFor(fileName);
        var path = PathFor(sha);
        DeadlineStream? deadline = null;

        try
        {
            ExtractedText extracted;
            if (extractor is null)
            {
                // Not a document format: treat as plain text, the same as a code file.
                var raw = await File.ReadAllBytesAsync(path, ct);
                extracted = new ExtractedText(DecodeText(raw), []);
            }
            else
            {
                await using var stream = File.OpenRead(path);

                // The deadline the indexing path puts on a workspace file (ExtractedTextCache.ParseAsync):
                // without it a file that keeps reading holds the request, and the files after it in an
                // upload, for as long as it takes. 0 disables it, as there.
                var timeoutSeconds = _indexing.ExtractionTimeoutSeconds;
                if (timeoutSeconds > 0)
                {
                    deadline = new DeadlineStream(stream, TimeSpan.FromSeconds(timeoutSeconds), fileName);
                    extracted = extractor.Extract(deadline, fileName);

                    // An extractor that caught the timeout and went on has read part of the file.
                    if (deadline.Expired) throw deadline.TimedOut();
                }
                else
                {
                    extracted = extractor.Extract(stream, fileName);
                }
            }

            // Whatever produced it. A repair that wrote the same NUL-bearing text back would
            // fail the length check again on the next read, and clear the chunk states again.
            extracted = extracted.WithoutNul();

            // "Produced no text" is not a failure, and saying WHY is the difference
            // between a user finding their scanned PDF in the UI and concluding the
            // upload silently vanished.
            var emptyReason = extracted.Text.Trim().Length > 0
                ? null
                : extractor is PdfTextExtractor
                    ? "no text layer: this is a scanned PDF, and OCR is not supported"
                    : "no extractable text content";

            return new BlobText
            {
                Sha256 = sha,
                Text = extracted.Text,
                UnitsJson = extracted.Units.Count > 0 ? JsonSerializer.Serialize(extracted.Units) : null,
                Title = extracted.Title,
                ExtractedChars = extracted.Text.Length,
                Extractor = extractor?.GetType().Name ?? "PlainText",
                ExtractorVersion = ExtractorVersions.Current,
                ExtractedUtc = DateTime.UtcNow,
                EmptyReason = emptyReason,
            };
        }
        // A timeout says how busy the host was when it ran, not what is in the document, so it is not
        // recorded as the blob's text: a row at the current extractor version is never extracted
        // again, and the same bytes uploaded again would reuse it. It reaches the caller, which
        // leaves what it has. An extractor may wrap the exception in its own, or fail with another one
        // after the deadline passed, so the deadline's own flag is read as well as the type.
        catch (Exception ex) when (ex is not OperationCanceledException
                                   && (ex is ExtractionTimeoutException || deadline is { Expired: true }))
        {
            if (ex is ExtractionTimeoutException) throw;
            throw deadline!.TimedOut(ex);
        }
        // Whatever an extractor or the read of the blob throws is classified as the indexing path
        // classifies a parser's exception (ExtractionFailures.Of). Only the verdict on the bytes is
        // recorded: a file that is encrypted or corrupt reads the same way every time, so the row says
        // why it is empty. Every other failure (an I/O error, a refused permission, a shortage of
        // memory, a TimeoutException, or an extraction failure that wraps one of those) says how the
        // host was when it ran, so it reaches the caller as a timeout does and no row is written.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = ex as ExtractionFailedException
                ?? ExtractionFailures.Of($"'{fileName}' could not be read: {ex.Message}", ex);
            if (failure is not UnreadableDocumentException)
            {
                if (ReferenceEquals(failure, ex)) throw;
                throw failure;
            }

            // Recorded rather than thrown away: the blob exists, so the UI can show it
            // as failed with a reason instead of the upload appearing to have worked.
            log.LogWarning(failure, "Extraction failed for uploaded '{File}'", fileName);
            return new BlobText
            {
                Sha256 = sha,
                Text = string.Empty,
                ExtractedChars = 0,
                Extractor = extractor?.GetType().Name ?? "PlainText",
                ExtractorVersion = ExtractorVersions.Current,
                ExtractedUtc = DateTime.UtcNow,
                EmptyReason = failure.Message,
            };
        }
    }
    /// <summary>
    /// Attach a stored document to a corpus. The SAME blob may be attached to any number
    /// of corpora; each chunks it with its own settings, producing independent chunk
    /// sets that never see each other.
    /// </summary>
    /// <exception cref="NameTakenException">
    /// The bytes are already attached to the corpus under another name, and the name asked for is another
    /// document's.
    /// </exception>
    public async Task<IndexedFile> AttachAsync(Corpus corpus, string sha256, string fileName,
        CancellationToken ct = default)
    {
        // As StoreAsync: no paramName, so the message reads the same to whoever is told it.
        if (FileNameProblem(fileName) is { } problem) throw new ArgumentException(problem);

        await Attaching.WaitAsync(ct);
        try { return await AttachHeldAsync(corpus, sha256, fileName, ct); }
        finally { Attaching.Release(); }
    }

    private async Task<IndexedFile> AttachHeldAsync(Corpus corpus, string sha256, string fileName,
        CancellationToken ct)
    {
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == sha256, ct)
            ?? throw new InvalidOperationException($"No stored document with hash {sha256}.");

        // Needed to give the new attachment a state row per set.
        await db.Entry(corpus).Collection(c => c.ChunkSets).LoadAsync(ct);

        var source = await UploadSourceFor(corpus, ct);

        // ONE BLOB, ONE ATTACHMENT PER CORPUS. Look up by blob first, not by name.
        //
        // Matching on name alone let the same document attach twice to one corpus under
        // two spellings, observed when the same PDF was uploaded once with a mangled
        // filename and once correctly. Two attachments of identical bytes means the
        // content is chunked and embedded twice, and every search over that corpus
        // returns each hit twice. A rename is a rename, not a second document.
        var byBlob = await db.Files.FirstOrDefaultAsync(
            f => f.SourceId == source.Id && f.BlobSha256 == sha256, ct);

        if (byBlob is not null)
        {
            if (!string.Equals(byBlob.RelativePath, fileName, StringComparison.Ordinal))
            {
                // These bytes are attached here already, under another name. Moving them to a name another
                // document holds would replace that document too, and a path is unique within a source, so
                // the rename is refused. Bytes that are not attached yet replace the document of that name,
                // below.
                if (await db.Files.AnyAsync(f => f.SourceId == source.Id && f.RelativePath == fileName, ct))
                    throw new NameTakenException(
                        $"'{fileName}' is the name of another document in this corpus. These bytes are already "
                        + $"attached to it as '{byBlob.RelativePath}'; use another name, or detach one of the two first.");

                // Renaming invalidates the old chunks, which are keyed by file_path.
                // Cleared here; the caller's reindex writes them back under the new name.
                byBlob.RelativePath = fileName;
                await InvalidateAsync(byBlob.Id, ct);
            }
            byBlob.SizeBytes = blob.SizeBytes;
            await db.SaveChangesAsync(ct);
            return byBlob;
        }

        var existing = await db.Files.FirstOrDefaultAsync(
            f => f.SourceId == source.Id && f.RelativePath == fileName, ct);

        if (existing is not null)
        {
            // Same name, different bytes: a replacement. The fingerprint changes, so
            // the incremental pass sees it as changed and re-chunks it.
            existing.BlobSha256 = sha256;
            existing.SizeBytes = blob.SizeBytes;
            await InvalidateAsync(existing.Id, ct);
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var file = new IndexedFile
        {
            Id = Ulid.NewUlid().ToString(),
            SourceId = source.Id,
            RelativePath = fileName,
            BlobSha256 = sha256,
            SizeBytes = blob.SizeBytes,
            MediaType = blob.MediaType,
        };

        db.Files.Add(file);

        // A row per chunk set, all Pending: a new attachment is outstanding work for
        // every way this corpus cuts its content, not just the default one.
        foreach (var set in corpus.ChunkSets)
        {
            db.FileChunkStates.Add(new FileChunkState
            {
                FileId = file.Id,
                ChunkSetId = set.Id,
                Status = FileStatus.Pending,
            });
        }

        await db.SaveChangesAsync(ct);
        return file;
    }

    /// <summary>
    /// Stop tracking changes that were not saved. After a save that was cancelled or failed the context
    /// still holds what that save was going to write, and the next save, from anything sharing the
    /// context, would write it. Called when the caller goes on to a save of its own after the failed step.
    /// </summary>
    public void DiscardUnsavedChanges() => db.ChangeTracker.Clear();

    /// <summary>
    /// Mark a file as needing re-indexing in EVERY chunk set. A rename invalidates chunks
    /// keyed by file path, and replaced bytes invalidate the chunks themselves, in both
    /// cases for all sets at once, because they all read the same attachment.
    /// </summary>
    private async Task InvalidateAsync(string fileId, CancellationToken ct)
    {
        var states = await db.FileChunkStates.Where(s => s.FileId == fileId).ToListAsync(ct);
        foreach (var state in states)
        {
            state.ContentHash = null;
            state.Status = FileStatus.Pending;
            state.StatusDetail = null;
        }
    }

    /// <summary>
    /// Every corpus gets at most one upload source, created on first attachment. A new one is added
    /// to the context and saved with the attachment it was made for: saved on its own, a cancel
    /// before the attachment's save left a source with nothing in it.
    /// </summary>
    private async Task<Source> UploadSourceFor(Corpus corpus, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(
            s => s.CorpusId == corpus.Id && s.Kind == SourceKind.Upload, ct);

        if (source is not null) return source;

        source = new Source
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = corpus.Id,
            Kind = SourceKind.Upload,
            UseGitignore = false,
            MaxFileBytes = int.MaxValue,
            CreatedUtc = DateTime.UtcNow,
        };
        db.Sources.Add(source);
        return source;
    }

    /// <summary>
    /// Detach an uploaded document from one corpus. The blob survives, since other corpora may still
    /// use it. A file a source read from a folder or a commit is not a document and is not detached.
    /// </summary>
    public async Task<bool> DetachAsync(string corpusId, string fileId, CancellationToken ct = default)
    {
        // Under the lock attaching holds: an attachment that found this file and was about to rename or
        // replace it saved against a row the detach had deleted, and the save failed on it.
        await Attaching.WaitAsync(ct);
        try
        {
            var file = await db.Files.Include(f => f.Source)
                .FirstOrDefaultAsync(f => f.Id == fileId && f.Source!.CorpusId == corpusId
                                          && f.Source.Kind == SourceKind.Upload, ct);

            if (file is null) return false;
            db.Files.Remove(file);
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally { Attaching.Release(); }
    }

    public Task<BlobText?> TextFor(string sha256, CancellationToken ct = default) =>
        db.BlobTexts.AsNoTracking().FirstOrDefaultAsync(t => t.Sha256 == sha256, ct);

    /// <summary>
    /// The cached text for a blob, re-extracting first if it was produced by an older
    /// extractor or no longer reads back as long as it was written. Called on the indexing
    /// path, so an extractor fix reaches a library that was ingested before it without
    /// anyone re-uploading anything.
    ///
    /// A re-extraction that fails for a reason that is not a verdict on the bytes (a timeout, an I/O
    /// error, a shortage of memory) leaves the row as it was and returns it, so a later pass tries again.
    /// </summary>
    /// <param name="failedThisPass">
    /// Hashes whose re-extraction has failed in this pass. The indexer calls this once per chunk set for
    /// every attachment, so a slow document would cost its whole time budget once per set. A hash that
    /// fails is added here, and one already here is not extracted again.
    /// </param>
    public async Task<BlobText?> CurrentTextFor(string sha256, string fileName,
        ISet<string>? failedThisPass = null, CancellationToken ct = default)
    {
        // Check the version alone before loading anything. Extracted text runs to
        // hundreds of thousands of characters, and the usual answer is "already current"
        // There is no reason to materialise and change-track a book to learn that.
        //
        // The length is screened the same way. SQLite counts code points and stops at a
        // U+0000, .NET counts UTF-16 units, so a shorter count here only says the row may be
        // damaged; the exact comparison is made on the loaded text below.
        var head = await db.BlobTexts.AsNoTracking()
            .Where(t => t.Sha256 == sha256)
            .Select(t => new { t.ExtractorVersion, Suspect = t.Text.Length < t.ExtractedChars })
            .FirstOrDefaultAsync(ct);

        if (head is null) return null;
        if (head.ExtractorVersion >= ExtractorVersions.Current && !head.Suspect) return await TextFor(sha256, ct);

        // Stale or suspect: this one is tracked, because it may be about to be rewritten.
        var cached = await db.BlobTexts.FirstAsync(t => t.Sha256 == sha256, ct);

        // The text of some PDFs holds U+0000, which SQLite ends a value at, so the row was
        // cut there while recording the length of the whole. Handing it out would index the
        // head of the document.
        var damaged = cached.Text.Length != cached.ExtractedChars;
        if (cached.ExtractorVersion >= ExtractorVersions.Current && !damaged) return cached;

        if (!File.Exists(PathFor(sha256)))
        {
            // The bytes are gone, so the old text is all there is. Better stale than none.
            log.LogWarning("Cannot re-extract {Sha}: the blob is missing, keeping v{Version} text",
                sha256[..12], cached.ExtractorVersion);
            return cached;
        }

        // Already failed in this pass, for another chunk set: the cost of a slow document is paid once.
        if (failedThisPass?.Contains(sha256) == true) return cached;

        BlobText fresh;
        try
        {
            fresh = await ExtractAsync(sha256, fileName, ct);
        }
        catch (ExtractionFailedException ex)
        {
            // A timeout, or an I/O error or a shortage of memory, says how the host was and not what the
            // document holds. The row stays as it was, version included, so a later pass extracts again,
            // and the caller carries on with the text it had.
            failedThisPass?.Add(sha256);
            log.LogWarning(ex,
                "Re-extraction of {File} ({Sha}) {Outcome}; keeping the cached v{Version} text and trying again on a later pass",
                fileName, sha256[..12], ex is ExtractionTimeoutException ? "timed out" : "failed", cached.ExtractorVersion);
            return cached;
        }

        if (damaged)
            log.LogWarning(
                "Cached text for {Sha} reads back {Read:N0} of the {Written:N0} characters stored with it; extracted again",
                sha256[..12], cached.Text.Length, cached.ExtractedChars);
        else
            log.LogInformation(
                "Re-extracted {Sha} with extractor v{Version}: {Before:N0} -> {After:N0} chars",
                sha256[..12], ExtractorVersions.Current, cached.ExtractedChars, fresh.ExtractedChars);

        cached.Text = fresh.Text;
        cached.UnitsJson = fresh.UnitsJson;
        cached.Title = fresh.Title;
        cached.ExtractedChars = fresh.ExtractedChars;
        cached.Extractor = fresh.Extractor;
        cached.ExtractorVersion = fresh.ExtractorVersion;
        cached.ExtractedUtc = fresh.ExtractedUtc;
        cached.EmptyReason = fresh.EmptyReason;

        // An upload's chunk state is fingerprinted by the blob hash, the chunk settings and the current
        // extractor version, not by the text, so a rewritten text changes nothing the skip check
        // compares. Two rewrites leave the chunks behind it: a repair of a damaged row, whose head would
        // stay searchable, and the first success after a failed re-extraction of a stale row, whose
        // chunks the indexer stamped with the current version although it chunked the old text. The
        // states are cleared the way the indexer clears a file it is about to redo, in every set that
        // holds this blob, and in the same save as the text so a crash leaves neither half. Where the
        // version bump is the first to reach the document its fingerprint changes as well, so this
        // makes the redo certain and not more frequent.
        var attached = await db.FileChunkStates
            .Where(s => s.File!.BlobSha256 == sha256).ToListAsync(ct);
        foreach (var state in attached)
        {
            state.ContentHash = null;
            state.Status = FileStatus.Pending;
            state.StatusDetail = null;
        }
        await db.SaveChangesAsync(ct);

        return cached;
    }

    public static IReadOnlyList<ExtractedUnit> UnitsFrom(BlobText? text) =>
        string.IsNullOrEmpty(text?.UnitsJson)
            ? []
            : JsonSerializer.Deserialize<List<ExtractedUnit>>(text.UnitsJson) ?? [];

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? bytes.AsSpan(3) : bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string MediaTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".epub" => "application/epub+zip",
        ".html" or ".htm" => "text/html",
        ".md" => "text/markdown",
        ".json" => "application/json",
        _ => "text/plain",
    };
}
