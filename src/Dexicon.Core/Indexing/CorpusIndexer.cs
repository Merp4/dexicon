using System.Security.Cryptography;
using System.Text;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Extraction;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Indexing;

public sealed record IndexProgress(
    string JobId,
    string CorpusId,
    string Phase,
    int FilesTotal,
    int FilesDone,
    int FilesSkipped,
    int FilesFailed,
    int ChunksWritten,
    string? CurrentFile,
    string? Error);

/// <summary>
/// Runs one indexing job: discover, triage, extract, chunk, embed, upsert, reconcile.
///
/// Two failure rules matter more than the happy path, and both exist because the
/// alternative was observed to be catastrophic upstream:
///
///  1. A file that cannot be embedded is SKIPPED, not fatal. Aborting the scan on the
///     first bad file left a repository stuck at zero chunks for ten hours, because the
///     same file failed at the same point on every retry and everything after it in
///     enumeration order never got a chance.
///  2. A file's hash is written ONLY on success. A failed file therefore looks changed
///     next scan and is retried, instead of being remembered as done.
/// </summary>
public sealed class CorpusIndexer(
    CatalogDbContext db,
    IVectorStore vectors,
    IEmbeddingProvider embedder,
    DocumentService documents,
    IOptions<DexiconOptions> options,
    ILogger<CorpusIndexer> log)
{
    private readonly IndexingOptions _indexing = options.Value.Indexing;
    private readonly EmbeddingOptions _embedding = options.Value.Embedding;

    public async Task<IndexJob> RunAsync(string jobId, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var job = await db.Jobs.FirstAsync(j => j.Id == jobId, ct);
        var corpus = await db.Corpora.Include(c => c.Sources)
            .FirstAsync(c => c.Id == job.CorpusId, ct);

        job.State = JobState.Running;
        job.StartedUtc = DateTime.UtcNow;
        job.Phase = "discover";
        corpus.State = CorpusState.Indexing;
        await db.SaveChangesAsync(ct);
        Report(progress, job, null);

        var embeddingFailed = false;

        try
        {
            await vectors.EnsureCollectionAsync(corpus.CollectionName, corpus.EmbeddingDimensions, ct);

            foreach (var source in corpus.Sources)
            {
                var full = job.Kind is JobKind.Full or JobKind.Rebuild;

                if (source.Kind == SourceKind.Workspace)
                {
                    await IndexWorkspaceSourceAsync(corpus, source, job, progress, full,
                        onEmbeddingFailure: () => embeddingFailed = true, ct);
                }
                else
                {
                    await IndexUploadSourceAsync(corpus, source, job, progress, full,
                        onEmbeddingFailure: () => embeddingFailed = true, ct);
                }
            }

            job.Phase = "reconcile";
            await db.SaveChangesAsync(ct);

            job.State = embeddingFailed ? JobState.Degraded : JobState.Succeeded;
            corpus.State = embeddingFailed ? CorpusState.Degraded : CorpusState.Ready;
            if (!embeddingFailed) corpus.LastIndexedUtc = DateTime.UtcNow;

            if (embeddingFailed)
                job.Error = "One or more files could not be embedded and were skipped. They will be retried on the next run.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.State = JobState.Cancelled;
            corpus.State = CorpusState.Ready;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Indexing job {JobId} for corpus {Corpus} failed", jobId, corpus.Name);
            job.State = JobState.Failed;
            job.Error = ex.Message;
            corpus.State = CorpusState.Degraded;
        }
        finally
        {
            job.Phase = null;
            job.FinishedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            Report(progress, job, null);
        }

        log.LogInformation(
            "Job {JobId} {State}: {Done} indexed, {Skipped} skipped, {Failed} failed, {Chunks} chunks",
            job.Id, job.State, job.FilesDone, job.FilesSkipped, job.FilesFailed, job.ChunksWritten);

        return job;
    }

    /// <summary>
    /// Index uploaded documents. The bytes are never re-read and the PDF is never
    /// re-opened: extraction was cached against the blob hash at upload time, so this
    /// only chunks and embeds. That is what makes the same document cheap to hold in
    /// several corpora with different chunk settings, and cheap to re-chunk when those
    /// settings change.
    /// </summary>
    private async Task IndexUploadSourceAsync(Corpus corpus, Source source, IndexJob job,
        IProgress<IndexProgress>? progress, bool full, Action onEmbeddingFailure, CancellationToken ct)
    {
        var attachments = await db.Files
            .Where(f => f.SourceId == source.Id && f.BlobSha256 != null)
            .ToListAsync(ct);

        job.FilesTotal += attachments.Count;
        job.Phase = "extract";
        await db.SaveChangesAsync(ct);
        Report(progress, job, null);

        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();

        foreach (var file in attachments)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Re-extracts first if this text came from an older extractor, so a fix
                // reaches documents that were ingested before it.
                var cached = await documents.CurrentTextFor(file.BlobSha256!, file.RelativePath, ct);

                if (cached is null)
                {
                    file.Status = FileStatus.Failed;
                    file.StatusDetail = "the stored document has no extracted text — re-upload it";
                    file.ContentHash = null;
                    job.FilesFailed++;
                    continue;
                }

                if (cached.EmptyReason is { Length: > 0 } || cached.Text.Trim().Length == 0)
                {
                    file.Status = FileStatus.Empty;
                    file.StatusDetail = cached.EmptyReason ?? "no extractable text content";
                    file.ChunkCount = 0;
                    file.ExtractedChars = 0;
                    // Hash IS recorded: an empty extraction is a settled outcome, not a
                    // failure to retry. Re-uploading the file is what changes it.
                    file.ContentHash = ChunkingFingerprint(corpus, cached.Sha256);
                    file.IndexedUtc = DateTime.UtcNow;
                    job.FilesSkipped++;
                    continue;
                }

                // The fingerprint mixes the blob hash WITH the corpus's chunk settings,
                // so changing chunk size or boundary mode makes every attachment look
                // changed and re-chunks it — without touching the bytes.
                var fingerprint = ChunkingFingerprint(corpus, cached.Sha256);
                if (!full && file.ContentHash == fingerprint && file.Status == FileStatus.Indexed)
                {
                    job.FilesSkipped++;
                    continue;
                }

                await vectors.DeleteFileChunksAsync(corpus.CollectionName, corpus.Id, file.RelativePath, ct);

                var units = Documents.DocumentService.UnitsFrom(cached);
                var extracted = new ExtractedText(cached.Text, units, cached.Title);
                var language = LanguageMap.Detect(file.RelativePath);

                // Documents are chunked as prose: a C# member-boundary regex finds
                // nothing useful in extracted PDF text.
                var pieces = CodeChunker.Chunk(file.RelativePath, cached.Text,
                    corpus.ChunkSize, corpus.ChunkOverlap, "blank-line");

                var chunks = pieces.Select(p => new Chunk
                {
                    CorpusId = corpus.Id,
                    TenantId = corpus.TenantId,
                    SourceId = source.Id,
                    FilePath = file.RelativePath,
                    FileHash = fingerprint,
                    MediaType = file.MediaType,
                    Language = language,
                    StartLine = p.StartLine,
                    EndLine = p.EndLine,
                    Section = p.Section ?? UnitLabelFor(extracted, p.StartLine, cached.Text),
                    Page = UnitNumberFor(extracted, p.StartLine, cached.Text),
                    Symbols = p.Symbols,
                    ChunkIndex = p.Index,
                    Content = p.Content,
                }).ToList();

                await EmbedAndUpsertAsync(corpus, chunks, file.RelativePath, job, progress, sinceFlush, ct);

                file.Status = FileStatus.Indexed;
                file.StatusDetail = null;
                file.ContentHash = fingerprint;
                file.Language = language;
                file.ChunkCount = chunks.Count;
                file.ExtractedChars = cached.ExtractedChars;
                file.IndexedUtc = DateTime.UtcNow;
                job.FilesDone++;
            }
            catch (EmbeddingUnavailableException ex)
            {
                log.LogWarning(ex, "Embedding failed for uploaded {File}; skipping it and continuing", file.RelativePath);
                file.Status = FileStatus.Failed;
                file.StatusDetail = $"embedding failed: {ex.Message}";
                file.ContentHash = null;
                job.FilesFailed++;
                onEmbeddingFailure();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Failed to index uploaded {File}", file.RelativePath);
                file.Status = FileStatus.Failed;
                file.StatusDetail = ex.Message;
                file.ContentHash = null;
                job.FilesFailed++;
            }

            if (sinceFlush.ElapsedMilliseconds >= 1000)
            {
                await db.SaveChangesAsync(ct);
                Report(progress, job, file.RelativePath);
                sinceFlush.Restart();
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Upload source: {Indexed} indexed, {Skipped} skipped, {Failed} failed",
            job.FilesDone, job.FilesSkipped, job.FilesFailed);
    }

    /// <summary>
    /// Embed and upsert a file's chunks in batches, reporting progress between them.
    ///
    /// Batched rather than one call per file, because a 437-page PDF is ONE file
    /// producing thousands of chunks: per-file progress left the UI on "0 done" for
    /// minutes with no way to tell a slow job from a hung one. It also caps peak memory
    /// at one batch of vectors instead of all of them. Shared by both source kinds.
    /// </summary>
    private async Task EmbedAndUpsertAsync(Corpus corpus, List<Chunk> chunks, string label,
        IndexJob job, IProgress<IndexProgress>? progress, System.Diagnostics.Stopwatch sinceFlush,
        CancellationToken ct)
    {
        // Hand the provider MaxConcurrency batches at a time so it can run them in
        // parallel, while still reporting progress at that granularity.
        var batchSize = Math.Max(1, _embedding.BatchSize) * Math.Max(1, _embedding.MaxConcurrency);

        for (var offset = 0; offset < chunks.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.GetRange(offset, Math.Min(batchSize, chunks.Count - offset));
            var through = offset + batch.Count;

            job.Phase = "embed";
            Report(progress, job, $"{label} - chunk {through}/{chunks.Count}");

            var embeddings = await embedder.EmbedAsync(batch.Select(c => c.Content).ToList(), ct);

            job.Phase = "upsert";
            await vectors.UpsertAsync(corpus.CollectionName, batch, embeddings, ct);

            job.ChunksWritten += batch.Count;
            Report(progress, job, $"{label} - chunk {through}/{chunks.Count}");

            // SSE alone is not enough: /api/jobs reads the catalogue, so without a
            // persist here a single-file corpus shows zero progress to anyone polling.
            // Throttled, because SaveChanges per batch is not free.
            if (sinceFlush.ElapsedMilliseconds >= 1000)
            {
                await db.SaveChangesAsync(ct);
                sinceFlush.Restart();
            }
        }
    }

    /// <summary>
    /// Identity of "this blob, chunked THIS way". Two corpora holding the same document
    /// with different settings produce different fingerprints, so neither can mistake
    /// the other's work for its own, and changing a setting invalidates exactly the
    /// attachments it should.
    /// </summary>
    /// <summary>
    /// The staleness key: everything that determines what ends up in Qdrant. If any part
    /// changes, the file is re-chunked; if none has, it is skipped at zero embedding
    /// cost. The extractor version is in here because an extraction fix changes the text
    /// itself — without it, improved text would be re-extracted and then skipped as
    /// "unchanged", which is the worst of both.
    /// </summary>
    internal static string ChunkingFingerprint(Corpus corpus, string blobSha) =>
        HashContent($"{blobSha}|{corpus.ChunkSize}|{corpus.ChunkOverlap}|{corpus.BoundaryMode}|" +
                    $"{corpus.EmbeddingModel}|x{ExtractorVersions.Current}|c{CodeChunker.Version}");

    private async Task IndexWorkspaceSourceAsync(Corpus corpus, Source source, IndexJob job,
        IProgress<IndexProgress>? progress, bool full, Action onEmbeddingFailure, CancellationToken ct)
    {
        var root = ResolveWorkspacePath(source.RootPath);
        if (!Directory.Exists(root))
        {
            // Not destructive: the existing index stays searchable. A missing mount is
            // an operational condition, not a reason to delete someone's corpus.
            corpus.State = CorpusState.Unavailable;
            job.Error = $"Workspace path '{source.RootPath}' is not available under {_indexing.WorkspaceRoot}.";
            log.LogWarning("{Error}", job.Error);
            return;
        }

        var walk = WorkspaceWalker.Walk(root, source.UseGitignore,
            ParseGlobs(source.IncludeGlobs), ParseGlobs(source.ExcludeGlobs), source.MaxFileBytes);

        job.FilesTotal = walk.Files.Count;
        job.Phase = "extract";
        await db.SaveChangesAsync(ct);
        Report(progress, job, null);

        var known = await db.Files.Where(f => f.SourceId == source.Id)
            .ToDictionaryAsync(f => f.RelativePath, f => f, StringComparer.Ordinal, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();

        foreach (var skip in walk.SkippedFiles)
        {
            seen.Add(skip.RelativePath);
            Upsert(known, source.Id, skip.RelativePath, f =>
            {
                f.Status = FileStatus.Skipped;
                f.StatusDetail = skip.Reason;
                f.ContentHash = null;
                f.ChunkCount = 0;
            });
            job.FilesSkipped++;
        }

        foreach (var candidate in walk.Files)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(candidate.RelativePath);

            try
            {
                var extractor = ExtractorRegistry.For(candidate.RelativePath);
                ExtractedText extracted;

                if (extractor is null)
                {
                    extracted = new ExtractedText(await ReadTextAsync(candidate.FullPath, ct), []);
                }
                else
                {
                    await using var stream = File.OpenRead(candidate.FullPath);
                    extracted = extractor.Extract(stream, candidate.RelativePath);
                }

                var content = extracted.Text;

                // The stored hash is the CHUNKING FINGERPRINT, not the raw content hash.
                // With a content hash alone, changing a corpus's chunk size left every
                // file looking unchanged, so a refresh re-chunked nothing and the new
                // setting silently did not apply. Mixing the settings in makes exactly
                // the right set of files look stale — and no others.
                var hash = ChunkingFingerprint(corpus, HashContent(content));

                if (!full && known.TryGetValue(candidate.RelativePath, out var existing)
                          && existing.ContentHash == hash && existing.Status == FileStatus.Indexed)
                {
                    job.FilesSkipped++;
                    continue;   // unchanged — zero embedding calls, which is the point
                }

                if (content.Trim().Length == 0)
                {
                    // Said plainly rather than left as an absence. "Why isn't my PDF
                    // searchable" is answered here, in the UI, instead of by silence.
                    var reason = extractor is PdfTextExtractor
                        ? "no text layer — this is a scanned PDF, and OCR is not supported"
                        : "no extractable text content";

                    Upsert(known, source.Id, candidate.RelativePath, f =>
                    {
                        f.Status = FileStatus.Empty;
                        f.StatusDetail = reason;
                        f.SizeBytes = candidate.SizeBytes;
                        f.ContentHash = hash;
                        f.ChunkCount = 0;
                        f.ExtractedChars = 0;
                        f.IndexedUtc = DateTime.UtcNow;
                    });
                    job.FilesSkipped++;
                    continue;
                }

                var language = LanguageMap.Detect(candidate.RelativePath);

                // A document is chunked as prose regardless of its extension: applying a
                // C# member-boundary regex to extracted PDF text finds nothing useful.
                var pieces = extractor is null
                    ? CodeChunker.Chunk(candidate.RelativePath, content,
                        corpus.ChunkSize, corpus.ChunkOverlap, corpus.BoundaryMode)
                    : CodeChunker.Chunk(candidate.RelativePath, content,
                        corpus.ChunkSize, corpus.ChunkOverlap, "blank-line");

                if (pieces.Count == 0)
                {
                    Upsert(known, source.Id, candidate.RelativePath, f =>
                    {
                        f.Status = FileStatus.Empty;
                        f.StatusDetail = "chunker produced no chunks";
                        f.ContentHash = hash;
                        f.ChunkCount = 0;
                        f.IndexedUtc = DateTime.UtcNow;
                    });
                    job.FilesSkipped++;
                    continue;
                }

                // Replace rather than merge: a changed file's old chunks are stale by
                // definition, and leaving them produces results pointing at lines that
                // no longer say what the result claims.
                await vectors.DeleteFileChunksAsync(corpus.CollectionName, corpus.Id, candidate.RelativePath, ct);

                var chunks = pieces.Select(p => new Chunk
                {
                    CorpusId = corpus.Id,
                    TenantId = corpus.TenantId,
                    SourceId = source.Id,
                    FilePath = candidate.RelativePath,
                    FileHash = hash,
                    MediaType = LanguageMap.MediaType(language),
                    Language = language,
                    StartLine = p.StartLine,
                    EndLine = p.EndLine,
                    Section = p.Section ?? UnitLabelFor(extracted, p.StartLine, content),
                    Page = UnitNumberFor(extracted, p.StartLine, content),
                    Symbols = p.Symbols,
                    ChunkIndex = p.Index,
                    Content = p.Content,
                }).ToList();

                // Embed and upsert in batches rather than in one go. A 500-page PDF is
                // ONE file producing thousands of chunks, so per-file progress leaves the
                // UI on "0 done" for minutes with no way to tell a slow job from a hung
                // one — observed on a 3 MB PDF at roughly 17 s per 32-chunk batch, with
                // the phase still reading "extract" because it was set but never reported
                // before the long call. Batching also caps peak memory at one batch of
                // vectors instead of all of them.
                await EmbedAndUpsertAsync(corpus, chunks, candidate.RelativePath, job, progress, sinceFlush, ct);

                Upsert(known, source.Id, candidate.RelativePath, f =>
                {
                    f.Status = FileStatus.Indexed;
                    f.StatusDetail = null;
                    f.ContentHash = hash;          // written ONLY here, on success
                    f.SizeBytes = candidate.SizeBytes;
                    f.Language = language;
                    f.MediaType = LanguageMap.MediaType(language);
                    f.ChunkCount = chunks.Count;
                    f.ExtractedChars = content.Length;
                    f.IndexedUtc = DateTime.UtcNow;
                });

                job.FilesDone++;   // ChunksWritten is accumulated per batch above
            }
            catch (ExtractionFailedException ex)
            {
                // A recognised format we could not read: encrypted, DRM'd, or corrupt.
                // Distinct from "produced no text", which is not a failure.
                log.LogWarning(ex, "Extraction failed for {File}", candidate.RelativePath);
                Upsert(known, source.Id, candidate.RelativePath, f =>
                {
                    f.Status = FileStatus.Failed;
                    f.StatusDetail = ex.Message;
                    f.ContentHash = null;
                });
                job.FilesFailed++;
            }
            catch (EmbeddingUnavailableException ex)
            {
                // Skip the FILE, flag the job, keep scanning. See the class remark.
                log.LogWarning(ex, "Embedding failed for {File}; skipping it and continuing", candidate.RelativePath);
                Upsert(known, source.Id, candidate.RelativePath, f =>
                {
                    f.Status = FileStatus.Failed;
                    f.StatusDetail = $"embedding failed: {ex.Message}";
                    f.ContentHash = null;          // deliberately unrecorded, so it retries
                });
                job.FilesFailed++;
                onEmbeddingFailure();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Failed to index {File}", candidate.RelativePath);
                Upsert(known, source.Id, candidate.RelativePath, f =>
                {
                    f.Status = FileStatus.Failed;
                    f.StatusDetail = ex.Message;
                    f.ContentHash = null;
                });
                job.FilesFailed++;
            }

            // Flush on EITHER a file count or a time budget. Count alone means a corpus
            // with fewer files than the batch size reports nothing at all until it
            // finishes — observed on a 12-file corpus sitting at done=0 for 24 seconds,
            // which is indistinguishable from a hung job.
            var processed = job.FilesDone + job.FilesSkipped + job.FilesFailed;
            if (processed % 25 == 0 || sinceFlush.ElapsedMilliseconds >= 1000)
            {
                await db.SaveChangesAsync(ct);
                Report(progress, job, candidate.RelativePath);
                sinceFlush.Restart();
            }
        }

        // Reconcile: anything the catalogue knows that the walk no longer saw is gone.
        var vanished = known.Keys.Except(seen, StringComparer.Ordinal).ToList();
        foreach (var path in vanished)
        {
            try
            {
                await vectors.DeleteFileChunksAsync(corpus.CollectionName, corpus.Id, path, ct);
                db.Files.Remove(known[path]);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to remove index entries for deleted file {File}", path);
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Source {Source}: {Indexed} indexed, {Skipped} skipped, {Failed} failed, {Removed} removed",
            source.RootPath, job.FilesDone, job.FilesSkipped, job.FilesFailed, vanished.Count);
    }

    /// <summary>
    /// Resolve a source path inside the workspace root and refuse anything that escapes
    /// it. A relative path with <c>..</c>, or an absolute one, must not be able to reach
    /// the container filesystem.
    /// </summary>
    public string ResolveWorkspacePath(string? relative)
    {
        var root = Path.GetFullPath(_indexing.WorkspaceRoot);
        var combined = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Workspace path '{relative}' resolves outside {_indexing.WorkspaceRoot} and was refused.");

        return combined;
    }

    private void Upsert(Dictionary<string, IndexedFile> known, string sourceId, string relativePath,
        Action<IndexedFile> mutate)
    {
        if (!known.TryGetValue(relativePath, out var file))
        {
            file = new IndexedFile
            {
                Id = Ulid.NewUlid().ToString(),
                SourceId = sourceId,
                RelativePath = relativePath,
                Status = FileStatus.Pending,   // discovered, not yet chunked
            };
            known[relativePath] = file;
            db.Files.Add(file);
        }
        mutate(file);
    }

    private static void Report(IProgress<IndexProgress>? progress, IndexJob job, string? currentFile) =>
        progress?.Report(new IndexProgress(job.Id, job.CorpusId, job.Phase ?? job.State.ToString(),
            job.FilesTotal, job.FilesDone, job.FilesSkipped, job.FilesFailed, job.ChunksWritten,
            currentFile, job.Error));

    private static List<string>? ParseGlobs(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);

    /// <summary>
    /// Map a chunk's first line back to the page / slide / chapter it came from, so a
    /// PDF citation can say "p. 34" rather than "chunk 87".
    /// </summary>
    private static int? UnitNumberFor(ExtractedText extracted, int startLine, string content)
    {
        if (extracted.Units.Count == 0) return null;
        var offset = OffsetOfLine(content, startLine);
        ExtractedUnit? found = null;
        foreach (var u in extracted.Units)
        {
            if (u.StartOffset > offset) break;
            found = u;
        }
        return found?.Number;
    }

    private static string? UnitLabelFor(ExtractedText extracted, int startLine, string content)
    {
        if (extracted.Units.Count == 0) return null;
        var offset = OffsetOfLine(content, startLine);
        ExtractedUnit? found = null;
        foreach (var u in extracted.Units)
        {
            if (u.StartOffset > offset) break;
            found = u;
        }
        return found?.Label;
    }

    private static int OffsetOfLine(string content, int oneBasedLine)
    {
        var line = 1;
        for (var i = 0; i < content.Length; i++)
        {
            if (line >= oneBasedLine) return i;
            if (content[i] == '\n') line++;
        }
        return content.Length;
    }

    internal static string HashContent(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>BOM, then UTF-8, then Latin-1 — never throw on a file with odd bytes.</summary>
    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? bytes.AsSpan(3)
                    : bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
