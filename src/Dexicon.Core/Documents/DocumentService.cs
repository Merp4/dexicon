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
///   - uploading the same PDF twice stores one blob and extracts once
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

    /// <summary>Where a blob's bytes live: /data/blobs/ab/abcdef…, with two hex chars of fan-out.</summary>
    public string PathFor(string sha256) =>
        Path.Combine(_storage.BlobRoot, sha256[..2], sha256);

    /// <summary>
    /// Store bytes, extract text once, and return what happened. Does NOT attach the
    /// document to anything; attachment is a separate, per-corpus operation.
    /// </summary>
    public async Task<StoredDocument> StoreAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.", nameof(fileName));

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

            if (size == 0) throw new ArgumentException($"'{fileName}' is empty.", nameof(content));

            await using (var fs = File.OpenRead(temp))
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));

            var final = PathFor(sha);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);

            if (File.Exists(final)) File.Delete(temp);
            else File.Move(temp, final);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        var existing = await db.Blobs.Include(b => b.Text).FirstOrDefaultAsync(b => b.Sha256 == sha, ct);
        if (existing is not null)
        {
            log.LogInformation("Upload '{File}' is an existing blob {Sha}; stored once, extraction reused",
                fileName, sha[..12]);
            return new StoredDocument(sha, existing.SizeBytes, fileName, existing.Text?.Title,
                existing.Text?.ExtractedChars ?? 0, AlreadyExisted: true, existing.Text?.EmptyReason);
        }

        var blob = new Blob
        {
            Sha256 = sha,
            SizeBytes = size,
            MediaType = MediaTypeFor(fileName),
            OriginalFileName = fileName,
            CreatedUtc = DateTime.UtcNow,
        };
        db.Blobs.Add(blob);

        var text = await ExtractAsync(sha, fileName, ct);
        db.BlobTexts.Add(text);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Stored '{File}' as {Sha} ({Size:N0} bytes, {Chars:N0} chars extracted)",
            fileName, sha[..12], size, text.ExtractedChars);

        return new StoredDocument(sha, size, fileName, text.Title, text.ExtractedChars,
            AlreadyExisted: false, text.EmptyReason);
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

    private async Task<BlobText> ExtractAsync(string sha, string fileName, CancellationToken ct)
    {
        var extractor = ExtractorRegistry.For(fileName);
        var path = PathFor(sha);

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
                extracted = extractor.Extract(stream, fileName);
            }

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
        catch (ExtractionFailedException ex)
        {
            // Recorded rather than thrown away: the blob exists, so the UI can show it
            // as failed with a reason instead of the upload appearing to have worked.
            log.LogWarning(ex, "Extraction failed for uploaded '{File}'", fileName);
            return new BlobText
            {
                Sha256 = sha,
                Text = string.Empty,
                ExtractedChars = 0,
                Extractor = extractor?.GetType().Name ?? "PlainText",
                ExtractorVersion = ExtractorVersions.Current,
                ExtractedUtc = DateTime.UtcNow,
                EmptyReason = ex.Message,
            };
        }
    }

    /// <summary>
    /// Attach a stored document to a corpus. The SAME blob may be attached to any number
    /// of corpora; each chunks it with its own settings, producing independent chunk
    /// sets that never see each other.
    /// </summary>
    public async Task<IndexedFile> AttachAsync(Corpus corpus, string sha256, string fileName,
        CancellationToken ct = default)
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

    /// <summary>Every corpus gets at most one upload source, created on first attachment.</summary>
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
        await db.SaveChangesAsync(ct);
        return source;
    }

    /// <summary>Detach from one corpus. The blob survives, since other corpora may still use it.</summary>
    public async Task<bool> DetachAsync(string corpusId, string fileId, CancellationToken ct = default)
    {
        var file = await db.Files.Include(f => f.Source)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.Source!.CorpusId == corpusId, ct);

        if (file is null) return false;
        db.Files.Remove(file);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<BlobText?> TextFor(string sha256, CancellationToken ct = default) =>
        db.BlobTexts.AsNoTracking().FirstOrDefaultAsync(t => t.Sha256 == sha256, ct);

    /// <summary>
    /// The cached text for a blob, re-extracting first if it was produced by an older
    /// extractor. Called on the indexing path, so an extractor fix reaches a library that
    /// was ingested before it without anyone re-uploading anything.
    /// </summary>
    public async Task<BlobText?> CurrentTextFor(string sha256, string fileName, CancellationToken ct = default)
    {
        // Check the version alone before loading anything. Extracted text runs to
        // hundreds of thousands of characters, and the usual answer is "already current"
        // There is no reason to materialise and change-track a book to learn that.
        var version = await db.BlobTexts.AsNoTracking()
            .Where(t => t.Sha256 == sha256)
            .Select(t => (int?)t.ExtractorVersion)
            .FirstOrDefaultAsync(ct);

        if (version is null) return null;
        if (version >= ExtractorVersions.Current) return await TextFor(sha256, ct);

        // Stale: this one is tracked, because it is about to be rewritten.
        var cached = await db.BlobTexts.FirstAsync(t => t.Sha256 == sha256, ct);

        if (!File.Exists(PathFor(sha256)))
        {
            // The bytes are gone, so the old text is all there is. Better stale than none.
            log.LogWarning("Cannot re-extract {Sha}: the blob is missing, keeping v{Version} text",
                sha256[..12], cached.ExtractorVersion);
            return cached;
        }

        var fresh = await ExtractAsync(sha256, fileName, ct);
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
