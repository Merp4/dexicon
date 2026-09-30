using System.ComponentModel;
using System.Globalization;
using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dexicon.Mcp;

/// <summary>
/// Setting up what is indexed, for a key holding <c>configure</c>: the mounted folders, a new
/// corpus, and a corpus's sources and filters. Every change is judged by
/// <see cref="CorpusConfiguration"/>, as the UI's are, and logged with the key that made it.
///
/// Nothing here removes or deletes, so a mistake stays until someone removes it in the UI.
/// That is why creating is asked for with <c>create</c> rather than inferred from a name that
/// matched nothing, and why a folder has to exist before it can be added. A key without the
/// scope is not shown these tools (Program.cs), and each checks the scope itself as well.
/// See docs/decisions.md D-36.
/// </summary>
[McpServerToolType]
public sealed class ConfigureTools
{
    /// <summary>The tools a key without <see cref="Scopes.Configure"/> is not shown.</summary>
    internal static readonly string[] Names = ["list_folders", "configure_corpus", "configure_source"];

    /// <summary>What <c>reset</c> takes for a filter, and the name the API clears it by.</summary>
    private static readonly Dictionary<string, string> FilterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["include"] = "includeGlobs",
        ["exclude"] = "excludeGlobs",
        ["gitignore"] = "useGitignore",
        ["maxFileKb"] = "maxFileBytes",
    };

    /// <summary>What <c>reset</c> takes for a history setting, each returning to its default.</summary>
    private static readonly string[] HistoryNames =
        ["follow", "message", "stat", "diff", "maxDiffKb", "merges", "maxCommits", "keepIndexed", "since"];

    [McpServerTool(Name = "list_folders")]
    [Description("List the folders mounted for indexing, marking git repositories and the folders your corpora already index. Its paths are what configure_source takes as folder.")]
    public static async Task<string> ListFoldersAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        IOptions<DexiconOptions> opts,
        [Description("Folder to list, relative to the workspace root, e.g. repos. Omit for the top level.")] string? path = null,
        CancellationToken ct = default)
    {
        DexiconTools.Require(rc, Scopes.Configure);
        var root = opts.Value.Indexing.WorkspaceRoot;
        var full = ExistingFolder(root, path);

        var indexed = await IndexedFoldersAsync(scopes, db, rc.RequirePrincipal(), root, ct);
        var here = Folder(Path.GetRelativePath(Path.GetFullPath(root), full));

        var sb = new StringBuilder($"Folders in {(here.Length == 0 ? "the workspace root" : DexiconTools.OneLine(here))}");
        var hereNotes = new List<string>();
        if (IsRepository(full)) hereNotes.Add("git repository");
        if (indexed.TryGetValue(here, out var hereBy)) hereNotes.Add($"indexed by {hereBy}");
        if (hereNotes.Count > 0) sb.Append($" ({string.Join("; ", hereNotes)})");
        sb.Append(":\n");

        var folders = SystemEndpoints.FoldersIn(root, full);
        if (folders.Count == 0) sb.Append("  none\n");

        foreach (var f in folders)
        {
            var notes = new List<string>();
            if (f.ChildCount is { } n) notes.Add(n >= 500 ? "500+ entries" : $"{n:N0} {(n == 1 ? "entry" : "entries")}");
            if (IsRepository(Path.Combine(full, f.Name))) notes.Add("git repository");
            if (indexed.TryGetValue(Folder(f.RelativePath), out var by)) notes.Add($"indexed by {by}");
            sb.Append($"  {DexiconTools.OneLine(Folder(f.RelativePath))}/  {string.Join("; ", notes)}\n");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "configure_corpus")]
    [Description("Create a corpus, or change an existing one's description or the filters its sources inherit. A new corpus holds nothing until configure_source adds a folder. Removing a corpus is done in the Dexicon UI.")]
    public static async Task<string> ConfigureCorpusAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        CorpusConfiguration config,
        ILoggerFactory logs,
        [Description("Corpus name. A new one is what agents will pass to search_index: short, with no colon.")] string corpus,
        [Description("True to create the corpus. Without it, the corpus must exist.")] bool create = false,
        [Description("What the corpus holds, which list_corpora shows to agents deciding where to search.")] string? description = null,
        [Description("Glob patterns every source indexes only, e.g. **/*.md. Replaces the current list.")] string[]? include = null,
        [Description("Glob patterns every source leaves out, e.g. **/generated/**. Replaces the current list.")] string[]? exclude = null,
        [Description("Whether sources respect .gitignore files.")] bool? gitignore = null,
        [Description("Largest code or text file indexed, in KB. PDFs and other documents have a limit of their own.")] int? maxFileKb = null,
        [Description("Filters to return to the server's setting: include, exclude, gitignore, maxFileKb.")] string[]? reset = null,
        CancellationToken ct = default)
    {
        DexiconTools.Require(rc, Scopes.Configure);
        var principal = rc.RequirePrincipal();
        var resets = Resets(reset, FilterNames.Keys);
        Clashes(resets, ("include", include), ("exclude", exclude), ("gitignore", gitignore), ("maxFileKb", maxFileKb));
        var maxBytes = Kilobytes("maxFileKb", maxFileKb);
        var filtersGiven = include is not null || exclude is not null || gitignore is not null || maxFileKb is not null
                           || resets.Count > 0;

        Corpus target;
        if (create)
        {
            if (resets.Count > 0) throw new McpException("reset applies to a corpus that exists. Leave it out when creating one.");

            var created = await config.CreateCorpusAsync(new CreateCorpusRequest(corpus, description), ct);
            if (created.Refusal is { } refused) throw new McpException(refused.Detail);
            target = created.Value!;

            // A mapped key reaches only its corpora, so without this the corpus it just made
            // would be invisible to it. An unmapped key already reaches every corpus.
            if (await db.TokenCorpora.AnyAsync(tc => tc.TokenId == principal.TokenId, ct))
            {
                db.TokenCorpora.Add(new TokenCorpus { TokenId = principal.TokenId, CorpusId = target.Id });
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            target = await WritableAsync(scopes, principal, corpus, "Pass create: true to create it.", ct);
            if (description is null && !filtersGiven)
                return $"Nothing to change in corpus '{target.Name}': pass a description, a filter, or reset.";
        }

        var changed = new List<string>();
        if (description is not null && (create || description != target.Description)) changed.Add("description");

        var queued = false;
        if (filtersGiven || (!create && changed.Count > 0))
        {
            var current = target.DefaultsOf();
            var defaults = filtersGiven
                ? new CorpusDefaults(
                    resets.Contains("gitignore") ? null : gitignore ?? current.UseGitignore,
                    resets.Contains("maxFileKb") ? null : maxBytes ?? current.MaxFileBytes,
                    resets.Contains("include") ? null : include ?? current.IncludeGlobs,
                    resets.Contains("exclude") ? null : exclude ?? current.ExcludeGlobs)
                : null;

            // The description was set at creation, so only the filters are sent after it.
            var update = await config.UpdateCorpusAsync(target,
                new UpdateCorpusRequest(create ? null : description, defaults), ct);
            if (update.Refusal is { } refused) throw new McpException(refused.Detail);

            // The service queues a refresh for moved filters only when there are sources to read.
            if (update.Value)
            {
                changed.Add("the filters its sources inherit");
                queued = await db.Sources.AnyAsync(s => s.CorpusId == target.Id, ct);
            }
        }

        logs.CreateLogger("Dexicon.Configure").LogInformation(
            "Key {Key} {Action} corpus {Corpus}; set: {Changed}",
            principal.Name, create ? "created" : changed.Count > 0 ? "changed" : "left unchanged", target.Name,
            changed.Count == 0 ? "none" : string.Join(", ", changed));

        var filtersSet = changed.Contains("the filters its sources inherit");
        if (create)
            return $"Created corpus '{target.Name}'{(filtersSet ? ", with the filters given" : "")}. " +
                   $"It holds nothing yet: add a folder with configure_source(corpus: \"{target.Name}\", " +
                   "folder: ..., create: true). list_folders shows what is mounted.";

        if (changed.Count == 0)
            return $"Nothing changed in corpus '{target.Name}': the values sent are the ones it has.";

        return $"Changed {string.Join(" and ", changed)} of corpus '{target.Name}'." +
               (queued ? $" A refresh is queued; index_status(corpus: \"{target.Name}\") reports it." : "");
    }

    [McpServerTool(Name = "configure_source")]
    [Description("Add a folder to a corpus, or change how one of its folders is indexed. A files source reads the files under the folder; a history source reads a git repository's commits. A change to what is read queues a refresh. Removing a source is done in the Dexicon UI.")]
    public static async Task<string> ConfigureSourceAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        CorpusConfiguration config,
        IOptions<DexiconOptions> opts,
        ILoggerFactory logs,
        [Description("Corpus name, as given by list_corpora.")] string corpus,
        [Description("Folder relative to the workspace root, as list_folders and index_status show it.")] string folder,
        [Description("files reads the files under the folder; history reads its git commits. A repository wanted both ways takes one source of each.")] string kind = "files",
        [Description("True to add the folder as a new source. Without it, the source must exist.")] bool create = false,
        [Description("Glob patterns this source indexes only, e.g. **/*.cs. For history, the paths whose commits are kept. Replaces the list.")] string[]? include = null,
        [Description("Glob patterns this source leaves out. Files only.")] string[]? exclude = null,
        [Description("Whether .gitignore files are respected. Files only.")] bool? gitignore = null,
        [Description("Largest code or text file indexed, in KB. Files only.")] int? maxFileKb = null,
        [Description("History only: what each commit's document holds and which commits are read. Settings left out keep their value.")] HistorySettings? history = null,
        [Description("Settings to return to their default, by the names used here: include, exclude, gitignore, maxFileKb, or a history setting such as since or maxCommits.")] string[]? reset = null,
        CancellationToken ct = default)
    {
        DexiconTools.Require(rc, Scopes.Configure);
        var principal = rc.RequirePrincipal();

        var isHistory = kind.Trim().ToLowerInvariant() switch
        {
            "files" => false,
            "history" => true,
            _ => throw new McpException($"kind is files or history, not '{kind}'."),
        };

        var fileOnly = new[] { exclude is null ? null : "exclude", gitignore is null ? null : "gitignore", maxFileKb is null ? null : "maxFileKb" }
            .OfType<string>().ToList();
        if (isHistory && fileOnly.Count > 0)
            throw new McpException(
                $"A history source does not take {string.Join(", ", fileOnly)}: it is read by git log, and takes "
                + "include (the paths whose commits are kept) and history settings.");
        if (!isHistory && history is not null)
            throw new McpException("history settings apply to a history source. Pass kind: \"history\" for one.");

        IEnumerable<string> resettable = isHistory ? HistoryNames.Prepend("include") : FilterNames.Keys;
        var resets = Resets(reset, resettable);
        Clashes(resets, ("include", include), ("exclude", exclude), ("gitignore", gitignore), ("maxFileKb", maxFileKb));
        if (history is not null)
            Clashes(resets, ("follow", history.Follow), ("message", history.Message), ("stat", history.Stat),
                ("diff", history.Diff), ("maxDiffKb", history.MaxDiffKb), ("merges", history.Merges),
                ("maxCommits", history.MaxCommits), ("keepIndexed", history.KeepIndexed), ("since", history.Since));
        var maxBytes = Kilobytes("maxFileKb", maxFileKb);

        var target = await WritableAsync(scopes, principal, corpus, "", ct);
        var workspace = opts.Value.Indexing.WorkspaceRoot;
        var root = WorkspaceDiscovery.Canonical(workspace, folder);
        // A segment of "..", not any name starting with two dots: "..data" is a folder.
        if (root == ".." || root.StartsWith("../", StringComparison.Ordinal))
            throw new McpException($"'{folder}' is outside the workspace. list_folders shows what is mounted.");
        var wanted = isHistory ? SourceKind.GitHistory : SourceKind.Workspace;
        var what = $"{(isHistory ? "commit history of" : "files under")} {(root.Length == 0 ? "the workspace root" : DexiconTools.OneLine(root))}";

        var sources = await db.Sources.AsNoTracking().Where(s => s.CorpusId == target.Id).ToListAsync(ct);
        // Both sides canonical, so docs/. and x/../docs find the source on docs, and compared
        // as the filesystem compares them, so Docs does too where case is not significant.
        var existing = sources.Find(s => s.Kind == wanted
            && WorkspaceDiscovery.PathComparer.Equals(WorkspaceDiscovery.Canonical(workspace, s.RootPath), root));

        string action;
        string? jobId;
        string sourceId;
        if (create)
        {
            if (existing is not null)
                throw new McpException($"Corpus '{target.Name}' already has a source for the {what}. Leave out create to change it.");
            if (resets.Count > 0)
                throw new McpException("reset applies to a source that exists. Leave it out when adding one.");

            // A source added by mistake cannot be removed from here, so a typed path has to
            // name a folder that is there. The API allows an absent one, for a mount that is away.
            ExistingFolder(workspace, root);

            var added = await config.AddSourceAsync(target, new AddSourceRequest(
                root, gitignore, maxBytes, include, exclude,
                GitHistory: isHistory,
                Git: isHistory && history is not null ? Merge(new GitHistoryOptions(), history, []) : null), ct);
            if (added.Refusal is { } refused) throw new McpException(refused.Detail);

            action = "added";
            jobId = added.Value!.IndexJob.Id;
            sourceId = added.Value.Source.Id;
        }
        else
        {
            if (existing is null)
                throw new McpException(
                    $"Corpus '{target.Name}' has no source for the {what}. Its sources: "
                    + (sources.Count == 0 ? "none" : string.Join("; ", sources.Select(Describe)))
                    + ". Pass create: true to add it.");

            var historyResets = resets.Where(r => HistoryNames.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            var git = isHistory && (history is not null || historyResets.Count > 0)
                ? Merge(GitHistoryOptions.FromJson(existing.GitOptions), history, historyResets)
                : null;

            var updated = await config.UpdateSourceAsync(target, existing.Id, new UpdateSourceRequest(
                gitignore, maxBytes, include, exclude,
                Clear: resets.Where(FilterNames.ContainsKey).Select(r => FilterNames[r]).ToList(),
                Git: git), ct);
            if (updated.Refusal is { } refused) throw new McpException(refused.Detail);

            action = updated.Value!.IndexJob is null ? "left unchanged" : "changed";
            jobId = updated.Value.IndexJob?.Id;
            sourceId = existing.Id;
        }

        logs.CreateLogger("Dexicon.Configure").LogInformation(
            "Key {Key} {Action} the source for the {Source} in corpus {Corpus}; job {Job}",
            principal.Name, action, what, target.Name, jobId ?? "none");

        // As index_status shows it, so the effective values, and which come from the corpus,
        // are confirmed in the words the agent will read them in later.
        var summary = await CorpusEndpoints.Summarise(db, target, opts.Value.Indexing, ct);
        var shown = summary.Sources.Where(s => s.Id == sourceId).ToList();
        var line = shown.Count == 0 ? "" : DexiconTools.RenderSources(shown, summary.Defaults).Split('\n')[1].Trim();

        return action switch
        {
            "left unchanged" => $"Nothing changed: the source for the {what} in '{target.Name}' already has those settings, "
                                + $"so no refresh was queued.\n  {line}\n",
            _ => $"{(action == "added" ? $"Added a source for the {what} to" : $"Changed the source for the {what} in")} corpus '{target.Name}', "
                 + $"and queued a refresh as job {jobId}. As of now:\n  {line}\n"
                 + $"index_status(corpus: \"{target.Name}\") reports the refresh, and what it read once it has run.\n",
        };
    }

    /// <summary>
    /// The resolved folder, which must exist. Outside the workspace, or reached through a
    /// link, is refused by the resolver with its own reason.
    /// </summary>
    private static string ExistingFolder(string workspaceRoot, string? path)
    {
        string full;
        try { full = WorkspaceDiscovery.Resolve(workspaceRoot, path); }
        catch (UnauthorizedAccessException ex) { throw new McpException(ex.Message); }

        return Directory.Exists(full)
            ? full
            : throw new McpException($"There is no folder '{path}' under the workspace. list_folders shows what is mounted.");
    }

    private static async Task<Corpus> WritableAsync(
        ScopeResolver scopes, Principal principal, string corpus, string hint, CancellationToken ct)
    {
        try { return await scopes.ResolveWritableAsync(principal, corpus, ct); }
        catch (ScopeResolutionException ex) { throw new McpException($"{ex.Message} {hint}".TrimEnd()); }
    }

    /// <summary>
    /// Every folder a corpus this key can reach reads from, with who reads it. Corpora the
    /// key cannot reach are left out, so their names are not disclosed.
    /// </summary>
    private static async Task<Dictionary<string, string>> IndexedFoldersAsync(
        ScopeResolver scopes, CatalogDbContext db, Principal principal, string workspaceRoot, CancellationToken ct)
    {
        var names = (await scopes.VisibleAsync(principal, ct)).ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        var ids = names.Keys.ToList();

        var sources = await db.Sources.AsNoTracking()
            .Where(s => ids.Contains(s.CorpusId) && s.Kind != SourceKind.Upload)
            .Select(s => new { s.CorpusId, s.RootPath, s.Kind })
            .ToListAsync(ct);

        return sources
            .GroupBy(s => WorkspaceDiscovery.Canonical(workspaceRoot, s.RootPath), WorkspaceDiscovery.PathComparer)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g
                    .Select(s => $"{names[s.CorpusId]} ({(s.Kind == SourceKind.GitHistory ? "history" : "files")})")
                    .Order(StringComparer.Ordinal)),
                WorkspaceDiscovery.PathComparer);
    }

    /// <summary>A path as the catalogue and the listing both compare it: forward slashes, no outer ones.</summary>
    private static string Folder(string? path)
    {
        var p = (path ?? "").Replace('\\', '/').Trim('/');
        return p == "." ? "" : p;
    }

    /// <summary>
    /// A repository's root, which holds <c>.git</c>: a directory, or a file in a linked
    /// worktree. Found by listing the folder, which reads the entry without following it, so
    /// a <c>.git</c> that is a link does not count and is not looked through.
    /// </summary>
    private static bool IsRepository(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos(".git").FirstOrDefault() is { LinkTarget: null };
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>A source in the words the tools' replies use for it, the root named as such.</summary>
    private static string Describe(Source s)
    {
        var where = Folder(s.RootPath) is { Length: > 0 } f ? DexiconTools.OneLine(f) : "the workspace root";
        return s.Kind switch
        {
            SourceKind.GitHistory => $"commit history of {where}",
            SourceKind.Upload => "uploaded documents",
            _ => $"files under {where}",
        };
    }

    /// <summary>The names in <paramref name="reset"/>, each one checked against what it may name.</summary>
    private static HashSet<string> Resets(string[]? reset, IEnumerable<string> allowed)
    {
        var valid = allowed.ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in reset ?? [])
        {
            var known = valid.Find(v => string.Equals(v, name.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException($"reset takes {string.Join(", ", valid)}; '{name}' is not one of them here.");
            names.Add(known);
        }
        return names;
    }

    /// <summary>A setting both given a value and named in reset is a contradiction, refused rather than resolved.</summary>
    private static void Clashes(HashSet<string> resets, params (string Name, object? Value)[] given)
    {
        var both = given.Where(g => g.Value is not null && resets.Contains(g.Name)).Select(g => g.Name).ToList();
        if (both.Count > 0)
            throw new McpException($"{string.Join(", ", both)} is both set and reset. Send one or the other.");
    }

    /// <summary>A size in KB as the bytes the API stores, refused when it is not positive or does not fit.</summary>
    private static int? Kilobytes(string name, int? kb) => kb switch
    {
        null => null,
        <= 0 => throw new McpException($"{name} must be greater than zero. To follow the default instead, reset it."),
        > int.MaxValue / 1024 => throw new McpException($"{name} can be at most {int.MaxValue / 1024:N0}."),
        _ => kb * 1024,
    };

    /// <summary>
    /// A history source's settings with those sent applied over <paramref name="current"/>,
    /// and those reset returned to their defaults. The API replaces the settings whole, so
    /// the merge is made here.
    /// </summary>
    internal static GitHistoryOptions Merge(GitHistoryOptions current, HistorySettings? sent, IReadOnlyCollection<string> resets)
    {
        var d = new GitHistoryOptions();
        bool R(string name) => resets.Contains(name, StringComparer.OrdinalIgnoreCase);

        DateOnly? since = current.Since;
        if (sent?.Since is { } text)
        {
            since = DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : throw new McpException($"since is a date written yyyy-MM-dd, not '{text}'.");
        }

        return current with
        {
            Ref = R("follow") ? d.Ref : sent?.Follow?.Trim() ?? current.Ref,
            IncludeMessage = R("message") ? d.IncludeMessage : sent?.Message ?? current.IncludeMessage,
            IncludeStat = R("stat") ? d.IncludeStat : sent?.Stat ?? current.IncludeStat,
            IncludeDiff = R("diff") ? d.IncludeDiff : sent?.Diff ?? current.IncludeDiff,
            MaxDiffBytes = R("maxDiffKb") ? d.MaxDiffBytes : Kilobytes("maxDiffKb", sent?.MaxDiffKb) ?? current.MaxDiffBytes,
            IncludeMerges = R("merges") ? d.IncludeMerges : sent?.Merges ?? current.IncludeMerges,
            MaxCommits = R("maxCommits") ? d.MaxCommits : sent?.MaxCommits ?? current.MaxCommits,
            KeepIndexed = R("keepIndexed") ? d.KeepIndexed : sent?.KeepIndexed ?? current.KeepIndexed,
            Since = R("since") ? d.Since : since,
        };
    }
}

/// <summary>A history source's settings as configure_source takes them. One left out keeps its value.</summary>
public sealed record HistorySettings(
    [property: Description("The ref to follow: a branch such as main, origin/main for the remote's copy as of the host's last fetch, a tag, or HEAD for whatever is checked out.")]
    string? Follow = null,
    [property: Description("Index each commit's message.")]
    bool? Message = null,
    [property: Description("Index which files each commit changed, with line counts.")]
    bool? Stat = null,
    [property: Description("Index each commit's patch. On one repository this was about 13 times the size of the message and stat.")]
    bool? Diff = null,
    [property: Description("Largest patch indexed per commit, in KB. A larger one is left out, and the commit's document says so.")]
    int? MaxDiffKb = null,
    [property: Description("Index merge commits.")]
    bool? Merges = null,
    [property: Description("Read only this many commits back from the tip.")]
    int? MaxCommits = null,
    [property: Description("With maxCommits, keep a commit once it is indexed, rather than keeping only the newest maxCommits.")]
    bool? KeepIndexed = null,
    [property: Description("Only commits made on or after this date, written yyyy-MM-dd.")]
    string? Since = null);
