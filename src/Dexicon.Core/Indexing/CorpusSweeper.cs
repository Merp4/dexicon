using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Indexing;

/// <summary>Why a sweep walked nothing, when it walked nothing.</summary>
public enum SweepOutcome
{
    /// <summary>The corpus was walked.</summary>
    Swept,

    /// <summary>
    /// The lease was held by someone else, so nothing was walked and the work is still
    /// outstanding. Under the scheduler that holder is another process, since a corpus
    /// busy in this one is never dispatched, so the only way to find out was to be
    /// refused and the only thing to do is try again.
    /// </summary>
    Held,

    /// <summary>
    /// The corpus is gone. Terminal, and the distinction is the whole reason this is an
    /// outcome rather than one <c>Skipped</c> flag: retrying a corpus that has been
    /// deleted is a loop with no end, and dropping a sweep that was only held loses it.
    /// </summary>
    NoSuchCorpus,
}

/// <param name="Swept">Files the sources own, whether or not they were already known.</param>
/// <param name="Added">Rows written that did not exist before.</param>
public sealed record SweepResult(int Swept, int Added, SweepOutcome Outcome, string? Reason = null)
{
    /// <summary>Nothing was walked, for either reason.</summary>
    public bool Skipped => Outcome != SweepOutcome.Swept;
}

/// <summary>
/// Walks a corpus and records what is in it, without extracting, chunking or embedding.
///
/// The half that answers "what is in here", separated from the half that costs real time.
/// Measured on the library this was written for: statting all 1,804 files through the
/// container's bind mount is about two seconds, against 773ms to extract a single ordinary
/// PDF from it. Queuing the first behind the second is what made a newly added corpus read
/// as empty for as long as another corpus took to index. See D-32.
///
/// It only ever ADDS. Removing a file that has vanished means removing its vectors from
/// every set's collection, and the shared <see cref="IndexedFile"/> row can only go once
/// the last set has let go; a sweep touches no collections, so a sweep that deleted rows
/// would strand the vectors those rows named. There is deliberately no exemption for rows
/// that merely look empty either: <see cref="FileStatus.Pending"/> is not evidence that a
/// file has no vectors, because the upsert runs before the status is written and the save
/// is throttled, so a crash between them leaves a durable Pending beside vectors that
/// exist. Indexing remains the only pass that removes anything.
/// </summary>
public sealed class CorpusSweeper(
    CatalogDbContext db,
    CorpusLeases leases,
    IOptions<DexiconOptions> options,
    ILogger<CorpusSweeper> log)
{
    /// <summary>
    /// Rows per save. Small enough that no single write holds the catalogue's one writer
    /// long enough to matter to an index job running beside it, which is the sweep's half
    /// of the bargain the busy timeout makes.
    /// </summary>
    private const int BatchSize = 200;

    private readonly IndexingOptions _indexing = options.Value.Indexing;

    public async Task<SweepResult> SweepAsync(string corpusId, CancellationToken ct)
    {
        var corpus = await db.Corpora.Include(c => c.Sources)
            .FirstOrDefaultAsync(c => c.Id == corpusId, ct);
        if (corpus is null) return new SweepResult(0, 0, SweepOutcome.NoSuchCorpus, "no such corpus");

        // Taken, not checked. The corpus state is set inside the indexer once a job is
        // already running, so reading it leaves a gap for a job to start in.
        await using var hold = await leases.TryAcquireAsync(corpusId, $"sweep-{Ulid.NewUlid()}", ct);
        if (hold is null)
        {
            // A failed claim is one conditional update that matched no rows, and no rows
            // means either "someone holds it" or "it is not there any more". The two
            // arrive identically and need opposite answers — try again, and stop — so
            // the ambiguous one is resolved by asking, on the failure path only.
            //
            // Without it, a corpus deleted between the lookup above and the claim reads
            // as held, and the caller waits out a timer for work that can never run.
            if (!await db.Corpora.AnyAsync(c => c.Id == corpusId, ct))
                return new SweepResult(0, 0, SweepOutcome.NoSuchCorpus, "the corpus was deleted");

            log.LogInformation("Corpus {Corpus} is held by another pass; not sweeping", corpus.Name);
            return new SweepResult(0, 0, SweepOutcome.Held, "the corpus is being indexed");
        }

        // Losing the lease ends the sweep for the same reason it ends a job: whoever took
        // the corpus is writing the rows this would be adding to.
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct, hold.Lost);
        ct = leaseLost.Token;

        var swept = 0;
        var added = 0;

        // Uploads are the exception and not an oversight: an attachment has no walk to
        // do, because the act of attaching it is what records it.
        foreach (var source in corpus.Sources.Where(s => s.Kind != SourceKind.Upload))
        {
            ct.ThrowIfCancellationRequested();

            var root = WorkspaceDiscovery.Resolve(_indexing.WorkspaceRoot, source.RootPath);
            if (!Directory.Exists(root))
            {
                // Not an error and not destructive: the inventory a previous sweep wrote
                // stays, because a mount being away is an operational condition rather
                // than a statement that the files are gone.
                log.LogWarning("Source {Source} is not available; leaving its inventory alone",
                    source.RootPath);
                continue;
            }

            var owned = source.Kind == SourceKind.GitHistory
                ? await CommitsAsync(corpus, source, root, ct)
                : WorkspaceDiscovery.Walk(corpus, source, root, _indexing).Owned;

            swept += owned.Count;
            added += await RecordAsync(corpus, source, owned, ct);
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Swept {Corpus}: {Swept} files, {Added} new", corpus.Name, swept, added);
        return new SweepResult(swept, added, SweepOutcome.Swept);
    }

    /// <summary>
    /// A git-history source's inventory: one candidate per commit the settings select.
    ///
    /// This is the discovery half for that kind of source, and it is cheap in exactly
    /// the way D-32 argues discovery should be: `git log` for shas, dates and subjects,
    /// no bodies and no patches. Measured on this repository, 77ms for 201 commits.
    ///
    /// Without it, adding a history source enqueued a sweep that walked nothing, so the
    /// corpus reported zero files and zero pending until an index job reached the front
    /// of the queue — the case D-32 exists to prevent, reintroduced for a new kind of
    /// source.
    ///
    /// Size is zero because a commit's document is not known until it is read, and a
    /// number nobody measured is worse than none.
    /// </summary>
    private async Task<IReadOnlyList<WorkspaceWalker.Candidate>> CommitsAsync(
        Corpus corpus, Source source, string root, CancellationToken ct)
    {
        var repo = GitHistory.RepositoryIn(_indexing.WorkspaceRoot, source.RootPath);
        if (repo is null || !await GitHistory.IsRepositoryAsync(repo, ct))
        {
            log.LogWarning("Source {Source} is not a git repository; leaving its inventory alone",
                source.RootPath);
            return [];
        }

        var options = GitHistoryOptions.FromJson(source.GitOptions);
        var filters = SourceFilters.Resolve(corpus, source, _indexing);

        try
        {
            var commits = await GitHistory.EnumerateAsync(repo, options, filters.IncludeGlobs, ct);
            return [.. commits.Select(c => new WorkspaceWalker.Candidate(root, c.RelativePath, 0))];
        }
        catch (GitHistoryException ex)
        {
            // Same shape as a mount that is away: the inventory a previous sweep wrote
            // stays, and nothing is removed. A sweep only ever adds.
            log.LogWarning("Source {Source}: {Error}", source.RootPath, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Writes the rows a file needs to be visible: one <see cref="IndexedFile"/> for the
    /// file, and one <see cref="FileChunkState"/> per chunk set, because the status is a
    /// property of a file AS CUT BY a set. A corpus carrying two sets therefore gets two
    /// rows per file, which is also what indexing would have written.
    /// </summary>
    private async Task<int> RecordAsync(
        Corpus corpus, Source source, IReadOnlyList<WorkspaceWalker.Candidate> owned,
        CancellationToken ct)
    {
        var known = await db.Files.Where(f => f.SourceId == source.Id)
            .ToDictionaryAsync(f => f.RelativePath, f => f, StringComparer.Ordinal, ct);

        var sets = corpus.ChunkSets.Count > 0
            ? corpus.ChunkSets
            : await db.ChunkSets.Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);

        var fileIds = known.Values.Select(f => f.Id).ToList();
        var states = (await db.FileChunkStates
                .Where(s => fileIds.Contains(s.FileId))
                .ToListAsync(ct))
            .ToHashSet(FileSetComparer.Instance);

        var added = 0;
        var sinceSave = 0;

        foreach (var candidate in owned)
        {
            ct.ThrowIfCancellationRequested();

            if (!known.TryGetValue(candidate.RelativePath, out var file))
            {
                file = new IndexedFile
                {
                    Id = Ulid.NewUlid().ToString(),
                    SourceId = source.Id,
                    RelativePath = candidate.RelativePath,
                    SizeBytes = candidate.SizeBytes,
                };
                known[candidate.RelativePath] = file;
                db.Files.Add(file);
                added++;
                sinceSave++;
            }

            foreach (var set in sets)
            {
                var probe = new FileChunkState { FileId = file.Id, ChunkSetId = set.Id };
                if (states.Contains(probe)) continue;

                // Pending: discovered, not yet chunked. Never overwrites an existing
                // status, so a file this sweep rediscovers keeps whatever indexing made
                // of it.
                probe.Status = FileStatus.Pending;
                db.FileChunkStates.Add(probe);
                states.Add(probe);
                sinceSave++;
            }

            if (sinceSave < BatchSize) continue;
            await db.SaveChangesAsync(ct);
            sinceSave = 0;
        }

        return added;
    }

    /// <summary>Identity of a per-set state row, which is its composite key.</summary>
    private sealed class FileSetComparer : IEqualityComparer<FileChunkState>
    {
        public static readonly FileSetComparer Instance = new();

        public bool Equals(FileChunkState? a, FileChunkState? b) =>
            a is not null && b is not null
            && string.Equals(a.FileId, b.FileId, StringComparison.Ordinal)
            && string.Equals(a.ChunkSetId, b.ChunkSetId, StringComparison.Ordinal);

        public int GetHashCode(FileChunkState s) => HashCode.Combine(s.FileId, s.ChunkSetId);
    }
}
