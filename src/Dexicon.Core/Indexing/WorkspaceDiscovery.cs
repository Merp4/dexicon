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
    /// <param name="Skipped">
    /// Excluded by the walk itself, with the reason it gives, shadowing applied as for
    /// <paramref name="Owned"/>.
    /// </param>
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
        //
        // What the walk skipped is shadowed the same way. Left alone, a file an exclusion
        // caught under a nested source was reported by every source above it, counted once
        // by each and given a catalogue row by each, and the Files list showed it twice.
        var shadowed = SourceScope.ShadowedPrefixes(corpus.Sources, source);
        var owned = shadowed.Count == 0
            ? walk.Files
            : walk.Files.Where(f => !SourceScope.IsShadowed(f.RelativePath, shadowed)).ToList();
        var skipped = shadowed.Count == 0
            ? walk.SkippedFiles
            : walk.SkippedFiles.Where(s => !SourceScope.IsShadowed(s.RelativePath, shadowed)).ToList();

        return new Result(owned, skipped, walk.Files.Count - owned.Count);
    }

    /// <summary>
    /// A source's root on disk, refusing anything outside the workspace or reached through
    /// a link.
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
    /// So the existing part of the path is walked here too, and a segment that is a link
    /// is refused. The walk follows no links either (GitignoreFilter.IsLink).
    ///
    /// The returned string is the one that was asked for. A caller that needs the
    /// filesystem's own spelling wants
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
    /// A workspace path in the one spelling a source's root is stored and compared in:
    /// relative to the root, forward slashes, <c>.</c> and <c>..</c> collapsed as
    /// <see cref="Resolve"/> collapses them, and empty for the root itself. Stored as
    /// written, <c>docs/.</c> and <c>x/../docs</c> would each read as a second source over
    /// the same folder.
    ///
    /// Lexical and checks nothing, so a caller about to store the result resolves the path
    /// first; one outside the workspace comes back starting <c>..</c>. The one refusal is a null
    /// character, which <c>Path.GetFullPath</c> cannot take.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The path holds a null character.</exception>
    public static string Canonical(string workspaceRoot, string? relative)
    {
        RefuseNullCharacter(relative);

        var root = Path.GetFullPath(workspaceRoot);
        var full = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));
        var canonical = Forward(Path.GetRelativePath(root, full)).Trim('/');
        return canonical == "." ? string.Empty : canonical;
    }

    /// <summary>
    /// <c>Path.GetFullPath</c> throws ArgumentException on a null character, which no caller catches. The
    /// refusal is the one every other rejection of a path in this class is.
    /// </summary>
    private static void RefuseNullCharacter(string? relative)
    {
        if (relative is not null && relative.Contains('\0'))
            throw new UnauthorizedAccessException("Workspace path holds a null character and was refused.");
    }

    /// <summary>
    /// A path with the OS's own separator written as a slash, which is how every workspace path is
    /// stored, listed and compared. Only that separator: on Linux a backslash is part of a name and
    /// <c>GetFullPath</c> leaves it, so <c>x\..\notes</c> is one odd name and not the folder
    /// notes. Turned into a slash it would pass for <c>x/../notes</c>, miss the duplicate check,
    /// and be collapsed to <c>notes</c> by the next call; and a folder genuinely named
    /// <c>a\b</c> would be listed as <c>a/b</c>, a different folder.
    /// </summary>
    public static string Forward(string path) =>
        Path.DirectorySeparatorChar == '/' ? path : path.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Compares workspace paths as the filesystem does, ignoring case only on Windows and
    /// macOS (<see cref="CorpusIndexer.PathComparison"/>). On those, a source stored as
    /// <c>Docs</c> is the folder listed as <c>docs</c>.
    /// </summary>
    public static StringComparer PathComparer =>
        CorpusIndexer.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Whether a directory is there and can be listed, told apart by what listing it
    /// throws. <c>Directory.Exists</c> cannot: it answers false for both, and "there and
    /// unreadable" is the case that must not be treated as safe.
    ///
    /// Lazy, so this costs the first entry rather than the listing.
    /// </summary>
    private static WalkOutcome Probe(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateDirectories(directory).GetEnumerator();
            entries.MoveNext();
            return WalkOutcome.Reached;
        }
        catch (DirectoryNotFoundException) { return WalkOutcome.Absent; }
        catch (IOException) { return WalkOutcome.Unreadable; }
        catch (UnauthorizedAccessException) { return WalkOutcome.Unreadable; }
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
        RefuseNullCharacter(relative);

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
    /// text is tested, then the existing part of the path is walked and a segment that is
    /// a link is refused. What this adds is that the path is then re-derived from the
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
    /// The file at <paramref name="fullPath"/>, if it exists inside
    /// <paramref name="workspaceRoot"/> by the same rule as a directory: contained on the
    /// text, reached segment by segment with no link on the way, and not a link itself.
    /// Null for anything else, including a path this cannot read.
    ///
    /// For a path git reports rather than one a caller typed. A repository's git directory
    /// need not be under its working tree: a <c>.git</c> file or <c>core.worktree</c> can
    /// put it anywhere, and reading a file there because git named it would be reading
    /// outside the workspace on the repository's say-so.
    /// </summary>
    internal static FileInfo? ExistingFileInside(string workspaceRoot, string fullPath)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var full = Path.GetFullPath(fullPath);
        if (!CorpusIndexer.IsInside(full, root) || Path.GetDirectoryName(full) is not { } parent) return null;

        try
        {
            if (ResolveExisting(workspaceRoot, Path.GetRelativePath(root, parent)) is not { } directory) return null;

            var file = new FileInfo(Path.Combine(directory, Path.GetFileName(full)));
            return file.Exists && file.LinkTarget is null ? file : null;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Walks the part of <paramref name="target"/> that exists, one segment at a time
    /// against the entries <see cref="Directory"/> reports, refusing any segment that is a
    /// link. Returns the deepest directory it reached, and sets <paramref name="outcome"/>
    /// to say whether every segment existed.
    ///
    /// One implementation, because two of a security check is how the two come to
    /// disagree. <see cref="Resolve"/> wants the refusal and keeps its own spelling;
    /// <see cref="ResolveExisting"/> wants the filesystem's.
    ///
    /// A link is the same escape as <c>..</c>, by a mechanism the string never shows:
    /// <c>Path.GetFullPath</c> canonicalises separators and dots and resolves no links, so
    /// <c>workspace/link</c> passes a boundary check on the text and then IS wherever it
    /// points. Links are not followed anywhere, for the reasons at GitignoreFilter.IsLink.
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
        out WalkOutcome outcome)
    {
        outcome = WalkOutcome.Absent;

        var current = root;

        // Not Directory.Exists. It answers false for a directory that is there and cannot
        // be read, which is the same answer it gives for one that is not there, and "not
        // there" is the answer a caller is allowed to pass on.
        var reached = Probe(current);
        if (reached is not WalkOutcome.Reached) { outcome = reached; return null; }

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
            catch (DirectoryNotFoundException) { return current; }
            catch (IOException) { outcome = WalkOutcome.Unreadable; return current; }
            catch (UnauthorizedAccessException) { outcome = WalkOutcome.Unreadable; return current; }

            if (match is null) return current;

            // Refused, not followed: see GitignoreFilter.IsLink. The link's own text goes in
            // the message, as `ls -l` would show it; nothing is resolved to produce it.
            var link = new DirectoryInfo(match).LinkTarget;
            if (link is not null)
                throw new UnauthorizedAccessException(
                    $"Workspace path '{relative}' passes through '{segment}', a link to '{link}'. "
                    + "Links are not followed, so it was refused.");

            current = match;
        }

        outcome = WalkOutcome.Reached;
        return current;
    }
}
