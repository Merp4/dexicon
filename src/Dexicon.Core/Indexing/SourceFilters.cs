using System.Text.Json;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;

namespace Dexicon.Core.Indexing;

/// <summary>
/// What a source is actually filtered by, once the corpus default and the configured
/// fallback have been applied.
///
/// Three layers, narrowest first: the source's own value, then the corpus default, then
/// the deployment's configuration. A corpus with ten folders under one parent is the case
/// the middle layer exists for; before it, ten sources carried ten copies of the same two
/// globs, set one at a time and editable not at all.
///
/// Every caller resolves through here. Reading <see cref="Source.MaxFileBytes"/> directly
/// gives the source's opinion, which is null for a source that has none, and a null size
/// cap is not a filter — it is a walk that indexes everything.
/// </summary>
public static class SourceFilters
{
    /// <param name="UseGitignore">
    /// Honour git's answer about this source's root: its <c>.gitignore</c>, and its
    /// <c>.git/info/exclude</c>, which is where worktrees and other local additions are
    /// excluded. One setting for both, so it says whether git decides what is indexed.
    /// </param>
    /// <param name="MaxFileBytes">The cap for ordinary files; documents get their own, larger one.</param>
    /// <param name="IncludeGlobs">Empty means everything not excluded, which is not the same as matching nothing.</param>
    public sealed record Effective(
        bool UseGitignore,
        int MaxFileBytes,
        IReadOnlyList<string> IncludeGlobs,
        IReadOnlyList<string> ExcludeGlobs);

    /// <summary>
    /// Where each effective value came from, so the UI can say "inherited" rather than
    /// showing a number the reader will assume they typed.
    /// </summary>
    public enum Origin { Source, Corpus, Configured }

    public static Effective Resolve(Corpus corpus, Source source, IndexingOptions configured) =>
        new(
            source.UseGitignore ?? corpus.DefaultUseGitignore ?? true,
            source.MaxFileBytes ?? corpus.DefaultMaxFileBytes ?? configured.MaxFileBytes,
            Globs(source.IncludeGlobs) ?? Globs(corpus.DefaultIncludeGlobs) ?? [],
            Globs(source.ExcludeGlobs) ?? Globs(corpus.DefaultExcludeGlobs) ?? []);

    /// <summary>
    /// Which layer supplied a field, given the pair of raw values. Mirrors
    /// <see cref="Resolve"/>: an empty array is a value, so a source holding one is the
    /// source's own opinion and stops the search there.
    /// </summary>
    public static Origin OriginOf(object? ownValue, object? corpusValue) =>
        ownValue is not null ? Origin.Source
        : corpusValue is not null ? Origin.Corpus
        : Origin.Configured;

    /// <summary>
    /// A stored JSON array, or null when the column is unset. Null and empty are
    /// deliberately distinguished: null is "inherit", empty is "none, whatever the corpus
    /// says", and collapsing them would make an override impossible to express.
    ///
    /// A column that will not parse is treated as unset rather than throwing. A corrupt
    /// value should not stop a corpus indexing, and it is visible in the UI as the
    /// inherited value rather than as a crash.
    /// </summary>
    public static IReadOnlyList<string>? Globs(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The inverse, for storing what an API caller sent. Null stays null, so omitting a
    /// field in a PATCH leaves the source inheriting rather than pinning it to whatever it
    /// happened to be showing.
    /// </summary>
    public static string? Store(IReadOnlyList<string>? globs) =>
        globs is null ? null : JsonSerializer.Serialize(globs);
}
