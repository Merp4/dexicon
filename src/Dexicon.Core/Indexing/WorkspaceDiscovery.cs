using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Which files a source owns: the walk, its filters, and the shadowing between sources.
///
/// Shared by the indexer and the sweep rather than written twice, because the answer has
/// to be the same one. A sweep that disagreed with the pass that follows it would report
/// an inventory the index never fills, or miss files the index then adds, and either reads
/// as a wrong count rather than as two walks differing.
/// </summary>
public static class WorkspaceDiscovery
{
    /// <param name="Owned">What this source is responsible for, shadowing applied.</param>
    /// <param name="Skipped">Excluded by the walk itself, with the reason it gives.</param>
    /// <param name="ShadowedCount">
    /// How many of the walk's files a more specific source owns. Not a skip: those files
    /// are indexed, just not here.
    /// </param>
    public sealed record Result(
        IReadOnlyList<WorkspaceWalker.Candidate> Owned,
        IReadOnlyList<WorkspaceWalker.Skipped> Skipped,
        int ShadowedCount);

    public static Result Walk(Corpus corpus, Source source, string root, IndexingOptions indexing)
    {
        // Through SourceFilters, not off the source: a null field there means the source
        // has no opinion and the corpus default applies. Reading the columns directly
        // indexed a source by its own emptiness.
        var filters = SourceFilters.Resolve(corpus, source, indexing);

        var walk = WorkspaceWalker.Walk(root, filters.UseGitignore,
            filters.IncludeGlobs, filters.ExcludeGlobs, filters.MaxFileBytes,
            indexing.DocumentMaxBytes);

        // The inventory is made distinct ACROSS sources here. A source covers its whole
        // tree, so one added above another makes every file beneath reachable twice, and
        // identity being (source, relative path) would index each of them twice over. The
        // most specific source owns a file; this one keeps what the deeper ones do not
        // claim.
        var shadowed = SourceScope.ShadowedPrefixes(corpus.Sources, source);
        var owned = shadowed.Count == 0
            ? walk.Files
            : walk.Files.Where(f => !SourceScope.IsShadowed(f.RelativePath, shadowed)).ToList();

        return new Result(owned, walk.SkippedFiles, walk.Files.Count - owned.Count);
    }

    /// <summary>
    /// A source's root on disk, refusing anything that resolves outside the workspace.
    ///
    /// The containment rule itself stays in <see cref="CorpusIndexer.IsInside"/>, which is
    /// where its reasoning and its regression tests live. This exists so the sweep reaches
    /// that rule too: it walks the same trees the indexer does and must be held to the same
    /// boundary, and a second implementation of a security check is how the two come to
    /// disagree.
    ///
    /// The string is only half of it. <c>Path.GetFullPath</c> canonicalises separators and
    /// dots and resolves no links, so <c>workspace/link</c> passes the check above and then
    /// IS whatever it points at. Measured, with a source rooted at such a link:
    ///
    ///   Resolve          accepted -&gt; workspace\link
    ///   Walk files       [host-secret.txt]      &lt;- from outside the workspace, indexed
    ///
    /// So the existing part of the path is walked here too, and a segment linking out is
    /// refused. The walk already applies this rule to every directory it descends into
    /// (GitignoreFilter.EnumerateFilesSafely); it is the root it never checked.
    ///
    /// The returned string is the one that was asked for, not the one the links resolve
    /// to. A caller that needs the filesystem's own spelling wants
    /// <see cref="ResolveExisting"/>; changing it here would change what every caller
    /// stores and compares.
    ///
    /// A path that does not exist is not refused. The mount being away is an operational
    /// condition, and a source created before its mount is attached is ordinary.
    /// </summary>
    public static string Resolve(string workspaceRoot, string? relative)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var combined = Contained(root, workspaceRoot, relative);

        WalkInside(root, combined, relative, out _);

        return combined;
    }

    /// <summary>
    /// Whether following <paramref name="path"/> stays under <paramref name="root"/>, with
    /// every component of it resolved rather than only the last.
    ///
    /// The walk needs the same answer this file's resolver needs, and asking it the same
    /// way is the point. A single <c>ResolveLinkTarget</c> plus <c>IsInside</c> is not
    /// enough, because how much of a target the platform has already canonicalised varies:
    /// with <c>alias -&gt; outside</c> and <c>link -&gt; workspace/alias/src</c>, Linux
    /// hands back <c>workspace/alias/src</c>, which passes a test on the text. Measured,
    /// the walk then returned a file from outside the workspace.
    ///
    /// A bool rather than an exception, because the two callers want different things from
    /// the same answer. The walk is enumerating and skips what it will not follow; the
    /// resolver was handed one path by an operator and owes them a refusal.
    /// </summary>
    internal static bool ResolvesInside(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!CorpusIndexer.IsInside(full, root)) return false;

            // Absent is harmless: a component that is not there leads nowhere to read.
            // Unreadable is NOT. A directory that could not be enumerated has not been
            // shown to stay inside, and the walk must not follow what it could not check —
            // an unreadable mount is a broken instrument, not a verdict.
            WalkInside(root, full, null, out var outcome);
            return outcome is not WalkOutcome.Unreadable;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// What a walk of the existing part of a path found.
    ///
    /// Three, not two, because a caller that cannot tell "there is nothing there" from "I
    /// could not look" reports one as the other. Absent is an operational condition the
    /// resolver is allowed to pass on; unreadable is a failure that must not be turned
    /// into either answer.
    /// </summary>
    private enum WalkOutcome
    {
        /// <summary>Every segment was found.</summary>
        Reached,

        /// <summary>A segment is not there. The mount is away, or the path is stale.</summary>
        Absent,

        /// <summary>A directory could not be enumerated. Nothing was decided.</summary>
        Unreadable,
    }

    /// <summary>
    /// The boundary test on the text alone, which is where the refusal is worded.
    ///
    /// <c>Path.GetFullPath</c> collapses <c>..</c> lexically, before any segment is looked
    /// at, so <c>link/../secret</c> is <c>root/secret</c> — where the OS would resolve
    /// <c>link</c> first, take the parent of wherever that landed, and reach somewhere
    /// else. The divergence is deliberate and it only ever narrows: what comes back is
    /// inside the root by construction. Resolving links first and applying <c>..</c>
    /// afterwards is what matching the OS would mean, and it lets <c>link/..</c> reach
    /// outside the workspace — the escape this collapse exists to prevent.
    /// </summary>
    private static string Contained(string root, string workspaceRoot, string? relative)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));

        if (!CorpusIndexer.IsInside(combined, root))
            throw new UnauthorizedAccessException(
                $"Workspace path '{relative}' resolves outside {workspaceRoot} and was refused.");

        return combined;
    }

    /// <summary>
    /// The same directory as <see cref="Resolve"/>, spelled the way the filesystem spells
    /// it, or null when there is no such directory.
    ///
    /// Containment is the same decision made in the same two places, and made first: the
    /// text is tested, then the existing part of the path is walked and a segment linking
    /// out is refused. What this adds is that the path is then re-derived from the
    /// filesystem rather than from the caller's text: each segment is matched against the
    /// entries <see cref="Directory"/> actually reports, and the string returned is the
    /// one enumeration produced.
    ///
    /// Two things follow, and both matter to a caller about to hand the path to another
    /// program. The directory exists, so "the mount is away" is a distinct answer from
    /// "this resolves outside the workspace" — the first is an operational condition and
    /// the second is a refusal, and a caller that cannot tell them apart reports one as
    /// the other. And no part of the returned value came from the request, which is what
    /// makes it safe to pass to <see cref="System.Diagnostics.ProcessStartInfo"/>: see
    /// <see cref="GitHistory.RepositoryIn"/>.
    ///
    /// The segments are compared, never used as a search pattern: a pattern would give
    /// `*` and `?` in a caller's text their glob meaning.
    /// </summary>
    public static string? ResolveExisting(string workspaceRoot, string? relative)
    {
        // Not via Resolve, which would walk the same segments a second time: the same
        // enumeration per segment, on the path a caller is about to hand to git.
        var root = Path.GetFullPath(workspaceRoot);
        var target = Contained(root, workspaceRoot, relative);
        var reached = WalkInside(root, target, relative, out var outcome);

        // Only a completed walk yields a path. Absent and unreadable both answer null —
        // this method's contract is "or null when there is no such directory", and a
        // directory it could not read is not one it can hand to git.
        return outcome is WalkOutcome.Reached ? reached : null;
    }

    /// <summary>
    /// Walks the part of <paramref name="target"/> that exists, one segment at a time
    /// against the entries <see cref="Directory"/> reports, refusing any that links out of
    /// <paramref name="root"/> and following any that links back in. Returns the deepest
    /// directory it reached, and sets <paramref name="complete"/> when every segment
    /// existed.
    ///
    /// One implementation, because two of a security check is how the two come to
    /// disagree. <see cref="Resolve"/> wants the refusal and keeps its own spelling;
    /// <see cref="ResolveExisting"/> wants the filesystem's.
    ///
    /// A link out of the root is the same escape as <c>..</c>, by a mechanism the string
    /// never shows: <c>Path.GetFullPath</c> canonicalises separators and dots and resolves
    /// no links, so <c>workspace/link</c> passes a boundary check on the text and then IS
    /// <c>/outside</c>. The walk applies this rule to every directory it descends into
    /// (GitignoreFilter.EnumerateFilesSafely); the root reached it through neither.
    ///
    /// Refused rather than skipped, which is where this differs from the walk. The walk is
    /// enumerating and a link it will not follow is simply not part of the tree; here the
    /// operator named this one path, and answering "there is nothing there" about a
    /// directory that plainly exists sends them looking at the mount instead of at the
    /// link.
    ///
    /// The segments are compared, never used as a search pattern: a pattern would give
    /// <c>*</c> and <c>?</c> in a caller's text their glob meaning.
    ///
    /// It reads paths, so it is a check at a moment rather than a lock: a segment replaced
    /// by a link between this and the read that follows is not caught here, and would need
    /// the walk to hold open handles rather than strings. What this stops is a workspace
    /// laid out to escape, which is the shape the mount actually has; a caller able to
    /// rewrite the tree mid-walk already has the filesystem.
    /// </summary>
    private static string? WalkInside(string root, string target, string? relative,
        out WalkOutcome outcome, int depth = 0)
    {
        outcome = WalkOutcome.Absent;

        // A followed link is walked again as its own path, so a chain is bounded by this
        // rather than by the filesystem. POSIX names the same limit SYMLOOP_MAX.
        if (depth > 40)
            throw new UnauthorizedAccessException(
                $"Workspace path '{relative}' follows more than 40 links. It was refused.");

        var current = root;
        if (!Directory.Exists(current)) return null;

        // Containment already holds, so this carries no `..` to walk back through.
        var within = Path.GetRelativePath(current, target);
        if (within == ".") { outcome = WalkOutcome.Reached; return current; }

        foreach (var segment in within.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            // A mount that goes away between the Exists above and this listing raises
            // DirectoryNotFoundException here. Callers translate UnauthorizedAccessException
            // and nothing else, so it reached them as a 500 from source creation or ended a
            // sweep — an outage reported as a decision. It is its own outcome instead, and
            // the caller decides: absent to a resolver, refused to the walk.
            //
            // UnauthorizedAccessException too, and it is NOT an IOException. A directory
            // the process cannot read would otherwise leave here as the same type this
            // method throws for a boundary violation, so a permissions problem read as
            // "resolves outside the workspace" — a true refusal for a false reason, which
            // sends the operator looking at the wrong thing entirely.
            string? match;
            try
            {
                match = Directory.EnumerateDirectories(current).FirstOrDefault(
                    d => string.Equals(Path.GetFileName(d), segment, CorpusIndexer.PathComparison));
            }
            catch (IOException) { outcome = WalkOutcome.Unreadable; return current; }
            catch (UnauthorizedAccessException) { outcome = WalkOutcome.Unreadable; return current; }

            if (match is null) return current;

            var info = new DirectoryInfo(match);
            if (info.LinkTarget is null)
            {
                current = match;
                continue;
            }

            // A link this cannot resolve is not inside, and saying so has to be a refusal
            // rather than a return value: `Resolve` does not read one, so an early return
            // here reached the walker as an accepted path while this comment claimed the
            // opposite. The previous `?? match` was the same bug by another route — it
            // read "unresolvable" as the link's own path, which is inside the root by
            // construction, while the walk's own check (GitignoreFilter.StaysInside)
            // refused it.
            //
            // Distinct from the absent case below, which IS a return value: not existing
            // is not a boundary problem, and `Resolve` is right to ignore it.
            // `returnFinalTarget` follows the chain, and throws IOException rather than
            // answering null when it cannot — a cycle, or more levels than the platform
            // allows. Callers translate UnauthorizedAccessException and not that, so it
            // would have surfaced as a 500 from source creation or aborted a sweep, in
            // place of the refusal this is documented to give.
            FileSystemInfo? resolved;
            try { resolved = info.ResolveLinkTarget(returnFinalTarget: true); }
            catch (IOException) { resolved = null; }

            if (resolved is null)
                throw new UnauthorizedAccessException(
                    $"Workspace path '{relative}' passes through '{segment}', a link whose "
                    + "target could not be resolved. It was refused.");

            var linked = Path.GetFullPath(resolved.FullName);

            if (!CorpusIndexer.IsInside(linked, root))
                throw new UnauthorizedAccessException(
                    $"Workspace path '{relative}' passes through '{segment}', which links to "
                    + $"'{linked}', outside {root}. It was refused.");

            // Inside as a STRING is not inside, and how much of it the platform has
            // already canonicalised differs. `alias` leaves the workspace and `link`
            // points at `alias/src`, so the target's own text is inside and only its
            // parent gives it away. Measured, same pair, same .NET:
            //
            //   Windows   ResolveLinkTarget(true) -> <outside>\src        refused
            //   Linux     ResolveLinkTarget(true) -> <workspace>/alias/src  ACCEPTED
            //
            // So the target is walked as its own path, which resolves each of ITS
            // components and refuses one that leaves. Depth-capped above, because a
            // followed link walks again.
            linked = WalkInside(root, linked, relative, out var targetOutcome, depth + 1) ?? linked;

            // Absent is fine to stop on; unreadable has to stop too, because a target
            // this could not walk has not been shown to stay inside.
            if (targetOutcome is not WalkOutcome.Reached)
            {
                outcome = targetOutcome;
                return current;
            }

            // Followed, not just checked. A link that stays inside is allowed, and a
            // path that kept its spelling would then be compared against a physical one
            // and lose: `rev-parse --show-toplevel` reports the physical working
            // directory — measured on Windows as well as POSIX — so `root/inward` was
            // answered with `root/real`, the two did not match, and the source was
            // reported as not a git repository and never indexed.
            //
            // Resolving here rather than at that comparison also covers a link part way
            // along the path, which resolving only the last component would not.
            //
            // The target has to actually be there. Measured: a dangling link resolves —
            // `ResolveLinkTarget(returnFinalTarget: true)` hands back the target it names
            // whether or not anything is at it — so following one set `current` to a
            // directory that does not exist. With no segment left that returned a path
            // this method is documented to answer null for; with one left it threw
            // DirectoryNotFoundException out of the next enumeration, which is neither of
            // the two answers it is allowed to give.
            if (!Directory.Exists(linked)) return current;

            current = linked;
        }

        outcome = WalkOutcome.Reached;
        return current;
    }
}
