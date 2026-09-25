using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Indexing;

/// <summary>
/// What a git-history source indexes, and how much of each commit.
///
/// Every one of these is per source rather than global: a repository whose commit
/// messages are the record wants different settings from one where the diff is, and both
/// can be corpora on the same instance.
/// </summary>
public sealed record GitHistoryOptions
{
    /// <summary>The ref to walk. A branch, a tag, or a sha.</summary>
    public string Ref { get; init; } = "HEAD";

    /// <summary>
    /// The subject and body of each commit. Off, neither appears: the document is then
    /// the sha, the author, the date and whatever the stat and patch settings allow,
    /// which is a record of what changed with no record of why.
    /// </summary>
    public bool IncludeMessage { get; init; } = true;

    /// <summary>
    /// Which files each commit touched, with insertion and deletion counts. On by
    /// default: it is what answers "when did this file last change and why", it costs
    /// about a tenth of what the patch costs, and it survives a diff being too large to
    /// include.
    /// </summary>
    public bool IncludeStat { get; init; } = true;

    /// <summary>
    /// The patch. Off by default. Measured over 201 commits of this repository: with
    /// patches the history is 5.99 MB and the median commit 11,393 characters; with the
    /// message and the stat it is 449 KB and the median 1,939, which fits in one chunk.
    /// A repository where the diff is the point turns this on knowing the cost.
    /// </summary>
    public bool IncludeDiff { get; init; }

    /// <summary>
    /// Per commit. Over it the patch is left out and the document says so, in bytes,
    /// rather than being cut where a diff stops making sense. A generated file or a
    /// vendored directory in one commit is what this is for.
    /// </summary>
    public int MaxDiffBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Merge commits. Off by default: the default patch of a merge is empty and its
    /// message is usually generated, so they add rows that say nothing.
    /// </summary>
    public bool IncludeMerges { get; init; }

    /// <summary>Stop after this many commits from the tip, or null for all of them.</summary>
    public int? MaxCommits { get; init; }

    /// <summary>
    /// What <see cref="MaxCommits"/> means once commits are indexed.
    ///
    /// Off, the limit is a window: the source holds the newest that many, and each new
    /// commit pushes the oldest out on the next pass. On, the limit sets how far back the
    /// first pass reaches, and a commit once indexed stays for as long as the ref reaches
    /// it and the other settings select it. Rewritten history, a ref moved to another
    /// line, a later <see cref="Since"/> or narrower paths still remove it.
    ///
    /// Off by default, because a window is what a limit meant before this existed and a
    /// stored source should not change meaning under an upgrade. It does nothing without
    /// a limit, so it is refused there rather than stored and ignored.
    /// </summary>
    public bool KeepIndexed { get; init; }

    /// <summary>
    /// Only commits committed at or after 00:00 UTC on this date, or null for all of them.
    /// </summary>
    public DateOnly? Since { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, GitHistoryJson.Options);

    public static GitHistoryOptions FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new GitHistoryOptions()
            : JsonSerializer.Deserialize<GitHistoryOptions>(json, GitHistoryJson.Options)
              ?? new GitHistoryOptions();

    /// <summary>
    /// What the synthesized text depends on, so that a commit already indexed can be
    /// skipped without being synthesized to find out.
    ///
    /// A commit is immutable, so its sha plus this settles the content. Only settings
    /// that change what a document SAYS belong here, and the distinction is not
    /// cosmetic: anything added re-reads and re-embeds every commit in the repository
    /// the first time it changes.
    ///
    /// Out: <see cref="Ref"/>, <see cref="MaxCommits"/>, <see cref="KeepIndexed"/>,
    /// <see cref="Since"/> and <see cref="IncludeMerges"/>, which decide WHICH commits are
    /// indexed and not what any one of them holds. Turning merges on adds documents; it
    /// does not alter a single existing one.
    ///
    /// <see cref="MaxDiffBytes"/> only when there is a diff for it to cap.
    /// </summary>
    /// <param name="pathspecs">
    /// The resolved include filters. They are passed to git, so they decide which files
    /// the stat lists and which hunks the patch holds: the same commit under a narrower
    /// filter is a different document, and leaving them out left old commits skipped
    /// with a stat cut to paths nobody had selected any more.
    /// </param>
    public string ContentFingerprint(IReadOnlyList<string>? pathspecs = null) => string.Join(
        '|',
        IncludeMessage ? "m" : "-",
        IncludeStat ? "s" : "-",
        IncludeDiff ? "d" + MaxDiffBytes.ToString(CultureInfo.InvariantCulture) : "-",
        Encode(pathspecs));

    /// <summary>
    /// The pathspecs as one string that only one list can produce.
    ///
    /// A separator alone will not do: a pathspec is a caller's text and may contain any
    /// character, so joining on a comma makes <c>["a,b"]</c> and <c>["a", "b"]</c>
    /// identical. They are different filters, and a fingerprint that cannot tell them
    /// apart skips a commit whose stat was cut to the other one's paths.
    ///
    /// Each entry is written with its length in front, so the reader of the string could
    /// recover the list exactly — which is the property that makes a collision
    /// impossible rather than unlikely.
    /// </summary>
    private static string Encode(IReadOnlyList<string>? pathspecs)
    {
        if (pathspecs is not { Count: > 0 }) return "-";

        var sb = new StringBuilder();
        foreach (var p in pathspecs.OrderBy(p => p, StringComparer.Ordinal))
            sb.Append(p.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(p).Append(';');

        return sb.ToString();
    }
}

internal static class GitHistoryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One commit as the enumeration pass found it.</summary>
/// <param name="Sha">The full object name. The identity of the commit and of its document.</param>
/// <remarks>
/// No subject. It was asked for and nothing read it: the document's subject comes from
/// the message pass, the path is the date and the sha, and the fingerprint is the sha and
/// the settings, so the only readers this record's Subject ever had were its own tests.
///
/// It also made the inventory the last unbounded read. A subject is a caller's one-line
/// text, the enumeration asks for every commit in the repository at once, and there is no
/// commit count to size a ceiling from before the call that discovers it. Truncating in
/// the format pads as well as cuts — `%&lt;(200,trunc)%s` returns exactly 200 characters
/// for a 500-character subject and 200 for a five-character one — so bounding it here
/// would have made every ordinary repository's inventory larger to cap a pathological
/// one. Leaving it out bounds the read by construction: a sha and an ISO date is a fixed
/// cost per commit.
///
/// A file list showing subjects would be a real improvement, and this is not an argument
/// against it — it is an argument for asking for them where something displays them, with
/// a bound chosen then.
/// </remarks>
public sealed record GitCommit(string Sha, DateTimeOffset AuthorDate)
{
    /// <summary>
    /// The path this commit is indexed under, relative to the source.
    ///
    /// Dated first so the file list reads chronologically: a thousand commits ordered by
    /// hash is a list nobody can scan. The author date rather than the committer date,
    /// because it is what people mean by when, and a rebase that moves the committer date
    /// produces a new sha in any case.
    /// </summary>
    public string RelativePath =>
        $"commits/{AuthorDate:yyyy-MM-dd}-{Sha[..Math.Min(12, Sha.Length)]}";
}

/// <summary>
/// A directory git may be started in: one the workspace boundary passed and the
/// filesystem reported.
///
/// A string becomes one only through <see cref="GitHistory.RepositoryIn"/>, and every
/// call that starts a process takes this rather than a string, so a directory that has
/// not been through that factory cannot reach <see cref="Process"/> at all.
///
/// The callers hold their paths against the same boundary before they ever get here, and
/// that is not enough on its own: refusing a path at the API and in the sweep is
/// usability and defence in depth, and the decision has to be made again where the
/// process is actually started.
/// </summary>
public sealed class GitRepository
{
    private GitRepository(string fullPath, string workspaceRoot)
    {
        FullPath = fullPath;
        WorkspaceRoot = workspaceRoot;
    }

    /// <summary>
    /// The directory, as the filesystem spells it. Every character of it was produced by
    /// <see cref="Directory.EnumerateDirectories"/> rather than by combining a caller's
    /// text onto a root, which is the property that makes it safe to hand to another
    /// program as a working directory.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// The boundary this repository was resolved against. A file read from under its git
    /// directory is held to it too, because a <c>.git</c> file or a separate git directory
    /// can point anywhere: see <see cref="GitHistory.LastFetchAsync"/>.
    /// </summary>
    internal string WorkspaceRoot { get; }

    internal static GitRepository Of(string fullPath, string workspaceRoot) => new(fullPath, workspaceRoot);
}

/// <summary>
/// Reading a repository's history as documents, one per commit.
///
/// A commit, not a file at a revision: indexing every version of every file multiplies a
/// large repository by its history and re-indexes text that did not change. And not a
/// hunk: a hunk has no author and no subject, and the question people ask of history is
/// why something changed, which lives in the message.
///
/// Two PHASES, because a refresh has to be cheap. The first asks git only for shas and
/// dates and is the inventory; the second asks for the message, the stat and the patch of
/// the commits that are not already indexed. A single <c>git log -p</c> would produce every patch in the
/// repository in order to discover that nothing had changed. Measured on this repository,
/// 201 commits: 77ms to enumerate, 1,069ms to read all of them with their patches.
///
/// Phases, not processes. The inventory is one call; reading is one call per 100 commits
/// for the messages and another for the stat and patch when either is wanted, so a first
/// pass over 201 commits with diffs is 1 + 3 + 3. That is bounded by what is NEW, which
/// is the number that matters: a refresh with nothing to read is the one call.
///
/// The git binary rather than a library, because the document is <c>git show</c>'s layout
/// and a library returns structured objects: the hunk headers and stat columns would be
/// written by hand, and any difference from git would re-index every commit. Alpine is not
/// the reason, since LibGit2Sharp ships musl builds. D-34 weighs what a library would
/// remove.
/// </summary>
public static partial class GitHistory
{
    /// <summary>
    /// The directory <paramref name="relativePath"/> names under
    /// <paramref name="workspaceRoot"/>, or null if there is no such directory.
    /// <see cref="UnauthorizedAccessException"/> if it resolves outside the root or passes
    /// through a link.
    ///
    /// Null and the exception are different answers on purpose. A mount that is away is
    /// an operational condition and the callers report it as one, leaving what they
    /// indexed last time alone; a path that escapes the root is a refusal.
    ///
    /// Delegates to <see cref="WorkspaceDiscovery.ResolveExisting"/>, which applies the
    /// one containment rule and then re-derives the directory from the filesystem. The
    /// re-derivation is what keeps a caller's text out of the working directory handed to
    /// git: the boundary check decides whether the path is allowed, and enumeration
    /// decides what the string is.
    /// </summary>
    public static GitRepository? RepositoryIn(string workspaceRoot, string? relativePath) =>
        WorkspaceDiscovery.ResolveExisting(workspaceRoot, relativePath) is { } path
            ? GitRepository.Of(path, workspaceRoot)
            : null;

    /// <summary>
    /// Bodies read per git invocation. Bounds peak memory rather than process count: the
    /// patches of a hundred commits of this repository are about 3 MB, and the whole
    /// history of a large one would not fit in a string.
    /// </summary>
    private const int ReadBatch = 100;

    /// <summary>
    /// A git call that never returns must not hold an indexing slot for ever. Generous,
    /// because the second pass over a large batch is real work.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether this path is the ROOT of a git repository, which is what a source has to be.
    ///
    /// Not merely inside one. `rev-parse --is-inside-work-tree` says true from
    /// `/repo/src`, and a source pointed there would then walk the whole of `/repo`'s
    /// history: every commit of the parent repository indexed under a source the
    /// operator scoped to one directory, including commits that never touched it. The
    /// top level has to BE the path.
    /// </summary>
    public static async Task<bool> IsRepositoryAsync(GitRepository repo, CancellationToken ct)
    {
        if (!Directory.Exists(repo.FullPath)) return false;

        var (ok, stdout, _) = await RunAsync(repo, ["rev-parse", "--show-toplevel"], ct);
        if (!ok) return false;

        var top = stdout.Trim();
        if (top.Length == 0) return false;

        // git answers in forward slashes whatever the platform, and either side may carry
        // a trailing separator or a differently-cased drive letter on Windows.
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.FullPath)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// The largest cap a read can actually honour.
    ///
    /// The absolute ceiling less the stat's allowance, because a commit's read has to fit
    /// the patch AND the stat and headers that come with it. Accepting the ceiling itself
    /// made a cap that refutes itself: at 64 MiB, a 63 MiB patch is inside the cap, its
    /// read is 64 MiB plus the stat, the call is killed, the retry drops the patch, and
    /// the document says the diff was over a limit it was under.
    /// </summary>
    internal static long MaxDiffCap => AbsoluteCeiling - StatAllowance;

    /// <summary>
    /// A diff cap this will size a read from.
    ///
    /// Operator input, stored as JSON on the source and read back on every pass, so a
    /// value that arrived once is used for ever. Negative makes every ceiling negative
    /// and the document's own "over the limit" line quote a negative number; near
    /// <see cref="int.MaxValue"/> it used to overflow the ceiling's addition. Above the
    /// absolute ceiling it is a cap that can never be the binding one, which is a
    /// setting that silently does nothing.
    /// </summary>
    internal static bool IsAcceptableDiffCap(int value) => value >= 0 && value <= MaxDiffCap;

    /// <summary>
    /// A commit limit this will pass to git.
    ///
    /// Null means all of them and is the documented default. A number has to be at least
    /// one, because git accepts the other values and does something silently unhelpful
    /// with each: `--max-count=0` returns no commits and exits 0, so the source indexes
    /// nothing and reports no error, and `--max-count=-1` is treated as unlimited, so a
    /// negative limit quietly means the opposite of a limit.
    /// </summary>
    internal static bool IsAcceptableCommitLimit(int? value) => value is null or >= 1;

    /// <summary>
    /// One commit per document path, keeping the first of any that collide.
    ///
    /// A path is a date and twelve characters of a sha, so two commits on one day whose
    /// shas share a prefix are one document. Both being carried forward is worse than
    /// either being dropped: each deletes the other's chunks and upserts its own, so
    /// which survives depends on the order they were read in, and the fingerprint can
    /// end up naming a sha the stored text did not come from.
    ///
    /// Shared by the indexing pass and the sweep because they must agree. A sweep that
    /// counted both would report an inventory the index can never fill, which reads as a
    /// wrong count rather than as two passes differing. The order is git's, newest
    /// first, so the first of a colliding pair is the newer.
    /// </summary>
    public static IReadOnlyList<GitCommit> OnePerPath(IReadOnlyList<GitCommit> commits)
    {
        var seen = new HashSet<string>(commits.Count, StringComparer.Ordinal);
        var kept = new List<GitCommit>(commits.Count);

        foreach (var commit in commits)
            if (seen.Add(commit.RelativePath))
                kept.Add(commit);

        return kept;
    }

    private static readonly IReadOnlySet<string> NothingHeld = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The commits a pass works on, from the inventory: all of it, unless
    /// <see cref="GitHistoryOptions.KeepIndexed"/> is on, in which case the newest
    /// <see cref="GitHistoryOptions.MaxCommits"/> and every commit the source already
    /// holds.
    ///
    /// Only a commit the inventory lists can be kept, and the inventory is everything the
    /// ref reaches under the other settings. A held commit that is not in it is left out
    /// here and removed by the reconcile, exactly as it is without the setting. The sweep
    /// and the index pass both select through this, for the reason they share
    /// <see cref="OnePerPath"/>: a sweep that recorded a commit the index would drop
    /// reports an inventory the index can never fill.
    /// </summary>
    /// <param name="held">From <see cref="HeldAsync"/>.</param>
    public static IReadOnlyList<GitCommit> Select(
        IReadOnlyList<GitCommit> inventory, GitHistoryOptions options, IReadOnlySet<string> held)
    {
        if (!options.KeepIndexed || options.MaxCommits is not { } max) return inventory;

        // The limit plus what is held bounds the result, and so does the inventory, which in
        // keep mode is the whole history. Summed as long: the limit accepts int.MaxValue, and
        // as int the sum overflowed to a negative capacity.
        var kept = new List<GitCommit>((int)Math.Min(inventory.Count, (long)max + held.Count));
        for (var i = 0; i < inventory.Count; i++)
            if (i < max || held.Contains(inventory[i].RelativePath))
                kept.Add(inventory[i]);

        return kept;
    }

    /// <summary>
    /// The document paths a history source has rows for, which is what
    /// <see cref="Select"/> keeps. Rows rather than indexed states, so a commit whose read
    /// failed is kept and retried like any other failure. Not queried when
    /// <see cref="Select"/> would not read it.
    /// </summary>
    public static async Task<IReadOnlySet<string>> HeldAsync(
        CatalogDbContext db, string sourceId, GitHistoryOptions options, CancellationToken ct)
    {
        if (!options.KeepIndexed || options.MaxCommits is null) return NothingHeld;

        var paths = await db.Files.AsNoTracking()
            .Where(f => f.SourceId == sourceId)
            .Select(f => f.RelativePath)
            .ToListAsync(ct);

        return paths.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// What is wrong with these settings, or null when git can be asked with them.
    ///
    /// Every check here is static and none runs git, so an endpoint can refuse a request
    /// with it before starting a process. A ref that is well formed but names nothing is
    /// left to the inventory, the first thing that asks git, and shows as the source
    /// being unavailable with git's reason.
    /// </summary>
    public static string? Problem(GitHistoryOptions options) =>
        RefProblem(options.Ref) ?? OptionsProblem(options);

    /// <summary>
    /// Why this ref will not be passed to git, or null. The same words wherever a ref is
    /// refused: a setting sent to an endpoint, and a branch listed by
    /// <see cref="RefsAsync"/> that cannot be picked.
    /// </summary>
    internal static string? RefProblem(string? value) =>
        IsAcceptableRef(value) ? null : $"'{value}' is not a usable ref. A branch, a tag or an object name.";

    private static string? OptionsProblem(GitHistoryOptions options)
    {
        if (!IsAcceptableDiffCap(options.MaxDiffBytes))
            return $"maxDiffBytes is {options.MaxDiffBytes:N0}, which is not a usable cap. "
                 + $"It must be between 0 and {MaxDiffCap:N0}, which is the {AbsoluteCeiling:N0} "
                 + $"byte read ceiling less the {StatAllowance:N0} bytes a commit's stat may take.";

        if (!IsAcceptableCommitLimit(options.MaxCommits))
            return $"maxCommits is {options.MaxCommits:N0}, which is not a usable limit. "
                 + "It must be at least 1, or absent for every commit.";

        if (options.KeepIndexed && options.MaxCommits is null)
            return "keepIndexed applies only with maxCommits. Without a limit, every commit "
                 + "the ref reaches is indexed already.";

        return null;
    }

    /// <summary>The read needs no ref, since it is given shas; the other checks apply.</summary>
    private static void RequireUsableOptions(GitHistoryOptions options)
    {
        if (OptionsProblem(options) is { } problem) throw new GitHistoryException(problem);
    }

    /// <summary>
    /// A ref this will pass to git, or null.
    ///
    /// Conservative on purpose. `--end-of-options` already stops a ref being read as an
    /// option and arguments go as a list rather than a command line, so this is the third
    /// guard rather than the only one; what it adds is that the set of things that can
    /// reach git is small enough to read. Branch and tag names, `HEAD` and its `~`/`^`
    /// forms, and object names.
    /// </summary>
    internal static bool IsAcceptableRef(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 200
        && !value.StartsWith('-')
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c)
                          || c is '/' or '_' or '-' or '.' or '~' or '^' or '@' or '{' or '}');

    /// <summary>
    /// Every commit the settings select, newest first, as shas and dates. With
    /// <see cref="GitHistoryOptions.KeepIndexed"/> the commit limit is not applied here,
    /// and <see cref="Select"/> applies it.
    ///
    /// This is the inventory, and it is deliberately the cheap half: no patches, no
    /// bodies. A refresh of a repository whose tip has not moved reads this, finds every
    /// sha already indexed, and stops.
    /// </summary>
    public static async Task<IReadOnlyList<GitCommit>> EnumerateAsync(
        GitRepository repo, GitHistoryOptions options, IReadOnlyList<string>? pathspecs,
        CancellationToken ct)
    {
        // Here rather than only on the read path: every pass enumerates first, so a
        // source carrying a nonsense cap says so on its inventory instead of on a
        // partial read, and it says so the same way a bad ref does. A source stored
        // before the endpoints refused these still reaches this.
        if (Problem(options) is { } problem) throw new GitHistoryException(problem);

        var marker = Marker();
        // A sha and an ISO date, and deliberately nothing else. See the remark on
        // GitCommit: the subject was the one field here whose size a caller controls,
        // and this call has no commit count to size a ceiling from, because it is the
        // call that discovers the count.
        var args = new List<string> { "log", $"--format={marker}%H%x00%aI" };

        if (!options.IncludeMerges) args.Add("--no-merges");
        // Kept commits are checked against the whole reachable history, which is how one
        // the ref still reaches is told apart from one it no longer does. A list cut at the
        // limit would say the same thing about both.
        if (options.MaxCommits is { } max && !options.KeepIndexed) args.Add($"--max-count={max}");
        // Midnight UTC, spelled out. A bare date is not a day to git: it is that date at the
        // current time of day, in git's local zone. Measured at 08:56 UTC, `--since=2026-01-02`
        // dropped that day's commits from 00:30 and 06:00, so the boundary moved with every
        // scheduled refresh; and under TZ=Etc/GMT-14 a zone-less midnight admitted a commit
        // from 23:30 UTC the day before. Git 2.31 (Windows) and 2.54 (Linux) both read this
        // form as UTC whatever TZ says.
        if (options.Since is { } since)
            args.Add($"--since={since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00Z");

        // Before the ref, so a ref beginning with a dash is a ref and not an option.
        args.Add("--end-of-options");
        args.Add(options.Ref);

        // And after it, so a pathspec cannot be read as a ref.
        if (pathspecs is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(pathspecs);
        }

        var (ok, stdout, stderr) = await RunAsync(repo, args, ct);
        if (!ok) throw new GitHistoryException($"git log failed: {Summarise(stderr)}");

        var commits = new List<GitCommit>();

        foreach (var record in stdout.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\0');
            if (fields.Length < 2) continue;

            if (!DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                continue;

            commits.Add(new GitCommit(fields[0].Trim(), date));
        }

        return commits;
    }

    /// <summary>
    /// The document for each of <paramref name="shas"/>, in batches of one git process.
    ///
    /// Yielded as they are read rather than returned in one list, so the caller can chunk
    /// and embed a commit while the next batch is being read, and so a large history does
    /// not have to be held at once.
    /// </summary>
    public static async IAsyncEnumerable<(string Sha, string Text)> ReadAsync(
        GitRepository repo, GitHistoryOptions options, IReadOnlyList<string> shas,
        IReadOnlyList<string>? pathspecs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Again, because this is where the cap actually sizes a read and it is a public
        // entry point of its own. The enumeration reaching here first is how it happens
        // today, not a property this method can rely on.
        RequireUsableOptions(options);

        for (var start = 0; start < shas.Count; start += ReadBatch)
        {
            ct.ThrowIfCancellationRequested();

            var batch = shas.Skip(start).Take(ReadBatch).ToList();
            foreach (var read in await ReadBatchAsync(repo, options, batch, pathspecs, ct))
                yield return read;
        }
    }

    /// <summary>
    /// One batch, as two git calls rather than one.
    ///
    /// The formatted record and git's own output cannot be told apart inside one call.
    /// A commit message is arbitrary text: a body line beginning `diff --git ` or a
    /// leading-space line containing ` | ` reads exactly like the start of a patch or a
    /// stat, and a message holding the record separator splits a record in half. Both
    /// mistakes are silent and both corrupt a document.
    ///
    /// So the message is asked for on its own, with `--no-patch`, and the stat and patch
    /// are asked for under a format that emits ONLY a marker and a sha — no message text
    /// at all, so everything between two markers is git's and nothing has to be guessed.
    /// The second call is skipped entirely when neither is wanted.
    /// </summary>
    private static async Task<List<(string Sha, string Text)>> ReadBatchAsync(
        GitRepository repo, GitHistoryOptions options, List<string> shas,
        IReadOnlyList<string>? pathspecs, CancellationToken ct)
    {
        var marker = Marker();
        var stdin = string.Join('\n', shas);
        var known = shas.ToHashSet(StringComparer.Ordinal);

        Dictionary<string, string[]> messages;
        Dictionary<string, string> tails;

        try
        {
            messages = await MessagesAsync(
                repo, marker, stdin, known, options.IncludeMessage, MessageCeilingFor(shas.Count), ct);
            tails = options.IncludeStat || options.IncludeDiff
                ? await TailsAsync(repo, options, marker, shas, pathspecs, known, ct)
                : [];
        }
        catch (GitOutputTooLargeException ex)
        {
            // A read that outgrew its ceiling and could not be narrowed any further:
            // the message pass has no bisect, and the tail pass stops bisecting at one
            // commit whose stat alone is over. Turned into the kind the caller handles,
            // so the source reports unavailable with the reason on it. Left as it was,
            // it reached the job's generic catch and killed the whole job instead —
            // an outage written as a terminal state for the corpus.
            throw new GitHistoryException(
                $"A batch of {shas.Count} commits could not be read: {ex.Message}", ex);
        }

        var read = new List<(string, string)>(shas.Count);

        foreach (var sha in shas)
        {
            if (!messages.TryGetValue(sha, out var message)) continue;
            read.Add((sha, Document(options, message, tails.GetValueOrDefault(sha, string.Empty))));
        }

        return read;
    }

    /// <summary>
    /// A record separator no commit can hold by accident: a control character and a
    /// fresh GUID, per invocation. The alternative is a fixed byte, and a commit message
    /// is arbitrary text, so a fixed byte is a collision waiting for the one repository
    /// that contains it.
    /// </summary>
    private static string Marker() => "" + Guid.NewGuid().ToString("N") + "";

    /// <param name="known">
    /// The shas asked for. A record whose first field is not one of them is not a record:
    /// it is the tail of the previous commit's body, split by a marker that improbably
    /// appeared inside it. Re-joining it there is what keeps a message from being cut in
    /// half, and what keeps a commit from going missing — a commit the reader drops is a
    /// commit the shared reconcile sees as vanished and deletes the vectors of.
    /// </param>
    /// <param name="wantsMessage">
    /// Whether to ask git for the subject and body at all.
    ///
    /// Off, the format stops at the author's address. Asking for them anyway and
    /// letting <see cref="Document"/> drop them meant a source that had switched
    /// messages OFF still read every message, paid for it on every refresh, and could
    /// be made unavailable by one large message it had already decided not to index.
    ///
    /// The re-joining below goes with them: it exists because a body can contain
    /// anything, including the marker. Every remaining field is git's own formatting of
    /// a sha, a date and an identity, so a short record is not a split body and there
    /// is nothing to put back.
    /// </param>
    private static async Task<Dictionary<string, string[]>> MessagesAsync(
        GitRepository repo, string marker, string stdin, HashSet<string> known,
        bool wantsMessage, long ceiling, CancellationToken ct)
    {
        var format = wantsMessage
            ? $"--format={marker}%H%x00%aI%x00%an%x00%ae%x00%s%x00%b"
            : $"--format={marker}%H%x00%aI%x00%an%x00%ae";

        var (ok, stdout, stderr) = await RunAsync(repo,
            ["log", "--no-walk", "--stdin", "--no-color", "--no-patch", format],
            ct, stdin, ceiling);

        if (!ok) throw new GitHistoryException($"git log --stdin failed: {Summarise(stderr)}");

        var fieldCount = wantsMessage ? 6 : 4;
        var messages = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? last = null;

        foreach (var record in stdout.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\0', fieldCount);

            if (fields.Length == fieldCount && known.Contains(fields[0].Trim()))
            {
                last = fields[0].Trim();

                // Padded to the six Document reads, so the caller's shape does not
                // depend on which format produced it.
                messages[last] = wantsMessage ? fields : [.. fields, string.Empty, string.Empty];
                continue;
            }

            // Put back what the split took out, marker included, so the body is the
            // body byte for byte.
            if (wantsMessage && last is not null) messages[last][5] += marker + record;
        }

        return messages;
    }

    /// <summary>
    /// The stat and the patch, under a format carrying nothing but the marker and the
    /// sha. Everything between one sha and the next marker is git's own output, so there
    /// is no boundary to infer and no message text that can imitate one.
    /// </summary>
    private static async Task<Dictionary<string, string>> TailsAsync(
        GitRepository repo, GitHistoryOptions options, string marker, List<string> shas,
        IReadOnlyList<string>? pathspecs, HashSet<string> known, CancellationToken ct)
    {
        if (shas.Count == 0) return [];

        try
        {
            return await ReadTailsAsync(repo, options, marker, shas, pathspecs, known,
                CeilingFor(options, shas.Count), ct);
        }
        catch (GitOutputTooLargeException) when (shas.Count > 1)
        {
            // Which commit produced it is not reported, so halve until it is alone. The
            // same shape as the embedder's refusal handling: log2 cheap attempts rather
            // than one call per commit.
            var half = shas.Count / 2;
            var left = await TailsAsync(repo, options, marker,
                shas.GetRange(0, half), pathspecs, known, ct);
            var right = await TailsAsync(repo, options, marker,
                shas.GetRange(half, shas.Count - half), pathspecs, known, ct);

            foreach (var (sha, tail) in right) left[sha] = tail;
            return left;
        }
        catch (GitOutputTooLargeException) when (options.IncludeDiff)
        {
            // One commit, and its patch will not fit. Read it again without one: the
            // stat is what still answers "which files", and `Document` sees a tail with
            // no `diff --git` in it and says the patch was left out. Which is what
            // MaxDiffBytes means — and now it is a bound on memory as well as on the
            // document, because the patch was never materialised.
            var stat = await ReadTailsAsync(repo, options with { IncludeDiff = false },
                marker, shas, pathspecs, known, CeilingFor(options with { IncludeDiff = false }, 1), ct);

            foreach (var sha in shas)
                stat[sha] = (stat.GetValueOrDefault(sha, string.Empty).TrimEnd() + '\n'
                             + OverCeiling(options)).TrimStart('\n');

            return stat;
        }
    }

    /// <summary>
    /// How much output one call may produce before git is killed.
    ///
    /// Sized from the per-commit cap times the batch, capped absolutely. The slack in
    /// the per-commit figure is the stat, the headers and the marker.
    ///
    /// What this bounds is one CALL, not one commit, and the difference is worth being
    /// exact about. At the default 64 KiB cap a batch of a hundred asks for 106 MB and
    /// gets the 64 MB ceiling, so a single 50 MB patch inside that batch is under the
    /// ceiling, is read in full, and is only then dropped by <see cref="Document"/> for
    /// being over the cap. Peak memory is held at the absolute ceiling; the per-commit
    /// bound is what the bisect in <see cref="TailsAsync"/> arrives at once a call has
    /// already been refused, and it is not in force before that.
    /// </summary>
    private static long CeilingFor(GitHistoryOptions options, int commits)
    {
        // long before the addition, not after. MaxDiffBytes is operator input and an
        // int: near int.MaxValue the sum wrapped negative, and a negative ceiling is a
        // read that stops on its first character. AcceptableDiffCap refuses the value
        // as well; this is the arithmetic not depending on that having happened.
        var perCommit = (long)StatAllowance + (options.IncludeDiff ? options.MaxDiffBytes : 0);
        return Math.Min(perCommit * commits, AbsoluteCeiling);
    }

    /// <summary>
    /// What the message pass may produce. A commit message is arbitrary text and this
    /// call asks for a hundred of them, so it was the one read with no bound at all: a
    /// repository with a large enough message could exhaust the indexer before anything
    /// decided the document was too big.
    ///
    /// Sized like the stat, which is the other thing measured per commit rather than
    /// shaped by a setting. There is no bisect behind it, so exceeding this fails the
    /// batch with a reason rather than narrowing to the commit responsible.
    /// </summary>
    private static long MessageCeilingFor(int commits) =>
        Math.Min((long)StatAllowance * commits, AbsoluteCeiling);

    /// <summary>
    /// What a commit's stat and headers may take, on top of any patch budget.
    ///
    /// Its own allowance rather than a share of the diff's, because the cap is on the
    /// patch and the stat is what survives the patch being dropped. Folded together, a
    /// commit with a long stat and a small patch was killed for exceeding a limit its
    /// patch was inside, and the stat-only retry was then given the same small budget
    /// and could fail as well — losing the one part the cap promises to keep.
    ///
    /// Generous, because a stat is one line per file and a commit that touches a
    /// vendored tree has thousands: a stat line is on the order of 80 characters, so a
    /// megabyte is something like twelve thousand files in one commit. It bounds memory
    /// rather than shaping a document, and it is the floor of what a single commit's
    /// read may buffer.
    /// </summary>
    private const int StatAllowance = 1024 * 1024;

    private const long AbsoluteCeiling = 64L * 1024 * 1024;

    private static string OverCeiling(GitHistoryOptions options) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[diff not included: over the {options.MaxDiffBytes:N0} byte limit for this source]");

    private static async Task<Dictionary<string, string>> ReadTailsAsync(
        GitRepository repo, GitHistoryOptions options, string marker, List<string> shas,
        IReadOnlyList<string>? pathspecs, HashSet<string> known, long ceiling, CancellationToken ct)
    {
        var args = new List<string>
        {
            // --no-textconv, because a repository's own settings must not run a command.
            // A `diff=<driver>` attribute plus a `diff.<driver>.textconv` in the
            // repository's config makes git run that command on every blob it diffs, and
            // both live inside the repository being read. Measured on a scratch repo:
            //
            //     *.bin diff=evil        in .gitattributes
            //     diff.evil.textconv = echo PWNED-BY-TEXTCONV
            //
            //     $ git log -1 --patch
            //     -PWNED-BY-TEXTCONV /tmp/PiWeOe_a.bin
            //
            // so the command runs and its output is what gets indexed. With
            // --no-textconv the same call prints the file's real content.
            //
            // `diff.external` is NOT the same case and needs no flag: git log ignores it
            // unless --ext-diff is passed, confirmed on the same repo.
            "log", "--no-walk", "--stdin", "--no-color", "--no-textconv", $"--format={marker}%H",
        };

        if (options.IncludeDiff) args.Add("--patch");
        // The width, given explicitly. `diff.statWidth` and its siblings are repository
        // config, and an option beats config; `--stat=80` is byte-identical to `--stat`
        // here, because 80 is what git uses when the output is not a terminal.
        if (options.IncludeStat) args.Add("--stat=80");
        if (!options.IncludeDiff && !options.IncludeStat) args.Add("--no-patch");

        if (pathspecs is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(pathspecs);
        }

        var (ok, stdout, stderr) = await RunAsync(
            repo, args, ct, string.Join('\n', shas), ceiling);

        if (!ok) throw new GitHistoryException($"git log --stdin failed: {Summarise(stderr)}");

        var tails = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var record in stdout.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = record.IndexOf('\n');
            if (newline < 0) continue;

            var sha = record[..newline].Trim();
            if (!known.Contains(sha)) continue;

            tails[sha] = record[(newline + 1)..].Trim('\n');
        }

        return tails;
    }

    /// <summary>
    /// The commit as a document, laid out the way <c>git show</c> lays it out.
    ///
    /// Not an invented format. Every model that has read code has read thousands of these,
    /// and a layout of our own would have to be explained to each one; the cost of
    /// following git is a few lines of formatting.
    /// </summary>
    private static string Document(GitHistoryOptions options, string[] fields, string tail)
    {
        var (sha, date, name, email, subject, body) =
            (fields[0].Trim(), fields[1], fields[2], fields[3], fields[4], fields[5]);

        var sb = new StringBuilder();
        sb.Append("commit ").Append(sha).Append('\n');
        sb.Append("Author: ").Append(name).Append(" <").Append(email).Append(">\n");
        sb.Append("Date:   ").Append(date).Append('\n');

        // The message means the subject AND the body, which is what the setting is
        // named for. Keeping the subject when it is off left the option unable to do
        // what it says, and made it change the fingerprint of every body-less commit
        // while producing the same text — a re-embed of the whole history for nothing.
        // A document without it is still identifiable: it carries the sha, the author,
        // the date and the stat.
        if (options.IncludeMessage)
        {
            sb.Append('\n');
            foreach (var line in (subject + "\n\n" + body).TrimEnd().Split('\n'))
                sb.Append("    ").Append(line.TrimEnd()).Append('\n');
        }

        if (tail.Length == 0) return sb.ToString();

        // The cap is over the PATCH, in the bytes the cap is named in.
        //
        // Measuring the stat as well let a long stat drop a patch that was inside the
        // limit; measuring UTF-16 characters let a non-ASCII patch pass a byte cap it
        // exceeded. The stat is what is left when the patch is removed, and it is never
        // dropped: it is the cheap half and it is what still answers "which files" when
        // the patch does not fit.
        var (stat, patch) = SplitPatch(tail);
        var patchBytes = Encoding.UTF8.GetByteCount(patch);

        if (options.IncludeDiff && patchBytes > options.MaxDiffBytes)
        {
            if (stat.Length > 0) sb.Append('\n').Append(stat.TrimEnd()).Append('\n');

            // Said, not cut. A patch truncated mid-hunk reads as a complete change that
            // did something different from what it did.
            sb.Append('\n')
              .Append(CultureInfo.InvariantCulture, $"[diff of {patchBytes:N0} bytes not included: over the {options.MaxDiffBytes:N0} byte limit for this source]")
              .Append('\n');

            return sb.ToString();
        }

        sb.Append('\n').Append(tail.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Git's trailing output split where the patch begins.
    ///
    /// Safe here in a way it was not against a commit message: this text came from the
    /// sha-only format, so every line of it is git's. `diff --git ` at the start of a
    /// line is then the patch and nothing else.
    /// </summary>
    /// <remarks>
    /// -1 for "no patch here", not 0. The two used to be the same value: a tail that
    /// BEGINS with the header gave 0, and so did the not-found branch, so the whole
    /// patch was returned as the stat and <c>Document</c> measured a patch of zero bytes
    /// against the cap — which is a cap that never fires.
    ///
    /// Not reachable as the format stands. The tail is what follows the sha's line, so
    /// it opens with that line's own newline and the header is at index 1; measured by
    /// reading the first bytes of a real `--patch` tail, which are 10, 'd', 'i', 'f'.
    /// It is fixed because the collision is in the function rather than in the format:
    /// anything that trims the tail or drops the newline turns a silent cap into the
    /// behaviour, with nothing failing to say so.
    /// </remarks>
    internal static (string Stat, string Patch) SplitPatch(string tail)
    {
        var at = tail.StartsWith("diff --git ", StringComparison.Ordinal)
            ? 0
            : tail.IndexOf("\ndiff --git ", StringComparison.Ordinal) is var found && found >= 0
                ? found + 1
                : -1;

        return at < 0 ? (tail, string.Empty) : (tail[..at], tail[at..]);
    }

    private static string Summarise(string stderr)
    {
        var line = stderr.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "no output";
        return line.Length <= 300 ? line : line[..300] + "…";
    }

    /// <summary>
    /// Runs git with an argument LIST, never a command line: nothing here is quoted or
    /// escaped by us, so a ref or a pathspec cannot become another argument. What it could
    /// still become is a git option, which is what <c>--end-of-options</c> and <c>--</c>
    /// are for at the call sites.
    /// </summary>
    private static async Task<(bool Ok, string Stdout, string Stderr)> RunAsync(
        GitRepository repo, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null,
        long? maxBytes = null, TimeSpan? timeout = null)
    {
        var info = StartInfo(repo, args, redirectStdin: stdin is not null);
        var limit = timeout ?? Timeout;

        using var process = new Process { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitHistoryException(
                "git could not be started. A git-history source needs the git binary on PATH.", ex);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);

        // Both streams read concurrently. Waiting for exit with either pipe unread is the
        // classic deadlock: git fills the buffer and blocks, and nothing drains it.
        var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
        var stdout = new StringBuilder();
        var over = false;
        var read = 0L;

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }

            // Read in chunks rather than to the end, so a ceiling can be enforced while
            // the output is arriving. Reading it all and measuring afterwards is not a
            // bound: by then it is allocated.
            var buffer = new char[32 * 1024];
            while (true)
            {
                var n = await process.StandardOutput.ReadAsync(buffer, deadline.Token);
                if (n == 0) break;

                // Counted in UTF-8 bytes, because that is the unit every ceiling here is
                // named in: MaxDiffBytes is the operator's setting and StatAllowance and
                // AbsoluteCeiling are sized against it. Against StringBuilder.Length it
                // was UTF-16 code units, so a patch of three-byte characters read three
                // times the stated ceiling before being stopped — the same mistake
                // Document already carries a comment about, one measurement further up.
                //
                // Per chunk rather than over the whole buffer again, so the cost is one
                // pass over what was just read.
                if (maxBytes is { } cap)
                {
                    read += Encoding.UTF8.GetByteCount(buffer, 0, n);
                    if (read > cap)
                    {
                        over = true;
                        break;
                    }
                }

                stdout.Append(buffer, 0, n);
            }

            if (over)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new GitOutputTooLargeException(maxBytes!.Value);
            }

            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // Killed either way, and that is the point of catching both.
            //
            // A cancelled wait abandons the readers and returns; the child keeps running,
            // now writing into pipes nobody drains, so it fills them and blocks for as
            // long as the process lives. Leaving that to the timeout branch alone meant a
            // cancelled JOB — a stopped index, a lost lease, a shutdown — leaked a git
            // process per call, which is the case most likely to produce several.
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

            // The caller's cancellation is the caller's to see. Only the deadline is this
            // method's own failure, and only that becomes an exception of ours.
            ct.ThrowIfCancellationRequested();
            throw new GitHistoryException(limit >= TimeSpan.FromMinutes(1)
                ? $"git did not finish within {limit.TotalMinutes:N0} minutes."
                : $"git did not finish within {limit.TotalSeconds:N0} seconds.");
        }

        return (process.ExitCode == 0, stdout.ToString(), await stderrTask);
    }

    /// <summary>
    /// How every git call here is started: the repository, the argument list, and the
    /// configuration pinned around it. Its own method so the pins can be read by a test
    /// without a git that shows the difference each one makes.
    /// </summary>
    internal static ProcessStartInfo StartInfo(GitRepository repo, IReadOnlyList<string> args, bool redirectStdin)
    {
        // A GitRepository and not a string: the only way to make one is
        // GitHistory.RepositoryIn, which holds the path against the workspace boundary.
        // The file name is the literal "git" and the arguments go as a list, so nothing
        // a source can set becomes an argument or a command line.
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo.FullPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) info.ArgumentList.Add(arg);

        // safe.directory, per invocation, or nothing here works in the shipped container.
        //
        // The image runs as uid 10001 and /workspaces is a host bind mount, so the
        // repository is owned by somebody else and git refuses it outright:
        //
        //     fatal: detected dubious ownership in repository at '/w'
        //
        // Measured, not assumed: `docker run -u 10001:10001 -v <repo>:/w alpine/git`
        // fails exactly that way and succeeds with `-c safe.directory=/w`. Without this
        // every history source reports unavailable on a normal deployment, while every
        // test passes, because a test runs as the user who owns the repository.
        //
        // `-c` rather than `git config --global`: this is one call's configuration, it
        // names the one repository, and it cannot be left behind for another.
        info.ArgumentList.Insert(0, "safe.directory=" + repo.FullPath);
        info.ArgumentList.Insert(0, "-c");

        // How a diff is PRESENTED is pinned, because the fingerprint says a commit's
        // document is settled by its sha and this source's settings, and a repository
        // can change the text of the same sha by changing its own config. Measured with
        // `core.quotePath`, which is the one most likely to be set for real:
        //
        //     unpinned   "\346\274\242.txt" | 1 +
        //     pinned     漢.txt | 1 +
        //
        // The pre-read skip then holds a document that no longer matches what git would
        // produce, and it is the path that avoids looking, so nothing notices. Pinned
        // rather than folded into the fingerprint: a config change would otherwise
        // re-read and re-embed an entire history, and an index wants the document to be
        // a function of the commit rather than of how somebody likes their diffs.
        //
        // Each value is git's own default, so this changes nothing for a repository that
        // has not set them. `core.quotePath=false` is the exception and is an
        // improvement: a non-ASCII path is indexed as itself rather than as octal.
        //
        // Known gaps, written down because a rule with no stated carve-out gets one
        // invented at the first hard case:
        //
        // - `diff.orderFile` reorders the files within a diff. `-c diff.orderFile=` is
        //   `fatal: failed to read orderfile ''` and git has no value meaning "none",
        //   so it would need a temporary empty file per call.
        // - `diff.statNameWidth` and `diff.statGraphWidth` size the columns either side
        //   of the stat's name. The width itself is pinned by passing `--stat=80`, an
        //   option, which beats config; these two have no option that does not also
        //   change the output. On git 2.31.1 neither took effect at all — set by `-c`
        //   and in the repository's own config, the stat was byte-identical, while
        //   `--stat=80,12` truncated as expected, so the mechanism is there and the
        //   config path is not honoured on that version. A newer git in the container
        //   may differ, which is why they are listed rather than dismissed.
        // - `diff.renameLimit` silently stops rename detection once a commit is big
        //   enough, and that changes the stat: measured, a rename reads
        //   `{old => new} | 0` with detection on and `old | 3 ---` with it off. Pinning
        //   it means choosing a number, and the default varies by git version, so
        //   pinning could itself change behaviour rather than preserve it.
        foreach (var pin in new[]
                 {
                     "core.quotePath=false", "diff.algorithm=myers", "diff.renames=true",
                     "diff.context=3", "diff.noprefix=false", "diff.mnemonicPrefix=false",
                     "diff.indentHeuristic=true", "diff.relative=false", "diff.submodule=short",
                 })
        {
            info.ArgumentList.Insert(0, pin);
            info.ArgumentList.Insert(0, "-c");
        }

        // log.showSignature, because verifying a signature means running a program the
        // repository names. `log.showSignature=true` and `gpg.program=<anything>` are
        // both settable in the repository being read, and together they make git execute
        // that program for any commit carrying a gpgsig header. Measured: a fake gpg
        // left its marker file behind, and with this flag it did not. An unsigned commit
        // does not trigger it, so the test builds the signed object by hand.
        //
        // Off rather than left alone: the verification output is not the commit, and
        // nothing here asks whether a signature is good.
        info.ArgumentList.Insert(0, "log.showSignature=false");
        info.ArgumentList.Insert(0, "-c");

        // --no-replace-objects, because the fingerprint says a sha settles the content
        // and a replace ref makes that false. `git replace <old> <new>` is honoured by
        // every read by default, so the same sha yields different text and the pre-read
        // skip keeps a document nobody would recognise. Measured:
        //
        //     $ git log -1 --format=%s <sha>                        REPLACEMENT MESSAGE
        //     $ git --no-replace-objects log -1 --format=%s <sha>   ORIGINAL MESSAGE
        //
        // Disabled rather than folded into the fingerprint: a replacement is a local
        // view of history, and indexing the object the sha names is what makes the sha
        // an identity at all.
        info.ArgumentList.Insert(0, "--no-replace-objects");

        // A repository someone else configured is not ours to trust with hooks, aliases
        // or a pager. --no-pager keeps the call to what was asked.
        //
        // Aliases need nothing here while the subcommands are `log` and `rev-parse`:
        // git ignores an alias that shadows a builtin. Measured on a repository with
        // `alias.log = !echo PWNED` and `alias.lg = !echo PWNED`, where `git log` ran
        // the builtin and `git lg` printed PWNED — the second is the control, without
        // which the first proves only that the probe missed. A NON-builtin subcommand
        // added here later would be expandable, and would need the alias disabled.
        info.ArgumentList.Insert(0, "--no-pager");
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        // The C locale, so what git prints does not depend on the process's language.
        // The stat's summary line (" 1 file changed") is translated where git has
        // translations, and it goes into a commit's document, which the fingerprint does
        // not cover. Measured on the shipped image's git 2.54.0 and Git for Windows
        // 2.31.1: neither has translations, so this changes nothing there. A deployment
        // outside the image, on a host whose git does, would otherwise index the same
        // commit in whichever language the service was started in. LANGUAGE goes too:
        // gettext prefers it to LC_ALL for messages unless the locale is C.
        info.Environment["LC_ALL"] = "C";
        info.Environment.Remove("LANGUAGE");

        return info;
    }
}

/// <summary>
/// git produced more output than the call allowed, and was killed part way through it.
///
/// Internal, and never reaches the indexer: <see cref="GitHistory.TailsAsync"/> answers
/// it by halving the batch, and for one commit by reading it again without the patch.
/// The point of killing rather than reading and measuring is that by the time it can be
/// measured it is allocated, so the per-commit cap would bound the document and nothing
/// else.
///
/// It is no longer answered everywhere: the message pass has no batch to halve, so
/// <see cref="GitHistory.ReadBatchAsync"/> turns what survives into a
/// <see cref="GitHistoryException"/> rather than letting it reach the job.
/// </summary>
internal sealed class GitOutputTooLargeException(long ceiling)
    : Exception($"git produced more than {ceiling:N0} bytes.");

/// <summary>
/// A git call that could not be made or could not be understood. Carried as an exception
/// rather than an empty result, because reading no commits and finding no commits are
/// different facts and the indexer records them differently.
/// </summary>
public sealed class GitHistoryException : Exception
{
    public GitHistoryException(string message) : base(message) { }
    public GitHistoryException(string message, Exception inner) : base(message, inner) { }
}
