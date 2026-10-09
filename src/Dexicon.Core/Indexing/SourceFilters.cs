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
    /// Honour git's answer about this source's tree: a <c>.gitignore</c> in every
    /// directory the walk reaches, and the root's <c>.git/info/exclude</c>, which is where
    /// worktrees and other local additions are excluded. One setting for all of it, so it
    /// says whether git decides what is indexed.
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

    /// <summary>The last layer for <c>.gitignore</c>, which configuration does not set.</summary>
    public const bool ConfiguredUseGitignore = true;

    public static Effective Resolve(Corpus corpus, Source source, IndexingOptions configured) =>
        new(
            source.UseGitignore ?? corpus.DefaultUseGitignore ?? ConfiguredUseGitignore,
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

    /// <summary>What reads a glob list, and so what makes an element of it unusable.</summary>
    public enum GlobReader
    {
        /// <summary>The file walk: <see cref="IgnoreRuleSet.AddPatterns"/> compiles each element.</summary>
        Walk,

        /// <summary>A history source's include list, which git reads as pathspecs.</summary>
        Git,

        /// <summary>A corpus's default include list, which a file source reads as patterns and a history source as pathspecs.</summary>
        WalkAndGit,
    }

    /// <summary>The most elements a stored glob list can hold. Each is compiled per walk and tested against every file.</summary>
    public const int MaxGlobsPerList = 200;

    /// <summary>The most characters one element of a glob list can hold.</summary>
    public const int MaxGlobLength = 500;

    /// <summary>Why <see cref="Check"/> refused a list.</summary>
    public enum GlobProblemKind
    {
        /// <summary>Null, or not readable by the reader: empty or a null character for git, an uncompilable pattern for the walk.</summary>
        Unusable,

        /// <summary>The list holds more than <see cref="MaxGlobsPerList"/> elements.</summary>
        TooMany,

        /// <summary>An element is longer than <see cref="MaxGlobLength"/> characters.</summary>
        TooLong,

        /// <summary>An element for git is a path whose <c>..</c> segments climb out of the repository, which git rejects.</summary>
        ClimbsOut,

        /// <summary>
        /// An element for git is still a rooted path after the one leading slash is removed: it starts with
        /// <c>//</c>, or its path starts with <c>/</c> after pathspec magic such as <c>:(glob)</c>.
        /// </summary>
        RootedPath,

        /// <summary>An element for git starts with <c>/:</c>, so removing the slash would turn a literal name into pathspec magic.</summary>
        SlashThenMagic,
    }

    /// <summary>The first element of a list that cannot be used, and why. For <see cref="GlobProblemKind.TooMany"/> it is the first element past the cap.</summary>
    public readonly record struct GlobProblem(int Index, GlobProblemKind Kind);

    /// <summary>
    /// The first problem with <paramref name="globs"/>, or null when it can be used. The count cap
    /// (<see cref="MaxGlobsPerList"/>) is judged before any element is read, and the check stops at the first
    /// problem, so a list far past the caps costs one comparison and not one regular expression per element. Lists
    /// already stored are not passed through here.
    ///
    /// Every reader: a null element is never usable, and an element is at most <see cref="MaxGlobLength"/>
    /// characters. For <see cref="GlobReader.Walk"/> an element that <see cref="IgnoreRuleSet.AddPatterns"/>
    /// cannot compile, such as <c>[z-a]</c>, is refused by the same parser. A path that climbs out with <c>..</c>
    /// is not refused there: the walk matches against paths below the root, so it compiles and matches nothing.
    ///
    /// For <see cref="GlobReader.Git"/> and <see cref="GlobReader.WalkAndGit"/> an empty element or one holding a
    /// null character is refused, because git rejects an empty pathspec and cannot be passed a null character.
    /// Git also fails with a fatal error, and a history source then fails every pass, on three other shapes, which
    /// are refused: a path that climbs out of the repository (<see cref="GlobProblemKind.ClimbsOut"/>, judged
    /// lexically on the part after any pathspec magic, with segments split on <c>/</c> only, so <c>a/../b</c> is
    /// accepted); a rooted path (<see cref="GlobProblemKind.RootedPath"/>); and <c>/:</c> at the start
    /// (<see cref="GlobProblemKind.SlashThenMagic"/>). A single leading <c>/</c> is accepted:
    /// <see cref="GitHistory.Pathspecs"/> removes it before git sees it. Anything else, pathspec magic included,
    /// is left to git. The check is of syntax: a pattern that compiles but is slow to match passes it.
    /// </summary>
    public static GlobProblem? Check(IReadOnlyList<string>? globs, GlobReader reader = GlobReader.Walk)
    {
        if (globs is null) return null;
        if (globs.Count > MaxGlobsPerList) return new GlobProblem(MaxGlobsPerList, GlobProblemKind.TooMany);

        for (var i = 0; i < globs.Count; i++)
        {
            var glob = globs[i];
            if (glob is null) return new GlobProblem(i, GlobProblemKind.Unusable);
            if (glob.Length > MaxGlobLength) return new GlobProblem(i, GlobProblemKind.TooLong);

            if (reader != GlobReader.Walk)
            {
                if (glob.Length == 0 || glob.Contains('\0')) return new GlobProblem(i, GlobProblemKind.Unusable);
                if (GitPathProblem(glob) is { } kind) return new GlobProblem(i, kind);
            }

            if (reader == GlobReader.Git) continue;

            try { new IgnoreRuleSet().AddPatterns([glob], "check"); }
            catch (ArgumentException) { return new GlobProblem(i, GlobProblemKind.Unusable); }
        }

        return null;
    }

    /// <summary>The position of the first element <see cref="Check"/> refuses, or null when all can be used.</summary>
    public static int? FirstUnusable(IReadOnlyList<string>? globs, GlobReader reader = GlobReader.Walk) =>
        Check(globs, reader)?.Index;

    /// <summary>
    /// The path part of a pathspec: the element without its leading magic. The long form is <c>:(</c> up to the
    /// first <c>)</c>; the short form is <c>:</c> followed by any of <c>!</c>, <c>^</c> and <c>/</c> and an
    /// optional second <c>:</c>. An element not starting with <c>:</c> has no magic and is returned whole. Null
    /// when a long form has no closing parenthesis, which is left to git.
    /// </summary>
    public static string? PathAfterMagic(string pathspec)
    {
        if (pathspec.Length == 0 || pathspec[0] != ':') return pathspec;

        if (pathspec.Length > 1 && pathspec[1] == '(')
        {
            var close = pathspec.IndexOf(')');
            return close < 0 ? null : pathspec[(close + 1)..];
        }

        var at = 1;
        while (at < pathspec.Length && pathspec[at] is '!' or '^' or '/') at++;
        if (at < pathspec.Length && pathspec[at] == ':') at++;

        return pathspec[at..];
    }

    private static GlobProblemKind? GitPathProblem(string glob)
    {
        // Removing this slash would read what follows as magic, and Pathspecs does not remove it then.
        if (glob.StartsWith("/:", StringComparison.Ordinal)) return GlobProblemKind.SlashThenMagic;

        if (PathAfterMagic(glob) is not { } path) return null;

        var magic = glob.StartsWith(':');
        if (magic ? path.StartsWith('/') : glob.StartsWith("//", StringComparison.Ordinal)) return GlobProblemKind.RootedPath;

        // Pathspecs removes one leading slash from an element without magic.
        return ClimbsOut(!magic && path.StartsWith('/') ? path[1..] : path) ? GlobProblemKind.ClimbsOut : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> goes above its root. Lexical, as git resolves it: empty and <c>.</c>
    /// segments are skipped and each <c>..</c> removes one level, so <c>a/../b</c> stays inside and <c>a/../..</c>
    /// does not.
    /// </summary>
    private static bool ClimbsOut(string path)
    {
        var depth = 0;

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;

            if (segment != "..") depth++;
            else if (depth == 0) return true;
            else depth--;
        }

        return false;
    }
}
