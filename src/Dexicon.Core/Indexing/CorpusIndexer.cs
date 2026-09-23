using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    WorkspaceFileReader reader,
    IVectorStore vectors,
    IEmbeddingService embedder,
    IModelProfiles profiles,
    DocumentService documents,
    CorpusLeases leases,
    IOptions<DexiconOptions> options,
    ILogger<CorpusIndexer> log)
{
    private readonly IndexingOptions _indexing = options.Value.Indexing;
    private readonly EmbeddingOptions _embedding = options.Value.Embedding;

    public async Task<IndexJob> RunAsync(string jobId, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var job = await db.Jobs.FirstAsync(j => j.Id == jobId, ct);
        var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
            .FirstAsync(c => c.Id == job.CorpusId, ct);

        // A job either targets one set, which is how a replacement is backfilled while
        // the live set keeps serving, or every set in the corpus.
        var targets = job.ChunkSetId is { Length: > 0 } only
            ? corpus.ChunkSets.Where(s => s.Id == only).ToList()
            : corpus.ChunkSets.ToList();

        var embeddingFailed = false;

        // A source this pass could not reach: a mount that is away, a folder with no
        // repository in it. Not a failure of the job and not a success either.
        var unavailable = false;

        // The caller's own cancellation, kept apart from losing the lease: one is someone
        // deciding to stop and the other is the machinery taking the corpus away, and they
        // are not the same outcome to record.
        var callerCancelled = ct;
        CorpusLeases.Hold? hold = null;

        // A deferred job did not run and must not be marked finished: it is still Queued,
        // which is what the worker reads to know to put it back.
        var deferred = false;

        try
        {
            // Inside the try, because everything that can go wrong here has to end up on
            // the job. Taking it outside left a job that could not get the lease sitting
            // Queued with no finish time, and EnqueueAsync then coalesced every later
            // request onto that stranded row without putting it back on the channel, so
            // the corpus could never be indexed again.
            //
            // Held for the whole job, so a sweep is turned away at the door rather than
            // walking the rows this is writing. Renewed in the background, so a job that
            // runs for hours keeps it without anything predicting how long it will take.
            hold = await TryTakeLeaseAsync(corpus.Id, $"index-{job.Id}", ct);
            if (hold is null)
            {
                // Someone else has the corpus, so this job is not runnable yet. It stays
                // Queued and the worker puts it back: parking here would hold the single
                // indexing reader, so one corpus being swept would stop every other from
                // indexing at all.
                deferred = true;
                log.LogInformation(
                    "Corpus {Corpus} is held by another pass; deferring job {JobId}",
                    corpus.Name, job.Id);
                return job;
            }

            // Losing the lease ends the job. Carrying on would mean writing beside
            // whoever now holds the corpus, which is the overlap the lease exists for.
            using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct, hold.Lost);
            ct = leaseLost.Token;

            job.State = JobState.Running;
            job.StartedUtc = DateTime.UtcNow;
            job.Phase = "discover";
            corpus.State = CorpusState.Indexing;
            foreach (var s in targets) s.State = CorpusState.Indexing;
            await db.SaveChangesAsync(ct);
            Report(progress, job, null);

            // The sources are read AFTER the row says Running, and the ordering is the
            // point rather than tidiness.
            //
            // `EnqueueAsync` coalesces a refresh onto a job that is still Queued, and
            // answers the caller with it: the promise is that this job covers what they
            // asked for. The corpus was loaded with its sources before the lease was
            // taken, so a source added while this job sat between those two reads was
            // never walked, and the request that added it was reported as covered by a
            // pass that could not have seen it. That is the defect the long comment in
            // EnqueueAsync describes, arrived at from the other end.
            //
            // Nothing can coalesce onto this job now, because the row is no longer
            // Queued, so reading here cannot miss a caller that was promised this pass.
            var sources = await db.Sources.Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);

            if (targets.Count == 0)
                throw new InvalidOperationException(
                    job.ChunkSetId is { Length: > 0 }
                        ? $"Corpus '{corpus.Name}' has no chunk set '{job.ChunkSetId}'."
                        : $"Corpus '{corpus.Name}' has no chunk sets, so there is nothing to index into.");

            // Each set is a separate vector space and a separate pass. A workspace tree is
            // therefore walked once per set: the duplication is real but bounded, and most
            // corpora carry one set. Sharing one walk across sets would mean holding the
            // whole discovery in memory, which a large monorepo makes a worse trade.
            foreach (var set in targets)
            {
                await vectors.EnsureCollectionAsync(set.CollectionName, set.EmbeddingDimensions, ct);

                // Once per set, not once per file: the templates are the same for every
                // file in it, and they are part of the staleness key for all of them.
                var templates = await profiles.ForAsync(set.Target(), ct);

                // The same, for what the model measured about itself. The chunk budget is
                // reconciled against it here rather than per file, and a set whose model
                // was never probed gets the configured size unchanged.
                var measured = await db.ModelMeasurements.AsNoTracking()
                    .FirstOrDefaultAsync(mm => mm.Provider == set.EmbeddingProvider
                                            && mm.Model == set.EmbeddingModel, ct);
                var chunking = set.Options(measured);

                if (chunking.ChunkSizeTokens != set.ChunkSize)
                    log.LogWarning(
                        "Chunk set {Set}: size {Configured:N0} tokens exceeds what {Model} reads "
                        + "in one go, chunking at {Effective:N0}",
                        set.Name, set.ChunkSize, set.EmbeddingModel, chunking.ChunkSizeTokens);

                foreach (var source in sources)
                {
                    var full = job.Kind is JobKind.Full or JobKind.Rebuild;

                    // Every kind named, and an unknown one throws rather than being
                    // skipped. It used to be an if with an else meaning Upload, which
                    // would have indexed a repository's history as a set of attachments
                    // and found none; a switch without the default below is no better,
                    // because C# does not check a switch STATEMENT for exhaustiveness
                    // and a fourth kind would silently match nothing and be left out of
                    // its own index with the job reporting success.
                    switch (source.Kind)
                    {
                        case SourceKind.Workspace:
                            await IndexWorkspaceSourceAsync(corpus, set, templates, chunking, source, job,
                                progress, full, onEmbeddingFailure: () => embeddingFailed = true, ct);
                            break;

                        case SourceKind.GitHistory:
                            await IndexGitHistorySourceAsync(corpus, set, templates, chunking, source, job,
                                progress, full, onEmbeddingFailure: () => embeddingFailed = true, ct);
                            break;

                        case SourceKind.Upload:
                            await IndexUploadSourceAsync(corpus, set, templates, chunking, source, job,
                                progress, full, onEmbeddingFailure: () => embeddingFailed = true, ct);
                            break;

                        default:
                            throw new InvalidOperationException(
                                $"Source {source.Id} is of kind {source.Kind}, which this pass "
                                + "does not know how to index.");
                    }
                }

                // A source that could not be reached sets the corpus Unavailable and
                // returns, and that outcome has to survive the assignments below. It did
                // not: a missing mount or a folder with no repository in it finished as a
                // ready corpus and a succeeded job, with only `job.Error` saying anything
                // was wrong, so a caller polling the job for success was told yes.
                unavailable |= corpus.State == CorpusState.Unavailable;

                // Unavailable ahead of Degraded: a source nobody can reach needs someone to
                // look at a mount, and files that failed to embed are retried next run.
                set.State = unavailable ? CorpusState.Unavailable
                    : embeddingFailed ? CorpusState.Degraded
                    : CorpusState.Ready;

                if (!embeddingFailed && !unavailable) set.LastIndexedUtc = DateTime.UtcNow;
            }

            job.Phase = "reconcile";
            await db.SaveChangesAsync(ct);

            // Degraded rather than Succeeded for an unreachable source: the pass did run
            // and the sources it could reach are indexed, so it is not Failed, and it is
            // not success either.
            job.State = embeddingFailed || unavailable ? JobState.Degraded : JobState.Succeeded;

            corpus.State = unavailable ? CorpusState.Unavailable
                : embeddingFailed ? CorpusState.Degraded
                : CorpusState.Ready;

            if (!embeddingFailed && !unavailable) corpus.LastIndexedUtc = DateTime.UtcNow;

            if (embeddingFailed)
                AddReason(job, "One or more files could not be embedded and were skipped. They will be retried on the next run.");
        }
        catch (OperationCanceledException) when (callerCancelled.IsCancellationRequested)
        {
            job.State = JobState.Cancelled;
            if (Holds(hold))
            {
                corpus.State = CorpusState.Ready;
                foreach (var s in targets) s.State = CorpusState.Ready;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Indexing job {JobId} for corpus {Corpus} failed", jobId, corpus.Name);
            job.State = JobState.Failed;
            AddReason(job, ex.Message);

            // Only while this job still owns the corpus. A job that never got the lease,
            // or lost it, would otherwise mark a corpus someone else is working as
            // degraded, and the save below would make that the record.
            if (Holds(hold))
            {
                corpus.State = CorpusState.Degraded;
                foreach (var s in targets) s.State = CorpusState.Degraded;
            }
        }
        finally
        {
            try
            {
                // A deferred job never ran, so it is not finished: it stays Queued with no
                // finish time, which is what the worker reads to know to put it back.
                if (!deferred)
                {
                    job.Phase = null;
                    job.FinishedUtc = DateTime.UtcNow;

                    // Nothing about the corpus or its sets is written by a job that does
                    // not own it. Detaching rather than reverting, because the tracked
                    // values came from this job's own work and the holder's are whatever
                    // is in the row.
                    if (!Holds(hold))
                    {
                        db.Entry(corpus).State = EntityState.Detached;
                        foreach (var s in targets) db.Entry(s).State = EntityState.Detached;
                    }

                    await db.SaveChangesAsync(CancellationToken.None);
                    Report(progress, job, null);
                }
            }
            finally
            {
                // Inner, so a failing save cannot skip it. Leaking a hold leaves the
                // renewal task extending a lease for a job nobody is running, and the
                // corpus is then blocked until the process ends rather than for the
                // expiry.
                if (hold is not null) await hold.DisposeAsync();
            }
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
    private async Task IndexUploadSourceAsync(Corpus corpus, ChunkSet set, ModelTemplates templates,
        ChunkOptions chunking, Source source, IndexJob job,
        IProgress<IndexProgress>? progress, bool full, Action onEmbeddingFailure, CancellationToken ct)
    {
        var attachments = await db.Files
            .Where(f => f.SourceId == source.Id && f.BlobSha256 != null)
            .ToListAsync(ct);

        var states = await StatesFor(set, attachments, ct);

        // The same comparison the workspace path makes, on a path-keyed view of the same
        // rows: an upload whose vectors were lost reaches the skip check below with a
        // matching fingerprint and stays unsearchable otherwise. The rows are the same
        // objects, so clearing a hash here is what the check reads a few lines down.
        var byPath = new Dictionary<string, FileChunkState>(attachments.Count, StringComparer.Ordinal);
        foreach (var f in attachments) byPath[f.RelativePath] = states[f.Id];
        await ReconcileChunkCountsAsync(set, source.Id, byPath, ct);

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
                var state = states[file.Id];

                if (cached is null)
                {
                    state.Status = FileStatus.Failed;
                    state.StatusDetail = "the stored document has no extracted text; re-upload it";
                    state.ContentHash = null;
                    job.FilesFailed++;
                    continue;
                }

                if (cached.EmptyReason is { Length: > 0 } || cached.Text.Trim().Length == 0)
                {
                    // Whatever this document produced before goes now, and nothing else
                    // would remove it. The delete on the success path is below the
                    // `continue`. The workspace method's closing reconcile, which drops
                    // the vectors of files a walk stopped seeing, has no counterpart
                    // here. And ReconcileChunkCountsAsync above looks only at rows
                    // recording Indexed, while the row written here says Empty.
                    //
                    // So a document that extracted to text under an older extractor and
                    // to nothing under this one would answer searches forever: this
                    // delete is the only thing between an Empty row and live chunks.
                    //
                    // A failure here is caught below and recorded as Failed, which is the
                    // honest outcome: the row must not claim Empty over live chunks.
                    await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, file.SourceId,
                        file.RelativePath, ct);

                    state.Status = FileStatus.Empty;
                    state.StatusDetail = cached.EmptyReason ?? "no extractable text content";
                    state.ChunkCount = 0;
                    file.ExtractedChars = 0;
                    // Hash IS recorded: an empty extraction is a settled outcome, not a
                    // failure to retry. Re-uploading the file is what changes it.
                    state.ContentHash = ChunkingFingerprint(set, cached.Sha256, templates, chunking);
                    state.IndexedUtc = DateTime.UtcNow;
                    job.FilesSkipped++;
                    continue;
                }

                // The fingerprint mixes the blob hash WITH the corpus's chunk settings,
                // so changing chunk size or boundary mode makes every attachment look
                // changed and re-chunks it, without touching the bytes.
                var fingerprint = ChunkingFingerprint(set, cached.Sha256, templates, chunking);
                if (!full && state.ContentHash == fingerprint && state.Status == FileStatus.Indexed)
                {
                    job.FilesSkipped++;
                    continue;
                }

                // Claimed before the delete and made durable, for the same reason as the
                // workspace path: until the success assignment below, this row still
                // describes vectors that are about to stop existing.
                state.ContentHash = null;
                state.Status = FileStatus.Pending;
                await db.SaveChangesAsync(ct);

                await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, file.SourceId, file.RelativePath, ct);

                var units = Documents.DocumentService.UnitsFrom(cached);
                var extracted = new ExtractedText(cached.Text, units, cached.Title);
                var language = LanguageMap.Detect(file.RelativePath);

                // Documents are chunked as prose: a C# member-boundary regex finds
                // nothing useful in extracted PDF text, so the set's boundary mode is
                // overridden here while everything else about the set is honoured.
                var pieces = CodeChunker.Chunk(file.RelativePath, cached.Text,
                    chunking with { BoundaryMode = "blank-line" }, extracted);

                var chunks = pieces.Select(p => new Chunk
                {
                    CorpusId = corpus.Id,
                    ChunkSetId = set.Id,
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
                    EmbedText = p.EmbedText,
                }).ToList();

                var stored = await EmbedAndUpsertAsync(set, chunks, file.RelativePath, job, progress, sinceFlush, ct);

                state.Status = FileStatus.Indexed;
                state.StatusDetail = null;
                state.ContentHash = fingerprint;
                state.ChunkCount = stored;
                state.IndexedUtc = DateTime.UtcNow;
                file.Language = language;
                file.ExtractedChars = cached.ExtractedChars;
                job.FilesDone++;
            }
            catch (EmbeddingUnavailableException ex)
            {
                log.LogWarning(ex, "Embedding failed for uploaded {File}; skipping it and continuing", file.RelativePath);
                var failed = states[file.Id];
                failed.Status = FileStatus.Failed;
                failed.StatusDetail = $"embedding failed: {ex.Message}";
                failed.ContentHash = null;
                job.FilesFailed++;
                onEmbeddingFailure();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Failed to index uploaded {File}", file.RelativePath);
                var failed = states[file.Id];
                failed.Status = FileStatus.Failed;
                failed.StatusDetail = ex.Message;
                failed.ContentHash = null;
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
    /// <returns>Chunks actually written, which exceeds <paramref name="chunks"/> when a split occurred.</returns>
    private async Task<int> EmbedAndUpsertAsync(ChunkSet set, List<Chunk> chunks, string label,
        IndexJob job, IProgress<IndexProgress>? progress, System.Diagnostics.Stopwatch sinceFlush,
        CancellationToken ct)
    {
        // Hand the provider MaxConcurrency batches at a time so it can run them in
        // parallel, while still reporting progress at that granularity.
        var batchSize = Math.Max(1, _embedding.BatchSize) * Math.Max(1, _embedding.MaxConcurrency);

        // Chunks are numbered as they are written rather than as they were cut, so a split
        // puts its halves between their neighbours instead of after the whole file. See
        // EmbedBatchAsync.
        var written = 0;

        for (var offset = 0; offset < chunks.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.GetRange(offset, Math.Min(batchSize, chunks.Count - offset));
            var through = offset + batch.Count;

            job.Phase = "embed";
            Report(progress, job, $"{label} - chunk {through}/{chunks.Count}");

            var count = await EmbedBatchAsync(
                set, batch, $"{label} chunks {offset + 1}-{through}", written, job, ct);

            job.ChunksWritten += count;
            written += count;
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

        return written;
    }

    /// <summary>
    /// Embed a batch and upsert it, dividing anything the provider will not accept.
    ///
    /// The refusal is the only exact statement about the model's limit available here, and
    /// it costs about 350 ms flat whatever the input size, against seconds for an accepted
    /// embed. That makes it cheap enough to size by rather than predict around. The
    /// dividing itself is <see cref="DivideAndWriteAsync"/>; this supplies the writing.
    /// </summary>
    private Task<int> EmbedBatchAsync(
        ChunkSet set, List<Chunk> batch, string source, int firstIndex, IndexJob job,
        CancellationToken ct) =>
        DivideAndWriteAsync(batch, firstIndex,
            write: async numbered =>
            {
                job.Phase = "embed";

                // TextToEmbed, not Content: a set with heading context embeds each chunk
                // under its heading trail while storing the chunk verbatim.
                var embeddings = await embedder.EmbedAsync(
                    set.Target(), EmbedPurpose.Document,
                    numbered.Select(c => c.TextToEmbed).ToList(), source: source, ct: ct);

                job.Phase = "upsert";
                await vectors.UpsertAsync(set.CollectionName, numbered, embeddings, ct);
            },
            onSplit: (original, second) => log.LogInformation(
                "{Source}: chunk {Index} exceeds the model's context and was split at line {Line}",
                source, original.ChunkIndex, second.StartLine));

    /// <summary>
    /// Write a batch, halving it and then splitting a single chunk for as long as the model
    /// refuses what it is given. The recursion ends at text the model accepts, so no vector
    /// is ever stored for less text than its chunk claims. See D-31.
    ///
    /// Separated from the embedding and upsert it drives because the index arithmetic is
    /// the subtle part and the plumbing is not: a fake <paramref name="write"/> that refuses
    /// anything over a length exercises halving, recursive splitting, write-order numbering
    /// and the returned count without a catalogue, a vector store or a model.
    /// </summary>
    /// <param name="firstIndex">
    /// The chunk index the first chunk of this batch is written under. Numbering happens
    /// here rather than at the chunker, because a split adds a chunk and its halves have to
    /// sit between their neighbours: <c>ChunkIndex</c> is the ORDERING and neighbour key as
    /// well as part of the point identity. ContextService selects neighbours by
    /// <c>Math.Abs(c.ChunkIndex - hit.ChunkIndex)</c> and three other sites order by it, so
    /// a tail numbered above every other chunk in the file would be sorted to the end of
    /// its own document and fall outside its own neighbourhood.
    /// </param>
    /// <param name="write">
    /// Stores the batch under the consecutive indices already assigned to it, or throws
    /// <see cref="EmbeddingInputTooLongException"/> if the model will not read one of them.
    /// </param>
    /// <returns>Chunks written, which exceeds the batch size when a split occurred.</returns>
    internal static async Task<int> DivideAndWriteAsync(
        List<Chunk> batch, int firstIndex,
        Func<List<Chunk>, Task> write,
        Action<Chunk, Chunk>? onSplit = null)
    {
        if (batch.Count == 0) return 0;

        try
        {
            List<Chunk> numbered = [.. batch.Select((c, i) => c with { ChunkIndex = firstIndex + i })];
            await write(numbered);
            return batch.Count;
        }
        catch (EmbeddingInputTooLongException) when (batch.Count > 1)
        {
            // Which input was too long is not reported, and asking costs a call per chunk.
            // Halving finds it in log2 refusals, each of them cheap.
            var half = batch.Count / 2;
            var left = await DivideAndWriteAsync(
                batch.GetRange(0, half), firstIndex, write, onSplit);
            var right = await DivideAndWriteAsync(
                batch.GetRange(half, batch.Count - half), firstIndex + left, write, onSplit);
            return left + right;
        }
        catch (EmbeddingInputTooLongException) when (Split(batch[0]) is { } halves)
        {
            onSplit?.Invoke(batch[0], halves.Second);

            // Left first, then right at the index after however many the left half needed:
            // a half can itself be refused and split again, so the count is the offset.
            var left = await DivideAndWriteAsync([halves.First], firstIndex, write, onSplit);
            var right = await DivideAndWriteAsync([halves.Second], firstIndex + left, write, onSplit);
            return left + right;
        }
    }

    /// <summary>
    /// Divide a chunk near its middle, or null when it is a single character and there is
    /// nothing to divide.
    ///
    /// A line boundary is preferred, because a chunk is read as text and cited by line
    /// range, and a split mid-line gives both halves a range that is partly wrong. It is
    /// not always available: a minified file is one line, and a PDF page can extract as
    /// one. Falling back to a word and then to the midpoint keeps the file indexable, and
    /// both halves then carry the line range they are genuinely inside, which is the line
    /// they share.
    ///
    /// The heading trail in <see cref="Chunk.EmbedText"/> is re-applied to each half: it is
    /// a prefix on the content, so dividing the content alone would leave the second half
    /// embedded under text that no longer precedes it.
    /// </summary>
    internal static (Chunk First, Chunk Second)? Split(Chunk chunk)
    {
        var content = chunk.Content;
        if (content.Length < 2) return null;

        var mid = content.Length / 2;
        var ceiling = Math.Min(mid, content.Length - 1);

        var cut = content.LastIndexOf('\n', ceiling);
        var onNewline = cut > 0;
        if (!onNewline) cut = content.LastIndexOf(' ', ceiling);
        // Neither, so this is one unbroken run: divide it rather than fail the file.
        if (cut <= 0) cut = mid - 1;

        // A chunk's content holds its lines newline-SEPARATED, never newline-terminated:
        // measured over the chunker, no piece begins or ends with one, and Passage.Stitch
        // reconstructs a file by appending the terminator itself. So on a line cut the
        // separator belongs to neither half. Keeping it on the head made that chunk the
        // only one in the file carrying its own terminator, and Stitch turned it into a
        // blank line numbered the same as the tail's first real line. Any other cut is
        // inside a line, where the two halves simply abut.
        var head = onNewline ? content[..cut] : content[..(cut + 1)];
        var tail = content[(cut + 1)..];
        if (head.Length == 0 || tail.Length == 0) return null;

        // EmbedText is the heading trail followed by the content, so whatever precedes the
        // content is the prefix both halves need.
        var prefix = chunk.EmbedText.Length > content.Length
            ? chunk.EmbedText[..^content.Length]
            : string.Empty;

        // The head's last line, counted from the separators inside it.
        var headEnd = Math.Min(chunk.StartLine + head.Count(c => c == '\n'), chunk.EndLine);

        // On a line cut the tail opens the NEXT line, and the head must not claim it too.
        // Giving both the same line made their ranges overlap, and Passage.Stitch drops
        // the lines a chunk shares with the one before it, so the tail's first line
        // disappeared from every assembled passage. Any other cut leaves both halves
        // inside one line, which is the line they share.
        var tailStart = onNewline ? Math.Min(headEnd + 1, chunk.EndLine) : headEnd;

        return (
            chunk with
            {
                Content = head,
                EmbedText = prefix.Length > 0 ? prefix + head : string.Empty,
                EndLine = headEnd,
                Symbols = SymbolsIn(chunk.Symbols, head),
            },
            chunk with
            {
                Content = tail,
                EmbedText = prefix.Length > 0 ? prefix + tail : string.Empty,
                StartLine = tailStart,
                Symbols = SymbolsIn(chunk.Symbols, tail),
            });
    }

    /// <summary>
    /// The symbols of the whole chunk that survive into one half of it.
    ///
    /// `symbols` is an exact Qdrant filter, so carrying the parent's list into both halves
    /// makes a search for a symbol declared at the top of a chunk also return its bottom.
    /// Matching on the text is coarser than re-parsing the half, and errs the safe way: a
    /// name the half does not contain cannot be in it.
    /// </summary>
    private static IReadOnlyList<string> SymbolsIn(IReadOnlyList<string> symbols, string half) =>
        symbols.Count == 0
            ? symbols
            : [.. symbols.Where(s => half.Contains(s, StringComparison.Ordinal))];

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
    /// itself. Without it, improved text would be re-extracted and then skipped as
    /// "unchanged", which is the worst of both outcomes.
    /// </summary>
    /// <param name="templates">
    /// The task framing in force for this set's model. Part of the key because it changes
    /// the vectors: text embedded as `search_document: …` is not the same point as the
    /// same text embedded raw. Without it, editing a model profile would leave every
    /// existing chunk in place while every new query used the new framing, leaving the
    /// two sides of a retrieval disagreeing with no error raised.
    /// </param>
    /// <summary>
    /// The set's own settings, with no model measurement to reconcile them against. The
    /// indexing paths all pass the reconciled options; this is for callers that have only
    /// a set. If the two ever disagree the effect is a file that looks changed and is
    /// chunked again, never a stale chunk kept as current.
    /// </summary>
    internal static string ChunkingFingerprint(ChunkSet set, string blobSha, ModelTemplates templates) =>
        ChunkingFingerprint(set, blobSha, templates, set.Options());

    internal static string ChunkingFingerprint(
        ChunkSet set, string blobSha, ModelTemplates templates, ChunkOptions chunking) =>
        HashContent($"{blobSha}|{chunking.ChunkSizeTokens}|{chunking.OverlapTokens}|" +
                    $"{chunking.CharsPerToken}|{set.BoundaryMode}|" +
                    $"{set.CustomBoundaryPattern}|{set.UnitAware}|{set.SentenceAware}|{set.HeadingContext}|" +
                    $"{set.EmbeddingProvider}|{set.EmbeddingModel}|t{templates.Fingerprint}|" +
                    $"x{ExtractorVersions.Current}|c{CodeChunker.Version}");

    /// <summary>
    /// Get-or-create the per-set state for a batch of files, in one round trip. A file
    /// attached before a set existed has no row yet, and a set added to a corpus full of
    /// documents has none for any of them.
    /// </summary>
    private async Task<Dictionary<string, FileChunkState>> StatesFor(
        ChunkSet set, IReadOnlyList<IndexedFile> files, CancellationToken ct)
    {
        var ids = files.Select(f => f.Id).ToList();
        var existing = await db.FileChunkStates
            .Where(s => s.ChunkSetId == set.Id && ids.Contains(s.FileId))
            .ToDictionaryAsync(s => s.FileId, StringComparer.Ordinal, ct);

        foreach (var file in files)
        {
            if (existing.ContainsKey(file.Id)) continue;
            var state = new FileChunkState
            {
                FileId = file.Id,
                ChunkSetId = set.Id,
                Status = FileStatus.Pending,
            };
            db.FileChunkStates.Add(state);
            existing[file.Id] = state;
        }

        return existing;
    }

    private async Task IndexWorkspaceSourceAsync(Corpus corpus, ChunkSet set, ModelTemplates templates,
        ChunkOptions chunking,
        Source source, IndexJob job,
        IProgress<IndexProgress>? progress, bool full, Action onEmbeddingFailure, CancellationToken ct)
    {
        string root;
        try { root = ResolveWorkspacePath(source.RootPath); }
        catch (UnauthorizedAccessException ex)
        {
            // Refused, and handled like the missing mount below: nothing deleted, the other
            // sources still indexed. A path is resolved on every pass, so a source can be
            // refused without anyone editing it, and one created through a link before
            // D-35 now is. Uncaught, this failed the whole job at the first such source.
            Unreachable(corpus, job, ex.Message);
            return;
        }

        if (!Directory.Exists(root))
        {
            // Not destructive: the existing index stays searchable. A missing mount is
            // an operational condition, not a reason to delete someone's corpus.
            Unreachable(corpus, job, $"Workspace path '{source.RootPath}' is not available under {_indexing.WorkspaceRoot}.");
            return;
        }

        // The same walk the sweep uses, so the inventory it records and the files this
        // indexes are one answer rather than two that have to agree.
        var walk = WorkspaceDiscovery.Walk(corpus, source, root, _indexing);

        if (walk.ShadowedCount > 0)
            log.LogInformation(
                "Source {Source}: {Owned} of {Found} files; {Shadowed} belong to a more specific source",
                source.RootPath, walk.Owned.Count, walk.Owned.Count + walk.ShadowedCount, walk.ShadowedCount);

        await IndexUnitsAsync(corpus, set, templates, chunking, source, job, progress, full,
            walk.Owned, walk.Skipped, reader.ReadAsync, alwaysProse: false,
            fingerprintOf: null, onEmbeddingFailure, ct);
    }

    /// <summary>
    /// One pass over a repository's history, one document per commit.
    ///
    /// The enumeration is the inventory and is deliberately cheap: shas and dates, no
    /// messages and no patches. Which of them still need reading is then settled against the
    /// catalogue before git is asked for anything else, because a commit's text is
    /// decided by its sha and this source's settings and a commit cannot change. A
    /// refresh of a repository whose tip has not moved therefore costs one `git log`.
    /// </summary>
    private async Task IndexGitHistorySourceAsync(Corpus corpus, ChunkSet set, ModelTemplates templates,
        ChunkOptions chunking,
        Source source, IndexJob job,
        IProgress<IndexProgress>? progress, bool full, Action onEmbeddingFailure, CancellationToken ct)
    {
        // Resolved into the type git is run against, so the boundary is applied again by
        // the code that starts the process rather than trusted to have happened here.
        // Null is the mount being away, which is the same condition the old
        // Directory.Exists check reported and takes the same branch.
        GitRepository? repo;
        try { repo = GitHistory.RepositoryIn(_indexing.WorkspaceRoot, source.RootPath); }
        catch (UnauthorizedAccessException ex)
        {
            // As for a workspace source (IndexWorkspaceSourceAsync).
            Unreachable(corpus, job, ex.Message);
            return;
        }

        if (repo is null)
        {
            Unreachable(corpus, job, $"Workspace path '{source.RootPath}' is not available under {_indexing.WorkspaceRoot}.");
            return;
        }

        var options = GitHistoryOptions.FromJson(source.GitOptions);

        // The source's include globs become pathspecs, so they mean whose history rather
        // than which files to read, and they narrow the diff at the same time. Excludes
        // are not passed: git's exclude pathspec syntax is its own, and quietly mapping
        // one glob language onto another is how a filter comes to mean something else.
        var filters = SourceFilters.Resolve(corpus, source, _indexing);

        IReadOnlyList<GitCommit> commits;

        // The probe and the enumeration share one catch, because they fail the same way.
        // RunAsync raises GitHistoryException for a missing git binary and for a timeout,
        // and the probe runs git: outside this, an image built without git failed the
        // whole JOB, while the identical failure from EnumerateAsync a few lines later
        // reported the source unavailable and left the other sources to index. One
        // exception, two outcomes, decided by which call happened to run first.
        //
        // Unavailable is the right one of the two. Git being absent or timing out is the
        // machinery breaking, not a statement about this corpus, and what a job failure
        // would assert is that the corpus could not be indexed at all.
        try
        {
            if (!await GitHistory.IsRepositoryAsync(repo, ct))
            {
                // Unavailable rather than failed, and nothing is removed: a repository
                // whose mount is present but whose .git is not is the same class of
                // problem as a mount that is away, and the history indexed last time is
                // still searchable.
                Unreachable(corpus, job, $"Source '{source.RootPath}' is not a git repository, so it has no history to index.");
                return;
            }

            commits = await GitHistory.EnumerateAsync(repo, options, filters.IncludeGlobs, ct);
        }
        catch (GitHistoryException ex)
        {
            Unreachable(corpus, job, ex.Message);
            return;
        }

        log.LogInformation("Source {Source}: {Commits} commits on {Ref}",
            source.RootPath, commits.Count, options.Ref);

        // One commit per path, decided in the one place the sweep decides it too.
        //
        // Not widened to the full sha, because the arithmetic does not justify a 40
        // character path in every file list: twelve hex characters is 48 bits, so an
        // accidental collision inside one day needs on the order of 16.7 million commits
        // dated that day. Deliberate collisions are not a threat worth pricing either —
        // the only party who can add commits to the repository is the party whose commit
        // would go missing. What is worth the lines is that the loss is decided and
        // counted rather than falling out of an ordering, because at these odds nobody
        // would ever go looking.
        var distinct = GitHistory.OnePerPath(commits);

        if (distinct.Count != commits.Count)
            log.LogWarning(
                "{Dropped} of {Total} commits in {Source} share a date and a twelve-character "
                + "sha prefix with a newer one, and are not indexed",
                commits.Count - distinct.Count, commits.Count, source.RootPath);

        var byPath = distinct.ToDictionary(c => c.RelativePath, StringComparer.Ordinal);

        // Size is the size of the document, which is not known until it is read. Zero
        // here rather than a guess: a number nobody measured is worse than none.
        var units = distinct
            .Select(c => new WorkspaceWalker.Candidate(repo.FullPath, c.RelativePath, 0))
            .ToList();

        // The pathspecs go in: they are passed to git, so they decide which files the
        // stat lists and which hunks the patch holds, and the same commit under a
        // narrower filter is a different document. Left out, a corpus whose include
        // globs changed kept every old commit skipped, with a stat cut to paths nobody
        // had selected any more.
        var fingerprint = options.ContentFingerprint(filters.IncludeGlobs);

        // The same handling for a git failure DURING the pass as for one before it. The
        // catch above covered the inventory only, so a repository that went away between
        // enumerating and reading — an unmounted share, a timed-out call — reached the
        // job's generic catch and was recorded as a failed job rather than an
        // unavailable source. Same condition, and it should not depend on when it
        // happened.
        try
        {
            await IndexUnitsAsync(corpus, set, templates, chunking, source, job, progress, full,
                units, [],
                (toRead, token) => ReadCommitsAsync(repo, options, byPath, toRead, filters.IncludeGlobs, token),
                alwaysProse: true,
                fingerprintOf: candidate => HashContent(
                    byPath[candidate.RelativePath].Sha + '|' + fingerprint),
                onEmbeddingFailure, ct);
        }
        catch (GitHistoryException ex)
        {
            Unreachable(corpus, job, ex.Message);
        }
    }

    /// <summary>
    /// The commits the pass still wants, as reads the shared loop understands.
    ///
    /// The sha is what the text is hashed from, for the same reason it is what the
    /// staleness check used: it settles the content, and hashing the document instead
    /// would mean the check could not run before the read.
    /// </summary>
    private static async IAsyncEnumerable<ReadFile> ReadCommitsAsync(
        GitRepository repo, GitHistoryOptions options, Dictionary<string, GitCommit> byPath,
        IReadOnlyList<WorkspaceWalker.Candidate> toRead, IReadOnlyList<string>? pathspecs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (toRead.Count == 0) yield break;

        var wanted = new Dictionary<string, WorkspaceWalker.Candidate>(toRead.Count, StringComparer.Ordinal);
        foreach (var candidate in toRead) wanted[byPath[candidate.RelativePath].Sha] = candidate;

        var fingerprint = options.ContentFingerprint(pathspecs);

        await foreach (var (sha, text) in
                       GitHistory.ReadAsync(repo, options, [.. wanted.Keys], pathspecs, ct))
        {
            if (!wanted.TryGetValue(sha, out var candidate)) continue;

            yield return new ReadFile(
                // UTF-8 bytes, because SizeBytes is a byte count everywhere else: it
                // sorts the file list and is rendered as a size, and a commit carrying
                // non-ASCII text would otherwise report smaller than it is.
                candidate with { SizeBytes = Encoding.UTF8.GetByteCount(text) },
                new ReadText(HashContent(sha + '|' + fingerprint), new ExtractedText(text, []), null),
                null);
        }
    }

    /// <summary>
    /// One pass over the units of one source, into one chunk set.
    ///
    /// A unit is a file for a workspace source, an attachment for an upload and a commit
    /// for a git history. Everything from here down is about text with a path, a hash and
    /// a size, and none of it knows which of the three it came from. That is deliberate:
    /// the staleness short-circuit, the four empty branches that have to drop their
    /// vectors, the claim written before the delete, the counters and the reconcile are
    /// each a defect that has already been fixed once, and a second copy of this loop is
    /// where those fixes would stop applying to half the sources.
    /// </summary>
    /// <param name="units">What to index, already filtered to what this source owns.</param>
    /// <param name="skipped">Units the discovery excluded. They get rows and are counted.</param>
    /// <param name="read">Turns the units into text, in whatever way this kind of source does.</param>
    /// <param name="alwaysProse">
    /// Chunk on blank lines whatever the path looks like. A commit is prose with a diff
    /// in it, and a path ending `.cs` under a source whose units are commits would
    /// otherwise be cut by a C# member regex.
    /// </param>
    /// <param name="fingerprintOf">
    /// The hash of a unit's content, where it is knowable without reading the unit, or
    /// null where it is not. A file's is a hash of its extracted text and so needs the
    /// extraction; a commit's is its sha, because a commit cannot change.
    /// </param>
    private async Task IndexUnitsAsync(Corpus corpus, ChunkSet set, ModelTemplates templates,
        ChunkOptions chunking,
        Source source, IndexJob job,
        IProgress<IndexProgress>? progress, bool full,
        IReadOnlyList<WorkspaceWalker.Candidate> units,
        IReadOnlyList<WorkspaceWalker.Skipped> skipped,
        Func<IReadOnlyList<WorkspaceWalker.Candidate>, CancellationToken, IAsyncEnumerable<ReadFile>> read,
        bool alwaysProse,
        Func<WorkspaceWalker.Candidate, string>? fingerprintOf,
        Action onEmbeddingFailure, CancellationToken ct)
    {
        var files = units;

        // Every file this pass will record, which is what the counters below add up to:
        // the ones it owns AND the ones the walk excluded, because both get a row and
        // both increment FilesSkipped. Counting only the owned ones reported 27,031
        // skipped against a total of 27,011, and any progress reading
        // (done + skipped + failed) / total past 1.0.
        //
        // It is also the number the Files list shows for the corpus, so the two agree.
        //
        // += , not =. A job covers every chunk set, and each set walks the tree again, so
        // an assignment here reported the files of one pass against the work done by all
        // of them: a corpus with two sets showed "24 / 12" and a progress bar past 100%.
        //
        // Shadowing applies to the first term only. WorkspaceDiscovery filters `Owned` by
        // it and returns `Skipped` as the walk produced it, so a file an exclusion caught
        // under a nested source is reported by every source above it and counted by each.
        // That inflates the total on a corpus with nested sources, but it does not break
        // what this line is for: each of those counts is matched by a FilesSkipped in the
        // same pass, so the two still add up.
        job.FilesTotal += files.Count + skipped.Count;
        job.Phase = "extract";

        // The job's counters accumulate across every source and every set, so this pass's
        // own numbers are the difference either side of it. Logging the running totals
        // reported the whole job against each source in turn: `books/manuals` owns one file
        // and its line said "96 indexed".
        var startedWith = (job.FilesDone, job.FilesSkipped, job.FilesFailed);
        await db.SaveChangesAsync(ct);
        Report(progress, job, null);

        var known = await db.Files.Where(f => f.SourceId == source.Id)
            .ToDictionaryAsync(f => f.RelativePath, f => f, StringComparer.Ordinal, ct);

        var states = (await StatesFor(set, known.Values.ToList(), ct))
            .ToDictionary(kv => known.Values.First(f => f.Id == kv.Key).RelativePath, kv => kv.Value,
                StringComparer.Ordinal);

        // Before anything is written, while the two records are both at rest.
        await ReconcileChunkCountsAsync(set, source.Id, states, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();

        // Every path below that records a file as having no chunks has to drop the
        // vectors it had. The reconcile pass at the end of this method only removes
        // files the walk stopped seeing, and all of these are files the walk DID see:
        // they are in `seen`, so nothing else will ever collect them.
        //
        // Returns false when the delete failed, so no caller writes a settled outcome
        // over chunks that are still answering searches.
        async Task<bool> ClearChunksAsync(string relativePath)
        {
            try
            {
                await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, source.Id, relativePath, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not remove existing chunks for {File}", relativePath);
                return false;
            }
        }

        // Both empty outcomes write the same row. The success path's delete sits below
        // the `continue` that brings a file here, so it has to happen again on this
        // side of the branch.
        async Task MarkEmptyAsync(WorkspaceWalker.Candidate candidate, string fileSha, string hash,
            string reason, int extractedChars)
        {
            var cleared = await ClearChunksAsync(candidate.RelativePath);
            var (file, state) = Track(known, states, set, source.Id, candidate.RelativePath, candidate.SizeBytes);
            file.ExtractedChars = extractedChars;
            state.SourceSha256 = fileSha;
            state.IndexedUtc = DateTime.UtcNow;

            if (!cleared)
            {
                // Recording Empty would assert the file has no chunks while its old ones
                // are still searchable. Failed keeps the hash off the row, so the next
                // refresh reaches this branch again and retries the delete.
                state.Status = FileStatus.Failed;
                state.StatusDetail = $"{reason}; its previous chunks could not be removed";
                state.ContentHash = null;
                job.FilesFailed++;
                return;
            }

            state.Status = FileStatus.Empty;
            state.StatusDetail = reason;
            state.ContentHash = hash;
            state.ChunkCount = 0;
            job.FilesSkipped++;
        }

        foreach (var skip in skipped)
        {
            seen.Add(skip.RelativePath);
            var (_, state) = Track(known, states, set, source.Id, skip.RelativePath, skip.SizeBytes);
            state.Status = FileStatus.Skipped;
            state.ContentHash = null;

            // An exclusion that has only just started matching leaves a file that was
            // indexed until now, and its vectors keep answering searches without this.
            if (await ClearChunksAsync(skip.RelativePath))
            {
                state.StatusDetail = skip.Reason;
                state.ChunkCount = 0;
            }
            else
            {
                // ChunkCount is left as it was: it is the only remaining record that
                // those chunks exist. The next refresh skips this file again and retries.
                state.StatusDetail = $"{skip.Reason}; its previous chunks could not be removed";
            }

            job.FilesSkipped++;
        }

        // Units whose fingerprint is knowable without reading them are settled here,
        // before anything is asked for.
        //
        // A file cannot be: its fingerprint is a hash of its EXTRACTED text, so deciding
        // it is unchanged means extracting it first, which is why the check below sits
        // after the read. A commit can, because a commit is immutable and its text is
        // decided by its sha and the source's settings. Left to the in-loop check, a
        // refresh of a repository whose tip had not moved would ask git for every patch
        // in it to discover that nothing had changed.
        //
        // They still go into `seen`. The reconcile at the end removes what a walk stopped
        // seeing, and a unit skipped as unchanged is a unit the walk very much saw.
        var toRead = files;

        if (!full && fingerprintOf is not null)
        {
            var fresh = new List<WorkspaceWalker.Candidate>(files.Count);

            foreach (var candidate in files)
            {
                var hash = ChunkingFingerprint(set, fingerprintOf(candidate), templates, chunking);

                if (states.TryGetValue(candidate.RelativePath, out var existing)
                    && existing.ContentHash == hash && existing.Status == FileStatus.Indexed)
                {
                    seen.Add(candidate.RelativePath);
                    job.FilesSkipped++;
                    continue;
                }

                fresh.Add(candidate);
            }

            toRead = fresh;
        }

        // Read in parallel, recorded here one at a time. Everything below this line
        // touches state belonging to this pass alone - the DbContext, the two
        // dictionaries, the job's counters - and none of it is what makes indexing slow.
        await foreach (var unit in read(toRead, ct))
        {
            var candidate = unit.Candidate;
            ct.ThrowIfCancellationRequested();
            seen.Add(candidate.RelativePath);

            try
            {
                // Rethrown here rather than handled where it was caught, so the catch
                // blocks below stay the one place a file's failure becomes a row.
                if (unit.Error is not null) throw unit.Error;

                var (fileSha, extracted, extractor) = unit.Read!;
                var content = extracted.Text;

                // The stored hash is the CHUNKING FINGERPRINT, not the raw content hash.
                // With a content hash alone, changing a corpus's chunk size left every
                // file looking unchanged, so a refresh re-chunked nothing and the new
                // setting had no effect. Mixing the settings in marks precisely the
                // affected files as stale, and no others.
                //
                // Over `fingerprintOf` where a unit has one, because that is what the
                // pre-read check above compares against, and a value written here in
                // some other shape is one it can never match. It could not: the check
                // hashed the commit's sha and the source's settings while this hashed
                // the document text, so the skip never fired and a refresh over an
                // unmoved tip read and re-hashed every patch in the repository to
                // conclude nothing had changed. The counters could not show it, because
                // the in-loop check below then skipped every one of them.
                var hash = ChunkingFingerprint(
                    set, fingerprintOf?.Invoke(candidate) ?? HashContent(content), templates, chunking);

                if (!full && states.TryGetValue(candidate.RelativePath, out var existing)
                          && existing.ContentHash == hash && existing.Status == FileStatus.Indexed)
                {
                    // Written here as well as on the success path below, because this is
                    // where an already-indexed corpus leaves. A hash assigned only on
                    // success would never be recorded outside a full rebuild, and the
                    // document would stay unreachable on exactly the corpora that have
                    // been indexed longest.
                    existing.SourceSha256 = fileSha;
                    job.FilesSkipped++;
                    continue;   // unchanged — zero embedding calls, which is the point
                }

                if (content.Trim().Length == 0)
                {
                    // The same words the cache stores against these bytes, from the same
                    // place, so a file's reason does not depend on which of the two
                    // answered.
                    await MarkEmptyAsync(candidate, fileSha, hash,
                        ExtractedTextCache.EmptyReason(extractor), extractedChars: 0);
                    continue;
                }

                var language = LanguageMap.Detect(candidate.RelativePath);

                // A document is chunked as prose regardless of its extension: applying a
                // C# member-boundary regex to extracted PDF text finds nothing useful.
                var pieces = extractor is null && !alwaysProse
                    ? CodeChunker.Chunk(candidate.RelativePath, content, chunking, extracted)
                    : CodeChunker.Chunk(candidate.RelativePath, content,
                        chunking with { BoundaryMode = "blank-line" }, extracted);

                if (pieces.Count == 0)
                {
                    // Extraction found text here, so the char count is the text's, not
                    // zero: the file is empty of chunks, not empty of content.
                    await MarkEmptyAsync(candidate, fileSha, hash, "chunker produced no chunks",
                        extractedChars: content.Length);
                    continue;
                }

                // Claim the file BEFORE its vectors are touched, and make the claim
                // durable. The delete below cannot be undone, and until the success
                // assignment runs the row still describes the vectors that were just
                // removed: a pass that dies in between - a restart, a lost lease, a
                // cancel - left a row reading Indexed, with a hash and a chunk count,
                // over nothing at all. Because the hash still matched, every later
                // refresh short-circuited it, so the file was unsearchable and no
                // refresh would ever repair it. Measured on a 1,834-file corpus: three
                // files, 13,016 points, two of them holding none while reporting
                // thousands.
                //
                // Written with no hash, so the same interruption now leaves the file
                // looking stale and the next pass indexes it again.
                var (okFile, okState) = Track(known, states, set, source.Id, candidate.RelativePath, candidate.SizeBytes);
                okState.ContentHash = null;
                okState.Status = FileStatus.Pending;
                await db.SaveChangesAsync(ct);

                // Replace rather than merge: a changed file's old chunks are stale by
                // definition, and leaving them produces results pointing at lines that
                // no longer say what the result claims.
                await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, source.Id, candidate.RelativePath, ct);

                var chunks = pieces.Select(p => new Chunk
                {
                    CorpusId = corpus.Id,
                    ChunkSetId = set.Id,
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
                    EmbedText = p.EmbedText,
                }).ToList();

                // Embed and upsert in batches rather than in one go. A 500-page PDF is
                // ONE file producing thousands of chunks, so per-file progress leaves the
                // UI on "0 done" for minutes with no way to tell a slow job from a hung
                // one, observed on a 3 MB PDF at roughly 17 s per 32-chunk batch, with
                // the phase still reading "extract" because it was set but never reported
                // before the long call. Batching also caps peak memory at one batch of
                // vectors instead of all of them.
                var stored = await EmbedAndUpsertAsync(set, chunks, candidate.RelativePath, job, progress, sinceFlush, ct);

                okState.Status = FileStatus.Indexed;
                okState.StatusDetail = null;
                okState.ContentHash = hash;          // written ONLY here, on success
                okState.SourceSha256 = fileSha;
                okState.ChunkCount = stored;
                okState.IndexedUtc = DateTime.UtcNow;
                okFile.Language = language;
                okFile.MediaType = LanguageMap.MediaType(language);
                okFile.ExtractedChars = content.Length;

                job.FilesDone++;   // ChunksWritten is accumulated per batch above
            }
            catch (ExtractionTimeoutException ex)
            {
                // The file outran its budget and the read threw to get the thread back.
                // Recorded as failed with no content hash, so fixing the file or raising
                // the budget lets a later refresh retry it rather than skipping it
                // forever on a hash that matches.
                log.LogWarning("Extraction timed out for {File}: {Reason}",
                    candidate.RelativePath, ex.Message);
                var (_, timedOut) = Track(known, states, set, source.Id, candidate.RelativePath, candidate.SizeBytes);
                timedOut.Status = FileStatus.Failed;
                timedOut.StatusDetail = ex.Message;
                timedOut.ContentHash = null;
                job.FilesFailed++;
            }
            catch (ExtractionFailedException ex)
            {
                // A recognised format we could not read: encrypted, DRM'd, or corrupt.
                // Distinct from "produced no text", which is not a failure.
                log.LogWarning(ex, "Extraction failed for {File}", candidate.RelativePath);
                var (_, failedState) = Track(known, states, set, source.Id, candidate.RelativePath, candidate.SizeBytes);
                failedState.Status = FileStatus.Failed;
                failedState.StatusDetail = ex.Message;
                failedState.ContentHash = null;
                job.FilesFailed++;
            }
            catch (EmbeddingUnavailableException ex)
            {
                // Skip the FILE, flag the job, keep scanning. See the class remark.
                log.LogWarning(ex, "Embedding failed for {File}; skipping it and continuing", candidate.RelativePath);
                var (_, embedFailed) = Track(known, states, set, source.Id, candidate.RelativePath, candidate.SizeBytes);
                embedFailed.Status = FileStatus.Failed;
                embedFailed.StatusDetail = $"embedding failed: {ex.Message}";
                embedFailed.ContentHash = null;    // deliberately unrecorded, so it retries
                job.FilesFailed++;
                onEmbeddingFailure();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Failed to index {File}", candidate.RelativePath);
                var (_, otherFailed) = Track(known, states, set, source.Id, candidate.RelativePath, candidate.SizeBytes);
                otherFailed.Status = FileStatus.Failed;
                otherFailed.StatusDetail = ex.Message;
                otherFailed.ContentHash = null;
                job.FilesFailed++;
            }

            // Flush on EITHER a file count or a time budget. Count alone means a corpus
            // with fewer files than the batch size reports nothing at all until it
            // finishes, observed on a 12-file corpus sitting at done=0 for 24 seconds,
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
                // Vectors belong to this SET; the catalogue row belongs to the corpus. A
                // file deleted from disk has to leave every set's collection, and this
                // pass only owns one of them, so the row survives until the last set has
                // let go of it. Removing it here would strand the other sets' vectors
                // with nothing left to name them.
                await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, source.Id, path, ct);

                var file = known[path];
                if (states.TryGetValue(path, out var state)) db.FileChunkStates.Remove(state);

                var remaining = await db.FileChunkStates
                    .CountAsync(s => s.FileId == file.Id && s.ChunkSetId != set.Id, ct);
                if (remaining == 0) db.Files.Remove(file);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to remove index entries for deleted file {File}", path);
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Source {Source}: {Indexed} indexed, {Skipped} skipped, {Failed} failed, {Removed} removed",
            source.RootPath,
            job.FilesDone - startedWith.FilesDone,
            job.FilesSkipped - startedWith.FilesSkipped,
            job.FilesFailed - startedWith.FilesFailed,
            vanished.Count);
    }

    /// <summary>
    /// Resolve a source path inside the workspace root and refuse anything that escapes
    /// it. A relative path with <c>..</c>, or an absolute one, must not be able to reach
    /// the container filesystem.
    /// </summary>
    public string ResolveWorkspacePath(string? relative) =>
        WorkspaceDiscovery.Resolve(_indexing.WorkspaceRoot, relative);

    /// <summary>
    /// A source this pass could not reach. Nothing already indexed is removed; the corpus
    /// is Unavailable and the reason joins the job's others.
    /// </summary>
    private void Unreachable(Corpus corpus, IndexJob job, string reason)
    {
        corpus.State = CorpusState.Unavailable;
        AddReason(job, reason);
        log.LogWarning("{Reason}", reason);
    }

    /// <summary>
    /// Adds a reason to the job's error rather than replacing what is there. One string
    /// serves the whole job, and each reason used to overwrite the last: two unreachable
    /// sources reported only the second, and an embedding failure at the end of the pass
    /// hid an unreachable source altogether.
    ///
    /// Joined as sentences, since the Jobs view shows it as one paragraph and index_status
    /// as one line. Once each: every chunk set is its own pass over the sources, so a
    /// missing source is found once per set.
    /// </summary>
    private static void AddReason(IndexJob job, string reason)
    {
        var sentence = reason.Trim();
        if (!sentence.EndsWith('.')) sentence += ".";

        if (job.Error is not { Length: > 0 }) job.Error = sentence;
        else if (!job.Error.Contains(sentence, StringComparison.Ordinal)) job.Error += " " + sentence;
    }

    /// <summary>
    /// Whether this job still owns the corpus, which is what licenses it to write shared
    /// state. Null means the lease was never taken; a cancelled token means it was taken
    /// from us while we worked.
    /// </summary>
    private static bool Holds(CorpusLeases.Hold? hold) =>
        hold is not null && !hold.Lost.IsCancellationRequested;

    /// <summary>
    /// Take the corpus, or give up quickly and let the job be put back.
    ///
    /// Short on purpose. The indexing queue has one reader, so waiting here does not wait
    /// for this corpus alone: it stops every other corpus indexing for as long as the wait
    /// lasts. A sweep of a code repository was measured at 6m37s, which is a long time to
    /// stop unrelated work for.
    ///
    /// A few seconds, because the common case is a sweep that has just started or is about
    /// to finish, and returning immediately would bounce the job round the queue for no
    /// reason. Longer than that is the worker's problem, not this method's.
    /// </summary>
    private async Task<CorpusLeases.Hold?> TryTakeLeaseAsync(
        string corpusId, string holder, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (true)
        {
            var hold = await leases.TryAcquireAsync(corpusId, holder, ct);
            if (hold is not null) return hold;
            if (DateTime.UtcNow >= deadline) return null;

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// <summary>
    /// Whether a resolved path is the root or sits beneath it.
    ///
    /// A bare StartsWith is NOT this test, and the difference is a directory boundary: with
    /// a root of <c>/workspaces</c>, the string <c>/workspaces-secret</c> starts with it and
    /// is not inside it. `..` was caught, which is what made the gap easy to miss: the
    /// escape that got through never needed to traverse anywhere, only a sibling
    /// whose name shares the prefix.
    /// </summary>
    internal static bool IsInside(string candidate, string root)
    {
        var bounded = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;

        var trimmed = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The root itself is inside the root: browsing the mount with no relative path is
        // the workspace picker's opening call, not an escape.
        return string.Equals(trimmed, bounded.TrimEnd(Path.DirectorySeparatorChar), PathComparison)
            || candidate.StartsWith(bounded, PathComparison);
    }

    /// <summary>
    /// Case-insensitive only where the filesystem is. On Linux, which is every container
    /// this ships in, <c>/Workspaces</c> and <c>/workspaces</c> are different directories, and
    /// comparing them as equal is the containment check agreeing to something the kernel
    /// does not.
    /// </summary>
    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Clear the hash of any file whose recorded chunk count and the vector store's own
    /// disagree, so the staleness check below re-indexes it.
    ///
    /// Either direction counts. A deficit is the one that prompted this, but a surplus
    /// is the same disagreement and the same repair, and the pass cannot tell which of
    /// the two records is the wrong one.
    ///
    /// The catalogue and the vector store are two records of the same fact, written at
    /// different moments, and nothing else compares them. A pass that dies between
    /// deleting a file's vectors and writing its row leaves the row describing points
    /// that no longer exist; because the hash still matches, every later refresh
    /// short-circuits the file and it stays unsearchable for good. Found on a
    /// 1,834-file corpus: three files short by 13,016 points, two of them holding none
    /// while reporting thousands, and no refresh repaired them.
    ///
    /// Only ever clears a hash. It never deletes, never writes a count, and never
    /// touches a file the two records agree on, so the worst it can cost is re-embedding
    /// a file that did not need it.
    /// </summary>
    private async Task ReconcileChunkCountsAsync(ChunkSet set, string sourceId,
        Dictionary<string, FileChunkState> states, CancellationToken ct)
    {
        IReadOnlyDictionary<string, int>? actual;
        try
        {
            actual = await vectors.CountByFileAsync(set.CollectionName, set.Id, sourceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A read that failed is not a report of an empty index, and the action this
            // drives is re-embedding. Leaving a mismatch for the next pass costs far
            // less than putting a corpus back through the model because Qdrant blinked.
            log.LogWarning(ex,
                "Could not read per-file point counts for set {Set}; no count comparison this pass", set.Name);
            return;
        }

        // Null means the answer was incomplete. Absent and zero are the same shape here,
        // so an incomplete answer would mark everything past the cutoff for re-embedding.
        if (actual is null) return;

        var mismatched = 0;
        foreach (var (path, state) in states)
        {
            if (state.Status != FileStatus.Indexed) continue;

            var held = actual.GetValueOrDefault(path);
            if (held == state.ChunkCount) continue;

            // Neutral about the direction. A surplus is as much a disagreement as a
            // deficit and is re-indexed the same way, so wording that assumes a shortfall
            // would misdescribe half the cases to whoever is reading the log.
            log.LogWarning(
                "{Set}: {File} records {Recorded:N0} chunks, the index holds {Held:N0}; re-indexing it",
                set.Name, path, state.ChunkCount, held);
            state.ContentHash = null;
            mismatched++;
        }

        if (mismatched > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogWarning(
                "Set {Set}: {Count} file(s) whose recorded chunk count and the index disagree; "
                + "they will be re-indexed",
                set.Name, mismatched);
        }
    }

    /// <summary>
    /// Get-or-create both halves of a file's record: the attachment, which is shared by
    /// every chunk set, and this set's view of it. Returning the pair rather than taking a
    /// mutator keeps each call site explicit about which half it is writing to. The split
    /// between "what the file is" and "what this set made of it" is easy to get wrong.
    /// </summary>
    /// <param name="sizeBytes">
    /// What the walk measured, recorded HERE rather than on the paths that succeed.
    ///
    /// It used to be written on the success and empty branches only, so a file that
    /// failed to read kept the default and the Files list showed it as 0 bytes. On a
    /// live index the eight failures in one corpus were among its largest files and
    /// every one of them displayed as empty, while the failure's own detail quoted the
    /// real size. The walk already knew it before the read was attempted, and this is
    /// the one place every path goes through, so no future branch can forget it.
    ///
    /// Zero is left alone rather than written, because zero here means "not measured":
    /// a walk that could not stat a file reports no size, and the walk never produces a
    /// zero-length candidate, since an empty file is skipped as empty.
    /// </param>
    private (IndexedFile File, FileChunkState State) Track(
        Dictionary<string, IndexedFile> known, Dictionary<string, FileChunkState> states,
        ChunkSet set, string sourceId, string relativePath, long sizeBytes)
    {
        if (!known.TryGetValue(relativePath, out var file))
        {
            file = new IndexedFile
            {
                Id = Ulid.NewUlid().ToString(),
                SourceId = sourceId,
                RelativePath = relativePath,
            };
            known[relativePath] = file;
            db.Files.Add(file);
        }

        if (sizeBytes > 0) file.SizeBytes = sizeBytes;

        if (!states.TryGetValue(relativePath, out var state))
        {
            state = new FileChunkState
            {
                FileId = file.Id,
                ChunkSetId = set.Id,
                Status = FileStatus.Pending,   // discovered, not yet chunked
            };
            states[relativePath] = state;
            db.FileChunkStates.Add(state);
        }

        return (file, state);
    }

    private static void Report(IProgress<IndexProgress>? progress, IndexJob job, string? currentFile) =>
        progress?.Report(new IndexProgress(job.Id, job.CorpusId, job.Phase ?? job.State.ToString(),
            job.FilesTotal, job.FilesDone, job.FilesSkipped, job.FilesFailed, job.ChunksWritten,
            currentFile, job.Error));

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
}
