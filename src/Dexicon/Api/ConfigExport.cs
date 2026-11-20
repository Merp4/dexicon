using System.Text.Json;
using System.Text.Json.Serialization;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Api;

/// <summary>
/// The configuration of this instance as a file (D-53): what a person would have to set again to rebuild it.
///
/// It holds the corpora with their filters, sources and chunk sets, the saved model profiles, and the keys' names,
/// scopes and reach. It names everything by name and holds none of the instance's state: no id, no count, no job, no
/// file, no vector and no secret. A key is here without its secret, because the secret exists only as a hash.
/// </summary>
/// <param name="Format">Always <c>dexicon-config</c>, so a file can be told from another JSON document.</param>
/// <param name="Version">The version of this layout. A change that a reader of version 1 would misread raises it.</param>
/// <param name="ExportedUtc">When the file was made. The only field that differs between two exports of one state.</param>
/// <param name="DexiconVersion">The build that wrote it.</param>
/// <param name="Omitted">What this instance holds that the file does not, one sentence each: a reader is not left to find out.</param>
public sealed record ConfigExport(
    string Format,
    int Version,
    DateTime ExportedUtc,
    string DexiconVersion,
    IReadOnlyList<ExportedCorpus> Corpora,
    IReadOnlyList<ExportedModelProfile> ModelProfiles,
    IReadOnlyList<ExportedKey> Keys,
    IReadOnlyList<string> Omitted);

/// <param name="Defaults">The filters every source inherits unless it sets its own. Null for each one the corpus does not set.</param>
public sealed record ExportedCorpus(
    string Name,
    string? Description,
    ExportedFilters Defaults,
    IReadOnlyList<ExportedSource> Sources,
    IReadOnlyList<ExportedChunkSet> ChunkSets);

/// <summary>
/// Filters as a corpus or a source sets them. A field that is left out inherits, and an empty list is a filter that
/// selects nothing or excludes nothing: the two are different, which is why a null list is written as no property.
/// </summary>
public sealed record ExportedFilters(
    IReadOnlyList<string>? IncludeGlobs,
    IReadOnlyList<string>? ExcludeGlobs,
    bool? UseGitignore,
    int? MaxFileBytes);

/// <param name="Kind"><c>workspace</c> or <c>githistory</c>. An upload source is not exported: see <see cref="ConfigExport.Omitted"/>.</param>
/// <param name="Filters">What the source sets for itself. What it inherits from its corpus is not repeated.</param>
public sealed record ExportedSource(
    string Kind,
    string? RootPath,
    ExportedFilters Filters,
    GitHistoryOptions? Git);

public sealed record ExportedChunkSet(
    string Name,
    string? Description,
    string EmbeddingProvider,
    string EmbeddingModel,
    int ChunkSize,
    int ChunkOverlap,
    string BoundaryMode,
    string? CustomBoundaryPattern,
    bool UnitAware,
    bool SentenceAware,
    bool HeadingContext,
    bool IsDefault);

public sealed record ExportedModelProfile(
    string Provider,
    string Model,
    string DocumentTemplate,
    string QueryTemplate,
    string? Notes);

/// <summary>A key without its secret. A revoked key is not exported; one that has expired is.</summary>
/// <param name="Corpora">The corpus names the key reaches. Empty, with <paramref name="AllCorpora"/> false, reaches nothing.</param>
public sealed record ExportedKey(
    string Name,
    IReadOnlyList<string> Scopes,
    bool AllCorpora,
    IReadOnlyList<string> Corpora,
    DateTime? ExpiresUtc);

public static class ConfigEndpoints
{
    public const string FormatName = "dexicon-config";
    public const int FormatVersion = 1;

    /// <summary>
    /// How the file is written: indented, because it is read in a diff, with properties in declaration order and none
    /// written for a null, so two exports of one state are the same text.
    /// </summary>
    internal static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapConfigEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/config/export", ExportAsync).Produces<ConfigExport>().WithTags("Config");

    internal static async Task<IResult> ExportAsync(
        HttpContext http, RequestContext rc, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

        var now = clock.GetUtcNow().UtcDateTime;
        var export = await BuildAsync(db, now, ThisAssembly.Version, ct);

        http.Response.Headers.ContentDisposition = $"attachment; filename=\"dexicon-config-{now:yyyy-MM-dd}.json\"";
        return Results.Json(export, FileJson);
    }

    /// <summary>
    /// Reads the catalogue and builds the file. Everything is ordered by name, ignoring case, so the order does not
    /// depend on how the rows were created.
    /// </summary>
    internal static async Task<ConfigExport> BuildAsync(CatalogDbContext db, DateTime now, string version, CancellationToken ct)
    {
        var corpora = await db.Corpora.AsNoTracking()
            .Include(c => c.Sources)
            .Include(c => c.ChunkSets)
            .ToListAsync(ct);
        var profiles = await db.ModelProfiles.AsNoTracking().ToListAsync(ct);
        var keys = await db.Tokens.AsNoTracking()
            .Include(t => t.Corpora).ThenInclude(tc => tc.Corpus)
            .Where(t => t.RevokedUtc == null)
            .ToListAsync(ct);

        var omitted = new List<string>();
        var exported = new List<ExportedCorpus>();

        foreach (var corpus in corpora.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var uploads = corpus.Sources.Count(s => s.Kind == SourceKind.Upload);
            if (uploads > 0)
                omitted.Add($"Corpus '{corpus.Name}' holds uploaded documents. Their content and their attachment are not in this file.");

            exported.Add(new ExportedCorpus(
                corpus.Name,
                corpus.Description,
                new ExportedFilters(
                    SourceFilters.Globs(corpus.DefaultIncludeGlobs), SourceFilters.Globs(corpus.DefaultExcludeGlobs), corpus.DefaultUseGitignore, corpus.DefaultMaxFileBytes),
                [.. corpus.Sources
                    .Where(s => s.Kind != SourceKind.Upload)
                    .OrderBy(s => s.Kind)
                    .ThenBy(s => s.RootPath, StringComparer.OrdinalIgnoreCase)
                    .Select(ToExported)],
                [.. corpus.ChunkSets
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(ToExported)]));
        }

        return new ConfigExport(
            FormatName,
            FormatVersion,
            now,
            version,
            exported,
            [.. profiles
                .OrderBy(p => p.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Model, StringComparer.OrdinalIgnoreCase)
                .Select(p => new ExportedModelProfile(p.Provider, p.Model, p.DocumentTemplate, p.QueryTemplate, p.Notes))],
            [.. keys
                .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                .Select(k => new ExportedKey(
                    k.Name,
                    [.. k.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Order(StringComparer.Ordinal)],
                    k.AllCorpora,
                    [.. k.Corpora.Select(tc => tc.Corpus!.Name).Order(StringComparer.OrdinalIgnoreCase)],
                    k.ExpiresUtc))],
            omitted);
    }

    private static ExportedSource ToExported(Source s) =>
        new(s.Kind.ToString().ToLowerInvariant(),
            s.RootPath,
            new ExportedFilters(SourceFilters.Globs(s.IncludeGlobs), SourceFilters.Globs(s.ExcludeGlobs), s.UseGitignore, s.MaxFileBytes),
            s.Kind == SourceKind.GitHistory ? GitHistoryOptions.FromJson(s.GitOptions) : null);

    private static ExportedChunkSet ToExported(ChunkSet c) =>
        new(c.Name, c.Description, c.EmbeddingProvider, c.EmbeddingModel, c.ChunkSize, c.ChunkOverlap, c.BoundaryMode,
            c.CustomBoundaryPattern, c.UnitAware, c.SentenceAware, c.HeadingContext, c.IsDefault);
}
