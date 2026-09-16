using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Auth;

/// <summary>
/// Raised when a caller's authorised corpus set would be empty, or when they named a
/// corpus they cannot see. Never degraded into "search everything" and never silently
/// narrowed — a caller who asked for a corpus they cannot read gets a named error,
/// because silently dropping it returns confidently incomplete results.
/// </summary>
public sealed class ScopeResolutionException(string message, IReadOnlyList<string> visibleNames)
    : Exception(message)
{
    public IReadOnlyList<string> VisibleNames { get; } = visibleNames;
}

public sealed record ResolvedScope(IReadOnlyList<Corpus> Corpora)
{
    public IReadOnlyList<string> Ids => Corpora.Select(c => c.Id).ToList();

    /// <summary>
    /// All corpora in a scope must share a collection, because a collection is one
    /// vector space. Mixed models are split and searched per collection.
    /// </summary>
    public IEnumerable<IGrouping<string, Corpus>> ByCollection =>
        Corpora.GroupBy(c => c.CollectionName, StringComparer.Ordinal);
}

/// <summary>
/// The authorization boundary. One function, one place, called by every read path.
/// See docs/07-tenancy-auth.md — this is the application-level guard; the repository
/// refuses an unfiltered query, and the storage layout makes one useless.
/// </summary>
public sealed class ScopeResolver(CatalogDbContext db)
{
    public async Task<ResolvedScope> ResolveReadableAsync(
        string tenantId, IReadOnlyList<string>? requestedNamesOrIds, CancellationToken ct = default)
    {
        var visible = await VisibleAsync(tenantId, ct);

        if (requestedNamesOrIds is { Count: > 0 })
        {
            var selected = new List<Corpus>();
            var unknown = new List<string>();

            foreach (var requested in requestedNamesOrIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var match = visible.FirstOrDefault(c =>
                    string.Equals(c.Name, requested, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Id, requested, StringComparison.Ordinal));

                if (match is null) unknown.Add(requested);
                else if (!selected.Contains(match)) selected.Add(match);
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

        return new ResolvedScope(visible);
    }

    /// <summary>
    /// Owned by the tenant, plus shared corpora granted to it, plus shared corpora with
    /// no grants at all (which means "shared with everyone").
    /// </summary>
    public async Task<List<Corpus>> VisibleAsync(string tenantId, CancellationToken ct = default)
    {
        var owned = await db.Corpora.Where(c => c.TenantId == tenantId).ToListAsync(ct);

        var shared = await db.Corpora
            .Include(c => c.Grants)
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
