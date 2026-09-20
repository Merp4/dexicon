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

        // Held for the whole job, so a sweep on the discovery lane is turned away at the
        // door rather than walking the rows this is writing. Renewed in the background, so
        // a job that runs for hours keeps it without anything predicting how long it will
        // take; a job that dies stops renewing and the corpus falls free.
        //
        // Not fatal when it cannot be taken: the queue already refuses a second job per
        // corpus, so this is a sweep in progress, and a sweep is short. Waiting for it is
        // better than failing a job the user asked for.
        await using var hold = await WaitForLeaseAsync(corpus.Id, $"index-{job.Id}", corpus.Name, ct);

        job.State = JobState.Running;
        job.StartedUtc = DateTime.UtcNow;
        job.Phase = "discover";
        corpus.State = CorpusState.Indexing;
        foreach (var s in targets) s.State = CorpusState.Indexing;
        await db.SaveChangesAsync(ct);
        Report(progress, job, null);

        var embeddingFailed = false;

        try
        {
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

                foreach (var source in corpus.Sources)
                {
                    var full = job.Kind is JobKind.Full or JobKind.Rebuild;

                    if (source.Kind == SourceKind.Workspace)
                    {
                        await IndexWorkspaceSourceAsync(corpus, set, templates, chunking, source, job, progress, full,
                            onEmbeddingFailure: () => embeddingFailed = true, ct);
                    }
                    else
                    {
                        await IndexUploadSourceAsync(corpus, set, templates, chunking, source, job, progress, full,
                            onEmbeddingFailure: () => embeddingFailed = true, ct);
                    }
                }

                set.State = embeddingFailed ? CorpusState.Degraded : CorpusState.Ready;
                if (!embeddingFailed) set.LastIndexedUtc = DateTime.UtcNow;
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
            foreach (var s in targets) s.State = CorpusState.Ready;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Indexing job {JobId} for corpus {Corpus} failed", jobId, corpus.Name);
            job.State = JobState.Failed;
            job.Error = ex.Message;
            corpus.State = CorpusState.Degraded;
            foreach (var s in targets) s.State = CorpusState.Degraded;
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
    private async Task IndexUploadSourceAsync(Corpus corpus, ChunkSet set, ModelTemplates templates,
        ChunkOptions chunking, Source source, IndexJob job,
        IProgress<IndexProgress>? progress, bool full, Action onEmbeddingFailure, CancellationToken ct)
    {
        var attachments = await db.Files
            .Where(f => f.SourceId == source.Id && f.BlobSha256 != null)
            .ToListAsync(ct);

        var states = await StatesFor(set, attachments, ct);

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

        // The same walk the sweep uses, so the inventory it records and the files this
        // indexes are one answer rather than two that have to agree.
        var walk = WorkspaceDiscovery.Walk(corpus, source, root, _indexing);
        var files = walk.Owned;

        if (walk.ShadowedCount > 0)
            log.LogInformation(
                "Source {Source}: {Owned} of {Found} files; {Shadowed} belong to a more specific source",
                source.RootPath, files.Count, files.Count + walk.ShadowedCount, walk.ShadowedCount);

        // += , not =. A job covers every chunk set, and each set walks the tree again, so
        // an assignment here reported the files of one pass against the work done by all
        // of them: a corpus with two sets showed "24 / 12" and a progress bar past 100%.
        job.FilesTotal += files.Count;
        job.Phase = "extract";

        // The job's counters accumulate across every source and every set, so this pass's
        // own numbers are the difference either side of it. Logging the running totals
        // reported the whole job against each source in turn: `books/orly` owns one file
        // and its line said "96 indexed".
        var startedWith = (job.FilesDone, job.FilesSkipped, job.FilesFailed);
        await db.SaveChangesAsync(ct);
        Report(progress, job, null);

        var known = await db.Files.Where(f => f.SourceId == source.Id)
            .ToDictionaryAsync(f => f.RelativePath, f => f, StringComparer.Ordinal, ct);

        var states = (await StatesFor(set, known.Values.ToList(), ct))
            .ToDictionary(kv => known.Values.First(f => f.Id == kv.Key).RelativePath, kv => kv.Value,
                StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();

        foreach (var skip in walk.Skipped)
        {
            seen.Add(skip.RelativePath);
            var (_, state) = Track(known, states, set, source.Id, skip.RelativePath);
            state.Status = FileStatus.Skipped;
            state.StatusDetail = skip.Reason;
            state.ContentHash = null;
            state.ChunkCount = 0;
            job.FilesSkipped++;
        }

        foreach (var candidate in files)
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

                    // Every read the extractor makes passes through the deadline, which is
                    // the only way to interrupt one: Extract is synchronous and the
                    // libraries under it take no cancellation token.
                    extracted = _indexing.ExtractionTimeoutSeconds > 0
                        ? extractor.Extract(
                            new DeadlineStream(stream,
                                TimeSpan.FromSeconds(_indexing.ExtractionTimeoutSeconds),
                                candidate.RelativePath),
                            candidate.RelativePath)
                        : extractor.Extract(stream, candidate.RelativePath);
                }

                var content = extracted.Text;

                // The stored hash is the CHUNKING FINGERPRINT, not the raw content hash.
                // With a content hash alone, changing a corpus's chunk size left every
                // file looking unchanged, so a refresh re-chunked nothing and the new
                // setting had no effect. Mixing the settings in marks precisely the
                // affected files as stale, and no others.
                var hash = ChunkingFingerprint(set, HashContent(content), templates, chunking);

                if (!full && states.TryGetValue(candidate.RelativePath, out var existing)
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
                        ? "no text layer: this is a scanned PDF, and OCR is not supported"
                        : "no extractable text content";

                    var (emptyFile, emptyState) = Track(known, states, set, source.Id, candidate.RelativePath);
                    emptyState.Status = FileStatus.Empty;
                    emptyState.StatusDetail = reason;
                    emptyState.ContentHash = hash;
                    emptyState.ChunkCount = 0;
                    emptyState.IndexedUtc = DateTime.UtcNow;
                    emptyFile.SizeBytes = candidate.SizeBytes;
                    emptyFile.ExtractedChars = 0;
                    job.FilesSkipped++;
                    continue;
                }

                var language = LanguageMap.Detect(candidate.RelativePath);

                // A document is chunked as prose regardless of its extension: applying a
                // C# member-boundary regex to extracted PDF text finds nothing useful.
                var pieces = extractor is null
                    ? CodeChunker.Chunk(candidate.RelativePath, content, chunking, extracted)
                    : CodeChunker.Chunk(candidate.RelativePath, content,
                        chunking with { BoundaryMode = "blank-line" }, extracted);

                if (pieces.Count == 0)
                {
                    var (_, noneState) = Track(known, states, set, source.Id, candidate.RelativePath);
                    noneState.Status = FileStatus.Empty;
                    noneState.StatusDetail = "chunker produced no chunks";
                    noneState.ContentHash = hash;
                    noneState.ChunkCount = 0;
                    noneState.IndexedUtc = DateTime.UtcNow;
                    job.FilesSkipped++;
                    continue;
                }

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

                var (okFile, okState) = Track(known, states, set, source.Id, candidate.RelativePath);
                okState.Status = FileStatus.Indexed;
                okState.StatusDetail = null;
                okState.ContentHash = hash;          // written ONLY here, on success
                okState.ChunkCount = stored;
                okState.IndexedUtc = DateTime.UtcNow;
                okFile.SizeBytes = candidate.SizeBytes;
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
                var (_, timedOut) = Track(known, states, set, source.Id, candidate.RelativePath);
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
                var (_, failedState) = Track(known, states, set, source.Id, candidate.RelativePath);
                failedState.Status = FileStatus.Failed;
                failedState.StatusDetail = ex.Message;
                failedState.ContentHash = null;
                job.FilesFailed++;
            }
            catch (EmbeddingUnavailableException ex)
            {
                // Skip the FILE, flag the job, keep scanning. See the class remark.
                log.LogWarning(ex, "Embedding failed for {File}; skipping it and continuing", candidate.RelativePath);
                var (_, embedFailed) = Track(known, states, set, source.Id, candidate.RelativePath);
                embedFailed.Status = FileStatus.Failed;
                embedFailed.StatusDetail = $"embedding failed: {ex.Message}";
                embedFailed.ContentHash = null;    // deliberately unrecorded, so it retries
                job.FilesFailed++;
                onEmbeddingFailure();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Failed to index {File}", candidate.RelativePath);
                var (_, otherFailed) = Track(known, states, set, source.Id, candidate.RelativePath);
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
    /// Take the corpus, giving a sweep already holding it a chance to finish first.
    ///
    /// A sweep is a walk and some rows, seconds on the library this was written against,
    /// so the job waits rather than failing immediately. It waits twice
    /// <see cref="CorpusLeases.Lease"/>, which is past the point where a holder that has
    /// stopped renewing would have lapsed, so anything still there is alive and working.
    ///
    /// Then it throws, and the job is recorded as failed with that reason. It does NOT
    /// proceed without the lease: indexing beside a sweep is the overlap the lease exists
    /// to prevent, and carrying on regardless would make it a suggestion. A job that could
    /// not take the corpus has not been decided against, it has been blocked, and the
    /// scheduled refresh will bring it back.
    /// </summary>
    private async Task<CorpusLeases.Hold> WaitForLeaseAsync(
        string corpusId, string holder, string corpusName, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        var limit = CorpusLeases.Lease * 2;
        var step = TimeSpan.FromSeconds(1);

        while (true)
        {
            var hold = await leases.TryAcquireAsync(corpusId, holder, ct);
            if (hold is not null)
            {
                if (waited > TimeSpan.Zero)
                    log.LogInformation("Waited {Seconds:N0}s for corpus {Corpus}",
                        waited.TotalSeconds, corpusName);
                return hold;
            }

            if (waited >= limit)
                throw new InvalidOperationException(
                    $"Corpus '{corpusName}' is still held by another pass after "
                    + $"{limit.TotalSeconds:N0}s, so this job did not run. It will be retried.");

            await Task.Delay(step, ct);
            waited += step;
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
    /// Get-or-create both halves of a file's record: the attachment, which is shared by
    /// every chunk set, and this set's view of it. Returning the pair rather than taking a
    /// mutator keeps each call site explicit about which half it is writing to. The split
    /// between "what the file is" and "what this set made of it" is easy to get wrong.
    /// </summary>
    private (IndexedFile File, FileChunkState State) Track(
        Dictionary<string, IndexedFile> known, Dictionary<string, FileChunkState> states,
        ChunkSet set, string sourceId, string relativePath)
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

    /// <summary>BOM, then UTF-8, then Latin-1. Never throws on a file with unusual bytes.</summary>
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
