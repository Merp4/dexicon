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

    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly, string Source);

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

            _rules.Add(new Rule(new Regex(ToRegex(line), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)),
                negated, directoryOnly, source));
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
/// Walks a workspace tree applying, in order: always-exclude, .gitignore,
/// .dexiconignore, per-source globs, size cap, binary sniff. See docs/04-ingestion.md.
/// </summary>
public sealed class WorkspaceWalker
{
    public const string IgnoreFileName = ".dexiconignore";

    /// <summary>
    /// Fallback document cap for callers that do not pass one — tests, and any code path
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
        ".git/", ".hg/", ".svn/",
        "node_modules/", "bin/", "obj/", ".vs/", ".idea/", ".vscode/",
        "target/", "dist/", "build/", "__pycache__/", ".venv/", "venv/",
        "*.exe", "*.dll", "*.pdb", "*.so", "*.dylib", "*.o", "*.obj", "*.a", "*.lib",
        "*.zip", "*.tar", "*.gz", "*.7z", "*.rar", "*.jar", "*.nupkg",
        "*.woff", "*.woff2", "*.ttf", "*.eot", "*.otf",
        "*.ico", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.svg",
        "*.mp3", "*.mp4", "*.avi", "*.mov", "*.wav", "*.flac",
        // NOTE: .pdf/.docx/.pptx/.epub are deliberately NOT here — they are extracted.
        "*.db", "*.sqlite", "*.sqlite3",
        "*.safetensors", "*.gguf", "*.bin", "*.pt", "*.pth", "*.pkl", "*.npy", "*.npz",
        "*.lock", "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
    ];

    public sealed record Candidate(string FullPath, string RelativePath, long SizeBytes);

    public sealed record Skipped(string RelativePath, string Reason);

    public sealed record WalkResult(IReadOnlyList<Candidate> Files, IReadOnlyList<Skipped> SkippedFiles);

    public static WalkResult Walk(string rootPath, bool useGitignore, IReadOnlyList<string>? includeGlobs,
        IReadOnlyList<string>? excludeGlobs, long maxFileBytes, long? documentMaxBytes = null)
    {
        var root = Path.GetFullPath(rootPath);
        var files = new List<Candidate>();
        var skipped = new List<Skipped>();

        var ignore = new IgnoreRuleSet();
        ignore.AddPatterns(AlwaysExclude, "always-exclude");
        if (useGitignore && File.Exists(Path.Combine(root, ".gitignore")))
            ignore.AddPatterns(File.ReadAllLines(Path.Combine(root, ".gitignore")), ".gitignore");
        if (File.Exists(Path.Combine(root, IgnoreFileName)))
            ignore.AddPatterns(File.ReadAllLines(Path.Combine(root, IgnoreFileName)), IgnoreFileName);
        if (excludeGlobs is { Count: > 0 }) ignore.AddPatterns(excludeGlobs, "source.exclude");

        var include = new IgnoreRuleSet();
        if (includeGlobs is { Count: > 0 }) include.AddPatterns(includeGlobs, "source.include");
        var hasInclude = include.Count > 0;

        foreach (var full in EnumerateFilesSafely(root))
        {
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');

            if (ignore.IsIgnored(relative, isDirectory: false)) continue;
            if (hasInclude && !include.IsIgnored(relative, isDirectory: false)) continue;

            FileInfo info;
            try { info = new FileInfo(full); }
            catch (Exception ex) { skipped.Add(new Skipped(relative, $"unreadable: {ex.Message}")); continue; }

            if (info.Length > maxFileBytes && !Extraction.ExtractorRegistry.IsDocumentFormat(relative))
            {
                skipped.Add(new Skipped(relative,
                    $"over the {maxFileBytes:N0} byte size cap ({info.Length:N0} bytes)"));
                continue;
            }

            if (info.Length == 0) { skipped.Add(new Skipped(relative, "empty file")); continue; }

            // A PDF, DOCX, PPTX or EPUB IS binary — it just happens to have text inside
            // that we know how to get at. Sniffing it would silently drop every PDF in a
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
                        "Raise DEXICON__INDEXING__DOCUMENTMAXBYTES if you have the memory for it."));
                    continue;
                }
            }
            else if (LooksBinary(full))
            {
                skipped.Add(new Skipped(relative, "binary content (NUL byte in the first 8 KB)"));
                continue;
            }

            files.Add(new Candidate(full, relative, info.Length));
        }

        return new WalkResult(files, skipped);
    }

    /// <summary>
    /// Enumerate without letting one unreadable directory abort the whole walk, and
    /// without following symlinks out of the root — a link to / would otherwise index
    /// the entire filesystem.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (Exception) { continue; }

            foreach (var sub in subdirs)
            {
                var info = new DirectoryInfo(sub);
                if (info.LinkTarget is not null)
                {
                    var target = Path.GetFullPath(info.ResolveLinkTarget(true)?.FullName ?? sub);
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
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
