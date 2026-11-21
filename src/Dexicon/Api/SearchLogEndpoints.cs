using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Search;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

/// <summary>
/// The record of searches that ran (D-52). Admin only: it holds what every key searched for, so a key,
/// including one with every scope but admin, cannot read another's.
/// </summary>
public static class SearchLogEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static void MapSearchLogEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/searches", ListAsync).Produces<SearchLogPage>().WithTags("Searches");

    /// <summary>A LIKE pattern that matches rows holding the text anywhere, with the characters LIKE treats specially taken literally.</summary>
    private static string Containing(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    /// <summary>
    /// Newest first, a page at a time. Every filter is applied in the query, so a page is always full of rows that match
    /// and the count says how many do, and the order is the row id, which no two rows share, so a page boundary
    /// cannot repeat or skip a row.
    /// </summary>
    internal static async Task<IResult> ListAsync(
        int? limit, long? before, string? key, string? corpus, string? surface, bool? noResults,
        RequestContext rc, CatalogDbContext db, SearchLog log, IOptions<DexiconOptions> options, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

        // By name only. Enum.TryParse also takes a number or a comma list ("1,2" is 3), which would filter on a surface
        // the caller did not name while the error message says only names are accepted.
        SearchSurface? onlySurface = null;
        if (!string.IsNullOrEmpty(surface))
        {
            var names = Enum.GetNames<SearchSurface>();
            var match = names.FirstOrDefault(n => string.Equals(n, surface.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return Results.Problem(title: "Unknown surface",
                    detail: $"Expected {string.Join(", ", names.Select(n => n.ToLowerInvariant()))}.",
                    statusCode: 400);
            onlySurface = Enum.Parse<SearchSurface>(match);
        }

        var matching = db.SearchLog.AsNoTracking().AsQueryable();
        // Part of the name, ignoring case, for both: a key shows as "bootstrap (pinned)" and a corpus column holds a list. The text
        // is matched literally, so a % or _ typed into the box is not a wildcard.
        if (!string.IsNullOrWhiteSpace(key)) matching = matching.Where(r => EF.Functions.Like(r.TokenName, Containing(key.Trim()), "\\"));
        if (!string.IsNullOrWhiteSpace(corpus)) matching = matching.Where(r => EF.Functions.Like(r.Corpora, Containing(corpus.Trim()), "\\"));
        if (onlySurface is { } only) matching = matching.Where(r => r.Surface == only);
        if (noResults == true) matching = matching.Where(r => r.Hits == 0);

        var count = await matching.CountAsync(ct);
        var oldest = await db.SearchLog.MinAsync(r => (DateTime?)r.AtUtc, ct);

        var paged = before is { } after ? matching.Where(r => r.Id < after) : matching;
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // One more than asked for, to learn whether another page exists without a second query.
        var rows = await paged.OrderByDescending(r => r.Id).Take(take + 1).ToListAsync(ct);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(take);

        var settings = options.Value.SearchLog;
        return Results.Ok(new SearchLogPage(
            [.. rows.Select(r => new SearchLogItem(
                r.Id, r.AtUtc, r.TokenName, r.Surface, r.Corpora, r.Mode, r.Query, r.QueryChars,
                r.Hits, r.TopScore, r.TookMs, r.Degraded, r.Noted))],
            more ? rows[^1].Id : null,
            count,
            oldest,
            new SearchLogSettings(settings.Enabled, settings.StoreQueryText, settings.RetentionDays, log.Dropped)));
    }
}
