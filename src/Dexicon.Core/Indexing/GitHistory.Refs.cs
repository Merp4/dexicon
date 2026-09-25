using System.Globalization;
using System.Text.Json;

namespace Dexicon.Core.Indexing;

/// <summary>A local branch's upstream, and how far apart the two were at the last fetch.</summary>
/// <param name="Name">The upstream's full name, such as <c>refs/remotes/origin/main</c>.</param>
/// <param name="Ahead">Commits on the branch that the upstream lacks. Null when git's answer could not be read.</param>
/// <param name="Behind">Commits on the upstream that the branch lacks. Null when git's answer could not be read.</param>
/// <param name="Gone">The branch names an upstream that no longer exists.</param>
public sealed record GitUpstream(string Name, string ShortName, int? Ahead, int? Behind, bool Gone);

/// <summary>One ref, as the picker offers it.</summary>
/// <param name="Name">The full name, which is what a source stores when this is picked.</param>
/// <param name="CommittedUtc">The tip commit's committer date: how recent the work is, not when anything was fetched.</param>
/// <param name="Upstream">A local branch's upstream, if it has one.</param>
/// <param name="Mirrors">For a prefetched ref, the remote-tracking ref it is a copy of.</param>
/// <param name="SameAsMirrored">Whether that remote-tracking ref points at the same commit. Null when it does not exist.</param>
/// <param name="Refusal">Why this ref cannot be followed, in the words a typed ref is refused with. Null when it can.</param>
public sealed record GitRef(
    string Name, string ShortName, string Sha, DateTime? CommittedUtc,
    GitUpstream? Upstream = null, string? Mirrors = null, bool? SameAsMirrored = null, string? Refusal = null);

/// <param name="Truncated">More refs of this kind exist than were listed; the newest are kept.</param>
public sealed record GitRefGroup(IReadOnlyList<GitRef> Refs, bool Truncated);

/// <param name="Branch">The branch HEAD is on, in full. Null when HEAD is detached.</param>
/// <param name="Sha">The commit HEAD is at. Null before the first commit.</param>
public sealed record GitHead(string? Branch, string? Sha);

/// <summary>A ref as a source stores it, and the ref git walks for it.</summary>
/// <param name="Ref">The ref as asked about.</param>
/// <param name="Name">
/// The full name git resolves it to, in git's order for a short name, so a tag named like a
/// branch is the tag. Null for <c>HEAD</c>, whose branch is the listing's head, for a ref
/// the ref rule refuses, and for anything that names no ref, such as a commit.
/// </param>
/// <param name="Shadowed">
/// Refs the same short name also matches, which git passes over for <paramref name="Name"/>,
/// in git's order: the branch <c>main</c> behind a tag <c>main</c>. Empty when there are none.
/// </param>
public sealed record GitFollowed(string Ref, string? Name, IReadOnlyList<string> Shadowed);

/// <summary>What a repository could be followed at.</summary>
/// <param name="Local">
/// Local branches, newest first. The checked-out branch and the followed ref are listed even
/// when the cap would leave them out, since the picker describes both.
/// </param>
/// <param name="Prefetched">Refs under <c>refs/prefetch/</c>, written by <c>git maintenance</c>'s prefetch task.</param>
/// <param name="LastFetchUtc">When a <c>git fetch</c> last ran, from FETCH_HEAD. A prefetch does not write it.</param>
/// <param name="Followed">What the ref a source follows resolves to. Null when the listing was not asked about one.</param>
public sealed record GitRefListing(
    GitHead Head, GitRefGroup Local, GitRefGroup RemoteTracking, GitRefGroup Prefetched, DateTime? LastFetchUtc,
    GitFollowed? Followed = null);

/// <summary>
/// How current the ref a source follows was, observed by the pass that indexed it.
///
/// Recorded rather than read live, because the page that shows it is a read that any key
/// can make, and reading it live would start git processes for every history source on
/// every list of corpora.
/// </summary>
/// <param name="Ref">The ref this was observed for. A source whose ref has changed since shows nothing.</param>
/// <param name="Branch">The local branch the ref resolved to, in full, or null for anything else.</param>
/// <param name="Upstream">That branch's upstream and the distance to it.</param>
/// <param name="LastFetchUtc">When a <c>git fetch</c> last ran, for a local branch with an upstream or a remote-tracking ref.</param>
public sealed record GitTracking(
    string Ref, string? Branch, GitUpstream? Upstream, DateTime? LastFetchUtc, DateTime ObservedUtc)
{
    public string ToJson() => JsonSerializer.Serialize(this, GitHistoryJson.Options);

    /// <summary>Null for no record, and for one that cannot be read: a list of sources must not fail on it.</summary>
    public static GitTracking? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<GitTracking>(json, GitHistoryJson.Options); }
        catch (JsonException) { return null; }
    }
}

public static partial class GitHistory
{
    /// <summary>
    /// Refs listed per kind. Newest first, so what is cut is what nobody has worked on
    /// lately. A repository fetched for years holds every branch anyone pushed, and each
    /// local branch with an upstream costs git a walk to count the distance.
    /// </summary>
    internal const int RefsPerGroup = 200;

    /// <summary>
    /// Room for symbolic refs such as <c>origin/HEAD</c>, which are dropped after git has
    /// counted them. git 2.31 has no <c>--exclude</c> for <c>for-each-ref</c>.
    /// </summary>
    private const int SymrefHeadroom = 32;

    private const long RefsOutputCeiling = 1024 * 1024;

    /// <summary>
    /// Listing refs answers a person waiting on a dialog, and nothing in it is slow work
    /// on a healthy repository, so it is not given the indexer's ten minutes.
    /// </summary>
    private static readonly TimeSpan RefsTimeout = TimeSpan.FromSeconds(30);

    private const string RefFormat =
        "%(refname)%00%(refname:short)%00%(objectname)%00%(committerdate:iso-strict)%00%(symref)"
        + "%00%(upstream)%00%(upstream:short)%00%(upstream:track,nobracket)";

    /// <summary>
    /// The refs of <paramref name="repo"/>, for choosing what a history source follows.
    ///
    /// Every call is a git builtin, so no alias in the repository can stand in for it, and
    /// none writes anything or reaches the network: the host fetches, with its own login,
    /// and this reads what it left. Not read: remote URLs, which can carry a credential,
    /// FETCH_HEAD's contents, which name the remotes, and any configuration.
    /// </summary>
    /// <param name="follows">
    /// The ref a source follows, resolved as <see cref="GitRefListing.Followed"/> so the
    /// picker shows the ref git walks for it rather than guessing from its short name.
    /// </param>
    public static async Task<GitRefListing> RefsAsync(GitRepository repo, CancellationToken ct, string? follows = null)
    {
        var head = await HeadAsync(repo, ct);
        var local = await GroupAsync(repo, "refs/heads/", ct);
        var remote = await GroupAsync(repo, "refs/remotes/", ct);

        // The whole prefix, because where a prefetch writes depends on the version of git
        // that ran it: 2.31 writes refs/prefetch/origin/main, 2.54 writes
        // refs/prefetch/remotes/origin/main. Both measured.
        var prefetched = await GroupAsync(repo, "refs/prefetch/", ct);

        GitFollowed? followed = null;
        if (follows is not null)
        {
            var (name, shadowed) = follows == "HEAD" || RefProblem(follows) is not null
                ? (null, [])
                : await ResolveAsync(repo, follows, ct);
            followed = new GitFollowed(follows, name, shadowed);
        }

        // The checked-out branch, the followed ref and any branch it shadows, when newer refs
        // have pushed them past the cap. Without them the picker could not say how far the
        // checked-out branch is behind, show a followed branch as picked, or offer the branch
        // a tag of the same name hides.
        var listed = local.Refs.Concat(remote.Refs).Concat(prefetched.Refs)
            .Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var missing = new[] { head.Branch, followed?.Name }.Concat(followed?.Shadowed ?? []).OfType<string>()
            .Where(n => !listed.Contains(n)).Distinct(StringComparer.Ordinal).ToList();

        if (missing.Count > 0)
            foreach (var r in await ExactAsync(repo, missing, ct))
            {
                if (r.Name.StartsWith("refs/heads/", StringComparison.Ordinal))
                    local = local with { Refs = [.. local.Refs, r] };
                else if (r.Name.StartsWith("refs/remotes/", StringComparison.Ordinal))
                    remote = remote with { Refs = [.. remote.Refs, r] };
                else if (r.Name.StartsWith("refs/prefetch/", StringComparison.Ordinal))
                    prefetched = prefetched with { Refs = [.. prefetched.Refs, r] };
            }

        var remoteTips = remote.Refs.ToDictionary(r => r.Name, r => r.Sha, StringComparer.Ordinal);

        // A mirrored ref can be past the listing's cap and still exist, so the ones the
        // listing does not hold are asked for by name, rather than reported as unknown.
        var unlisted = prefetched.Refs.Select(p => MirroredBy(p.Name)).OfType<string>()
            .Where(m => !remoteTips.ContainsKey(m)).Distinct(StringComparer.Ordinal).ToList();
        if (unlisted.Count > 0)
            foreach (var (name, sha) in await TipsAsync(repo, unlisted, ct)) remoteTips[name] = sha;

        prefetched = prefetched with
        {
            Refs =
            [
                .. prefetched.Refs.Select(p => MirroredBy(p.Name) is { } mirrors
                    ? p with
                    {
                        Mirrors = mirrors,
                        SameAsMirrored = remoteTips.TryGetValue(mirrors, out var tip) ? tip == p.Sha : null,
                    }
                    : p),
            ],
        };

        return new GitRefListing(head, local, remote, prefetched, await LastFetchAsync(repo, ct), followed);
    }

    /// <summary>
    /// How current <paramref name="ref"/> is: the local branch it names, that branch's
    /// upstream and the distance to it, and when a fetch last ran.
    ///
    /// Resolved without <c>rev-parse --end-of-options</c>, which git 2.31 and 2.54 both
    /// print back as output rather than honour. <c>HEAD</c> is read with
    /// <c>symbolic-ref</c>; any other name is looked up exactly with <c>for-each-ref</c>,
    /// which takes no revision syntax, in the order git resolves a short name.
    /// </summary>
    public static async Task<GitTracking> TrackingAsync(
        GitRepository repo, string @ref, DateTime observedUtc, CancellationToken ct)
    {
        if (RefProblem(@ref) is { } problem) throw new GitHistoryException(problem);

        var (branch, remoteTracking) = await FollowedAsync(repo, @ref, ct);
        var upstream = branch is null ? null : await UpstreamOfAsync(repo, branch, ct);
        var lastFetch = upstream is not null || remoteTracking ? await LastFetchAsync(repo, ct) : null;

        return new GitTracking(@ref, branch, upstream, lastFetch, observedUtc);
    }

    private static async Task<GitHead> HeadAsync(GitRepository repo, CancellationToken ct)
    {
        // -q: a detached HEAD is an answer, printed as nothing, not an error.
        var (onBranch, branch, _) = await AskAsync(repo, ["symbolic-ref", "-q", "HEAD"], ct);
        var (hasCommit, sha, _) = await AskAsync(repo, ["rev-parse", "-q", "--verify", "HEAD^{commit}"], ct);

        return new GitHead(
            onBranch && branch.Trim() is { Length: > 0 } b ? b : null,
            hasCommit && sha.Trim() is { Length: > 0 } s ? s : null);
    }

    private static async Task<GitRefGroup> GroupAsync(GitRepository repo, string prefix, CancellationToken ct)
    {
        var asked = RefsPerGroup + 1 + SymrefHeadroom;

        // --count applies after sorting and before formatting, so the distance to an
        // upstream is counted for the refs listed and not for every branch there is.
        var (ok, stdout, stderr) = await AskAsync(repo,
            ["for-each-ref", "--sort=-committerdate", $"--count={asked}", $"--format={RefFormat}", prefix], ct);
        if (!ok) throw new GitHistoryException($"git for-each-ref failed: {Summarise(stderr)}");

        var rows = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var refs = rows.Select(RefFrom).OfType<GitRef>().ToList();

        return new GitRefGroup(
            [.. refs.Take(RefsPerGroup)],
            Truncated: refs.Count > RefsPerGroup || rows.Length >= asked);
    }

    /// <summary>These refs in the listing's form, where they exist. A pattern also matches refs beneath it, so only exact names count.</summary>
    private static async Task<IReadOnlyList<GitRef>> ExactAsync(
        GitRepository repo, IReadOnlyList<string> names, CancellationToken ct)
    {
        var (ok, stdout, stderr) = await AskAsync(repo, ["for-each-ref", $"--format={RefFormat}", .. names], ct);
        if (!ok) throw new GitHistoryException($"git for-each-ref failed: {Summarise(stderr)}");

        var wanted = names.ToHashSet(StringComparer.Ordinal);
        return
        [
            .. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(RefFrom).OfType<GitRef>().Where(r => wanted.Contains(r.Name)),
        ];
    }

    /// <summary>One row of <see cref="RefFormat"/>, or null for a symbolic ref or a row that does not parse.</summary>
    private static GitRef? RefFrom(string row)
    {
        var f = row.Split('\0');
        if (f.Length != 8) return null;

        // origin/HEAD and its like: a pointer to another ref in the same list.
        if (f[4].Length > 0) return null;

        return new GitRef(
            Name: f[0],
            ShortName: f[1],
            Sha: f[2],
            CommittedUtc: DateTimeOffset.TryParse(f[3], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var at) ? at.UtcDateTime : null,
            Upstream: UpstreamFrom(f[5], f[6], f[7]),
            Refusal: RefProblem(f[0]));
    }

    private static GitUpstream? UpstreamFrom(string name, string shortName, string track)
    {
        if (name.Length == 0) return null;
        var (ahead, behind, gone) = ParseTrack(track);
        return new GitUpstream(name, shortName, ahead, behind, gone);
    }

    /// <summary>
    /// <c>%(upstream:track,nobracket)</c> as git prints it in the C locale: empty when the
    /// two agree, <c>ahead N</c>, <c>behind N</c>, both joined by a comma, or <c>gone</c>.
    /// Anything else is unknown and comes back as no count, never a guessed one: a
    /// translated or future wording must not become a number on the page.
    /// </summary>
    internal static (int? Ahead, int? Behind, bool Gone) ParseTrack(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return (0, 0, false);
        if (text == "gone") return (null, null, true);

        int? ahead = null, behind = null;
        foreach (var part in text.Split(", "))
        {
            var words = part.Split(' ');
            if (words.Length != 2
                || !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return (null, null, false);

            switch (words[0])
            {
                case "ahead" when ahead is null: ahead = n; break;
                case "behind" when behind is null: behind = n; break;
                default: return (null, null, false);
            }
        }

        return (ahead ?? 0, behind ?? 0, false);
    }

    /// <summary>
    /// The remote-tracking ref a prefetched ref is a copy of, for either layout:
    /// <c>refs/prefetch/origin/main</c> (git 2.31) and
    /// <c>refs/prefetch/remotes/origin/main</c> (2.54) are both copies of
    /// <c>refs/remotes/origin/main</c>.
    /// </summary>
    internal static string? MirroredBy(string prefetchRef)
    {
        const string prefix = "refs/prefetch/";
        if (!prefetchRef.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rest = prefetchRef[prefix.Length..];
        return rest.StartsWith("remotes/", StringComparison.Ordinal) ? "refs/" + rest : "refs/remotes/" + rest;
    }

    /// <summary>
    /// When a <c>git fetch</c> last ran here: the newest FETCH_HEAD, which a fetch rewrites
    /// even when it brought nothing. Null when there is none, or none this may read.
    ///
    /// Each worktree has its own, measured on 2.31 and 2.54: a fetch run from a linked
    /// worktree writes <c>.git/worktrees/&lt;name&gt;/FETCH_HEAD</c> and leaves the main one,
    /// while moving the remote-tracking refs every worktree shares. So all of them count.
    /// A prefetch writes none, so a prefetched ref has no fetch time and is dated by its
    /// tip alone.
    ///
    /// Held to the workspace: git reports where its directory is, and a <c>.git</c> file or
    /// a separate git directory can put that anywhere.
    /// </summary>
    internal static async Task<DateTime?> LastFetchAsync(GitRepository repo, CancellationToken ct)
    {
        var (ok, stdout, _) = await AskAsync(repo, ["rev-parse", "--git-dir", "--git-common-dir"], ct);
        if (!ok) return null;

        var dirs = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dirs.Length < 2) return null;

        var gitDir = Path.GetFullPath(Path.Combine(repo.FullPath, dirs[0]));
        var common = Path.GetFullPath(Path.Combine(repo.FullPath, dirs[1]));

        var candidates = new List<string> { Path.Combine(gitDir, "FETCH_HEAD"), Path.Combine(common, "FETCH_HEAD") };

        try
        {
            var root = Path.GetFullPath(repo.WorkspaceRoot);
            var worktrees = Path.Combine(common, "worktrees");
            if (CorpusIndexer.IsInside(worktrees, root)
                && WorkspaceDiscovery.ResolveExisting(repo.WorkspaceRoot, Path.GetRelativePath(root, worktrees)) is { } listed)
                candidates.AddRange(Directory.EnumerateDirectories(listed).Select(d => Path.Combine(d, "FETCH_HEAD")));
        }
        catch (UnauthorizedAccessException) { /* outside, or through a link: not read */ }
        catch (IOException) { /* unreadable: not read */ }

        DateTime? newest = null;
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (WorkspaceDiscovery.ExistingFileInside(repo.WorkspaceRoot, candidate) is not { } file) continue;
            var at = file.LastWriteTimeUtc;
            if (newest is null || at > newest) newest = at;
        }

        return newest;
    }

    /// <summary>
    /// The local branch and whether the ref is a remote-tracking one, for a ref as a
    /// source stores it: <c>HEAD</c>, a full name, or a short name resolved the way git
    /// resolves one (<c>refs/</c>, then tags, then branches, then remotes), so that a tag
    /// named like a branch is reported as the tag git would walk.
    /// </summary>
    private static async Task<(string? Branch, bool RemoteTracking)> FollowedAsync(
        GitRepository repo, string @ref, CancellationToken ct)
    {
        if (@ref == "HEAD") return ((await HeadAsync(repo, ct)).Branch, false);

        return (await ResolveAsync(repo, @ref, ct)).Name switch
        {
            { } name when name.StartsWith("refs/heads/", StringComparison.Ordinal) => (name, false),
            { } name when name.StartsWith("refs/remotes/", StringComparison.Ordinal) => (null, true),
            _ => (null, false),
        };
    }

    /// <summary>
    /// The full name of the ref <paramref name="ref"/> names, in the order git resolves a
    /// short name (<c>refs/</c>, then tags, then branches, then remotes), or null when it
    /// names none, and the refs further down that order that git passes over for it. A full
    /// name is looked up as it is and shadows nothing.
    /// </summary>
    private static async Task<(string? Name, IReadOnlyList<string> Shadowed)> ResolveAsync(
        GitRepository repo, string @ref, CancellationToken ct)
    {
        string[] candidates = @ref.StartsWith("refs/", StringComparison.Ordinal)
            ? [@ref]
            : [$"refs/{@ref}", $"refs/tags/{@ref}", $"refs/heads/{@ref}", $"refs/remotes/{@ref}"];

        var (ok, stdout, stderr) = await AskAsync(repo, ["for-each-ref", "--format=%(refname)", .. candidates], ct);
        if (!ok) throw new GitHistoryException($"git for-each-ref failed: {Summarise(stderr)}");

        // A pattern also matches refs beneath it, so only exact names count.
        var present = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var found = candidates.Where(present.Contains).ToList();
        return found.Count == 0 ? (null, []) : (found[0], found[1..]);
    }

    /// <summary>The tips of exactly these refs, where they exist. A pattern also matches refs beneath it, so only exact names count.</summary>
    private static async Task<IEnumerable<(string Name, string Sha)>> TipsAsync(
        GitRepository repo, IReadOnlyList<string> names, CancellationToken ct)
    {
        var (ok, stdout, stderr) = await AskAsync(repo, ["for-each-ref", "--format=%(refname)%00%(objectname)", .. names], ct);
        if (!ok) throw new GitHistoryException($"git for-each-ref failed: {Summarise(stderr)}");

        var wanted = names.ToHashSet(StringComparer.Ordinal);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split('\0'))
            .Where(f => f.Length == 2 && wanted.Contains(f[0]))
            .Select(f => (f[0], f[1]))
            .ToList();
    }

    private static async Task<GitUpstream?> UpstreamOfAsync(GitRepository repo, string branch, CancellationToken ct)
    {
        var (ok, stdout, stderr) = await AskAsync(repo,
            ["for-each-ref", "--format=%(refname)%00%(upstream)%00%(upstream:short)%00%(upstream:track,nobracket)", branch],
            ct);
        if (!ok) throw new GitHistoryException($"git for-each-ref failed: {Summarise(stderr)}");

        foreach (var row in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = row.Split('\0');
            if (f.Length == 4 && f[0] == branch) return UpstreamFrom(f[1], f[2], f[3]);
        }

        return null;
    }

    /// <summary>
    /// A git call for the listing and tracking: bounded to a megabyte of output and thirty
    /// seconds. Too much output is reported as git's failure, like any other.
    /// </summary>
    private static async Task<(bool Ok, string Stdout, string Stderr)> AskAsync(
        GitRepository repo, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            return await RunAsync(repo, args, ct, maxBytes: RefsOutputCeiling, timeout: RefsTimeout);
        }
        catch (GitOutputTooLargeException)
        {
            throw new GitHistoryException($"git {args[0]} printed more than {RefsOutputCeiling:N0} bytes.");
        }
    }
}
