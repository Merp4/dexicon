using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Api;

/// <summary>
/// What an import did, or with <c>apply</c> left off what it would do (D-54). One line for each thing in the file, and
/// for each thing here that the file does not name.
/// </summary>
/// <param name="Applied">False for a dry run, which changes nothing.</param>
public sealed record ConfigImportResult(bool Applied, IReadOnlyList<ImportItem> Items, IReadOnlyDictionary<string, int> Counts);

/// <param name="Kind"><c>corpus</c>, <c>source</c>, <c>chunk set</c>, <c>model profile</c> or <c>key</c>.</param>
/// <param name="Name">What it is called: a corpus name, a source's root path, a chunk set, profile or key name.</param>
/// <param name="Outcome">
/// <c>create</c> and <c>update</c> (made, or with a dry run, would be), <c>unchanged</c>, <c>not imported</c> (the file holds it and
/// import does not read it), <c>extra</c> (here and not in the file, and left alone), <c>refused</c> (the rules that govern a
/// change turned it down) and <c>failed</c> (something other than a rule stopped it).
/// </param>
/// <param name="Detail">What changes, or why it was not.</param>
public sealed record ImportItem(string Kind, string Name, string Outcome, string? Detail);

/// <summary>
/// Reads a configuration file (<see cref="ConfigExport"/>) into this instance through <see cref="CorpusConfiguration"/>, so a
/// change is judged by the rules the screens and the agents' tools are judged by.
///
/// It adds and updates and never deletes. A corpus is matched by name ignoring case, and a source by its kind and root. Each
/// is its own step: one that is refused or fails is reported and the rest go on.
///
/// It does not read chunk sets beyond the one a new corpus is born with, keys or model profiles, and says so for each.
/// </summary>
internal sealed class ConfigImporter(CatalogDbContext db, CorpusConfiguration config, ILogger<ConfigImporter> log)
{
    public async Task<ConfigImportResult> RunAsync(ConfigExport file, bool apply, CancellationToken ct)
    {
        var items = new List<ImportItem>();
        var existing = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets).AsSplitQuery().ToListAsync(ct);
        var corpora = (file.Corpora ?? []).Select(Normalised).ToList();

        foreach (var wanted in corpora)
        {
            var corpus = existing.Find(c => string.Equals(c.Name, wanted.Name, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (corpus is null) corpus = await CreateAsync(wanted, apply, items, ct);
                else await UpdateAsync(corpus, wanted, apply, items, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not a rule: a failure of the machinery. It is said as one, never as a refusal.
                log.LogWarning(ex, "Configuration import stopped on corpus {Corpus}", DexiconAuthMiddleware.OneLine(wanted.Name));
                items.Add(new ImportItem("corpus", wanted.Name, "failed", "An error stopped this corpus. It is in the log; the corpus may be partly changed."));
                continue;
            }

            if (corpus is not null) Extras(corpus, wanted, items);
        }

        var named = corpora.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var here in existing.Where(c => !named.Contains(c.Name)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            items.Add(new ImportItem("corpus", here.Name, "extra", "Here and not in the file. Import does not remove it."));

        foreach (var profile in file.ModelProfiles ?? [])
            items.Add(new ImportItem("model profile", $"{profile.Provider}/{profile.Model}", "not imported", "Model profiles are not imported."));
        foreach (var key in file.Keys ?? [])
            items.Add(new ImportItem("key", key.Name, "not imported", "Keys are not imported."));

        return new ConfigImportResult(
            apply,
            items,
            items.GroupBy(i => i.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
    }

    // ---------------------------------------------------------------------------------------------------- new corpus

    private async Task<Corpus?> CreateAsync(ExportedCorpus wanted, bool apply, List<ImportItem> items, CancellationToken ct)
    {
        var set = wanted.ChunkSets.FirstOrDefault(s => s.IsDefault) ?? wanted.ChunkSets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (set is null)
        {
            items.Add(new ImportItem("corpus", wanted.Name, "refused", "The file has no chunk set for it, and a corpus is made with one."));
            return null;
        }

        var born = $"made with a chunk set named 'default' from the file's '{set.Name}' ({set.EmbeddingProvider}/{set.EmbeddingModel}, "
                   + $"{set.ChunkSize} tokens, overlap {set.ChunkOverlap}, {set.BoundaryMode})";

        if (!apply)
        {
            items.Add(new ImportItem("corpus", wanted.Name, "create", $"Would be {born}. Applying probes the embedding model first."));
            foreach (var source in Sorted(wanted)) items.Add(new ImportItem("source", Label(wanted, source), "create", "Would be added, and a refresh queued."));
            SetItems(wanted, set, created: true, existing: null, items);
            return null;
        }

        var made = await config.CreateCorpusAsync(
            new CreateCorpusRequest(wanted.Name, wanted.Description, set.EmbeddingProvider, set.EmbeddingModel, set.ChunkSize, set.ChunkOverlap, set.BoundaryMode),
            ct,
            defaults: DefaultsOf(wanted.Defaults));
        if (made.Refusal is { } refusal)
        {
            items.Add(Refused("corpus", wanted.Name, refusal));
            return null;
        }

        var corpus = made.Value!;
        items.Add(new ImportItem("corpus", wanted.Name, "create", $"Made, {born}."));
        foreach (var source in Sorted(wanted)) await AddSourceAsync(corpus, wanted, source, items, ct);
        SetItems(wanted, set, created: true, existing: null, items);
        return corpus;
    }

    // ------------------------------------------------------------------------------------------------ existing corpus

    private async Task UpdateAsync(Corpus corpus, ExportedCorpus wanted, bool apply, List<ImportItem> items, CancellationToken ct)
    {
        var defaultsDiffer = !SameFilters(wanted.Defaults, corpus.DefaultsAsFilters());
        var descriptionDiffers = wanted.Description is not null && wanted.Description != corpus.Description;
        var changes = new List<string>();
        if (descriptionDiffers) changes.Add("the description");
        if (defaultsDiffer) changes.Add("the default filters (a refresh is queued)");

        if (changes.Count == 0)
            items.Add(new ImportItem("corpus", corpus.Name, "unchanged", wanted.Description is null && corpus.Description is not null
                ? "The file has no description, and the one here is kept."
                : null));
        else if (!apply)
            items.Add(new ImportItem("corpus", corpus.Name, "update", $"Would change {string.Join(" and ", changes)}."));
        else
        {
            var outcome = await config.UpdateCorpusAsync(
                corpus,
                new UpdateCorpusRequest(descriptionDiffers ? wanted.Description : null, defaultsDiffer ? DefaultsOf(wanted.Defaults) : null),
                ct);
            items.Add(outcome.Refusal is { } refusal
                ? Refused("corpus", corpus.Name, refusal)
                : new ImportItem("corpus", corpus.Name, "update", $"Changed {string.Join(" and ", changes)}."));
        }

        foreach (var source in Sorted(wanted))
        {
            var here = corpus.Sources.FirstOrDefault(s => s.Kind != SourceKind.Upload && SameSource(s, source));
            if (here is null)
            {
                if (apply) await AddSourceAsync(corpus, wanted, source, items, ct);
                else items.Add(new ImportItem("source", Label(wanted, source), "create", "Would be added, and a refresh queued."));
                continue;
            }

            await UpdateSourceAsync(corpus, wanted, here, source, apply, items, ct);
        }

        SetItems(wanted, set: null, created: false, existing: corpus, items);
    }

    private async Task AddSourceAsync(Corpus corpus, ExportedCorpus wanted, ExportedSource source, List<ImportItem> items, CancellationToken ct)
    {
        var added = await config.AddSourceAsync(
            corpus,
            new AddSourceRequest(
                source.RootPath ?? string.Empty,
                source.Filters.UseGitignore,
                source.Filters.MaxFileBytes,
                source.Filters.IncludeGlobs,
                source.Filters.ExcludeGlobs,
                GitHistory: source.Kind == "githistory",
                Git: source.Git),
            ct);

        items.Add(added.Refusal is { } refusal
            ? Refused("source", Label(wanted, source), refusal)
            : new ImportItem("source", Label(wanted, source), "create", "Added, and a refresh queued."));
    }

    private async Task UpdateSourceAsync(
        Corpus corpus, ExportedCorpus wanted, Source here, ExportedSource source, bool apply, List<ImportItem> items, CancellationToken ct)
    {
        var name = Label(wanted, source);
        var current = here.ToFilters();
        var differs = !SameFilters(source.Filters, current)
                      || (source.Kind == "githistory" && source.Git is not null
                          && !string.Equals(source.Git.ToJson(), GitHistoryOptions.FromJson(here.GitOptions).ToJson(), StringComparison.Ordinal));

        if (!differs)
        {
            items.Add(new ImportItem("source", name, "unchanged", null));
            return;
        }

        if (!apply)
        {
            items.Add(new ImportItem("source", name, "update", "Would change its filters or options, and queue a refresh."));
            return;
        }

        // A value the file leaves unset and this source sets is cleared, so it inherits again, which is what the file says.
        var clear = new List<string>();
        if (source.Filters.UseGitignore is null && here.UseGitignore is not null) clear.Add("useGitignore");
        if (source.Filters.MaxFileBytes is null && here.MaxFileBytes is not null) clear.Add("maxFileBytes");
        if (source.Filters.IncludeGlobs is null && here.IncludeGlobs is not null) clear.Add("includeGlobs");
        if (source.Filters.ExcludeGlobs is null && here.ExcludeGlobs is not null) clear.Add("excludeGlobs");

        var updated = await config.UpdateSourceAsync(
            corpus,
            here.Id,
            new UpdateSourceRequest(
                source.Filters.UseGitignore,
                source.Filters.MaxFileBytes,
                source.Filters.IncludeGlobs,
                source.Filters.ExcludeGlobs,
                clear.Count > 0 ? clear : null,
                source.Kind == "githistory" ? source.Git : null),
            ct);

        items.Add(updated.Refusal is { } refusal
            ? Refused("source", name, refusal)
            : new ImportItem("source", name, "update", "Changed its filters or options" + (updated.Value!.IndexJob is not null ? ", and a refresh was queued." : ".")));
    }

    // ------------------------------------------------------------------------------------------------- what is left

    /// <summary>Sources of a corpus here that the file does not name.</summary>
    private static void Extras(Corpus corpus, ExportedCorpus wanted, List<ImportItem> items)
    {
        foreach (var here in corpus.Sources.Where(s => s.Kind != SourceKind.Upload && !wanted.Sources.Any(w => SameSource(s, w)))
                     .OrderBy(s => s.RootPath, StringComparer.OrdinalIgnoreCase))
            items.Add(new ImportItem("source", $"{corpus.Name}: {here.Kind.ToString().ToLowerInvariant()} {here.RootPath}", "extra",
                "Here and not in the file. Import does not remove it."));
    }

    /// <summary>
    /// A file's chunk sets. A new corpus is made with one, which the corpus item says. Beyond that import does not read them: a
    /// set it finds here is compared, and one it does not is left for the corpus's page, where adding one starts a backfill.
    /// </summary>
    private static void SetItems(ExportedCorpus wanted, ExportedChunkSet? set, bool created, Corpus? existing, List<ImportItem> items)
    {
        foreach (var s in wanted.ChunkSets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var label = $"{wanted.Name}: {s.Name}";

            if (created && ReferenceEquals(s, set))
            {
                var unapplied = new List<string>();
                if (s.CustomBoundaryPattern is not null) unapplied.Add("the boundary pattern");
                if (s.UnitAware) unapplied.Add("unitAware");
                if (s.SentenceAware) unapplied.Add("sentenceAware");
                if (s.HeadingContext) unapplied.Add("headingContext");
                items.Add(new ImportItem("chunk set", label, unapplied.Count == 0 ? "create" : "not imported",
                    unapplied.Count == 0
                        ? "Made with the corpus."
                        : $"Made with the corpus without {string.Join(", ", unapplied)}, which a corpus is not made with. Set them on the chunk set."));
                continue;
            }

            var here = existing?.ChunkSets.FirstOrDefault(h => string.Equals(h.Name, s.Name, StringComparison.OrdinalIgnoreCase));
            items.Add(here is null
                ? new ImportItem("chunk set", label, "not imported", "Adding a chunk set starts a backfill, so it is added from the corpus's page.")
                : SameSet(here, s)
                    ? new ImportItem("chunk set", label, "unchanged", null)
                    : new ImportItem("chunk set", label, "not imported", "It differs from the set here, and import does not change a chunk set."));
        }
    }

    // ----------------------------------------------------------------------------------------------------- helpers

    private static IEnumerable<ExportedSource> Sorted(ExportedCorpus corpus) => corpus.Sources;

    /// <summary>A file can leave a list or a filter block out, and a reader of it should not have to ask each time.</summary>
    private static ExportedCorpus Normalised(ExportedCorpus c) => c with
    {
        Defaults = c.Defaults ?? new ExportedFilters(null, null, null, null),
        Sources = [.. (c.Sources ?? []).Select(s => s with { Filters = s.Filters ?? new ExportedFilters(null, null, null, null) })],
        ChunkSets = c.ChunkSets ?? [],
    };

    private static string Label(ExportedCorpus corpus, ExportedSource source) => $"{corpus.Name}: {source.Kind} {source.RootPath}";

    private static ImportItem Refused(string kind, string name, ConfigRefusal refusal) =>
        new(kind, name, "refused", string.Equals(refusal.Detail, refusal.Title, StringComparison.Ordinal) ? refusal.Title : $"{refusal.Title}: {refusal.Detail}");

    /// <summary>The four settings as the create and update requests take them, or null when the file sets none of them.</summary>
    private static CorpusDefaults? DefaultsOf(ExportedFilters f) =>
        f is { IncludeGlobs: null, ExcludeGlobs: null, UseGitignore: null, MaxFileBytes: null }
            ? null
            : new CorpusDefaults(f.UseGitignore, f.MaxFileBytes, f.IncludeGlobs, f.ExcludeGlobs);

    private static bool SameSource(Source here, ExportedSource wanted) =>
        string.Equals(here.Kind.ToString(), wanted.Kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Root(here.RootPath), Root(wanted.RootPath), StringComparison.OrdinalIgnoreCase);

    private static string Root(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');

    private static bool SameFilters(ExportedFilters? a, ExportedFilters? b) =>
        a is null || b is null
            ? a is null && b is null
            : SameList(a.IncludeGlobs, b.IncludeGlobs) && SameList(a.ExcludeGlobs, b.ExcludeGlobs)
              && a.UseGitignore == b.UseGitignore && a.MaxFileBytes == b.MaxFileBytes;

    /// <summary>Null is "inherit" and an empty list is "none", so a null is the same as a null and as nothing else.</summary>
    private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

    private static bool SameSet(ChunkSet here, ExportedChunkSet wanted) =>
        string.Equals(here.EmbeddingProvider, wanted.EmbeddingProvider, StringComparison.OrdinalIgnoreCase)
        && string.Equals(here.EmbeddingModel, wanted.EmbeddingModel, StringComparison.OrdinalIgnoreCase)
        && here.ChunkSize == wanted.ChunkSize && here.ChunkOverlap == wanted.ChunkOverlap
        && string.Equals(here.BoundaryMode, wanted.BoundaryMode, StringComparison.Ordinal)
        && here.CustomBoundaryPattern == wanted.CustomBoundaryPattern
        && here.UnitAware == wanted.UnitAware && here.SentenceAware == wanted.SentenceAware && here.HeadingContext == wanted.HeadingContext;
}

/// <summary>How a stored row reads as the file's filter shapes, so a file and a row are compared as one thing.</summary>
internal static class ImportReading
{
    public static ExportedFilters DefaultsAsFilters(this Corpus c) =>
        new(SourceFilters.Globs(c.DefaultIncludeGlobs), SourceFilters.Globs(c.DefaultExcludeGlobs), c.DefaultUseGitignore, c.DefaultMaxFileBytes);

    public static ExportedFilters ToFilters(this Source s) =>
        new(SourceFilters.Globs(s.IncludeGlobs), SourceFilters.Globs(s.ExcludeGlobs), s.UseGitignore, s.MaxFileBytes);
}

public static class ConfigImportEndpoints
{
    public static void MapConfigImportEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/config/import", ImportAsync).Produces<ConfigImportResult>().WithTags("Config");

    /// <summary>
    /// <c>POST /api/config/import</c> with a configuration file as the body. Without <c>apply=true</c> it is a dry run that
    /// changes nothing, so reading the plan is the default and changing the instance is asked for by name.
    /// </summary>
    internal static async Task<IResult> ImportAsync(
        [FromBody] ConfigExport file, bool? apply, RequestContext rc, ConfigImporter importer, CancellationToken ct)
    {
        if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

        if (!string.Equals(file.Format, ConfigEndpoints.FormatName, StringComparison.Ordinal) || file.Version != ConfigEndpoints.FormatVersion)
            return Results.Problem(
                title: "Not a configuration file this version reads",
                detail: $"Expected format '{ConfigEndpoints.FormatName}' version {ConfigEndpoints.FormatVersion}.",
                statusCode: 400);

        if ((file.Corpora ?? []).Any(c => string.IsNullOrWhiteSpace(c.Name)))
            return Results.Problem(title: "A corpus has no name", detail: "Every corpus in the file needs a name.", statusCode: 400);

        var duplicate = (file.Corpora ?? []).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return Results.Problem(
                title: "A corpus is named twice",
                detail: $"The file names a corpus '{DexiconAuthMiddleware.OneLine(duplicate.Key)}' more than once. Names are unique, ignoring case.",
                statusCode: 400);

        return Results.Ok(await importer.RunAsync(file, apply == true, ct));
    }
}
