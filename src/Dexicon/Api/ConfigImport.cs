using System.Diagnostics.CodeAnalysis;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Api;

/// <summary>
/// What an import did, or with <c>apply</c> left off what it would do (D-54). One item for each corpus, source, chunk set,
/// model profile and key in the file, and one for each corpus, and each source of a corpus in the file, that is here and that
/// the file does not name. A corpus's item comes first, then its sources and its chunk sets; items for what is here and not in the
/// file follow.
/// </summary>
/// <param name="Applied">False for a dry run, which changes nothing.</param>
public sealed record ConfigImportResult(bool Applied, IReadOnlyList<ImportItem> Items, IReadOnlyDictionary<string, int> Counts);

/// <param name="Kind"><c>corpus</c>, <c>source</c>, <c>chunk set</c>, <c>model profile</c> or <c>key</c>.</param>
/// <param name="Name">
/// What it is called: a corpus name, <c>corpus: kind root</c> for a source, <c>corpus: name</c> for a chunk set, and the
/// name of a key or <c>provider/model</c> of a profile.
/// </param>
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
/// It adds and updates and never deletes a corpus, source or chunk set. An update that narrows filters still removes the files
/// the filters now exclude from the index, at the refresh it queues. A corpus is matched by name ignoring case, and a source by
/// its kind and its canonical root. Each is its own step: one that is refused or fails is reported and the rest go on.
///
/// It does not read chunk sets beyond the one a new corpus is born with, keys or model profiles, and says so for each.
/// </summary>
internal sealed class ConfigImporter(CatalogDbContext db, CorpusConfiguration config, ILogger<ConfigImporter> log)
{
    private const string FileSource = "workspace";
    private const string HistorySource = "githistory";

    private const string StopsBetweenSteps =
        "Each step is saved whole by CorpusConfiguration, and the import is repeatable: a caller that goes away stops it between "
        + "steps, and applying the file again makes what is missing and reports the rest as unchanged.";

    private const string Narrowing = "A narrower filter removes the files it now excludes from the index at the refresh.";

    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "CreateAsync", Justification = StopsBetweenSteps)]
    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "UpdateAsync", Justification = StopsBetweenSteps)]
    public async Task<ConfigImportResult> RunAsync(ConfigExport file, bool apply, CancellationToken ct)
    {
        var items = new List<ImportItem>();
        var existing = await LoadAsync(ct);
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

                // The context still tracks what the failed step added or changed, and the next corpus's save would write it.
                // Read the catalogue again so every later step starts from what is stored.
                db.ChangeTracker.Clear();
                existing = await LoadAsync(ct);
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
            items.Add(new ImportItem("key", key.Name ?? string.Empty, "not imported", "Keys are not imported."));

        return new ConfigImportResult(
            apply,
            items,
            items.GroupBy(i => i.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
    }

    private Task<List<Corpus>> LoadAsync(CancellationToken ct) =>
        db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets).AsSplitQuery().ToListAsync(ct);

    /// <summary>
    /// Why the whole file is refused before anything is made, or null. A file of another format or version, an entry that is
    /// null, a corpus with no name or named twice (ignoring case and surrounding space), a source with no root or a kind other
    /// than <c>workspace</c> or <c>githistory</c>, and a corpus that lists one source twice (the same kind and canonical root).
    /// </summary>
    public ConfigRefusal? Check(ConfigExport file)
    {
        if (!string.Equals(file.Format, ConfigEndpoints.FormatName, StringComparison.Ordinal) || file.Version != ConfigEndpoints.FormatVersion)
            return new ConfigRefusal(
                "Not a configuration file this version reads",
                $"Expected format '{ConfigEndpoints.FormatName}' version {ConfigEndpoints.FormatVersion}.",
                400);

        var corpora = file.Corpora ?? [];
        if (corpora.Any(c => c is null) || (file.Keys ?? []).Any(k => k is null) || (file.ModelProfiles ?? []).Any(p => p is null))
            return new ConfigRefusal("The file has an empty entry", "A list in the file holds null where an entry should be.", 400);

        if (corpora.Any(c => string.IsNullOrWhiteSpace(c.Name)))
            return new ConfigRefusal("A corpus has no name", "Every corpus in the file needs a name.", 400);

        var duplicate = corpora.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return new ConfigRefusal(
                "A corpus is named twice",
                $"The file names a corpus '{DexiconAuthMiddleware.OneLine(duplicate.Key)}' more than once. Names are unique, ignoring case.",
                400);

        foreach (var corpus in corpora)
        {
            var name = DexiconAuthMiddleware.OneLine(corpus.Name.Trim());
            var sources = corpus.Sources ?? [];
            if (sources.Any(s => s is null) || (corpus.ChunkSets ?? []).Any(s => s is null))
                return new ConfigRefusal("The file has an empty entry", $"A list of corpus '{name}' holds null where an entry should be.", 400);

            foreach (var source in sources)
            {
                if (!string.Equals(source.Kind, FileSource, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(source.Kind, HistorySource, StringComparison.OrdinalIgnoreCase))
                    return new ConfigRefusal(
                        "A source has a kind this version does not read",
                        $"A source of corpus '{name}' has kind '{DexiconAuthMiddleware.OneLine(source.Kind ?? "(none)")}'. The kinds are '{FileSource}' and '{HistorySource}'.",
                        400);

                if (source.RootPath is null)
                    return new ConfigRefusal(
                        "A source has no root path",
                        $"A {source.Kind} source of corpus '{name}' has no rootPath. An empty string is the workspace root.",
                        400);
            }

            var twice = sources
                .GroupBy(s => $"{s.Kind!.ToLowerInvariant()}|{Spelled(s.RootPath)}", WorkspaceDiscovery.PathComparer)
                .FirstOrDefault(g => g.Count() > 1);
            if (twice is not null)
                return new ConfigRefusal(
                    "A source is listed twice",
                    $"Corpus '{name}' lists the {twice.First().Kind} source '{DexiconAuthMiddleware.OneLine(twice.First().RootPath!)}' more than once, "
                    + "under spellings that name one folder.",
                    400);
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------------- new corpus

    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "AddSourceAsync", Justification = StopsBetweenSteps)]
    private async Task<Corpus?> CreateAsync(ExportedCorpus wanted, bool apply, List<ImportItem> items, CancellationToken ct)
    {
        var set = wanted.ChunkSets.FirstOrDefault(s => s.IsDefault) ?? wanted.ChunkSets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (set is null)
        {
            items.Add(new ImportItem("corpus", wanted.Name, "refused", "The file has no chunk set for it, and a corpus is made with one."));
            return null;
        }

        var born = $"with a chunk set named 'default' from the file's '{set.Name}' ({set.EmbeddingProvider}/{set.EmbeddingModel}, "
                   + $"{set.ChunkSize} tokens, overlap {set.ChunkOverlap}, {set.BoundaryMode})";

        if (!apply)
        {
            items.Add(new ImportItem("corpus", wanted.Name, "create", $"Would be made {born}. Applying probes the embedding model first."));
            foreach (var source in wanted.Sources) items.Add(new ImportItem("source", Label(wanted, source), "create", "Would be added, and a refresh queued."));
            SetItems(wanted, newSet: set, apply, existing: null, items);
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
        items.Add(new ImportItem("corpus", wanted.Name, "create", $"Made {born}."));
        foreach (var source in wanted.Sources) await AddSourceAsync(corpus, wanted, source, items, ct);
        SetItems(wanted, newSet: set, apply, existing: null, items);
        return corpus;
    }

    // ------------------------------------------------------------------------------------------------ existing corpus

    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "UpdateSourceAsync", Justification = StopsBetweenSteps)]
    [SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", MessageId = "AddSourceAsync", Justification = StopsBetweenSteps)]
    private async Task UpdateAsync(Corpus corpus, ExportedCorpus wanted, bool apply, List<ImportItem> items, CancellationToken ct)
    {
        var differing = Differing(wanted.Defaults, corpus.DefaultsAsFilters());
        var defaultsDiffer = differing.Count > 0;
        var descriptionDiffers = wanted.Description is not null && wanted.Description != corpus.Description;
        var changes = new List<string>();
        if (descriptionDiffers) changes.Add("the description");

        // The request replaces all four default filters, so the file's four are what is stored afterwards, and the item names
        // the ones that differ. A file that sets none sends four nulls, which clear them.
        if (defaultsDiffer) changes.Add($"the default filters ({string.Join(", ", differing)})");
        var note = defaultsDiffer ? $" A refresh of the corpus's sources follows. {Narrowing}" : string.Empty;

        if (changes.Count == 0)
            items.Add(new ImportItem("corpus", corpus.Name, "unchanged", wanted.Description is null && corpus.Description is not null
                ? "The file has no description, and the one here is kept."
                : null));
        else if (!apply)
            items.Add(new ImportItem("corpus", corpus.Name, "update", $"Would change {string.Join(" and ", changes)}.{note}"));
        else
        {
            var outcome = await config.UpdateCorpusAsync(
                corpus,
                new UpdateCorpusRequest(
                    descriptionDiffers ? wanted.Description : null,
                    defaultsDiffer ? DefaultsOf(wanted.Defaults) ?? new CorpusDefaults(null, null, null, null) : null),
                ct);
            items.Add(outcome.Refusal is { } refusal
                ? Refused("corpus", corpus.Name, refusal)
                : new ImportItem("corpus", corpus.Name, "update", $"Changed {string.Join(" and ", changes)}.{note}"));
        }

        foreach (var source in wanted.Sources)
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

        SetItems(wanted, newSet: null, apply, existing: corpus, items);
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
                GitHistory: IsHistory(source),
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
                      || (IsHistory(source) && source.Git is not null
                          && !string.Equals(source.Git.ToJson(), GitHistoryOptions.FromJson(here.GitOptions).ToJson(), StringComparison.Ordinal));

        if (!differs)
        {
            items.Add(new ImportItem("source", name, "unchanged", null));
            return;
        }

        if (!apply)
        {
            items.Add(new ImportItem("source", name, "update", $"Would change its filters or options, and queue a refresh. {Narrowing}"));
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
                IsHistory(source) ? source.Git : null),
            ct);

        items.Add(updated.Refusal is { } refusal
            ? Refused("source", name, refusal)
            : new ImportItem("source", name, "update",
                "Changed its filters or options" + (updated.Value!.IndexJob is not null ? $", and a refresh was queued. {Narrowing}" : ".")));
    }

    // ------------------------------------------------------------------------------------------------- what is left

    /// <summary>Sources of a corpus here that the file does not name.</summary>
    private void Extras(Corpus corpus, ExportedCorpus wanted, List<ImportItem> items)
    {
        foreach (var here in corpus.Sources.Where(s => s.Kind != SourceKind.Upload && !wanted.Sources.Any(w => SameSource(s, w)))
                     .OrderBy(s => s.RootPath, StringComparer.OrdinalIgnoreCase))
            items.Add(new ImportItem("source", $"{corpus.Name}: {here.Kind.ToString().ToLowerInvariant()} {here.RootPath}", "extra",
                "Here and not in the file. Import does not remove it."));
    }

    /// <summary>
    /// A file's chunk sets. A new corpus is made with one (<paramref name="newSet"/>), which the corpus item says. Beyond that
    /// import does not read them: a set it finds here is compared, and one it does not is left for the corpus's page, where adding
    /// one starts a backfill.
    /// </summary>
    private static void SetItems(ExportedCorpus wanted, ExportedChunkSet? newSet, bool apply, Corpus? existing, List<ImportItem> items)
    {
        foreach (var s in wanted.ChunkSets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var label = $"{wanted.Name}: {s.Name}";

            if (ReferenceEquals(s, newSet))
            {
                var unapplied = new List<string>();
                if (s.CustomBoundaryPattern is not null) unapplied.Add("the boundary pattern");
                if (s.UnitAware) unapplied.Add("unitAware");
                if (s.SentenceAware) unapplied.Add("sentenceAware");
                if (s.HeadingContext) unapplied.Add("headingContext");
                var tense = apply ? "Made" : "Would be made";
                items.Add(new ImportItem("chunk set", label, unapplied.Count == 0 ? "create" : "not imported",
                    unapplied.Count == 0
                        ? $"{tense} with the corpus."
                        : $"{tense} with the corpus without {string.Join(", ", unapplied)}, which a corpus is not made with. Set them on the chunk set."));
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

    private static bool IsHistory(ExportedSource source) => string.Equals(source.Kind, HistorySource, StringComparison.Ordinal);

    /// <summary>
    /// A file can leave a list or a filter block out, and a reader of it should not have to ask each time. A name loses its
    /// surrounding space, as <c>CreateCorpusAsync</c> trims it, and a kind is written in lower case. <see cref="Check"/> has
    /// refused a null entry and a kind that is neither.
    /// </summary>
    private static ExportedCorpus Normalised(ExportedCorpus c) => c with
    {
        Name = c.Name.Trim(),
        Defaults = c.Defaults ?? new ExportedFilters(null, null, null, null),
        Sources = [.. (c.Sources ?? []).Select(s => s with { Kind = s.Kind.ToLowerInvariant(), Filters = s.Filters ?? new ExportedFilters(null, null, null, null) })],
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

    /// <summary>
    /// The same kind over the same folder. Roots are compared in the spelling a source is stored in, so <c>docs/.</c> and
    /// <c>x/../docs</c> are <c>docs</c>, and ignoring case only where the filesystem does.
    /// </summary>
    private bool SameSource(Source here, ExportedSource wanted) =>
        string.Equals(here.Kind.ToString(), wanted.Kind, StringComparison.OrdinalIgnoreCase)
        && WorkspaceDiscovery.PathComparer.Equals(Spelled(here.RootPath), Spelled(wanted.RootPath));

    /// <summary>
    /// A root as the catalogue stores it. A path the platform cannot read as a path (a character it forbids) cannot be stored
    /// either, so it is compared as written with its slashes made alike, and adding it is refused with the reason.
    /// </summary>
    private string Spelled(string? path)
    {
        try
        {
            return config.CanonicalRoot(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }
    }

    /// <summary>The names of the filters that differ, in the order the file writes them.</summary>
    private static List<string> Differing(ExportedFilters wanted, ExportedFilters here)
    {
        var names = new List<string>();
        if (!SameList(wanted.IncludeGlobs, here.IncludeGlobs)) names.Add("includeGlobs");
        if (!SameList(wanted.ExcludeGlobs, here.ExcludeGlobs)) names.Add("excludeGlobs");
        if (wanted.UseGitignore != here.UseGitignore) names.Add("useGitignore");
        if (wanted.MaxFileBytes != here.MaxFileBytes) names.Add("maxFileBytes");
        return names;
    }

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

        if (importer.Check(file) is { } refused)
            return Results.Problem(title: refused.Title, detail: refused.Detail, statusCode: refused.Status);

        return Results.Ok(await importer.RunAsync(file, apply == true, ct));
    }
}
