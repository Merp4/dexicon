using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Auth;

/// <summary>
/// Raised when a caller's authorised corpus set would be empty, or when they named a
/// corpus they cannot see. Never degraded into "search everything" and never silently
/// narrowed: a caller who asked for a corpus they cannot read gets a named error,
/// because dropping it would return incomplete results without saying so.
/// </summary>
public sealed class ScopeResolutionException(string message, IReadOnlyList<string> visibleNames)
    : Exception(message)
{
    public IReadOnlyList<string> VisibleNames { get; } = visibleNames;
}

/// <summary>
/// One corpus in a resolved scope, together with the chunk set the caller will actually
/// search. The corpus is the authorisation and tenancy unit; the set is the vector space.
/// </summary>
public sealed record ScopedCorpus(Corpus Corpus, ChunkSet Set)
{
    public string Id => Corpus.Id;
    public string Name => Corpus.Name;

    /// <summary>What the caller asked for: `books`, or `books:fine` for a named set.</summary>
    public string QualifiedName => Set.IsDefault ? Corpus.Name : $"{Corpus.Name}:{Set.Name}";
}

public sealed record ResolvedScope(IReadOnlyList<ScopedCorpus> Targets)
{
    public IReadOnlyList<Corpus> Corpora => Targets.Select(t => t.Corpus).ToList();
    public IReadOnlyList<string> Ids => Targets.Select(t => t.Corpus.Id).ToList();

    /// <summary>
    /// A collection is one vector space, so a scope spanning two embedding models is two
    /// queries. Grouped by the CHUNK SET's collection: with sets, two corpora can share a
    /// model while one of them is mid-migration to another, and each is searched in the
    /// space its own set actually lives in.
    /// </summary>
    public IEnumerable<IGrouping<string, ScopedCorpus>> ByCollection =>
        Targets.GroupBy(t => t.Set.CollectionName, StringComparer.Ordinal);
}

/// <summary>
/// The authorization boundary. One function, one place, called by every read path.
/// See docs/07-tenancy-auth.md. This is the application-level guard; the repository
/// refuses an unfiltered query, and the storage layout makes one useless.
/// </summary>
public sealed class ScopeResolver(CatalogDbContext db)
{
    /// <summary>Root path per source id, for every source of the corpora in scope.</summary>
    public async Task<IReadOnlyDictionary<string, string>> SourceRootsAsync(
        IReadOnlyList<string> corpusIds, CancellationToken ct = default)
    {
        var rows = await db.Sources
            .Where(s => corpusIds.Contains(s.CorpusId) && s.RootPath != null)
            .Select(s => new { s.Id, s.RootPath })
            .ToListAsync(ct);

        return rows.ToDictionary(s => s.Id, s => s.RootPath!, StringComparer.Ordinal);
    }

    /// <summary>
    /// The sources within an already-authorised scope whose root path matches
    /// <paramref name="rootPath"/>, exactly or as a parent folder.
    ///
    /// Takes corpus ids that resolution has ALREADY authorised, so this cannot widen a
    /// scope, only narrow one. Matching a parent is what makes `orly` mean all ten topic
    /// folders beneath it and `orly/AI` mean one.
    /// </summary>
    public async Task<IReadOnlyList<string>> SourceIdsAsync(
        IReadOnlyList<string> corpusIds, string rootPath, CancellationToken ct = default)
    {
        var needle = rootPath.Trim('/', '\\').Replace('\\', '/');

        var candidates = await db.Sources
            .Where(s => corpusIds.Contains(s.CorpusId))
            .Select(s => new { s.Id, s.RootPath })
            .ToListAsync(ct);

        // An upload source has no root path at all; it can never match a folder.
        var rooted = candidates.Where(s => s.RootPath is not null).ToList();

        var matched = rooted
            .Where(s => string.Equals(s.RootPath, needle, StringComparison.OrdinalIgnoreCase)
                        || s.RootPath!.StartsWith(needle + "/", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Id)
            .ToList();

        if (matched.Count == 0)
            throw new ScopeResolutionException(
                $"No source at '{rootPath}' in the corpora searched.",
                [.. rooted.Select(s => s.RootPath!).Distinct().Order(StringComparer.Ordinal)]);

        return matched;
    }

    public async Task<ResolvedScope> ResolveReadableAsync(
        string tenantId, IReadOnlyList<string>? requestedNamesOrIds, CancellationToken ct = default)
    {
        var visible = await VisibleAsync(tenantId, ct);

        if (requestedNamesOrIds is { Count: > 0 })
        {
            var selected = new List<ScopedCorpus>();
            var unknown = new List<string>();

            foreach (var requested in requestedNamesOrIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // `books` is the corpus's default set; `books:fine` names one explicitly.
                // Qualifying the name rather than adding a parameter keeps the MCP surface
                // as wide as it was; see D-11 on why the tool count is a budget.
                var (corpusPart, setPart) = Split(requested);

                var match = visible.FirstOrDefault(c =>
                    string.Equals(c.Name, corpusPart, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Id, corpusPart, StringComparison.Ordinal));

                if (match is null) { unknown.Add(requested); continue; }

                var set = setPart is null
                    ? DefaultSetOf(match)
                    : match.ChunkSets.FirstOrDefault(s =>
                        string.Equals(s.Name, setPart, StringComparison.OrdinalIgnoreCase));

                if (set is null)
                {
                    var sets = match.ChunkSets.Select(s => s.Name).Order(StringComparer.Ordinal).ToList();
                    throw new ScopeResolutionException(
                        $"Corpus '{match.Name}' has no chunk set named '{setPart}'. " +
                        (sets.Count == 0
                            ? "It has no chunk sets at all, which means nothing is indexed."
                            : $"Its sets: {string.Join(", ", sets)}."),
                        visible.Select(c => c.Name).ToList());
                }

                if (!selected.Exists(s => s.Set.Id == set.Id)) selected.Add(new ScopedCorpus(match, set));
            }

            if (unknown.Count > 0)
            {
                var names = visible.Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
                throw new ScopeResolutionException(
                    $"Unknown corpus {string.Join(", ", unknown.Select(u => $"'{u}'"))}. " +
                    (names.Count == 0
                        ? $"Tenant '{tenantId}' can see no corpora at all."
                        : $"Visible corpora: {string.Join(", ", names)}."),
                    names);
            }

            return new ResolvedScope(selected);
        }

        if (visible.Count == 0)
            throw new ScopeResolutionException(
                $"No corpora are visible to tenant '{tenantId}'. Create one in the UI, " +
                "or check the X-Dexicon-Tenant header.", []);

        // Unqualified scope is every visible corpus at its DEFAULT set. A corpus whose
        // replacement set is still backfilling keeps serving from the live one.
        var all = visible
            .Select(c => (Corpus: c, Set: DefaultSetOf(c)))
            .Where(x => x.Set is not null)
            .Select(x => new ScopedCorpus(x.Corpus, x.Set!))
            .ToList();

        if (all.Count == 0)
            throw new ScopeResolutionException(
                $"Tenant '{tenantId}' can see {visible.Count} " +
                $"{(visible.Count == 1 ? "corpus" : "corpora")}, but none has a chunk set. " +
                "Nothing is indexed yet.", visible.Select(c => c.Name).ToList());

        return new ResolvedScope(all);
    }

    /// <summary>
    /// Splits `corpus:set`. A corpus name cannot contain a colon, so this is unambiguous;
    /// an id cannot either, being a ULID.
    /// </summary>
    private static (string Corpus, string? Set) Split(string nameOrId)
    {
        var i = nameOrId.IndexOf(':');
        return i < 0 ? (nameOrId, null) : (nameOrId[..i], nameOrId[(i + 1)..]);
    }

    /// <summary>
    /// The set marked default, or the only one, or the oldest. The fallbacks matter: a
    /// corpus with no default flag set must still be searchable rather than invisible.
    /// </summary>
    private static ChunkSet? DefaultSetOf(Corpus c) =>
        c.ChunkSets.FirstOrDefault(s => s.IsDefault)
        ?? c.ChunkSets.OrderBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// Owned by the tenant, plus shared corpora granted to it, plus shared corpora with
    /// no grants at all (which means "shared with everyone").
    /// </summary>
    public async Task<List<Corpus>> VisibleAsync(string tenantId, CancellationToken ct = default)
    {
        var owned = await db.Corpora
            .Include(c => c.ChunkSets)
            .Where(c => c.TenantId == tenantId).ToListAsync(ct);

        var shared = await db.Corpora
            .Include(c => c.Grants)
            .Include(c => c.ChunkSets)
            .Where(c => c.TenantId != tenantId && c.Visibility == CorpusVisibility.Shared)
            .ToListAsync(ct);

        var readable = shared.Where(c => c.Grants.Count == 0 || c.Grants.Exists(g => g.TenantId == tenantId));

        return owned.Concat(readable).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Writes are always owner-only, whatever the visibility. Sharing is read-only
    /// sharing and there is no setting that changes that.
    /// </summary>
    public async Task<Corpus> ResolveWritableAsync(string tenantId, string nameOrId, CancellationToken ct = default)
    {
        var corpus = await db.Corpora.FirstOrDefaultAsync(
            c => c.TenantId == tenantId && (c.Name == nameOrId || c.Id == nameOrId), ct);

        if (corpus is not null) return corpus;

        var visible = await VisibleAsync(tenantId, ct);
        var sharedMatch = visible.FirstOrDefault(c =>
            string.Equals(c.Name, nameOrId, StringComparison.OrdinalIgnoreCase) || c.Id == nameOrId);

        throw new ScopeResolutionException(
            sharedMatch is not null
                ? $"Corpus '{nameOrId}' is shared with tenant '{tenantId}' but owned by '{sharedMatch.TenantId}'. " +
                  "Sharing grants read access only."
                : $"Tenant '{tenantId}' owns no corpus named '{nameOrId}'.",
            visible.Select(c => c.Name).ToList());
    }
}
