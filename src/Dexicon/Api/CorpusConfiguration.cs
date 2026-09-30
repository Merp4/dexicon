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

    /// <summary>
    /// Held from the last name check to the insert, which makes the two one step. The unique
    /// index compares names exactly, so "Notes" and "notes" created at the same moment would
    /// both pass it. Dexicon is one process owning its catalogue (D-01), and this is blind to
    /// a second process writing the same file.
    /// </summary>
    private static readonly SemaphoreSlim Naming = new(1, 1);

    /// <summary>
    /// A conflict when a corpus already holds this name, compared as
    /// <see cref="Dexicon.Core.Auth.ScopeResolver"/> resolves one: ordinal, ignoring case. The column
    /// has no collation, so its unique index compares exactly, and SQLite's NOCASE folds
    /// ASCII only. Neither refuses "å" beside "Å", and one name would then resolve to either
    /// corpus. The comparison is therefore made here, over the names read whole, since there
    /// are tens of them.
    /// </summary>
    private async Task<ConfigRefusal?> TakenAsync(string name, CancellationToken ct)
    {
        var names = await db.Corpora.Select(c => c.Name).ToListAsync(ct);
        var existing = names.Find(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return existing is null
            ? null
            : new ConfigRefusal(
                "Corpus already exists",
                $"A corpus named '{existing}' already exists. Names are unique, ignoring case, " +
                "because the name is what an agent passes to search_index.",
                409);
    }

    public async Task<ConfigOutcome<Corpus>> CreateCorpusAsync(CreateCorpusRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return new ConfigRefusal("Name is required", "A corpus needs a name.", 400);

        // Compared as it will be stored, trimmed. Compared as sent, " notes " passed the check
        // and failed the unique index, which is a 500 where a 409 was promised. Checked here
        // so that a taken name does not wait on the model probe, and again at the insert.
        var name = body.Name.Trim();
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

            corpus.Sources.Add(CorpusEndpoints.FirstSource(corpus.Id, body.WorkspacePath));
        }

        await Naming.WaitAsync(ct);
        try
        {
            if (await TakenAsync(name, ct) is { } taken) return taken;
            db.Corpora.Add(corpus);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Naming.Release();
        }

        await vectors.EnsureCollectionAsync(corpus.ChunkSets[0].CollectionName, dims, ct);

        // Naming a folder is asking for it to be indexed. Without this the corpus is
        // created EMPTY and reports itself ready, and the only sign is a file count of
        // zero that reads like "this folder had nothing in it": the first thing a new
        // user does appears to do nothing until someone thinks to press Refresh.
        if (corpus.Sources.Count > 0)
        {
            await queue.EnqueueAsync(corpus.Id, JobKind.Full, ct: ct);

            // And a sweep, which is the case D-32 was written for: a corpus created
            // while another is indexing would otherwise report nothing at all until
            // the job above reached the front of the queue.
            sweeps.Enqueue(corpus.Id);
        }

        return corpus;
    }

    /// <summary>Changes a corpus's description and default filters. The value says whether the filters moved.</summary>
    public async Task<ConfigOutcome<bool>> UpdateCorpusAsync(Corpus corpus, UpdateCorpusRequest body, CancellationToken ct)
    {
        // Judged before anything is assigned: the corpus is tracked, so a field set ahead
        // of a refusal would be saved by the next change made through the same context.
        if (body.Defaults is { MaxFileBytes: <= 0 })
            return new ConfigRefusal("Invalid size cap",
                "maxFileBytes must be greater than zero, or null to follow the server's setting.", 400);

        // Chunk settings are NOT here any more. They belong to a chunk set, because a
        // corpus can carry several and "the corpus's chunk size" stopped meaning
        // anything the moment that became true. See /api/corpora/{id}/chunk-sets.
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

        if (filtersChanged) await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct);

        return filtersChanged;
    }

    public async Task<ConfigOutcome<SourceAdded>> AddSourceAsync(Corpus corpus, AddSourceRequest body, CancellationToken ct)
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
            RootPath = body.WorkspacePath.Trim('/', '\\'),
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

        // Same reason as creation: adding a folder is asking for it to be read. A
        // Refresh rather than a Full, because the corpus's other sources are already
        // indexed and re-embedding them costs real money on a hosted provider.
        var job = await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct);

        // And a sweep, on its own lane. The job above answers "what is in this folder"
        // as well, but only once it reaches the front of a queue that may be hours
        // deep; the sweep answers it in seconds. Adding a folder and being told the
        // corpus holds nothing is the case D-32 exists for.
        sweeps.Enqueue(corpus.Id);

        return new SourceAdded(source.ToSummary(corpus, Indexing), job.ToSummary());
    }

    public async Task<ConfigOutcome<SourceUpdated>> UpdateSourceAsync(
        Corpus corpus, string sourceId, UpdateSourceRequest body, CancellationToken ct)
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

        // Only when something moved. A form submitted unchanged should not re-walk a
        // library, and a refresh on every save is how that happens.
        var job = changed ? await queue.EnqueueAsync(corpus.Id, JobKind.Refresh, ct: ct) : null;

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
}
