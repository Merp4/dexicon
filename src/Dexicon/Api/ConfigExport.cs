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
/// <param name="Omitted">
/// What this instance holds that the file does not, one sentence each: a corpus's uploaded documents, and a stored setting
/// that could not be read. A reader is not left to find out.
/// </param>
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
/// Filters as a corpus or a source sets them. A property that is null inherits, and an empty list is a filter that
/// selects nothing or excludes nothing: the two are different, and both are written, null as <c>null</c> and the empty
/// list as <c>[]</c>.
/// </summary>
public sealed record ExportedFilters(
    IReadOnlyList<string>? IncludeGlobs,
    IReadOnlyList<string>? ExcludeGlobs,
    bool? UseGitignore,
    int? MaxFileBytes);

/// <param name="Kind"><c>workspace</c> or <c>githistory</c>. An upload source is not exported: see <see cref="ConfigExport.Omitted"/>.</param>
/// <param name="Filters">What the source sets for itself. What it inherits from its corpus is not repeated.</param>
/// <param name="Git">A history source's options. Null for a workspace source.</param>
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
    /// How the file is written: indented, because it is read in a diff, with properties in declaration order and every
    /// property written, a null as <c>null</c>, so the file says what the OpenAPI document says and a reader can tell
    /// "inherits" from "none". Two exports of one state are the same text.
    /// </summary>
    internal static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
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

        // Key names, scopes and reach and every workspace path: not for a cache to keep.
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(export, FileJson);
    }

    /// <summary>
    /// Reads the catalogue and builds the file. Everything is ordered by name ignoring case, and rows that tie are ordered
    /// by how they are written, so the order depends neither on how the rows were created nor on what order the database
    /// returned them in.
    /// </summary>
    internal static async Task<ConfigExport> BuildAsync(CatalogDbContext db, DateTime now, string version, CancellationToken ct)
    {
        // A split query: one joined statement would return every source once for each chunk set of its corpus.
        var corpora = await db.Corpora.AsNoTracking()
            .Include(c => c.Sources)
            .Include(c => c.ChunkSets)
            .AsSplitQuery()
            .ToListAsync(ct);
        var profiles = await db.ModelProfiles.AsNoTracking().ToListAsync(ct);
        var keys = await db.Tokens.AsNoTracking()
            .Include(t => t.Corpora).ThenInclude(tc => tc.Corpus)
            .Where(t => t.RevokedUtc == null)
            .ToListAsync(ct);

        // Each corpus is built with its own notes, so that the notes follow the corpora's order.
        var built = corpora.Select(Build).OrderBy(b => b.Corpus.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => Written(b.Corpus), StringComparer.Ordinal).ToList();

        return new ConfigExport(
            FormatName,
            FormatVersion,
            now,
            version,
            [.. built.Select(b => b.Corpus)],
            [.. profiles
                .Select(p => new ExportedModelProfile(p.Provider, p.Model, p.DocumentTemplate, p.QueryTemplate, p.Notes))
                .OrderBy(p => p.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(Written, StringComparer.Ordinal)],
            [.. keys
                .Select(k => new ExportedKey(
                    k.Name,
                    [.. k.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Order(StringComparer.Ordinal)],
                    k.AllCorpora,
                    [.. k.Corpora.Select(tc => tc.Corpus!.Name).Order(StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal)],
                    k.ExpiresUtc))
                .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(Written, StringComparer.Ordinal)],
            [.. built.SelectMany(b => b.Notes.Order(StringComparer.Ordinal))]);
    }

    private static string Written<T>(T value) => JsonSerializer.Serialize(value, FileJson);

    private static (ExportedCorpus Corpus, List<string> Notes) Build(Corpus corpus)
    {
        var notes = new List<string>();

        if (corpus.Sources.Any(s => s.Kind == SourceKind.Upload))
            notes.Add($"Corpus '{corpus.Name}' holds uploaded documents. Their content and their attachment are not in this file.");

        // A stored list that will not parse reads as "no opinion" everywhere else (SourceFilters.Globs), so the file says
        // when it wrote one as unset for that reason.
        IReadOnlyList<string>? Globs(string? stored, string what)
        {
            var globs = SourceFilters.Globs(stored);
            if (stored is not null && globs is null)
                notes.Add($"{what} of corpus '{corpus.Name}' could not be read from its stored form, and is written as unset.");
            return globs;
        }

        var sources = new List<ExportedSource>();
        foreach (var s in corpus.Sources.Where(s => s.Kind != SourceKind.Upload))
        {
            var source = $"{s.Kind.ToString().ToLowerInvariant()} source '{s.RootPath}'";
            GitHistoryOptions? git = null;

            if (s.Kind == SourceKind.GitHistory)
            {
                try
                {
                    git = GitHistoryOptions.FromJson(s.GitOptions);
                }
                catch (JsonException)
                {
                    notes.Add($"The {source} of corpus '{corpus.Name}' has stored options that could not be read, and is not in this file.");
                    continue;
                }
            }

            sources.Add(new ExportedSource(
                s.Kind.ToString().ToLowerInvariant(),
                s.RootPath,
                new ExportedFilters(
                    Globs(s.IncludeGlobs, $"The include globs of the {source}"),
                    Globs(s.ExcludeGlobs, $"The exclude globs of the {source}"),
                    s.UseGitignore,
                    s.MaxFileBytes),
                git));
        }

        var exported = new ExportedCorpus(
            corpus.Name,
            corpus.Description,
            new ExportedFilters(
                Globs(corpus.DefaultIncludeGlobs, "The default include globs"),
                Globs(corpus.DefaultExcludeGlobs, "The default exclude globs"),
                corpus.DefaultUseGitignore,
                corpus.DefaultMaxFileBytes),
            [.. sources
                .OrderBy(s => s.Kind == "workspace" ? 0 : 1)
                .ThenBy(s => s.RootPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(Written, StringComparer.Ordinal)],
            [.. corpus.ChunkSets
                .Select(ToExported)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(Written, StringComparer.Ordinal)]);

        return (exported, notes);
    }

    private static ExportedChunkSet ToExported(ChunkSet c) =>
        new(c.Name, c.Description, c.EmbeddingProvider, c.EmbeddingModel, c.ChunkSize, c.ChunkOverlap, c.BoundaryMode,
            c.CustomBoundaryPattern, c.UnitAware, c.SentenceAware, c.HeadingContext, c.IsDefault);
}
