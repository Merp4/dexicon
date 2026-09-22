using System.Text;
using System.Text.RegularExpressions;

namespace Dexicon.Core.Indexing;

/// <summary>
/// gitignore-syntax path matching: <c>*</c>, <c>**</c>, <c>?</c>, <c>[a-z]</c>, a leading
/// <c>/</c> to anchor at the root, a trailing <c>/</c> to match directories only, and
/// <c>!</c> to negate. Later patterns win, which is what git does.
/// </summary>
public sealed partial class IgnoreRuleSet
{
    private readonly List<Rule> _rules = [];

    /// <param name="LiteralPrefix">
    /// The deepest path this rule is certain to sit under: its text up to the last path
    /// boundary before its first wildcard. Used to decide whether a negation could reach
    /// beneath a directory that is otherwise prunable.
    /// </param>
    /// <param name="MatchesAnyDepth">
    /// The pattern has no interior slash and no anchor, so it applies at every level:
    /// `*.md` re-includes a file anywhere, and nothing can be pruned on its account.
    /// </param>
    private sealed record Rule(
        Regex Pattern, bool Negated, bool DirectoryOnly, string Source,
        string LiteralPrefix, bool MatchesAnyDepth);

    public int Count => _rules.Count;

    public void AddPatterns(IEnumerable<string> patterns, string source)
    {
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

            _rules.Add(new Rule(
                new Regex(ToRegex(line), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)),
                negated, directoryOnly, source,
                LiteralPrefix: LiteralPrefixOf(bare),
                MatchesAnyDepth: !anchored && !bare.Contains('/', StringComparison.Ordinal)));
        }
    }

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

    internal static string ToRegex(string glob)
    {
        var anchored = glob.StartsWith('/');
        if (anchored) glob = glob[1..];

        // A pattern with no interior slash matches at any depth: `*.dll`, `node_modules`.
        var matchAtAnyDepth = !anchored && !glob.TrimEnd('/').Contains('/', StringComparison.Ordinal);

        var sb = new StringBuilder("^");
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
/// .gitignore, .dexiconignore, per-source globs, size cap, binary sniff. See
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

        var ignore = new IgnoreRuleSet();
        ignore.AddPatterns(AlwaysExclude, "always-exclude");
        if (useGitignore) AddLocalGitExcludes(ignore, root);
        if (useGitignore && File.Exists(Path.Combine(root, ".gitignore")))
            ignore.AddPatterns(File.ReadAllLines(Path.Combine(root, ".gitignore")), ".gitignore");
        if (File.Exists(Path.Combine(root, IgnoreFileName)))
            ignore.AddPatterns(File.ReadAllLines(Path.Combine(root, IgnoreFileName)), IgnoreFileName);
        if (excludeGlobs is { Count: > 0 }) ignore.AddPatterns(excludeGlobs, "source.exclude");

        var include = new IgnoreRuleSet();
        if (includeGlobs is { Count: > 0 }) include.AddPatterns(includeGlobs, "source.include");
        var hasInclude = include.Count > 0;

        foreach (var full in EnumerateFilesSafely(root, ignore, topLevelOnly))
        {
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');

            if (ignore.IsIgnored(relative, isDirectory: false)) continue;
            if (hasInclude && !include.IsIgnored(relative, isDirectory: false)) continue;

            FileInfo info;
            try { info = new FileInfo(full); }
            catch (Exception ex) { skipped.Add(new Skipped(relative, $"unreadable: {ex.Message}", 0)); continue; }

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
    /// or <c>exclude</c> would read a host file from inside a read-only mount. Every
    /// segment is tested against the same boundary <see cref="EnumerateFilesSafely"/>
    /// holds while it descends.
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
        if (!Directory.Exists(gitDir) || !StaysInside(new DirectoryInfo(gitDir), root)) return;

        var info = Path.Combine(gitDir, "info");
        if (!Directory.Exists(info) || !StaysInside(new DirectoryInfo(info), root)) return;

        var exclude = Path.Combine(info, "exclude");
        if (!File.Exists(exclude) || !StaysInside(new FileInfo(exclude), root)) return;

        try { ignore.AddPatterns(File.ReadAllLines(exclude), ".git/info/exclude"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// True when <paramref name="entry"/> is not a link, or is one whose target is still
    /// under <paramref name="root"/>. An entry that cannot be resolved is not inside.
    /// </summary>
    private static bool StaysInside(FileSystemInfo entry, string root)
    {
        if (entry.LinkTarget is null) return true;

        try
        {
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            return target is not null && CorpusIndexer.IsInside(Path.GetFullPath(target.FullName), root);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Enumerate without letting one unreadable directory abort the whole walk, and
    /// without following symlinks out of the root, since a link to / would otherwise index
    /// the entire filesystem.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafely(
        string root, IgnoreRuleSet ignore, bool topLevelOnly = false)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs;
            try { subdirs = topLevelOnly ? [] : Directory.GetDirectories(dir); }
            catch (Exception) { continue; }

            foreach (var sub in subdirs)
            {
                var info = new DirectoryInfo(sub);
                if (info.LinkTarget is not null)
                {
                    // Same directory-boundary test as the workspace root, and for the same
                    // reason: a symlink to a sibling that merely shares the root's name
                    // prefix is outside the tree being walked, however much of the string
                    // it has in common with it.
                    var target = Path.GetFullPath(info.ResolveLinkTarget(true)?.FullName ?? sub);
                    if (!CorpusIndexer.IsInside(target, root)) continue;
                }
                // Skipped rather than walked and thrown away. A repository carrying .git,
                // node_modules and a database's data directory enumerated 240,704 files to
                // keep 27,001, and every one of those was a stat across the mount: 99s
                // against 14s for the same result. Only where nothing beneath could be
                // re-included, because being wrong here means quietly not indexing
                // something that is indexed today.
                var relativeSub = Path.GetRelativePath(root, sub).Replace('\\', '/');
                if (ignore.IsIgnored(relativeSub, isDirectory: true)
                    && !ignore.MayReincludeBeneath(relativeSub))
                {
                    continue;
                }

                stack.Push(sub);
            }

            string[] entries;
            try { entries = Directory.GetFiles(dir); }
            catch (Exception) { continue; }

            foreach (var f in entries) yield return f;
        }
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
