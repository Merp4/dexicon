using System.Text;
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
        /// <c>//</c>, or its path starts with <c>/</c> after pathspec magic such as <c>:(glob)</c>. Git rejects it
        /// as a path outside the repository.
        /// </summary>
        RootedPath,

        /// <summary>An element for git starts with <c>/:</c>, so removing the slash would turn a literal name into pathspec magic.</summary>
        SlashThenMagic,

        /// <summary>An element for git has pathspec magic git rejects whatever the path, see <see cref="ParseMagic"/>.</summary>
        MalformedMagic,
    }

    /// <summary>The first element of a list that cannot be used, and why. For <see cref="GlobProblemKind.TooMany"/> it is the first element past the cap.</summary>
    public readonly record struct GlobProblem(int Index, GlobProblemKind Kind);

    /// <summary>
    /// The first problem with <paramref name="globs"/>, or null when it can be used. The count cap
    /// (<see cref="MaxGlobsPerList"/>) is judged before any element is read, so a list far past it is refused
    /// without a regular expression compiled or an element examined. Past the count, elements are examined in
    /// order, each for its length before it is compiled or parsed, and the check stops at the first problem; its
    /// work is bounded by <see cref="MaxGlobsPerList"/> times <see cref="MaxGlobLength"/>. Lists already stored
    /// are not passed through here.
    ///
    /// Every reader: a null element is never usable, and an element is at most <see cref="MaxGlobLength"/>
    /// characters. For <see cref="GlobReader.Walk"/> an element that <see cref="IgnoreRuleSet.AddPatterns"/>
    /// cannot compile, such as <c>[z-a]</c>, is refused by the same parser. A path that climbs out with <c>..</c>
    /// is not refused there: the walk matches against paths below the root, so it compiles and matches nothing.
    ///
    /// For <see cref="GlobReader.Git"/> and <see cref="GlobReader.WalkAndGit"/> an empty element or one holding a
    /// null character is refused, because git rejects an empty pathspec and cannot be passed a null character.
    /// Git also fails with a fatal error, and a history source then fails every pass, on four other shapes, which
    /// are refused: pathspec magic git rejects (<see cref="GlobProblemKind.MalformedMagic"/>); a path that climbs
    /// out of the repository (<see cref="GlobProblemKind.ClimbsOut"/>, judged lexically on the part after any
    /// pathspec magic, with segments split on <c>/</c> only, so <c>a/../b</c> is accepted); a rooted path
    /// (<see cref="GlobProblemKind.RootedPath"/>); and <c>/:</c> at the start
    /// (<see cref="GlobProblemKind.SlashThenMagic"/>). The two path checks are skipped for magic with <c>top</c>
    /// (<c>:(top)</c>, or <c>/</c> in the short form), where git does not look at the path. A single leading
    /// <c>/</c> is accepted: <see cref="GitHistory.Pathspecs"/> removes it before git sees it. The rules are those of
    /// git on Linux, measured on git 2.54.0. Anything else is left to git, such as an invalid attribute name. The
    /// check is of syntax: a pattern that compiles but is slow to match passes it.
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
    /// A pathspec split into its magic and its path, with what git's parser does to each.
    /// </summary>
    /// <param name="Path">What follows the magic, or the whole element when it has none. Null when <paramref name="Malformed"/> is set and the magic never closes.</param>
    /// <param name="Top">
    /// The magic names <c>top</c> (<c>:(top)</c>, or <c>/</c> in the short form). Git then reads the path from
    /// the repository root and does not reject a rooted path or one with <c>..</c>.
    /// </param>
    /// <param name="Malformed">The magic is one git rejects whatever the path: git fails with a fatal error.</param>
    public readonly record struct PathspecMagic(string? Path, bool Top, bool Malformed);

    /// <summary>
    /// Splits a pathspec as git parses it, measured on git 2.54.0 on Linux (Git for Windows 2.31.1 agrees).
    /// An element not starting with <c>:</c> has no magic. The long form is <c>:(</c>, comma-separated words and
    /// <c>)</c>; the words git accepts are <c>top</c>, <c>literal</c>, <c>icase</c>, <c>glob</c>, <c>exclude</c>
    /// and <c>attr:</c> followed by a specification, matched exactly, with empty words allowed. It is malformed
    /// when it has no closing <c>)</c>, has any other word, names both <c>glob</c> and <c>literal</c>, or holds
    /// an <c>attr:</c> that is empty or uses <c>\)</c> or <c>\\</c> (a backslash escapes the next character
    /// in an <c>attr:</c> word). The short form is <c>:</c> followed by characters of git's magic set
    /// (<c>!"#%&amp;',-/;&lt;=&gt;@_`~^</c>) and an optional <c>:</c> that ends it; of those only <c>!</c>, <c>^</c>
    /// and <c>/</c> are implemented, so any other makes it malformed. A character outside the set, such as a
    /// letter, <c>.</c> or <c>*</c>, ends the magic and starts the path. What else is wrong with an <c>attr:</c>
    /// specification, such as an invalid attribute name, is left to git.
    /// </summary>
    public static PathspecMagic ParseMagic(string pathspec)
    {
        if (pathspec.Length == 0 || pathspec[0] != ':') return new PathspecMagic(pathspec, false, false);

        return pathspec.Length > 1 && pathspec[1] == '(' ? ParseLongMagic(pathspec) : ParseShortMagic(pathspec);
    }

    private const string ShortMagicCharacters = "!\"#%&',-/;<=>@_`~^";

    private static PathspecMagic ParseShortMagic(string pathspec)
    {
        var at = 1;
        var top = false;
        var malformed = false;

        while (at < pathspec.Length && ShortMagicCharacters.Contains(pathspec[at]))
        {
            if (pathspec[at] == '/') top = true;
            else if (pathspec[at] is not ('!' or '^')) malformed = true;

            at++;
        }

        if (at < pathspec.Length && pathspec[at] == ':') at++;

        return new PathspecMagic(pathspec[at..], top, malformed);
    }

    private static PathspecMagic ParseLongMagic(string pathspec)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var malformed = false;
        var closed = false;
        var at = 2;

        for (; at < pathspec.Length; at++)
        {
            var c = pathspec[at];

            if (c == '\\' && word.ToString().StartsWith("attr:", StringComparison.Ordinal) && at + 1 < pathspec.Length)
            {
                // An escaped character belongs to the word, and git cannot match a value holding ) or \.
                if (pathspec[at + 1] is ')' or '\\') malformed = true;

                word.Append(c).Append(pathspec[++at]);
                continue;
            }

            if (c is ',' or ')')
            {
                words.Add(word.ToString());
                word.Clear();

                if (c == ')')
                {
                    closed = true;
                    break;
                }

                continue;
            }

            word.Append(c);
        }

        if (!closed) return new PathspecMagic(null, false, true);

        var top = false;
        var glob = false;
        var literal = false;

        foreach (var w in words)
        {
            switch (w)
            {
                case "": case "icase": case "exclude": break;
                case "top": top = true; break;
                case "glob": glob = true; break;
                case "literal": literal = true; break;
                default:
                    // Anything after "attr:" is for git to judge, bar an empty specification.
                    if (!w.StartsWith("attr:", StringComparison.Ordinal) || w.Length == "attr:".Length) malformed = true;
                    break;
            }
        }

        return new PathspecMagic(pathspec[(at + 1)..], top, malformed || (glob && literal));
    }

    private static GlobProblemKind? GitPathProblem(string glob)
    {
        // Removing this slash would read what follows as magic, and Pathspecs does not remove it then.
        if (glob.StartsWith("/:", StringComparison.Ordinal)) return GlobProblemKind.SlashThenMagic;

        var magic = ParseMagic(glob);
        if (magic.Malformed) return GlobProblemKind.MalformedMagic;

        // With top, git reads the path from the repository root and does not look at where it goes.
        if (magic.Top || magic.Path is not { } path) return null;

        if (glob.StartsWith(':') ? path.StartsWith('/') : glob.StartsWith("//", StringComparison.Ordinal))
            return GlobProblemKind.RootedPath;

        return ClimbsOut(path) ? GlobProblemKind.ClimbsOut : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> goes above its root. Lexical, as git resolves it: empty and <c>.</c>
    /// segments are skipped, so a doubled slash is one separator and a leading one adds nothing, and each
    /// <c>..</c> removes one level: <c>a/../b</c> stays inside and <c>a/../..</c> and <c>a//../..</c> do not.
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
