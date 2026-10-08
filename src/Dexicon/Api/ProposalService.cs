using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

/// <summary>What deleting the target would take, worked out when a person looks, not when it was asked.</summary>
/// <param name="Blocker">Why approving it would be refused now, or null when nothing stands in the way.</param>
public sealed record ProposalFacts(int Sources, int Files, int Chunks, int ChunkSets, string? Blocker);

/// <param name="Gone">The target is no longer there, so approving would record the proposal as failed.</param>
/// <param name="Facts">Null once decided, and when the target is gone.</param>
public sealed record ProposalView(
    string Id, DateTime CreatedUtc, string KeyName, string CorpusName, string Kind, string Target, string Reason,
    string Status, DateTime? DecidedUtc, string? Error, bool Gone, ProposalFacts? Facts);

/// <param name="AlreadyPending">The same thing was already waiting for a decision, so nothing new was recorded.</param>
public sealed record ProposalAsked(Proposal Proposal, bool AlreadyPending);

/// <summary>The spellings an agent and the API use for a proposal's kind.</summary>
public static class ProposalKinds
{
    public static string Name(ProposalKind kind) => kind switch
    {
        ProposalKind.Source => "source",
        ProposalKind.ChunkSet => "chunk_set",
        ProposalKind.Document => "document",
        _ => "corpus",
    };

    public static bool TryParse(string? text, out ProposalKind kind)
    {
        switch (text?.Trim().ToLowerInvariant().Replace('-', '_'))
        {
            case "source": kind = ProposalKind.Source; return true;
            case "chunk_set" or "chunkset" or "set": kind = ProposalKind.ChunkSet; return true;
            case "document": kind = ProposalKind.Document; return true;
            case "corpus": kind = ProposalKind.Corpus; return true;
            default: kind = default; return false;
        }
    }

    public const string Choices = "source, chunk_set, document or corpus";
}

/// <summary>
/// Removals an agent asks for and a person decides (docs/decisions.md D-39).
///
/// Approving runs the code the admin's DELETE endpoints run, <see cref="CorpusConfiguration"/>'s
/// removals and the document detach, so the two cannot drift. Nothing is stored about a decision
/// in flight. The proposal is marked decided on the tracked row before the removal runs, and the
/// removal's own save writes both, so the target is gone and the proposal is approved together
/// or neither. A crash before that save leaves it pending, and approving again retries.
/// </summary>
public sealed class ProposalService(
    CatalogDbContext db,
    CorpusConfiguration config,
    IVectorStoreCleanup cleanup,
    DocumentService documents,
    IOptions<DexiconOptions> options,
    TimeProvider clock,
    ILogger<ProposalService> log)
{
    /// <summary>How many a key may have waiting. A cap is what stops a loop filling the page.</summary>
    public const int MaxPendingPerKey = 10;

    public const int ReasonMax = 300;

    /// <summary>
    /// Held from the check for a proposal already waiting to the insert, so two calls proposing the
    /// same thing cannot both find it free. Dexicon is one process owning its catalogue (D-01).
    /// </summary>
    private static readonly SemaphoreSlim Proposing = new(1, 1);

    /// <summary>
    /// Held from reading a proposal to saving its decision. An approval deletes vectors before it
    /// saves, and the vector store cannot be rolled back, so a rejection saved in between would leave a
    /// rejected proposal whose vectors are gone while the catalogue still lists them. One process owns
    /// the catalogue (D-01), so one lock is enough. The row's concurrency token stays as the check.
    /// </summary>
    private static readonly SemaphoreSlim Deciding = new(1, 1);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- asking -----------------------------------------------------------------------------

    /// <summary>
    /// Records a request to remove one thing, or finds the same request already waiting. The target is
    /// resolved here, by the rules the admin endpoint uses, and held by id from then on.
    /// </summary>
    public async Task<ConfigOutcome<ProposalAsked>> ProposeAsync(
        Principal principal, Corpus corpus, ProposalKind kind, string? target, string? reason, CancellationToken ct)
    {
        var why = (reason ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (why.Length == 0)
            return new ConfigRefusal("A reason is required",
                "Say in a line why this should be removed: the person deciding reads it beside what would go.", 400);
        if (why.Length > ReasonMax)
            return new ConfigRefusal("The reason is too long",
                $"A reason is at most {ReasonMax} characters and this one is {why.Length}.", 400);

        var resolved = await ResolveAsync(corpus, kind, target, ct);
        if (resolved.Refusal is { } refused) return refused;
        var (targetId, label) = resolved.Value;

        await Proposing.WaitAsync(ct);
        try
        {
            var waiting = await db.Proposals.FirstOrDefaultAsync(
                p => p.Kind == kind && p.TargetId == targetId && p.Status == ProposalStatus.Pending, ct);
            if (waiting is not null) return new ProposalAsked(waiting, AlreadyPending: true);

            var pending = await db.Proposals.CountAsync(
                p => p.TokenId == principal.TokenId && p.Status == ProposalStatus.Pending, ct);
            if (pending >= MaxPendingPerKey)
                return new ConfigRefusal("Too many proposals waiting",
                    $"This key has {pending} proposals waiting for a decision, which is the most it may have. "
                    + "Whoever runs Dexicon has to decide them first.", 429);

            var proposal = new Proposal
            {
                Id = Ulid.NewUlid().ToString(),
                CreatedUtc = Now,
                TokenId = principal.TokenId,
                TokenName = Trim(principal.TokenName, 200),
                CorpusId = corpus.Id,
                CorpusName = Trim(corpus.Name, 200),
                Kind = kind,
                TargetId = targetId,
                TargetLabel = Trim(label, 500),
                Reason = why,
                Status = ProposalStatus.Pending,
            };
            db.Proposals.Add(proposal);
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "Key {Key} proposed removing the {Kind} {Target} from corpus {Corpus}: {Reason}",
                DexiconAuthMiddleware.OneLine(principal.Name), ProposalKinds.Name(kind),
                DexiconAuthMiddleware.OneLine(label), DexiconAuthMiddleware.OneLine(corpus.Name),
                DexiconAuthMiddleware.OneLine(why));

            return new ProposalAsked(proposal, AlreadyPending: false);
        }
        finally { Proposing.Release(); }
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// The one thing a request names, as an id and a label, or why it names none. A refusal lists what
    /// there is, because an agent that is told only "not found" will guess again.
    /// </summary>
    private async Task<ConfigOutcome<(string Id, string Label)>> ResolveAsync(
        Corpus corpus, ProposalKind kind, string? target, CancellationToken ct)
    {
        var named = (target ?? string.Empty).Trim();

        // A blank folder is the workspace root, so a source request that leaves target out would name
        // the root source if there is one. The root is asked for as files: or history:, which is not blank.
        if (kind != ProposalKind.Corpus && named.Length == 0)
            return new ConfigRefusal("A target is required",
                "Name what to remove in target: a source's folder, a chunk set's name or a document's path. "
                + $"Only a corpus leaves it out. kind was {ProposalKinds.Name(kind)}.", 400);

        switch (kind)
        {
            case ProposalKind.Corpus:
                if (named.Length > 0
                    && !string.Equals(named, corpus.Name, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(named, corpus.Id, StringComparison.Ordinal))
                    return new ConfigRefusal("Not that corpus",
                        $"To remove a corpus, name it in corpus and leave target out. target was '{Line(named)}' "
                        + $"and corpus is '{Line(corpus.Name)}'.", 400);
                return (corpus.Id, corpus.Name);

            case ProposalKind.ChunkSet:
            {
                var sets = await db.ChunkSets.AsNoTracking().Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);
                var set = sets.FirstOrDefault(s =>
                    string.Equals(s.Name, named, StringComparison.OrdinalIgnoreCase) || s.Id == named);
                if (set is null)
                    return new ConfigRefusal("No such chunk set",
                        $"Corpus '{Line(corpus.Name)}' has no chunk set '{Line(named)}'. Its sets: "
                        + $"{string.Join(", ", sets.Select(s => Line(s.Name)).Order(StringComparer.Ordinal))}.", 404);

                // Refused now rather than left to fail when approved: an agent can act on it.
                if (sets.Count == 1)
                    return new ConfigRefusal("Cannot remove the only chunk set",
                        $"'{Line(set.Name)}' is the only way '{Line(corpus.Name)}' is indexed. Propose removing the corpus, "
                        + "or add another set and promote it first.", 409);
                if (set.IsDefault)
                    return new ConfigRefusal("Cannot remove the default chunk set",
                        "Search would have nothing to fall back to. The default has to be changed in the UI first.", 409);
                return (set.Id, set.Name);
            }

            case ProposalKind.Document:
            {
                var files = await db.Files.AsNoTracking()
                    .Where(f => f.Source!.CorpusId == corpus.Id && f.Source.Kind == SourceKind.Upload)
                    .ToListAsync(ct);
                var file = files.FirstOrDefault(f => f.Id == named)
                           ?? files.FirstOrDefault(f => string.Equals(f.RelativePath, named, StringComparison.Ordinal));
                if (file is null)
                    return new ConfigRefusal("No such document",
                        $"Corpus '{Line(corpus.Name)}' has no uploaded document '{Line(named)}'. Documents attached to it: "
                        + (files.Count == 0
                            ? "none."
                            : string.Join(", ", files.Select(f => Line(f.RelativePath)).Order(StringComparer.Ordinal).Take(20))
                              + (files.Count > 20 ? $" and {files.Count - 20} more." : ".")), 404);
                return (file.Id, file.RelativePath);
            }

            default:
            {
                var sources = await db.Sources.AsNoTracking()
                    .Where(s => s.CorpusId == corpus.Id && s.Kind != SourceKind.Upload).ToListAsync(ct);
                var root = options.Value.Indexing.WorkspaceRoot;
                string Describe(Source s) =>
                    $"{(s.Kind == SourceKind.GitHistory ? "history" : "files")}:{WorkspaceDiscovery.Canonical(root, s.RootPath)}";
                string Listing() => sources.Count == 0
                    ? "none"
                    : string.Join(", ", sources.Select(s => Line(Describe(s))).Order(StringComparer.Ordinal));

                var byId = sources.FirstOrDefault(s => s.Id == named);
                if (byId is not null) return (byId.Id, Describe(byId));

                SourceKind? only = null;
                var folder = named;
                if (folder.StartsWith("files:", StringComparison.OrdinalIgnoreCase))
                { only = SourceKind.Workspace; folder = folder[6..]; }
                else if (folder.StartsWith("history:", StringComparison.OrdinalIgnoreCase))
                { only = SourceKind.GitHistory; folder = folder[8..]; }

                string canonical;
                try { canonical = WorkspaceDiscovery.Canonical(root, folder); }
                catch (ArgumentException)
                {
                    return new ConfigRefusal("Not a folder",
                        $"'{Line(named)}' is not a folder path. The sources of '{Line(corpus.Name)}': {Listing()}.", 400);
                }

                var matches = sources
                    .Where(s => (only is null || s.Kind == only)
                                && WorkspaceDiscovery.PathComparer.Equals(WorkspaceDiscovery.Canonical(root, s.RootPath), canonical))
                    .ToList();
                if (matches.Count == 1) return (matches[0].Id, Describe(matches[0]));
                if (matches.Count > 1)
                    return new ConfigRefusal("Two sources read that folder",
                        $"Both its files and its commit history are sources of '{Line(corpus.Name)}'. Pass "
                        + $"files:{Line(canonical)} or history:{Line(canonical)}.", 409);
                return new ConfigRefusal("No such source",
                    $"Corpus '{Line(corpus.Name)}' has no source on '{Line(named)}'. Its sources: {Listing()}.", 404);
            }
        }
    }

    private static string Line(string value) => DexiconTools.OneLine(value);

    // ---- reading ----------------------------------------------------------------------------

    public Task<int> PendingCountAsync(CancellationToken ct) =>
        db.Proposals.CountAsync(p => p.Status == ProposalStatus.Pending, ct);

    /// <summary>
    /// What is waiting, oldest first so nothing is buried, or what has been decided, newest first. What
    /// each waiting one would take is worked out now.
    /// </summary>
    public async Task<List<ProposalView>> ListAsync(bool decided, int take, CancellationToken ct)
    {
        var rows = decided
            ? await db.Proposals.AsNoTracking().Where(p => p.Status != ProposalStatus.Pending)
                .OrderByDescending(p => p.DecidedUtc).ThenByDescending(p => p.Id).Take(take).ToListAsync(ct)
            : await db.Proposals.AsNoTracking().Where(p => p.Status == ProposalStatus.Pending)
                .OrderBy(p => p.CreatedUtc).ThenBy(p => p.Id).Take(take).ToListAsync(ct);

        var worked = decided ? [] : await FactsAsync(rows, ct);
        return rows.Select(p =>
        {
            var (gone, facts) = worked.GetValueOrDefault(p.Id);
            return View(p, gone, facts);
        }).ToList();
    }

    private static ProposalView View(Proposal p, bool gone = false, ProposalFacts? facts = null) => new(
        p.Id, p.CreatedUtc, p.TokenName, p.CorpusName, ProposalKinds.Name(p.Kind), p.TargetLabel, p.Reason,
        p.Status.ToString().ToLowerInvariant(), p.DecidedUtc, p.Error, gone, facts);

    /// <summary>
    /// What each of these would take, or that its target has gone. The figures are read once for the
    /// whole page, by kind: the screen lists them every time it polls, and a read per request was
    /// about five queries each.
    /// </summary>
    private async Task<Dictionary<string, (bool Gone, ProposalFacts? Facts)>> FactsAsync(
        List<Proposal> rows, CancellationToken ct)
    {
        var facts = new Dictionary<string, (bool Gone, ProposalFacts? Facts)>(rows.Count);
        if (rows.Count == 0) return facts;

        var corpusIds = rows.Select(p => p.CorpusId).Distinct().ToList();
        var present = (await db.Corpora.AsNoTracking().Where(c => corpusIds.Contains(c.Id)).Select(c => c.Id)
            .ToListAsync(ct)).ToHashSet();
        var sets = (await db.ChunkSets.AsNoTracking().Where(s => corpusIds.Contains(s.CorpusId)).ToListAsync(ct))
            .ToLookup(s => s.CorpusId);

        // A request is gone with its corpus.
        foreach (var p in rows.Where(p => !present.Contains(p.CorpusId))) facts[p.Id] = (true, null);
        var here = rows.Where(p => present.Contains(p.CorpusId)).ToList();

        await SourceFactsAsync(here.Where(p => p.Kind == ProposalKind.Source).ToList(), sets, facts, ct);
        await ChunkSetFactsAsync(here.Where(p => p.Kind == ProposalKind.ChunkSet).ToList(), sets, facts, ct);
        await DocumentFactsAsync(here.Where(p => p.Kind == ProposalKind.Document).ToList(), sets, facts, ct);
        await CorpusFactsAsync(here.Where(p => p.Kind == ProposalKind.Corpus).ToList(), sets, facts, ct);
        return facts;
    }

    private async Task SourceFactsAsync(
        List<Proposal> asked, ILookup<string, ChunkSet> sets, Dictionary<string, (bool Gone, ProposalFacts? Facts)> facts,
        CancellationToken ct)
    {
        if (asked.Count == 0) return;
        var ids = asked.Select(p => p.TargetId).Distinct().ToList();

        var corpusOf = await db.Sources.AsNoTracking().Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.CorpusId, ct);
        var files = await db.Files.Where(f => ids.Contains(f.SourceId)).GroupBy(f => f.SourceId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N, ct);
        var chunks = await db.FileChunkStates.Where(s => ids.Contains(s.File!.SourceId)).GroupBy(s => s.File!.SourceId)
            .Select(g => new { g.Key, N = g.Sum(s => s.ChunkCount) }).ToDictionaryAsync(g => g.Key, g => g.N, ct);

        foreach (var p in asked)
            facts[p.Id] = corpusOf.GetValueOrDefault(p.TargetId) == p.CorpusId
                ? (false, new ProposalFacts(
                    1, files.GetValueOrDefault(p.TargetId), chunks.GetValueOrDefault(p.TargetId), sets[p.CorpusId].Count(), null))
                : (true, null);
    }

    private async Task ChunkSetFactsAsync(
        List<Proposal> asked, ILookup<string, ChunkSet> sets, Dictionary<string, (bool Gone, ProposalFacts? Facts)> facts,
        CancellationToken ct)
    {
        if (asked.Count == 0) return;
        var ids = asked.Select(p => p.TargetId).Distinct().ToList();

        var counted = await db.FileChunkStates.Where(s => ids.Contains(s.ChunkSetId)).GroupBy(s => s.ChunkSetId)
            .Select(g => new { g.Key, Chunks = g.Sum(s => s.ChunkCount), Files = g.Count(s => s.ChunkCount > 0) })
            .ToDictionaryAsync(g => g.Key, ct);
        var activity = await IndexingActivity.ReadAsync(db, asked.Select(p => p.CorpusId).Distinct().ToList(), ct);

        foreach (var p in asked)
        {
            var inCorpus = sets[p.CorpusId].ToList();
            var set = inCorpus.FirstOrDefault(s => s.Id == p.TargetId);
            if (set is null)
            {
                facts[p.Id] = (true, null);
                continue;
            }

            var blocker = inCorpus.Count == 1 ? "it is the only chunk set"
                : set.IsDefault ? "it is the default chunk set"
                : activity.Of(set) == CorpusState.Indexing ? "a job is working on it"
                : null;
            var n = counted.GetValueOrDefault(set.Id);
            facts[p.Id] = (false, new ProposalFacts(0, n?.Files ?? 0, n?.Chunks ?? 0, 1, blocker));
        }
    }

    private async Task DocumentFactsAsync(
        List<Proposal> asked, ILookup<string, ChunkSet> sets, Dictionary<string, (bool Gone, ProposalFacts? Facts)> facts,
        CancellationToken ct)
    {
        if (asked.Count == 0) return;
        var ids = asked.Select(p => p.TargetId).Distinct().ToList();

        var corpusOf = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id))
            .Select(f => new { f.Id, CorpusId = f.Source!.CorpusId }).ToDictionaryAsync(f => f.Id, f => f.CorpusId, ct);
        var chunks = await db.FileChunkStates.Where(s => ids.Contains(s.FileId)).GroupBy(s => s.FileId)
            .Select(g => new { g.Key, N = g.Sum(s => s.ChunkCount) }).ToDictionaryAsync(g => g.Key, g => g.N, ct);

        foreach (var p in asked)
            facts[p.Id] = corpusOf.GetValueOrDefault(p.TargetId) == p.CorpusId
                ? (false, new ProposalFacts(0, 1, chunks.GetValueOrDefault(p.TargetId), sets[p.CorpusId].Count(), null))
                : (true, null);
    }

    private async Task CorpusFactsAsync(
        List<Proposal> asked, ILookup<string, ChunkSet> sets, Dictionary<string, (bool Gone, ProposalFacts? Facts)> facts,
        CancellationToken ct)
    {
        if (asked.Count == 0) return;
        var ids = asked.Select(p => p.CorpusId).Distinct().ToList();

        var sources = await db.Sources.Where(s => ids.Contains(s.CorpusId)).GroupBy(s => s.CorpusId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N, ct);
        var files = await db.Files.Where(f => ids.Contains(f.Source!.CorpusId)).GroupBy(f => f.Source!.CorpusId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N, ct);
        var chunks = await db.FileChunkStates.Where(s => ids.Contains(s.File!.Source!.CorpusId))
            .GroupBy(s => s.File!.Source!.CorpusId)
            .Select(g => new { g.Key, N = g.Sum(s => s.ChunkCount) }).ToDictionaryAsync(g => g.Key, g => g.N, ct);

        foreach (var p in asked)
            facts[p.Id] = (false, new ProposalFacts(
                sources.GetValueOrDefault(p.CorpusId), files.GetValueOrDefault(p.CorpusId),
                chunks.GetValueOrDefault(p.CorpusId), sets[p.CorpusId].Count(), null));
    }

    // ---- deciding ---------------------------------------------------------------------------

    public async Task<ConfigOutcome<ProposalView>> RejectAsync(string id, CancellationToken ct)
    {
        await Deciding.WaitAsync(ct);
        try { return await RejectHeldAsync(id, ct); }
        finally { Deciding.Release(); }
    }

    private async Task<ConfigOutcome<ProposalView>> RejectHeldAsync(string id, CancellationToken ct)
    {
        var p = await db.Proposals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(id);
        if (p.Status != ProposalStatus.Pending) return AlreadyDecided(p);

        p.Status = ProposalStatus.Rejected;
        p.DecidedUtc = Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return DecidedElsewhere(); }

        log.LogInformation("Proposal {Id} to remove the {Kind} {Target} from corpus {Corpus} was rejected",
            p.Id, ProposalKinds.Name(p.Kind), DexiconAuthMiddleware.OneLine(p.TargetLabel), DexiconAuthMiddleware.OneLine(p.CorpusName));
        return View(p);
    }

    /// <summary>
    /// Removes the target and records the approval in one save. Refused, and left waiting, when the
    /// removal is (a set a job is working on, the default set): that can change, and the person decides
    /// again. A target that is no longer there is not that, and fails the proposal with the reason.
    /// </summary>
    public async Task<ConfigOutcome<ProposalView>> ApproveAsync(string id, CancellationToken ct)
    {
        await Deciding.WaitAsync(ct);
        try { return await ApproveHeldAsync(id, ct); }
        finally { Deciding.Release(); }
    }

    private async Task<ConfigOutcome<ProposalView>> ApproveHeldAsync(string id, CancellationToken ct)
    {
        var p = await db.Proposals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(id);
        if (p.Status != ProposalStatus.Pending) return AlreadyDecided(p);

        var corpus = await db.Corpora.FirstOrDefaultAsync(c => c.Id == p.CorpusId, ct);
        if (corpus is null) return await FailAsync(p, "The corpus is no longer there.", ct);

        // Marked on the tracked row first. The removal below saves the context, and what it saves
        // includes this, so the two are one transaction. Put back if the removal does not happen.
        p.Status = ProposalStatus.Approved;
        p.DecidedUtc = Now;

        ConfigRefusal? refusal;
        try
        {
            refusal = p.Kind switch
            {
                ProposalKind.Source => (await config.RemoveSourceAsync(corpus, p.TargetId, ct)).Refusal,
                ProposalKind.ChunkSet => (await config.RemoveChunkSetAsync(corpus, p.TargetId, ct)).Refusal,
                ProposalKind.Corpus => (await config.RemoveCorpusAsync(corpus, ct)).Refusal,
                _ => await cleanup.RemoveAttachmentAsync(corpus, p.TargetId, documents, ct)
                    ? null
                    : new ConfigRefusal("No such document", "The document is no longer attached.", 404),
            };
        }
        catch (DbUpdateConcurrencyException) { return DecidedElsewhere(); }
        catch
        {
            Undecide(p);
            throw;
        }

        if (refusal is null)
        {
            log.LogInformation("Proposal {Id} to remove the {Kind} {Target} from corpus {Corpus} was approved",
                p.Id, ProposalKinds.Name(p.Kind), DexiconAuthMiddleware.OneLine(p.TargetLabel), DexiconAuthMiddleware.OneLine(p.CorpusName));
            return View(p);
        }

        Undecide(p);
        // Nothing to remove is a fact about the target; anything else is about now, and is told to the
        // person who asked to approve it.
        return refusal.Status == 404 ? await FailAsync(p, refusal.Detail, ct) : refusal;
    }

    private static void Undecide(Proposal p)
    {
        p.Status = ProposalStatus.Pending;
        p.DecidedUtc = null;
    }

    private async Task<ConfigOutcome<ProposalView>> FailAsync(Proposal p, string reason, CancellationToken ct)
    {
        p.Status = ProposalStatus.Failed;
        p.DecidedUtc = Now;
        p.Error = Trim(reason, 500);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return DecidedElsewhere(); }

        log.LogInformation("Proposal {Id} to remove the {Kind} {Target} from corpus {Corpus} failed: {Reason}",
            p.Id, ProposalKinds.Name(p.Kind), DexiconAuthMiddleware.OneLine(p.TargetLabel),
            DexiconAuthMiddleware.OneLine(p.CorpusName), DexiconAuthMiddleware.OneLine(reason));
        return View(p);
    }

    private static ConfigRefusal NotFound(string id) =>
        new("No such proposal", $"There is no proposal '{DexiconTools.OneLine(id)}'.", 404);

    private static ConfigRefusal AlreadyDecided(Proposal p) =>
        new("Already decided", $"This proposal was already {p.Status.ToString().ToLowerInvariant()}.", 409);

    private static ConfigRefusal DecidedElsewhere() =>
        new("Already decided", "This proposal was decided by someone else a moment ago. Reload to see how.", 409);
}
