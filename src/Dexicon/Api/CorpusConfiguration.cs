using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

/// <summary>A change to a corpus or a source, refused, in the words every caller is shown.</summary>
/// <param name="Status">The HTTP status the API answers with.</param>
public sealed record ConfigRefusal(string Title, string Detail, int Status)
{
    public IResult ToResult() => Results.Problem(title: Title, detail: Detail, statusCode: Status);
}

/// <summary>What a saved corpus change altered, passed to the caller's callback.</summary>
public readonly record struct CorpusChange(bool Description, bool Filters);

/// <summary>What a change produced, or why it was refused. Exactly one is set.</summary>
public sealed record ConfigOutcome<T>(T? Value, ConfigRefusal? Refusal)
{
    public static implicit operator ConfigOutcome<T>(T value) => new(value, null);
    public static implicit operator ConfigOutcome<T>(ConfigRefusal refusal) => new(default, refusal);
}

/// <summary>
/// Creating and changing corpora and sources, for every caller allowed to.
///
/// The rules a change is judged by lived in the API's handlers, where the UI was their only
/// caller. Agents are about to make the same changes over MCP, and two copies of the rules
/// would drift until one door accepted what the other refused. So they live here, and each
/// caller decides only whether its principal may ask and which corpus it means.
/// </summary>
public sealed class CorpusConfiguration(
    CatalogDbContext db,
    IVectorStore vectors,
    IEmbeddingService embedder,
    IOptions<DexiconOptions> opts,
    IndexJobQueue queue,
    SweepQueue sweeps)
{
    private IndexingOptions Indexing => opts.Value.Indexing;

    /// <summary>The length the model declares for a corpus name.</summary>
    internal const int NameMax = 200;

    /// <summary>
    /// Held from the last name check to the insert, which makes the two one step. The unique
    /// index compares names exactly, so "Notes" and "notes" created at the same moment would
    /// both pass it. Dexicon is one process owning its catalogue (D-01), and this is blind to
    /// a second process writing the same file.
    ///
    /// Also held while a key's mapping is replaced. A creation reads whether its key has a
    /// mapping and adds the new corpus to it, and the admin could clear that mapping, which
    /// means every corpus, between the two: the key would be left reaching only the corpus it
    /// just made.
    /// </summary>
    internal static readonly SemaphoreSlim Naming = new(1, 1);

    /// <summary>
    /// A conflict when a corpus already holds this name, compared as
    /// <see cref="Dexicon.Core.Auth.ScopeResolver"/> resolves one: ordinal, ignoring case. The column
    /// has no collation, so its unique index compares exactly, and SQLite's NOCASE folds
    /// ASCII only. Neither refuses "å" beside "Å", and one name would then resolve to either
    /// corpus. The comparison is therefore made here, over the names read whole, since there
    /// are tens of them.
    ///
    /// An id is an address too: the resolver matches a name or an id, the id exactly, so a
    /// corpus named with another's id would resolve to either of the two.
    /// </summary>
    private async Task<ConfigRefusal?> TakenAsync(string name, CancellationToken ct)
    {
        var corpora = await db.Corpora.Select(c => new { c.Id, c.Name }).ToListAsync(ct);

        if (corpora.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } named)
            return new ConfigRefusal(
                "Corpus already exists",
                $"A corpus named '{named.Name}' already exists. Names are unique, ignoring case, " +
                "because the name is what an agent passes to search_index.",
                409);

        if (corpora.Find(c => string.Equals(c.Id, name, StringComparison.Ordinal)) is { } identified)
            return new ConfigRefusal(
                "Name is another corpus's id",
                $"'{name}' is the id of corpus '{identified.Name}', and a corpus is addressed by its " +
                "name or its id, so the name would reach either of them.",
                409);

        return null;
    }

    /// <param name="grantToKeyId">
    /// A key that is to reach the corpus it is creating. When it is mapped to corpora, the new
    /// one joins its mapping in the same save as the corpus, so a failure between the two
    /// cannot leave a corpus its creator cannot reach, and one it cannot create again by name.
    /// A key with no mapping already reaches every corpus.
    /// </param>
    /// <param name="committed">
    /// Called once the corpus is saved and before the work that can still fail after it, so a
    /// caller can record the creation where it became true. Without it a failure in the
    /// collection setup below is an exception with the corpus already there and nothing said.
    /// </param>
    /// <param name="defaults">
    /// The filters its sources are to inherit, saved with the corpus. A second call after
    /// creation had a window of its own, in which the corpus existed without them and a retry
    /// of the creation was refused as taken.
    /// </param>
    public async Task<ConfigOutcome<Corpus>> CreateCorpusAsync(
        CreateCorpusRequest body, CancellationToken ct, string? grantToKeyId = null, Action<Corpus>? committed = null,
        CorpusDefaults? defaults = null)
    {
        // Judged before anything is built, as UpdateCorpusAsync judges its own.
        if (defaults is { MaxFileBytes: <= 0 })
            return new ConfigRefusal("Invalid size cap",
                "maxFileBytes must be greater than zero, or null to follow the server's setting.", 400);

        if (string.IsNullOrWhiteSpace(body.Name))
            return new ConfigRefusal("Name is required", "A corpus needs a name.", 400);

        // Compared as it will be stored, trimmed. Compared as sent, " notes " passed the check
        // and failed the unique index, which is a 500 where a 409 was promised. Checked here
        // so that a taken name does not wait on the model probe, and again at the insert.
        var name = body.Name.Trim();

        // The model declares 200 and SQLite stores the column as unbounded TEXT, so nothing else
        // enforces it. A name is repeated in every listing and log line, and a description,
        // which an agent can also set, is capped at 500 for the same reason.
        if (name.Length > NameMax)
            return new ConfigRefusal(
                "A corpus name is too long",
                $"A corpus name is at most {NameMax} characters; this one is {name.Length}.",
                400);

        // Shown on a line of its own in every listing, logged, and typed as an argument, so
        // a line break or an escape sequence in it breaks all three. U+2028 and U+2029 break
        // a line too, and char.IsControl does not count them.
        if (name.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            return new ConfigRefusal(
                "A corpus name cannot contain a control character",
                "A line break, tab or other control character in a name breaks every listing and log line "
                + "that shows it.",
                400);

        // ScopeResolver.Split reads everything after the first colon as a chunk set, so a
        // corpus named with one could be listed and never searched by its name.
        if (name.Contains(':'))
            return new ConfigRefusal(
                "A corpus name cannot contain ':'",
                "Colon separates corpus from chunk set in `corpus:set`, so a corpus named with one "
                + "could not be addressed by its name.",
                400);

        if (await TakenAsync(name, ct) is { } early) return early;

        var model = string.IsNullOrWhiteSpace(body.EmbeddingModel)
            ? opts.Value.Embedding.Model
            : body.EmbeddingModel.Trim();

        var provider = string.IsNullOrWhiteSpace(body.EmbeddingProvider)
            ? opts.Value.Embedding.Provider
            : body.EmbeddingProvider.Trim();

        var target = new EmbeddingTarget(provider, model);

        int dims;
        try
        {
            dims = await embedder.ProbeDimensionsAsync(target, ct);
        }
        catch (UnknownEmbeddingProviderException ex)
        {
            return new ConfigRefusal("Unknown embedding provider", ex.Message, 400);
        }
        catch (EmbeddingUnavailableException ex)
        {
            // Refuse rather than guess. A corpus created with the wrong dimension
            // count is unusable and the failure surfaces much later, as bad results.
            return new ConfigRefusal(
                "Embedding model unavailable",
                $"Could not probe '{target}': {ex.Message}. The corpus was not created.",
                503);
        }

        var corpus = new Corpus
        {
            Id = Ulid.NewUlid().ToString(),
            Name = name,
            Description = body.Description,
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        };

        if (defaults is { } inherited)
        {
            corpus.DefaultUseGitignore = inherited.UseGitignore;
            corpus.DefaultMaxFileBytes = inherited.MaxFileBytes;
            corpus.DefaultIncludeGlobs = SourceFilters.Store(inherited.IncludeGlobs);
            corpus.DefaultExcludeGlobs = SourceFilters.Store(inherited.ExcludeGlobs);
        }

        // Every corpus is born with one set. Nothing else has to special-case the
        // "no sets yet" state, and the settings a caller passed at creation have a
        // home that is honest about what they configure.
        corpus.ChunkSets.Add(new ChunkSet
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = corpus.Id,
            Name = "default",
            EmbeddingProvider = provider,
            EmbeddingModel = model,
            EmbeddingDimensions = dims,
            CollectionName = vectors.CollectionNameFor(target, dims),
            ChunkSize = body.ChunkSize ?? Indexing.ChunkSize,
            ChunkOverlap = body.ChunkOverlap ?? Indexing.ChunkOverlap,
            BoundaryMode = body.BoundaryMode ?? Indexing.BoundaryMode,
            IsDefault = true,
            State = CorpusState.Ready,
            CreatedUtc = DateTime.UtcNow,
        });

        if (!string.IsNullOrWhiteSpace(body.WorkspacePath))
        {
            try { WorkspaceDiscovery.Resolve(Indexing.WorkspaceRoot, body.WorkspacePath); }
            catch (UnauthorizedAccessException ex)
            { return new ConfigRefusal("Invalid workspace path", ex.Message, 400); }

            corpus.Sources.Add(CorpusEndpoints.FirstSource(corpus.Id,
                WorkspaceDiscovery.Canonical(Indexing.WorkspaceRoot, body.WorkspacePath)));
        }

        await Naming.WaitAsync(ct);
        try
        {
            if (await TakenAsync(name, ct) is { } taken) return taken;
            db.Corpora.Add(corpus);
            if (grantToKeyId is not null && await db.TokenCorpora.AnyAsync(tc => tc.TokenId == grantToKeyId, ct))
                db.TokenCorpora.Add(new TokenCorpus { TokenId = grantToKeyId, CorpusId = corpus.Id });
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Naming.Release();
        }

        committed?.Invoke(corpus);

        // Not cancellable either: the corpus is saved, and a cancel in this call threw before the
        // job below was queued.
        await vectors.EnsureCollectionAsync(corpus.ChunkSets[0].CollectionName, dims, CancellationToken.None);

        // Naming a folder is asking for it to be indexed. Without this the corpus is
        // created EMPTY and reports itself ready, and the only sign is a file count of
        // zero that reads like "this folder had nothing in it": the first thing a new
        // user does appears to do nothing until someone thinks to press Refresh.
        if (corpus.Sources.Count > 0)
        {
            // Not cancellable: the corpus and its source are saved, and a cancel before the job
            // left a corpus nothing reads until the next scheduled refresh.
            await queue.EnqueueAsync(corpus.Id, JobKind.Full, ct: CancellationToken.None);

            // And a sweep, which is the case D-32 was written for: a corpus created
            // while another is indexing would otherwise report nothing at all until
            // the job above reached the front of the queue.
            sweeps.Enqueue(corpus.Id);
        }

        return corpus;
    }

    /// <summary>Changes a corpus's description and default filters. The value says whether the filters moved.</summary>
    /// <param name="committed">
    /// Called after a change is saved and before its refresh is queued, with what it altered, and
    /// not for a request that altered nothing. The refresh is queued after the save, so a failure
    /// there leaves the change in place, and a caller recording changes needs to know at the save.
    /// </param>
    public async Task<ConfigOutcome<bool>> UpdateCorpusAsync(
        Corpus corpus, UpdateCorpusRequest body, CancellationToken ct, Action<CorpusChange>? committed = null)
    {
        // Judged before anything is assigned: the corpus is tracked, so a field set ahead
        // of a refusal would be saved by the next change made through the same context.
        if (body.Defaults is { MaxFileBytes: <= 0 })
            return new ConfigRefusal("Invalid size cap",
                "maxFileBytes must be greater than zero, or null to follow the server's setting.", 400);

        // Chunk settings are NOT here any more. They belong to a chunk set, because a
        // corpus can carry several and "the corpus's chunk size" stopped meaning
        // anything the moment that became true. See /api/corpora/{id}/chunk-sets.
        var descriptionChanged = body.Description is not null && body.Description != corpus.Description;
        if (body.Description is not null) corpus.Description = body.Description;

        // Changing what sources inherit changes which files are in the index, so it
        // queues a refresh the way adding a source does. Narrowing a glob removes the
        // files it now excludes through the walk's own reconcile: they are simply not
        // seen, which is the path a deleted file already takes.
        var filtersChanged = false;
        if (body.Defaults is { } d)
        {
            filtersChanged =
                corpus.DefaultUseGitignore != d.UseGitignore ||
                corpus.DefaultMaxFileBytes != d.MaxFileBytes ||
                corpus.DefaultIncludeGlobs != SourceFilters.Store(d.IncludeGlobs) ||
                corpus.DefaultExcludeGlobs != SourceFilters.Store(d.ExcludeGlobs);

            corpus.DefaultUseGitignore = d.UseGitignore;
            corpus.DefaultMaxFileBytes = d.MaxFileBytes;
            corpus.DefaultIncludeGlobs = SourceFilters.Store(d.IncludeGlobs);
            corpus.DefaultExcludeGlobs = SourceFilters.Store(d.ExcludeGlobs);
        }

        await db.SaveChangesAsync(ct);
        if (descriptionChanged || filtersChanged) committed?.Invoke(new CorpusChange(descriptionChanged, filtersChanged));

        // Not for a corpus with no sources, which has nothing to re-read: defaults set just
        // after creation, before a folder is added, would otherwise leave an empty job.
        //
        // Not cancellable: the change is committed, and the job is what makes it take effect.
        // A cancel between the two left filters changed with no refresh, and sending the same
        // values again reads as no change, so nothing queued one.
        if (filtersChanged && await db.Sources.AnyAsync(s => s.CorpusId == corpus.Id, CancellationToken.None))
            await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: CancellationToken.None);

        return filtersChanged;
    }

    /// <param name="committed">
    /// Called once the source is saved and before its refresh is queued, so a caller can record
    /// the change where it became true. A failure queuing the job leaves the source in place.
    /// </param>
    public async Task<ConfigOutcome<SourceAdded>> AddSourceAsync(
        Corpus corpus, AddSourceRequest body, CancellationToken ct, Action<Source>? committed = null)
    {
        // The shape of the request first, because judging it costs nothing and the
        // check below it starts a git process. A request carrying both a bad path
        // and a setting that cannot apply was answered with the path, so the caller
        // fixed that, resent, and only then learnt about the setting.
        if (body.Git is not null && !body.GitHistory)
            return new ConfigRefusal(
                "Not a git-history source",
                "History settings were sent for a source that indexes files. Set "
                + "gitHistory: true to index the repository's commits, or leave them out.",
                400);

        if (CorpusEndpoints.FileOnlySettingsFor(
                body.GitHistory ? SourceKind.GitHistory : SourceKind.Workspace,
                body.UseGitignore, body.MaxFileBytes, body.ExcludeGlobs) is { } unusable)
            return new ConfigRefusal(
                "Not a file source",
                $"{unusable} apply to a folder being walked, and a history source is "
                + "walked by git log. Include globs work there, as pathspecs.",
                400);

        if (body.MaxFileBytes is <= 0)
            return new ConfigRefusal("Invalid size cap",
                "maxFileBytes must be greater than zero, or omitted to follow the corpus.", 400);

        if (body.GitHistory && CorpusEndpoints.UnusableHistorySettings(body.Git) is { } refused) return refused;

        // The boundary, for every kind of source. Existence is deliberately not
        // required here: a workspace source may be added while its mount is away,
        // and a pass reports that as unavailable rather than losing the source.
        //
        // A history source is the exception, refused at the door rather than
        // recorded and discovered on the first pass. A source that can never produce
        // anything is worse than a 400: it sits in the list looking configured, and
        // the reason only ever appears in a job.
        //
        // Both inside the one catch, because both resolve the same path by the same
        // rule and either can refuse it — RepositoryIn walks the real directories
        // and so also refuses a link that leaves the root, which the string check
        // cannot see.
        try
        {
            var root = Indexing.WorkspaceRoot;
            WorkspaceDiscovery.Resolve(root, body.WorkspacePath);

            if (body.GitHistory
                && await CorpusEndpoints.NotAHistoryRootAsync(root, body.WorkspacePath, GitHistory.IsRepositoryAsync, ct)
                    is { } notARepository)
                return notARepository;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ConfigRefusal("Invalid workspace path", ex.Message, 400);
        }

        var source = new Source
        {
            Id = Ulid.NewUlid().ToString(),
            CorpusId = corpus.Id,
            Kind = body.GitHistory ? SourceKind.GitHistory : SourceKind.Workspace,
            GitOptions = body.GitHistory ? (body.Git ?? new GitHistoryOptions()).ToJson() : null,
            // Resolved above, so the canonical spelling is inside the workspace.
            RootPath = WorkspaceDiscovery.Canonical(Indexing.WorkspaceRoot, body.WorkspacePath),
            // Null, not a default. An omitted field means this source has no opinion
            // and follows the corpus, which is the point of the corpus having one.
            UseGitignore = body.UseGitignore,
            MaxFileBytes = body.MaxFileBytes,
            IncludeGlobs = SourceFilters.Store(body.IncludeGlobs),
            ExcludeGlobs = SourceFilters.Store(body.ExcludeGlobs),
            CreatedUtc = DateTime.UtcNow,
        };

        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);
        committed?.Invoke(source);

        // Same reason as creation: adding a folder is asking for it to be read. A
        // Refresh rather than a Full, because the corpus's other sources are already
        // indexed and re-embedding them costs real money on a hosted provider.
        //
        // Not cancellable, as in UpdateCorpusAsync: the source is saved, and a cancel before
        // the job leaves a source nothing reads until the next scheduled refresh.
        var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: CancellationToken.None);

        // And a sweep, on its own lane. The job above answers "what is in this folder"
        // as well, but only once it reaches the front of a queue that may be hours
        // deep; the sweep answers it in seconds. Adding a folder and being told the
        // corpus holds nothing is the case D-32 exists for.
        sweeps.Enqueue(corpus.Id);

        return new SourceAdded(source.ToSummary(corpus, Indexing), job.ToSummary());
    }

    /// <param name="committed">Called after a change is saved and before its refresh is queued, as for AddSourceAsync.</param>
    public async Task<ConfigOutcome<SourceUpdated>> UpdateSourceAsync(
        Corpus corpus, string sourceId, UpdateSourceRequest body, CancellationToken ct, Action<Source>? committed = null)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId && s.CorpusId == corpus.Id, ct);

        if (source is null)
            return new ConfigRefusal("No such source", $"Corpus '{corpus.Name}' has no source '{sourceId}'.", 404);

        if (source.Kind == SourceKind.Upload)
            return new ConfigRefusal("Not a walked source",
                "Filters apply to a folder being walked. An upload source has no tree to filter.", 400);

        if (body.Git is not null && source.Kind != SourceKind.GitHistory)
            return new ConfigRefusal(
                "Not a git-history source",
                $"Source '{sourceId}' indexes files, not commits, so it has no history "
                + "settings. Add a second source over the same folder with gitHistory: true.",
                400);

        if (body.MaxFileBytes is { } m && m <= 0)
            return new ConfigRefusal("Invalid size cap",
                "maxFileBytes must be greater than zero. Name it in `clear` to inherit the corpus default.", 400);

        if (CorpusEndpoints.UnknownClearName(body.Clear) is { } unknown)
            return new ConfigRefusal(
                "Unknown filter",
                $"'{unknown}' is not a filter that can be cleared. "
                + $"Name one of: {string.Join(", ", CorpusEndpoints.ClearableFilters)}.",
                400);

        if (CorpusEndpoints.FileOnlySettingsFor(source.Kind, body.UseGitignore, body.MaxFileBytes, body.ExcludeGlobs)
            is { } inapplicable)
            return new ConfigRefusal(
                "Not a file source",
                $"Source '{sourceId}' indexes commits, so {inapplicable} would be stored "
                + "and never read. Include globs work there, as pathspecs.",
                400);

        if (CorpusEndpoints.UnusableHistorySettings(body.Git) is { } refused) return refused;

        var changed = CorpusEndpoints.ApplyFilters(source, body);

        if (body.Git is { } git && CorpusEndpoints.ApplyHistorySettings(source, git)) changed = true;

        await db.SaveChangesAsync(ct);
        if (changed) committed?.Invoke(source);

        // Only when something moved. A form submitted unchanged should not re-walk a
        // library, and a refresh on every save is how that happens.
        // Not cancellable: the change is saved, and the job is what applies it.
        var job = changed ? await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: CancellationToken.None) : null;

        return new SourceUpdated(source.ToSummary(corpus, Indexing), job?.ToSummary());
    }

    /// <summary>
    /// Removes a source and everything it indexed, in every chunk set. The folder on disk is
    /// untouched; Dexicon only ever reads it.
    /// </summary>
    public async Task<ConfigOutcome<bool>> RemoveSourceAsync(Corpus corpus, string sourceId, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId && s.CorpusId == corpus.Id, ct);

        if (source is null)
            return new ConfigRefusal("No such source", $"Corpus '{corpus.Name}' has no source '{sourceId}'.", 404);

        var paths = await db.Files.Where(f => f.SourceId == source.Id)
            .Select(f => f.RelativePath).ToListAsync(ct);

        // Vectors first, for the same reason RemoveAttachmentAsync does it: if the
        // catalogue row went first and this threw, the corpus would keep returning
        // hits for files it no longer lists.
        //
        // Once per set, because a removed folder has to leave every chunking of the
        // corpus rather than only the default, and scoped to this source, because a file_path is
        // relative to a source root and another source may hold the same name.
        var sets = await db.ChunkSets.Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);
        foreach (var set in sets)
            foreach (var path in paths)
                await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, source.Id, path, ct);

        // The file rows and their per-set chunk states go with it: both cascade from
        // Source, so removing it is the whole of the catalogue side.
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>
    /// Removes a chunk set and its vectors. Refused where it would leave the corpus unable to
    /// answer: the only set, the default set, or a set a job is working on.
    /// </summary>
    public async Task<ConfigOutcome<bool>> RemoveChunkSetAsync(Corpus corpus, string setName, CancellationToken ct)
    {
        await db.Entry(corpus).Collection(c => c.ChunkSets).LoadAsync(ct);
        var set = corpus.ChunkSets.FirstOrDefault(s =>
            string.Equals(s.Name, setName, StringComparison.OrdinalIgnoreCase) || s.Id == setName);

        if (set is null)
            return new ConfigRefusal("Unknown chunk set",
                $"Corpus '{corpus.Name}' has no chunk set named '{setName}'.", 404);

        // A corpus with no sets is a corpus nothing can search. Refuse rather than
        // leave it in a state whose only exit is creating a set by hand.
        if (corpus.ChunkSets.Count == 1)
            return new ConfigRefusal("Cannot delete the only chunk set",
                $"'{set.Name}' is the only way '{corpus.Name}' is indexed. Delete the corpus instead, " +
                "or add another set and promote it first.", 409);

        if (set.IsDefault)
            return new ConfigRefusal("Cannot delete the default chunk set",
                "Promote another set first; search would otherwise have nothing to fall back to.", 409);

        // A job that names this set, or names none, is working on it. Deleting the row under
        // a job scoped to it nulls the job's ChunkSetId, which reads as every set of the
        // corpus: it would run against, and report as indexing, sets it was never asked for.
        var activity = await IndexingActivity.ReadAsync(db, [corpus.Id], ct);
        if (activity.Of(set) == CorpusState.Indexing)
            return new ConfigRefusal("Cannot delete a chunk set while it is being indexed",
                $"A job is working on '{set.Name}'. Delete it once the job has finished.", 409);

        // Vectors first: if the row went first and this threw, the collection would
        // keep points that nothing in the catalogue can name or clean up.
        await vectors.DeleteChunkSetAsync(set.CollectionName, set.Id, ct);
        db.ChunkSets.Remove(set);
        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>
    /// Removes a corpus: its vectors in every collection it has sets in, then its rows, which
    /// take its sources, files, chunk states and jobs with them.
    /// </summary>
    public async Task<ConfigOutcome<bool>> RemoveCorpusAsync(Corpus corpus, CancellationToken ct)
    {
        // Per collection, because a corpus mid-migration has sets in two of them and
        // a single delete would leave one half behind with nothing left to name it.
        var collections = await db.ChunkSets.Where(s => s.CorpusId == corpus.Id)
            .Select(s => s.CollectionName).Distinct().ToListAsync(ct);

        foreach (var collection in collections)
            await vectors.DeleteCorpusAsync(collection, corpus.Id, ct);

        db.Corpora.Remove(corpus);
        await db.SaveChangesAsync(ct);

        return true;
    }
}
