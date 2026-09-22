using System.Text;
using System.Text.RegularExpressions;

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
public sealed partial class IgnoreRuleSet
{
    private readonly List<Rule> _rules = [];

    public IgnoreRuleSet() { }

    /// <summary>
    /// A set carrying everything <paramref name="other"/> holds, which further patterns can
    /// then be added to without changing it.
    ///
    /// The walk needs this because a subdirectory's own ignore file applies to that subtree
    /// and to nothing beside it: its siblings keep the set their parent had. Copied rather
    /// than chained because a rule carries a compiled Regex and the copy is a reference to
    /// the same one, and because a directory holding an ignore file is rare enough that the
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
        Regex Pattern, bool Negated, bool DirectoryOnly, string Source,
        string LiteralPrefix, bool MatchesAnyDepth);

    public int Count => _rules.Count;

    /// <param name="directoryPrefix">
    /// Where the patterns were written, as a forward-slash path relative to the scan root,
    /// or empty for the root itself. Every rule is anchored beneath it, so `secret.txt` in
    /// `sub/.gitignore` is `sub/**/secret.txt` and cannot reach a sibling of `sub`.
    /// </param>
    public void AddPatterns(IEnumerable<string> patterns, string source, string directoryPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(directoryPrefix);

        foreach (var raw in patterns)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var negated = line.StartsWith('!');
            if (negated) line = line[1..];

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly) line = line[..^1];
            if (line.Length == 0) continue;

            var anchored = line.StartsWith('/');
            var bare = anchored ? line[1..] : line;

            // No anchor and no interior slash: it applies at every level beneath wherever
            // it was written. The deepest path it is CERTAIN to sit under is therefore that
            // directory and not the pattern's own text — `!keep.txt` in `sub/.gitignore`
            // matches `sub/deep/keep.txt`, so a LiteralPrefix of `sub/keep.txt` would let
            // `sub/deep` be pruned and the file it re-includes never be reached.
            var anyDepth = !anchored && !bare.Contains('/', StringComparison.Ordinal);

            _rules.Add(new Rule(
                new Regex(ToRegex(line, directoryPrefix), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)),
                negated, directoryOnly, source,
                LiteralPrefix: anyDepth ? directoryPrefix : Join(directoryPrefix, LiteralPrefixOf(bare)),
                MatchesAnyDepth: anyDepth && directoryPrefix.Length == 0));
        }
    }

    private static string Join(string prefix, string rest) =>
        prefix.Length == 0 ? rest
        : rest.Length == 0 ? prefix
        : prefix + "/" + rest;

    /// <param name="relativePath">Forward-slash path relative to the scan root.</param>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory && !ContainsDirectorySegmentMatch(rule, relativePath)) continue;
            if (!rule.Pattern.IsMatch(relativePath)) continue;
            ignored = !rule.Negated;
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
    /// </summary>
    public bool MayReincludeBeneath(string relativeDirectory)
    {
        foreach (var rule in _rules)
        {
            if (!rule.Negated) continue;
            if (rule.MatchesAnyDepth || rule.LiteralPrefix.Length == 0) return true;

            // The negation sits under this directory, or this directory sits under it.
            if (rule.LiteralPrefix.Equals(relativeDirectory, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.LiteralPrefix.StartsWith(relativeDirectory + "/", StringComparison.OrdinalIgnoreCase)) return true;
            if (relativeDirectory.StartsWith(rule.LiteralPrefix + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

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

    // `bin/` must exclude `bin/Debug/App.dll`, not just the directory entry itself.
    private static bool ContainsDirectorySegmentMatch(Rule rule, string relativePath)
    {
        var idx = 0;
        while (true)
        {
            var next = relativePath.IndexOf('/', idx);
            if (next < 0) return false;
            if (rule.Pattern.IsMatch(relativePath[..next])) return true;
            idx = next + 1;
        }
    }

    internal static string ToRegex(string glob) => ToRegex(glob, string.Empty);

    /// <param name="directoryPrefix">
    /// The directory the pattern was written in, relative to the scan root. Everything the
    /// glob would otherwise match at the root is matched beneath this instead, including
    /// the any-depth case: `secret.txt` in `sub/` is `sub/**/secret.txt`, never
    /// `other/secret.txt`.
    /// </param>
    internal static string ToRegex(string glob, string directoryPrefix)
    {
        var anchored = glob.StartsWith('/');
        if (anchored) glob = glob[1..];

        // A pattern with no interior slash matches at any depth: `*.dll`, `node_modules`.
        var matchAtAnyDepth = !anchored && !glob.TrimEnd('/').Contains('/', StringComparison.Ordinal);

        var sb = new StringBuilder("^");
        if (directoryPrefix.Length > 0) sb.Append(Regex.Escape(directoryPrefix)).Append('/');
        if (matchAtAnyDepth) sb.Append("(?:.*/)?");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                    {
                        var close = glob.IndexOf(']', i);
                        if (close < 0) { sb.Append("\\["); break; }
                        sb.Append(glob, i, close - i + 1);
                        i = close;
                        break;
                    }
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        // Matching a directory implies matching everything beneath it.
        sb.Append("(?:/.*)?$");
        return sb.ToString();
    }
}

/// <summary>
/// Walks a workspace tree applying, in order: always-exclude, .git/info/exclude,
/// .gitignore, .dexiconignore, per-source globs, size cap, binary sniff. The two ignore
/// files are read in every directory the walk reaches, deeper outranking shallower. See
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
    /// Hard-coded and not configurable. Nothing good comes of embedding a .dll, and
    /// making it configurable invites someone to try.
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

    public sealed record Candidate(string FullPath, string RelativePath, long SizeBytes);

    /// <param name="SizeBytes">
    /// What the walk measured, or 0 where it could not: the row a skip writes shows a
    /// size like any other, and for the two cap reasons the size IS the reason.
    /// </param>
    public sealed record Skipped(string RelativePath, string Reason, long SizeBytes);

    public sealed record WalkResult(IReadOnlyList<Candidate> Files, IReadOnlyList<Skipped> SkippedFiles);

    /// <param name="topLevelOnly">
    /// Files directly in <paramref name="rootPath"/> and no deeper. Used by
    /// <see cref="SourceCoverage"/>, which asks what is in a directory without descending
    /// into the subdirectories that already have sources of their own.
    /// </param>
    public static WalkResult Walk(string rootPath, bool useGitignore, IReadOnlyList<string>? includeGlobs,
        IReadOnlyList<string>? excludeGlobs, long maxFileBytes, long? documentMaxBytes = null,
        bool topLevelOnly = false)
    {
        var root = Path.GetFullPath(rootPath);
        var files = new List<Candidate>();
        var skipped = new List<Skipped>();

        // The tree's own rules, which a subdirectory's ignore file appends to as the walk
        // reaches it. The source's exclude globs are held back and put on the end of
        // whatever set is in force, so they keep outranking everything a repository says
        // about itself — including a nested file, which the operator has never seen.
        var treeRules = new IgnoreRuleSet();
        treeRules.AddPatterns(AlwaysExclude, "always-exclude");
        if (useGitignore) AddLocalGitExcludes(treeRules, root);
        // The root's own `.gitignore` and `.dexiconignore` are read by the walk, which
        // reaches the root before anything else and treats it like any other directory.

        var layer = Layer.Of(treeRules, excludeGlobs);

        var include = new IgnoreRuleSet();
        if (includeGlobs is { Count: > 0 }) include.AddPatterns(includeGlobs, "source.include");
        var hasInclude = include.Count > 0;

        foreach (var (full, ignore) in EnumerateFilesSafely(
                     root, layer, useGitignore, excludeGlobs, skipped, topLevelOnly))
        {
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
                        "Raise DEXICON__INDEXING__DOCUMENTMAXBYTES if you have the memory for it.", info.Length));
                    continue;
                }
            }
            else if (LooksBinary(full))
            {
                skipped.Add(new Skipped(relative, "binary content (NUL byte in the first 8 KB)", info.Length));
                continue;
            }

            files.Add(new Candidate(full, relative, info.Length));
        }

        return new WalkResult(files, skipped);
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
    private static void AddLocalGitExcludes(IgnoreRuleSet ignore, string root)
    {
        var gitDir = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDir) || IsLink(new DirectoryInfo(gitDir))) return;

        var info = Path.Combine(gitDir, "info");
        if (!Directory.Exists(info) || IsLink(new DirectoryInfo(info))) return;

        var exclude = Path.Combine(info, "exclude");
        if (!File.Exists(exclude) || IsLink(new FileInfo(exclude))) return;

        try { ignore.AddPatterns(File.ReadAllLines(exclude), ".git/info/exclude"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        public static Layer Of(IgnoreRuleSet tree, IReadOnlyList<string>? excludeGlobs)
        {
            if (excludeGlobs is not { Count: > 0 }) return new Layer(tree, tree);

            var effective = new IgnoreRuleSet(tree);
            effective.AddPatterns(excludeGlobs, "source.exclude");
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
        IReadOnlyList<string>? excludeGlobs, List<Skipped> skipped, bool topLevelOnly = false)
    {
        var stack = new Stack<(string Dir, string Prefix, Layer Layer)>();
        stack.Push((root, string.Empty, rootLayer));

        while (stack.Count > 0)
        {
            var (dir, prefix, layer) = stack.Pop();

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
            var gitignore = useGitignore ? Named(entries, ".gitignore") : null;
            var dexiconignore = Named(entries, IgnoreFileName);

            if (gitignore is not null || dexiconignore is not null)
            {
                var tree = new IgnoreRuleSet(layer.Tree);
                var added = AddIgnoreFile(tree, gitignore, ".gitignore", prefix);
                added |= AddIgnoreFile(tree, dexiconignore, IgnoreFileName, prefix);
                if (added) layer = Layer.Of(tree, excludeGlobs);
            }

            var ignore = layer.Effective;

            string[] subdirs;
            try { subdirs = topLevelOnly ? [] : Directory.GetDirectories(dir); }
            catch (Exception) { continue; }

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

                stack.Push((sub, relativeSub, layer));
            }

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
    /// Neither is read when it is a link: `File.ReadAllLines` follows one. Git makes the
    /// same call (<see cref="IsLink"/>).
    /// </summary>
    private static string? Named(IReadOnlyList<string> entries, string name)
    {
        foreach (var entry in entries)
            if (string.Equals(Path.GetFileName(entry), name, CorpusIndexer.PathComparison))
                return entry;

        return null;
    }

    private static bool AddIgnoreFile(
        IgnoreRuleSet rules, string? path, string name, string directoryPrefix)
    {
        if (path is null || IsLink(new FileInfo(path))) return false;

        try
        {
            rules.AddPatterns(File.ReadAllLines(path), name, directoryPrefix);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
