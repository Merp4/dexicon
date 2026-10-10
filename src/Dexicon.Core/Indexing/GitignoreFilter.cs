using System.Text;

namespace Dexicon.Core.Indexing;

/// <summary>
/// gitignore-syntax path matching: <c>*</c>, <c>**</c>, <c>?</c>, <c>[a-z]</c>, a leading
/// <c>/</c> to anchor at the root, a trailing <c>/</c> to match directories only, and
/// <c>!</c> to negate. Later patterns win, which is what git does.
///
/// Paths are relative to the scan root throughout. A file read from a subdirectory says
/// what it means relative to ITSELF, so its patterns are anchored to that directory as
/// they go in; see the <c>directoryPrefix</c> argument to <see cref="AddPatterns"/>.
/// </summary>
public sealed class IgnoreRuleSet
{
    private readonly List<Rule> _rules = [];

    public IgnoreRuleSet() { }

    /// <summary>
    /// A set carrying everything <paramref name="other"/> holds, which further patterns can
    /// then be added to without changing it.
    ///
    /// The walk needs this because a subdirectory's own ignore file applies to that subtree
    /// and to nothing beside it: its siblings keep the set their parent had. Copied rather
    /// than chained because a rule holds its tokens and the copy is a reference to
    /// the same ones, and because a directory holding an ignore file is rare enough that the
    /// list copy is not worth avoiding.
    /// </summary>
    public IgnoreRuleSet(IgnoreRuleSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _rules.AddRange(other._rules);
    }

    /// <param name="LiteralPrefix">
    /// The deepest path this rule is certain to sit under: its text up to the last path
    /// boundary before its first wildcard, beneath the directory whose file it came from.
    /// Used to decide whether a negation could reach beneath a directory that is otherwise
    /// prunable.
    /// </param>
    /// <param name="MatchesAnyDepth">
    /// The pattern has no interior slash and no anchor AND came from the scan root, so it
    /// applies at every level: `*.md` re-includes a file anywhere, and nothing can be
    /// pruned on its account. The same pattern in `sub/.gitignore` reaches every level
    /// beneath `sub` and no further, which is what LiteralPrefix then says.
    /// </param>
    private sealed record Rule(
        GlobMatcher Pattern, bool Negated, bool DirectoryOnly, string Source,
        string LiteralPrefix, bool MatchesAnyDepth);

    public int Count => _rules.Count;

    /// <summary>Appends the rules <paramref name="other"/> holds, already compiled.</summary>
    internal void AddRules(IgnoreRuleSet other) => _rules.AddRange(other._rules);

    /// <summary>The longest line of an ignore file, or entry of a glob list, that is read, in characters.</summary>
    public const int MaxPatternLength = 500;

    // The limits below bound the work a walk does for each file it meets, which is tested against every rule in force:
    // a rule costs a search for its longest literal run, and its weight in word operations (see GlobMatcher). The
    // worst case at the limits, measured, is the table docs/04-ingestion.md gives, which IgnoreRuleCostTests prints.

    /// <summary>The most rules the ignore files of one walk may add (`.git/info/exclude` and every `.gitignore` and `.dexiconignore`).</summary>
    public const int MaxRulesPerSource = 5_000;

    /// <summary>The most weight (<see cref="GlobMatcher.Weight"/>) the ignore files of one walk may add.</summary>
    public const int MaxWeightPerSource = 60_000;

    /// <summary>
    /// The most rules one stored glob list may add. A list is capped at <c>SourceFilters.MaxGlobsPerList</c> entries when
    /// it is stored; this holds a list stored before that cap.
    /// </summary>
    public const int MaxRulesPerList = 1_000;

    /// <summary>The most weight one stored glob list may add.</summary>
    public const int MaxWeightPerList = 20_000;

    /// <summary>
    /// What a caller needs to read patterns out of a file or list: where they came from, how a message names a
    /// position, what the limits are, and what to do with a line that cannot be used.
    /// </summary>
    /// <param name="Source">The file's path from the scan root, or the list's name.</param>
    /// <param name="DirectoryPrefix">
    /// Where the patterns were written, as a forward-slash path relative to the scan root, or empty for the root
    /// itself. Every rule is anchored beneath it, so `secret.txt` in `sub/.gitignore` is `sub/**/secret.txt` and
    /// cannot reach a sibling of `sub`.
    /// </param>
    /// <param name="IsList">
    /// The patterns are the entries of a glob list, which a message gives a zero-based position
    /// (<c>excludeGlobs[1]</c>, as the API names it). Otherwise they are the lines of a file, numbered from 1 as an
    /// editor and git number them.
    /// </param>
    /// <param name="Unusable">
    /// Where a line that cannot be used is described when it is skipped. Null makes the first such line throw.
    /// </param>
    /// <param name="Budget">The limits the lines are counted against. Passing one makes the walk fail, whatever <paramref name="Unusable"/> is.</param>
    /// <param name="Remedy">Says what an operator should do, in a message about a budget that was passed.</param>
    /// <param name="MaxSkipped">
    /// How many lines may be skipped before the rest of the file is left unread, with one description saying so.
    /// </param>
    internal sealed record PatternSource(
        string Source, string DirectoryPrefix = "", bool IsList = false, WarningSink? Unusable = null,
        RuleBudget? Budget = null, Func<string>? Remedy = null, int MaxSkipped = 1_000);

    /// <summary>
    /// Reads each line of <paramref name="patterns"/> into a rule and appends it. A line that cannot be used (null,
    /// longer than <see cref="MaxPatternLength"/>, or a class that cannot be read such as <c>[z-a]</c>) throws
    /// <see cref="IgnorePatternException"/> naming <paramref name="source"/> and the line's position, unless
    /// <paramref name="unusable"/> is given: then the line is skipped, its description (the message, then
    /// <c>; the line was skipped</c>) is added there, and the remaining lines are read. A line past the
    /// <paramref name="budget"/> throws whatever <paramref name="unusable"/> is.
    /// </summary>
    /// <param name="directoryPrefix">See <see cref="PatternSource.DirectoryPrefix"/>.</param>
    /// <param name="isList">See <see cref="PatternSource.IsList"/>.</param>
    public void AddPatterns(IEnumerable<string> patterns, string source, string directoryPrefix = "",
        WarningSink? unusable = null, bool isList = false, RuleBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(directoryPrefix);

        Add(patterns, new PatternSource(source, directoryPrefix, isList, unusable, budget));
    }

    internal void Add(IEnumerable<string?> lines, PatternSource from)
    {
        var position = 0;
        var skipped = 0;
        foreach (var raw in lines)
        {
            position++;
            var where = from.IsList ? $"{from.Source}[{position - 1}]" : $"{from.Source} line {position}";

            if (TryAdd(raw, where, from) is not { } problem) continue;

            if (problem.OverBudget || from.Unusable is null)
                throw new IgnorePatternException(problem.Message());

            from.Unusable.Add(() => $"{problem.Message()}; the line was skipped");
            if (++skipped < from.MaxSkipped) continue;

            // Held whatever else the sink has dropped (up to the sink's own limit of notices): after this line,
            // nothing in the file applies.
            from.Unusable.AddNotice(IgnorePatternException.Clean(
                $"{from.Source} has {skipped:N0} lines that cannot be used; the rest of the file was not read, so no rule after them applies"));
            break;
        }
    }

    /// <summary>Why a line was not added, kept as parts until a message is wanted: a file of a million bad lines asks for none.</summary>
    private readonly record struct Problem(string Where, string? Pattern, string Reason, bool OverBudget)
    {
        public string Message() => IgnorePatternException.Clean(IgnorePatternException.Compose(Where, Pattern, Reason));
    }

    private Problem? TryAdd(string? raw, string where, PatternSource from)
    {
        if (raw is null) return new Problem(where, null, "is null", false);

        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;

        if (line.Length > MaxPatternLength)
            return new Problem(where, line, $"is longer than {MaxPatternLength} characters", false);

        var negated = line.StartsWith('!');
        if (negated) line = line[1..];

        var directoryOnly = line.EndsWith('/');
        if (directoryOnly) line = line[..^1];
        if (line.Length == 0) return null;

        var anchored = line.StartsWith('/');
        var bare = anchored ? line[1..] : line;

        // No anchor and no interior slash: it applies at every level beneath wherever
        // it was written. The deepest path it is CERTAIN to sit under is therefore that
        // directory and not the pattern's own text — `!keep.txt` in `sub/.gitignore`
        // matches `sub/deep/keep.txt`, so a LiteralPrefix of `sub/keep.txt` would let
        // `sub/deep` be pruned and the file it re-includes never be reached.
        var anyDepth = !anchored && !bare.Contains('/', StringComparison.Ordinal);

        if (!GlobMatcher.TryCompile(line, from.DirectoryPrefix, out var matcher, out var why))
            return new Problem(where, line, $"cannot be compiled ({why})", false);

        if (from.Budget is { } budget && !budget.TryCharge(matcher!.Weight))
        {
            var limit = $"{budget.MaxRules:N0} rules or {budget.MaxWeight:N0} pattern parts";
            return new Problem(where, line, $"is past the limit of {limit} for {(from.IsList ? "one list" : "one source")}"
                + (from.Remedy is null ? string.Empty : $"; {from.Remedy()}"), true);
        }

        _rules.Add(new Rule(
            matcher!, negated, directoryOnly, where,
            LiteralPrefix: anyDepth ? from.DirectoryPrefix : Join(from.DirectoryPrefix, LiteralPrefixOf(bare)),
            MatchesAnyDepth: anyDepth && from.DirectoryPrefix.Length == 0));
        return null;
    }

    internal static string Join(string prefix, string rest) =>
        prefix.Length == 0 ? rest
        : rest.Length == 0 ? prefix
        : prefix + "/" + rest;

    /// <param name="relativePath">Forward-slash path relative to the scan root.</param>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var path = relativePath.AsSpan();
        var ignored = false;
        long steps = 0;

        // The positions of the path's characters are worked out once, for the first rule that needs them.
        var masks = PathMasks.Rent();
        try
        {
            foreach (var rule in _rules)
            {
                // `bin/` excludes `bin/Debug/App.dll` and not a file named `bin`: for a file it asks for a directory
                // above it, which is a match that ends at a slash before the end.
                var beneath = rule.DirectoryOnly && !isDirectory;
                if (rule.Pattern.Match(path, beneath, masks, ref steps)) ignored = !rule.Negated;
            }
        }
        finally
        {
            masks.Return();
        }

        return ignored;
    }

    /// <summary>
    /// Whether any negation could re-include something at or beneath this directory, which
    /// is what decides if the directory can be skipped rather than walked and filtered.
    ///
    /// Conservative on purpose: it answers "possibly" wherever it cannot be sure, because
    /// being wrong means silently not indexing a file that is indexed today. `!*.md`
    /// applies at every depth and stops all pruning; `!.vscode/launch.json` stops `.vscode`
    /// from being pruned and nothing else, which is the case that prompted this, since
    /// `.vscode/` is always excluded and that one file is deliberately kept.
    ///
    /// It answers about the rules this set holds, which during a walk is the rules in scope
    /// where the question is asked: the root's, plus those of every directory already
    /// descended into. An ignore file INSIDE a pruned directory is never read, so it cannot
    /// re-include anything and cannot be consulted about it. Git decides the same way, and
    /// for the same reason: it does not read ignore files in a directory it has excluded.
    ///
    /// Paths are compared with <see cref="Fold"/>, as the matcher compares them, so a directory the matcher would
    /// reach a re-inclusion in is not pruned.
    /// </summary>
    public bool MayReincludeBeneath(string relativeDirectory)
    {
        var directory = relativeDirectory.AsSpan();
        foreach (var rule in _rules)
        {
            if (!rule.Negated) continue;
            if (rule.MatchesAnyDepth || rule.LiteralPrefix.Length == 0) return true;

            // The negation sits under this directory, or this directory sits under it.
            var prefix = rule.LiteralPrefix.AsSpan();
            if (Fold.Equal(prefix, directory)) return true;
            if (IsBeneath(prefix, directory)) return true;
            if (IsBeneath(directory, prefix)) return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="directory"/>, on a slash.</summary>
    private static bool IsBeneath(ReadOnlySpan<char> path, ReadOnlySpan<char> directory) =>
        path.Length > directory.Length && path[directory.Length] == '/' && Fold.StartsWith(path, directory);

    /// <summary>
    /// The deepest path this glob is certain to sit under: its text up to the last path
    /// boundary BEFORE its first wildcard.
    ///
    /// Cutting at the wildcard itself is not the same thing and is wrong here. `foo*/bar`
    /// would give `foo`, which an ignored `foo123` does not match, so that directory would
    /// be pruned even though `foo123/bar` is exactly what the rule re-includes. Cut at the
    /// boundary instead, which for that glob leaves nothing and therefore prunes nothing.
    /// </summary>
    private static string LiteralPrefixOf(string glob)
    {
        var wildcard = glob.IndexOfAny(['*', '?', '[']);
        if (wildcard < 0) return glob.TrimEnd('/');

        var boundary = glob.LastIndexOf('/', wildcard);
        return boundary <= 0 ? string.Empty : glob[..boundary];
    }
}

/// <summary>
/// Walks a workspace tree applying, in order: always-exclude, .git/info/exclude,
/// .gitignore, .dexiconignore, per-source globs, size cap, binary sniff. The two ignore
/// files are read in every directory the walk reaches, deeper outranking shallower, except
/// a directory another source owns and, for a .gitignore, a directory the rules ignore. See
/// docs/04-ingestion.md.
/// </summary>
public sealed class WorkspaceWalker
{
    public const string IgnoreFileName = ".dexiconignore";

    /// <summary>
    /// Fallback document cap for callers that do not pass one: tests, and any code path
    /// that predates the setting. The configured value is
    /// <c>DEXICON__INDEXING__DOCUMENTMAXBYTES</c>; see IndexingOptions.
    /// </summary>
    public const long DefaultDocumentMaxBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The patterns every walk starts from: version-control directories, build output, dependency folders, binaries and
    /// lock files. A `.gitignore`, a `.dexiconignore` or an exclude list can re-include any of them with `!`
    /// (`!.vscode/launch.json`, `!*.dll`), which is how the always-excluded `.vscode` can keep one file. Only
    /// `.git` is enforced (<see cref="IsGitPlumbing"/>).
    /// </summary>
    private static readonly string[] AlwaysExclude =
    [
        // `.git` without the slash, because in a linked worktree and in a submodule it is
        // a FILE holding `gitdir: <absolute host path>`. `.git/` matches directories only,
        // so that pointer was indexed as content, and an absolute host path in a payload
        // is a leak (docs/03). Without the slash it still prunes the directory.
        ".git", ".hg/", ".svn/",
        "node_modules/", "bin/", "obj/", ".vs/", ".idea/", ".vscode/",
        "target/", "dist/", "build/", "__pycache__/", ".venv/", "venv/",
        "*.exe", "*.dll", "*.pdb", "*.so", "*.dylib", "*.o", "*.obj", "*.a", "*.lib",
        "*.zip", "*.tar", "*.gz", "*.7z", "*.rar", "*.jar", "*.nupkg",
        "*.woff", "*.woff2", "*.ttf", "*.eot", "*.otf",
        "*.ico", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.svg",
        "*.mp3", "*.mp4", "*.avi", "*.mov", "*.wav", "*.flac",
        // Note: .pdf/.docx/.pptx/.epub are intentionally absent here; they are extracted.
        "*.db", "*.sqlite", "*.sqlite3",
        "*.safetensors", "*.gguf", "*.bin", "*.pt", "*.pth", "*.pkl", "*.npy", "*.npz",
        "*.lock", "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
    ];

    /// <param name="ModifiedTicks">
    /// When the file was last written, as the same stat that gave its size reported it, or 0
    /// where that is not known (a commit, which has no file). Together with the size it is
    /// the key a pass uses to leave a file it has already finished with unopened, and 0
    /// means there is no such key, so the file is read.
    /// </param>
    public sealed record Candidate(string FullPath, string RelativePath, long SizeBytes, long ModifiedTicks = 0);

    /// <param name="SizeBytes">
    /// What the walk measured, or 0 where it could not: the row a skip writes shows a
    /// size like any other, and for the two cap reasons the size IS the reason.
    /// </param>
    public sealed record Skipped(string RelativePath, string Reason, long SizeBytes);

    /// <param name="Warnings">
    /// Lines of <c>.gitignore</c> and <c>.git/info/exclude</c> that could not be used and were skipped,
    /// each naming the file, the line number (from 1) and the line, and files of those names that were
    /// skipped whole. The walk has no logger, so the caller writes these out. Anything of a
    /// <c>.dexiconignore</c> that cannot be used is not here: it fails the walk with
    /// <see cref="IgnorePatternException"/>. Only the first <see cref="WarningSink.MaxKept"/> are held, and
    /// <paramref name="WarningsOmitted"/> counts the rest.
    /// </param>
    public sealed record WalkResult(
        IReadOnlyList<Candidate> Files, IReadOnlyList<Skipped> SkippedFiles, IReadOnlyList<string> Warnings,
        int WarningsOmitted = 0);

    /// <param name="topLevelOnly">
    /// Files directly in <paramref name="rootPath"/> and no deeper. Used by
    /// <see cref="SourceCoverage"/>, which asks what is in a directory without descending
    /// into the subdirectories that already have sources of their own.
    /// </param>
    /// <param name="shadowedPrefixes">
    /// Directories, relative to <paramref name="rootPath"/>, that a more specific source owns
    /// (<see cref="SourceScope.ShadowedPrefixes"/>). Their files are dropped by the caller whatever
    /// they hold, so the ignore files inside them are not read: a bad line there is the owning
    /// source's to report, and it cannot fail this walk.
    /// </param>
    /// <param name="includeListName">
    /// What a message calls the include list. The walk gets the resolved list, which may be the source's own or its
    /// corpus's default, and the caller knows which.
    /// </param>
    /// <param name="excludeListName">As <paramref name="includeListName"/>, for the exclude list.</param>
    /// <param name="ct">
    /// Polled for each directory and every 256 files, since a walk of a large tree runs for minutes and a caller whose
    /// job was cancelled, or lost its lease, should stop.
    /// </param>
    /// <exception cref="IgnorePatternException">
    /// An ignore file or list that cannot be used or is past a limit. <see cref="IgnorePatternException.Warnings"/>
    /// holds what the walk had skipped by then.
    /// </exception>
    public static WalkResult Walk(string rootPath, bool useGitignore, IReadOnlyList<string>? includeGlobs,
        IReadOnlyList<string>? excludeGlobs, long maxFileBytes, long? documentMaxBytes = null,
        bool topLevelOnly = false, IReadOnlyList<string>? shadowedPrefixes = null,
        string includeListName = IncludeListName, string excludeListName = ExcludeListName,
        CancellationToken ct = default)
    {
        var warnings = new WarningSink();
        try
        {
            return WalkCore(rootPath, useGitignore, includeGlobs, excludeGlobs, maxFileBytes, documentMaxBytes,
                topLevelOnly, shadowedPrefixes, includeListName, excludeListName, warnings, ct);
        }
        catch (IgnorePatternException ex)
        {
            ex.Warnings = warnings.Kept;
            ex.WarningsOmitted = warnings.Omitted;
            throw;
        }
    }

    private static WalkResult WalkCore(string rootPath, bool useGitignore, IReadOnlyList<string>? includeGlobs,
        IReadOnlyList<string>? excludeGlobs, long maxFileBytes, long? documentMaxBytes,
        bool topLevelOnly, IReadOnlyList<string>? shadowedPrefixes, string includeListName, string excludeListName,
        WarningSink warnings, CancellationToken ct)
    {
        var root = Path.GetFullPath(rootPath);
        var files = new List<Candidate>();
        var skipped = new List<Skipped>();
        var state = new ReadState(warnings, RuleBudget.ForIgnoreFiles());

        // The tree's own rules, which a subdirectory's ignore file appends to as the walk
        // reaches it. The source's exclude globs are held back and put on the end of
        // whatever set is in force, so they keep outranking everything a repository says
        // about itself — including a nested file, which the operator has never seen.
        var treeRules = new IgnoreRuleSet();
        treeRules.AddPatterns(AlwaysExclude, "always-exclude");
        if (useGitignore) AddLocalGitExcludes(treeRules, root, state);
        // The root's own `.gitignore` and `.dexiconignore` are read by the walk, which
        // reaches the root before anything else and treats it like any other directory.

        // Read once. The set in force is rebuilt for every directory that holds an ignore file, and the
        // exclude rules are added to each from here. Each list has its own budget: a list is the operator's
        // and is checked when it is stored, and the tree's ignore files must not use up what it may hold.
        var excludeRules = new IgnoreRuleSet();
        if (excludeGlobs is { Count: > 0 })
            excludeRules.Add(excludeGlobs, ListSource(excludeListName));

        var layer = Layer.Of(treeRules, excludeRules);

        var include = new IgnoreRuleSet();
        if (includeGlobs is { Count: > 0 })
            include.Add(includeGlobs, ListSource(includeListName));
        var hasInclude = include.Count > 0;

        var seen = 0;
        foreach (var (full, ignore) in EnumerateFilesSafely(
                     root, layer, useGitignore, excludeRules, skipped, state, topLevelOnly,
                     shadowedPrefixes ?? [], ct))
        {
            if (++seen % FilesBetweenPolls == 0) ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');

            // Before the rule sets, and not expressible in them. `.git` sits in
            // always-exclude for the pruning, but a pattern list is offered, not enforced:
            // later patterns win there, so a `!.git` in anyone's .gitignore,
            // .dexiconignore or exclude_globs takes the pointer file back — and what it
            // holds is `gitdir: <absolute host path>`.
            //
            // Git makes the same call: `.git` cannot be un-ignored at all, whatever the
            // ignore files say. A repository's history has its own source type
            // (docs/04); nothing needs the plumbing indexed as text.
            if (IsGitPlumbing(relative)) continue;

            if (ignore.IsIgnored(relative, isDirectory: false)) continue;
            if (hasInclude && !include.IsIgnored(relative, isDirectory: false)) continue;

            FileInfo info;
            try { info = new FileInfo(full); }
            catch (Exception ex) { skipped.Add(new Skipped(relative, $"unreadable: {ex.Message}", 0)); continue; }

            // `FileInfo.Length` and LooksBinary's `File.OpenRead` both follow a link, so a
            // link is decided here, before either runs. See IsLink.
            //
            // Recorded rather than dropped: a file the operator can see in their tree
            // that appears in no count is the absence nothing can read back. Here, beside
            // the FileInfo that already exists, because doing it in the enumerator costs
            // a second stat on every file in the walk.
            if (IsLink(info))
            {
                skipped.Add(new Skipped(relative, LinkNotFollowed, 0));
                continue;
            }

            if (info.Length > maxFileBytes && !Extraction.ExtractorRegistry.IsDocumentFormat(relative))
            {
                skipped.Add(new Skipped(relative,
                    $"over the {maxFileBytes:N0} byte size cap ({info.Length:N0} bytes)", info.Length));
                continue;
            }

            if (info.Length == 0) { skipped.Add(new Skipped(relative, "empty file", 0)); continue; }

            // A PDF, DOCX, PPTX or EPUB is binary, but has text inside that an extractor
            // can reach. Sniffing it would drop every PDF in a
            // repository's docs/ folder. They also routinely exceed a code-sized cap, so
            // they get their own, larger one.
            var isDocument = Extraction.ExtractorRegistry.IsDocumentFormat(relative);
            if (isDocument)
            {
                var documentCap = documentMaxBytes ?? DefaultDocumentMaxBytes;
                if (info.Length > documentCap)
                {
                    skipped.Add(new Skipped(relative,
                        $"document over the {documentCap:N0} byte cap ({info.Length:N0} bytes). " +
                        "Raise DEXICON_INDEXING_DOCUMENTMAXBYTES in .env if you have the memory for it.", info.Length));
                    continue;
                }
            }
            else if (LooksBinary(full))
            {
                skipped.Add(new Skipped(relative, "binary content (NUL byte in the first 8 KB)", info.Length));
                continue;
            }

            files.Add(new Candidate(full, relative, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        return new WalkResult(files, skipped, warnings.Kept, warnings.Omitted);
    }

    /// <summary>
    /// How an unusable entry of an include or exclude list is named in a message: the API's field name and
    /// the entry's zero-based position, as <c>excludeGlobs[1]</c>.
    /// </summary>
    public const string IncludeListName = "includeGlobs";

    /// <inheritdoc cref="IncludeListName"/>
    public const string ExcludeListName = "excludeGlobs";

    private static IgnoreRuleSet.PatternSource ListSource(string name) =>
        new(name, IsList: true, Budget: RuleBudget.ForList(), Remedy: () => "shorten the list");

    /// <summary>How many files the walk goes through between two looks at its token.</summary>
    internal const int FilesBetweenPolls = 256;

    /// <summary>The most ignore-file text one walk reads, whatever the number of files.</summary>
    private const long MaxBytesPerWalk = 16L * 1024 * 1024;

    /// <summary>What reading the ignore files of one walk shares: the warnings, the budget and the bytes read.</summary>
    private sealed class ReadState(WarningSink warnings, RuleBudget budget)
    {
        public WarningSink Warnings => warnings;

        public RuleBudget Budget => budget;

        // A field, since the reader adds to it by reference.
        public long BytesRead;

        /// <summary>The files read so far, with the rules and weight each added to the budget.</summary>
        public List<(string Label, int Rules, int Weight)> Files { get; } = [];
    }

    /// <summary>
    /// Adds <c>.git/info/exclude</c>, git's per-clone ignore file. It holds what a working
    /// copy excludes without the repository saying so, which is where a tool that adds
    /// directories to someone's checkout puts them: <c>git worktree</c>, and the editors
    /// and agents that create worktrees inside the repository.
    ///
    /// Reported against a checkout with four worktrees: 22,004 files walked to 5,463
    /// tracked ones, and search returning the same document at two older commits. The
    /// copies are not noise, they are earlier versions of the answer, so a hit carries a
    /// real path and a real line and says something that stopped being true.
    ///
    /// This repository has the same shape. Measured on it: 239 tracked files, six
    /// worktrees under <c>.claude/worktrees/</c>, and the only thing excluding them is
    /// <c>**/.claude/worktrees/</c> in <c>.git/info/exclude</c> — <c>.gitignore</c> says
    /// nothing about them.
    ///
    /// Added BEFORE <c>.gitignore</c> because later patterns win here and
    /// <c>.gitignore</c> outranks <c>info/exclude</c> in git.
    ///
    /// Three things it does not reach, each covered by <c>.dexiconignore</c>:
    ///
    /// A linked worktree, where <c>.git</c> is a FILE pointing at a gitdir that is
    /// normally outside the tree being walked. Following it would read a file the source
    /// root does not contain.
    ///
    /// A link, for the same reason and more sharply: <c>Directory.Exists</c> and
    /// <c>File.ReadAllLines</c> both follow one, so a symlinked <c>.git</c>, <c>info</c>
    /// or <c>exclude</c> would read a file from outside the tree. None of the three is
    /// read when it is a link (<see cref="IsLink"/>).
    ///
    /// A source rooted BELOW the repository, which has no <c>.git</c> of its own. The
    /// repository's rules are not read for it — exactly as its <c>.gitignore</c> is not.
    ///
    /// <c>core.excludesFile</c>, git's third layer, is per-user and outside the workspace
    /// entirely; it is not read at all.
    /// </summary>
    private static void AddLocalGitExcludes(IgnoreRuleSet ignore, string root, ReadState state)
    {
        var gitDir = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDir) || IsLink(new DirectoryInfo(gitDir))) return;

        var info = Path.Combine(gitDir, "info");
        if (!Directory.Exists(info) || IsLink(new DirectoryInfo(info))) return;

        var exclude = Path.Combine(info, "exclude");
        if (!File.Exists(exclude) || IsLink(new FileInfo(exclude))) return;

        // Read as `.gitignore` is (AddIgnoreFile).
        AddIgnoreFile(ignore, exclude, ".git/info/exclude", string.Empty, state);
    }

    /// <summary>
    /// A path with a <c>.git</c> segment: the directory's contents, and the pointer file a
    /// linked worktree or a submodule has in its place.
    /// </summary>
    private static bool IsGitPlumbing(string relativePath)
    {
        var rest = relativePath.AsSpan();
        while (!rest.IsEmpty)
        {
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
            if (slash < 0) break;
            rest = rest[(slash + 1)..];
        }

        return false;
    }

    internal const string LinkNotFollowed = "a link; links are not followed";

    /// <summary>
    /// Whether <paramref name="entry"/> is a symbolic link or a junction. The walk follows
    /// neither: not into a directory, not to a file, and not to read an ignore file. A
    /// source path through one is refused (<see cref="WorkspaceDiscovery.Resolve"/>).
    ///
    /// Following a link means deciding where it leads, and the platforms disagree about
    /// that. POSIX takes <c>..</c> from wherever a link led; Windows, and
    /// <c>ResolveLinkTarget(...).FullName</c> on either, collapse it in the text. Measured
    /// on Linux, <c>link -&gt; alias/../hostdir</c> with <c>alias</c> pointing out of the
    /// workspace read as inside, and the walk returned a file from outside it. A link to
    /// the root also walked the tree again at every level, 41 times over.
    ///
    /// Git reads an ignore file the same way: measured with git 2.54, a
    /// <c>.gitignore</c> that is a symbolic link is not applied ("unable to access
    /// '.gitignore': Symbolic link loop") and what it names is reported as untracked.
    /// </summary>
    private static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null;

    /// <summary>
    /// The rules in force inside one directory.
    ///
    /// <paramref name="Tree"/> is what the tree itself says — the always-exclude list, the
    /// local git excludes, and every ignore file from the root down to here.
    /// <paramref name="Effective"/> is that with the source's exclude globs on the end, so
    /// they keep winning over anything a repository says about itself. Both are carried
    /// because a deeper directory appends to <paramref name="Tree"/>, not to
    /// <paramref name="Effective"/>, and appending to the latter would put the operator's
    /// globs in the middle.
    ///
    /// Shared by reference between every directory that adds nothing, which is almost all
    /// of them.
    /// </summary>
    private readonly record struct Layer(IgnoreRuleSet Tree, IgnoreRuleSet Effective)
    {
        public static Layer Of(IgnoreRuleSet tree, IgnoreRuleSet excludeRules)
        {
            if (excludeRules.Count == 0) return new Layer(tree, tree);

            var effective = new IgnoreRuleSet(tree);
            effective.AddRules(excludeRules);
            return new Layer(tree, effective);
        }
    }

    /// <summary>
    /// Enumerate without letting one unreadable directory abort the whole walk, and
    /// without following links (<see cref="IsLink"/>). A directory link is added to
    /// <paramref name="skipped"/> unless the rules in force ignore it.
    ///
    /// Each file comes with the rules in force where it sits, because a subdirectory's own
    /// ignore file applies to that subtree and to nothing beside it.
    /// </summary>
    private static IEnumerable<(string FilePath, IgnoreRuleSet Ignore)> EnumerateFilesSafely(
        string root, Layer rootLayer, bool useGitignore,
        IgnoreRuleSet excludeRules, List<Skipped> skipped, ReadState state, bool topLevelOnly,
        IReadOnlyList<string> shadowedPrefixes, CancellationToken ct)
    {
        var stack = new Stack<(string Dir, string Prefix, Layer Layer, bool Ignored)>();
        stack.Push((root, string.Empty, rootLayer, false));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, prefix, layer, directoryIgnored) = stack.Pop();

            // Listed before the subdirectories, because an ignore file here governs them
            // and this is the listing that finds it. Probing for the two names instead
            // costs a stat per directory per name, which on a 10,000-file tree measured
            // as 1,545 ms against 1,420: the listing is already being fetched.
            string[] entries;
            var listed = true;
            try { entries = Directory.GetFiles(dir); }
            catch (Exception) { entries = []; listed = false; }

            // Including the root, which is popped first and whose files therefore go in
            // ahead of every nested one, after the always-exclude list and the local git
            // excludes the caller put in.
            //
            // Looked for before anything is copied. Almost every directory has neither
            // file and inherits its parent's sets by reference; copying first and
            // discarding the copy is a rule list per directory rather than per file found.
            //
            // Neither is looked for inside a directory another source owns (its files are dropped by the caller). A
            // `.gitignore` is also not looked for inside a directory the rules in force ignore, which the walk enters
            // only because a negation could re-include a file there: git reads no ignore file below an ignored
            // directory, so a vendored tree's `.gitignore` files neither apply nor use up the budget. A
            // `.dexiconignore` is the operator's own file and is read there, because the files the negation brings
            // back are indexed, and skipping the file would index what it was written to exclude. A directory that a
            // negation re-includes is not ignored and is read like any other.
            var owned = shadowedPrefixes.Count > 0
                && SourceScope.IsShadowed(IgnoreRuleSet.Join(prefix, "x"), shadowedPrefixes);
            var gitignore = useGitignore && !owned && !directoryIgnored ? Named(entries, ".gitignore", prefix, state.Warnings) : null;
            var dexiconignore = owned ? null : Named(entries, IgnoreFileName, prefix, state.Warnings);

            if (gitignore is not null || dexiconignore is not null)
            {
                var tree = new IgnoreRuleSet(layer.Tree);
                var added = AddIgnoreFile(tree, gitignore, ".gitignore", prefix, state);
                added |= AddIgnoreFile(tree, dexiconignore, IgnoreFileName, prefix, state);
                if (added) layer = Layer.Of(tree, excludeRules);
            }

            var ignore = layer.Effective;

            string[] subdirs;
            try { subdirs = topLevelOnly ? [] : Directory.GetDirectories(dir); }
            catch (Exception) { continue; }

            // In ordinal order, so that the directory a failure is reported in, and what a message names, is the
            // same on every pass and not whatever order the filesystem lists.
            Array.Sort(subdirs, StringComparer.Ordinal);
            var descend = new List<(string Dir, string Prefix, bool Ignored)>();

            foreach (var sub in subdirs)
            {
                var relativeSub = Path.GetRelativePath(root, sub).Replace('\\', '/');

                // Unconditionally, ahead of the re-inclusion test below. A `!.git`
                // anywhere makes MayReincludeBeneath say "possibly", so the walk would
                // descend the whole object store to drop every file of it at the filter.
                // Nothing under here can be indexed whatever the rules say, so there is
                // no re-inclusion to be conservative about.
                if (IsGitPlumbing(relativeSub)) continue;

                var ignored = ignore.IsIgnored(relativeSub, isDirectory: true);

                // Never entered. Listed like a file link, unless the rules would have
                // excluded it anyway, in which case it is as absent as any ignored entry.
                if (IsLink(new DirectoryInfo(sub)))
                {
                    if (!ignored) skipped.Add(new Skipped(relativeSub, LinkNotFollowed, 0));
                    continue;
                }

                // Skipped rather than walked and thrown away. A repository carrying .git,
                // node_modules and a database's data directory enumerated 240,704 files to
                // keep 27,001, and every one of those was a stat across the mount: 99s
                // against 14s for the same result. Only where nothing beneath could be
                // re-included, because being wrong here means quietly not indexing
                // something that is indexed today.
                if (ignored && !ignore.MayReincludeBeneath(relativeSub)) continue;

                descend.Add((sub, relativeSub, ignored));
            }

            // The stack pops the last pushed, so the first in order goes on last.
            for (var i = descend.Count - 1; i >= 0; i--)
                stack.Push((descend[i].Dir, descend[i].Prefix, layer, descend[i].Ignored));

            // A directory whose files could not be listed still had its subdirectories
            // walked before this change, and still does.
            if (listed)
                foreach (var f in entries) yield return (f, ignore);
        }
    }

    /// <summary>
    /// Reads the ignore files a directory holds, in the order the root's are read, and says
    /// whether either existed.
    ///
    /// `.gitignore` only when the source honours git; `.dexiconignore` always, as at the
    /// root, because it is Dexicon's own file and `use_gitignore` is a statement about git.
    ///
    /// Names are compared without regard to case on every platform, because a case-insensitive mount (Docker
    /// Desktop, a share written from Windows) resolves `.DexiconIgnore` to the file git or the operator meant.
    /// Neither is read when it is a link, which git does not read either (<see cref="IsLink"/>).
    ///
    /// Where a directory of a case-sensitive filesystem holds more than one name that matches, the file with the
    /// exact name is read and otherwise the first in ordinal order, so the answer does not depend on the order the
    /// filesystem lists. The others are not read: for a <c>.gitignore</c> that is a warning, and for a
    /// <c>.dexiconignore</c> it fails the walk, because the rules in the one that is not read would not apply.
    /// </summary>
    internal static string? Named(IReadOnlyList<string> entries, string name, string directoryPrefix, WarningSink warnings)
    {
        var matches = entries
            .Where(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (matches.Count < 2) return matches.Count == 0 ? null : matches[0];

        var label = IgnoreRuleSet.Join(directoryPrefix, name);
        var names = string.Join(", ", matches.Take(MaxNamesShown).Select(Path.GetFileName))
            + (matches.Count > MaxNamesShown ? $", and {matches.Count - MaxNamesShown:N0} more" : string.Empty);
        if (name == IgnoreFileName)
            throw IgnorePatternException.ForFile(label,
                $"cannot be used because the directory holds {matches.Count:N0} files whose names differ only in case ({names}); keep one");

        var chosen = matches.FirstOrDefault(entry => Path.GetFileName(entry) == name) ?? matches[0];
        var others = matches.Count - 1;
        warnings.Add(IgnorePatternException.ForFile(label,
            $"has {others:N0} other {(others == 1 ? "file whose name differs" : "files whose names differ")} only in case ({names}); only {Path.GetFileName(chosen)} was read").Message);
        return chosen;
    }

    /// <summary>The most names of one ignore file's case variants that a message lists. A tree can hold 8,192 of them.</summary>
    private const int MaxNamesShown = 5;

    /// <summary>
    /// Adds one ignore file's lines. What happens to a file or line that cannot be used depends on whose
    /// file it is, except for the limits, which fail the walk for every file.
    ///
    /// <c>.gitignore</c> and <c>.git/info/exclude</c> belong to git, and the walk does not hold a tree to a stricter
    /// reading than it needs. A line that cannot be compiled or is longer than
    /// <see cref="IgnoreRuleSet.MaxPatternLength"/>, or holds bytes that are not valid in the file's encoding, is skipped and added to
    /// the warnings with the file and the line number, and the file's other lines still apply, up to 1,000 skipped
    /// lines. A file that cannot be opened, is not a regular file, or holds a NUL is skipped whole with a warning. A
    /// link is not read (<see cref="IsLink"/>), as git does not read it.
    ///
    /// <c>.dexiconignore</c> is Dexicon's own and is written to keep content out of the index. Skipping
    /// anything of it indexes what it was meant to exclude, so a line, a link, a read failure or a file that is
    /// not decodable text each fail the walk with <see cref="IgnorePatternException"/>, and nothing is indexed
    /// from the source until it is fixed.
    ///
    /// Past a limit (the rules or weight of the walk's budget, a file over 1 MiB, or 16 MiB of ignore files read, the
    /// bytes of a refused file included) the walk fails for every file, because the rules after the limit are the
    /// ones the operator wrote last and a walk that dropped them would index what they excluded. The message says
    /// what to do.
    ///
    /// Either file is named in a message by its path from the scan root. A hard link to a file elsewhere is read
    /// as the file it is, and its text can appear in a message; this is not detected.
    /// </summary>
    private static bool AddIgnoreFile(
        IgnoreRuleSet rules, string? path, string name, string directoryPrefix, ReadState state)
    {
        if (path is null) return false;

        var label = IgnoreRuleSet.Join(directoryPrefix, name);
        var ownFile = name == IgnoreFileName;

        string text;
        try
        {
            if (IsLink(new FileInfo(path)))
            {
                if (ownFile) throw IgnorePatternException.ForFile(label, "is a link, and links are not followed");
                return false;
            }

            text = IgnoreFileText.Read(path, strict: ownFile, ref state.BytesRead);
        }
        catch (IgnoreFileTooLargeException ex)
        {
            throw IgnorePatternException.ForFile(label, $"cannot be used as patterns because it {ex.Message}; {FileRemedy(ownFile)}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A file that is refused has still been read.
            ThrowIfPastWalkBytes(label, state);

            // The exception's own text is not used for an I/O failure: it carries the absolute path on the host.
            var reason = ex switch
            {
                UnauthorizedAccessException => "cannot be read (permission denied)",
                InvalidDataException => $"cannot be used as patterns because it {ex.Message}",
                _ => "cannot be read",
            };

            if (ownFile) throw IgnorePatternException.ForFile(label, reason, ex);

            state.Warnings.Add(IgnorePatternException.ForFile(label, reason + "; the file was skipped", ex).Message);
            return false;
        }

        ThrowIfPastWalkBytes(label, state);

        var lines = SplitLines(text);
        if (ownFile) RejectEmbeddedCarriageReturns(lines, label);
        else SkipUndecodedLines(lines, label, state.Warnings);

        var startRules = state.Budget.Rules;
        var startWeight = state.Budget.Weight;
        rules.Add(lines, new IgnoreRuleSet.PatternSource(
            label, directoryPrefix, Unusable: ownFile ? null : state.Warnings, Budget: state.Budget,
            Remedy: () => BudgetRemedy(state, label, state.Budget.Rules - startRules, state.Budget.Weight - startWeight)));
        state.Files.Add((label, state.Budget.Rules - startRules, state.Budget.Weight - startWeight));
        return true;
    }

    /// <summary>
    /// What a message about the walk's budget tells the operator. The file that passed it is rarely the file that used
    /// it up, so the three files that used most are named, this one among them. The way out that skips every
    /// <c>.gitignore</c> is given whichever file passed the limit, but only when a <c>.gitignore</c> or
    /// <c>.git/info/exclude</c> has used some of the budget; a <c>.dexiconignore</c> is read whatever
    /// <c>use_gitignore</c> says, so the setting frees nothing when the files using the budget are all of that name.
    /// </summary>
    private static string BudgetRemedy(ReadState state, string label, int rules, int weight)
    {
        var used = state.Files.Append((Label: label, Rules: rules, Weight: weight))
            .Where(file => file.Rules > 0)
            .ToList();
        var biggest = used
            .OrderByDescending(file => file.Rules)
            .ThenByDescending(file => file.Weight)
            .ThenBy(file => file.Label, StringComparer.Ordinal)
            .Take(3)
            .Select(file => $"{file.Label} ({file.Rules:N0} rules, {file.Weight:N0} parts)");
        var gitFilesUsed = used.Any(file => !file.Label.EndsWith(IgnoreFileName, StringComparison.Ordinal));

        return $"the files using most so far are {string.Join(", ", biggest)}; reduce them"
            + (gitFilesUsed ? ", or turn off use_gitignore for the source" : string.Empty);
    }

    private static string FileRemedy(bool ownFile) =>
        ownFile ? "reduce the file" : "reduce the file, or turn off use_gitignore for the source";

    private static void ThrowIfPastWalkBytes(string label, ReadState state)
    {
        if (state.BytesRead > MaxBytesPerWalk)
            throw IgnorePatternException.ForFile(label,
                $"takes the walk past the {MaxBytesPerWalk / (1024 * 1024)} MiB of ignore files it may read; reduce the files");
    }

    /// <summary>
    /// The lines of <paramref name="text"/>, split on LF as git splits them. A CR, U+0085 or U+2028 inside a line stays in
    /// it; a CR before the LF is trimmed with the rest of the line's whitespace.
    /// </summary>
    private static List<string?> SplitLines(string text)
    {
        var lines = new List<string?>();
        var start = 0;
        while (true)
        {
            var end = text.IndexOf('\n', start);
            var stop = end < 0 ? text.Length : end;

            lines.Add(text[start..stop]);
            if (end < 0) break;

            start = end + 1;
        }

        // A final newline ends the last line and does not begin another.
        if (text.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// Fails on a line of a <c>.dexiconignore</c> that has a carriage return inside it. Lines end at a line feed, so a
    /// file with old Mac line endings is one line holding a pattern that matches nothing; a file that was meant to
    /// exclude its names would exclude none of them.
    /// </summary>
    private static void RejectEmbeddedCarriageReturns(List<string?> lines, string label)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i]!.Trim();
            if (line.Contains('\r', StringComparison.Ordinal))
                throw IgnorePatternException.For($"{label} line {i + 1}", line,
                    "has a carriage return that does not end the line; end the lines with LF or CRLF");
        }
    }

    /// <summary>
    /// Blanks the lines that hold the replacement character (U+FFFD) a decoder writes for a byte that is not valid in
    /// the file's encoding, so they are skipped without moving the numbers of the lines after them, and counts them in
    /// one warning. A U+FFFD that the file holds itself is read the same way.
    /// </summary>
    private static void SkipUndecodedLines(List<string?> lines, string label, WarningSink warnings)
    {
        var replacement = (char)0xFFFD;
        var count = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i]!.Contains(replacement, StringComparison.Ordinal))
            {
                lines[i] = string.Empty;
                count++;
            }
        }

        if (count > 0)
            warnings.Add(IgnorePatternException.ForFile(label, count == 1
                ? "has 1 line with bytes that are not valid in the file's encoding; that line was skipped"
                : $"has {count:N0} lines with bytes that are not valid in the file's encoding; those lines were skipped").Message);
    }

    internal static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[8192];
            var n = fs.Read(buf);
            for (var i = 0; i < n; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return true; }
    }
}
