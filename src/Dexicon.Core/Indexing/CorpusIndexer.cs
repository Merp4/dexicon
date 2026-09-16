using System.Security.Cryptography;
using System.Text;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
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
                if (source.Kind != SourceKind.Workspace) continue;   // uploads land in M2
                await IndexWorkspaceSourceAsync(corpus, source, job, progress,
                    full: job.Kind is JobKind.Full or JobKind.Rebuild,
                    onEmbeddingFailure: () => embeddingFailed = true, ct);
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
                var content = await ReadTextAsync(candidate.FullPath, ct);
                var hash = HashContent(content);

                if (!full && known.TryGetValue(candidate.RelativePath, out var existing)
                          && existing.ContentHash == hash && existing.Status == FileStatus.Indexed)
                {
                    job.FilesSkipped++;
                    continue;   // unchanged — zero embedding calls, which is the point
                }

                if (content.Trim().Length == 0)
                {
                    Upsert(known, source.Id, candidate.RelativePath, f =>
                    {
                        f.Status = FileStatus.Empty;
                        f.StatusDetail = "no extractable text content";
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
                var pieces = CodeChunker.Chunk(candidate.RelativePath, content,
                    corpus.ChunkSize, corpus.ChunkOverlap, corpus.BoundaryMode);

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
                    Section = p.Section,
                    Symbols = p.Symbols,
                    ChunkIndex = p.Index,
                    Content = p.Content,
                }).ToList();

                job.Phase = "embed";
                var embeddings = await embedder.EmbedAsync(chunks.Select(c => c.Content).ToList(), ct);

                job.Phase = "upsert";
                await vectors.UpsertAsync(corpus.CollectionName, chunks, embeddings, ct);

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

                job.FilesDone++;
                job.ChunksWritten += chunks.Count;
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
                Status = FileStatus.Indexed,
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
