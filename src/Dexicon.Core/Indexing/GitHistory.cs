using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Only commits at or after this date, or null for all of them.</summary>
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
    /// Out: <see cref="Ref"/>, <see cref="MaxCommits"/>, <see cref="Since"/> and
    /// <see cref="IncludeMerges"/>, which decide WHICH commits are indexed and not what
    /// any one of them holds. Turning merges on adds documents; it does not alter a
    /// single existing one.
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
public sealed record GitCommit(string Sha, DateTimeOffset AuthorDate, string Subject)
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
/// Reading a repository's history as documents, one per commit.
///
/// A commit, not a file at a revision: indexing every version of every file multiplies a
/// large repository by its history and re-indexes text that did not change. And not a
/// hunk: a hunk has no author and no subject, and the question people ask of history is
/// why something changed, which lives in the message.
///
/// Two PHASES, because a refresh has to be cheap. The first asks git only for shas and
/// subjects and is the inventory; the second asks for the bodies of the commits that are
/// not already indexed. A single <c>git log -p</c> would produce every patch in the
/// repository in order to discover that nothing had changed. Measured on this repository,
/// 201 commits: 77ms to enumerate, 1,069ms to read all of them with their patches.
///
/// Phases, not processes. The inventory is one call; reading is one call per 100 commits
/// for the messages and another for the stat and patch when either is wanted, so a first
/// pass over 201 commits with diffs is 1 + 3 + 3. That is bounded by what is NEW, which
/// is the number that matters: a refresh with nothing to read is the one call.
///
/// The git binary rather than a library. The runtime image is Alpine, so a native library
/// means musl builds to keep working, and a handful of processes per pass is not a cost
/// worth that.
/// </summary>
public static class GitHistory
{
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
    public static async Task<bool> IsRepositoryAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return false;

        var (ok, stdout, _) = await RunAsync(path, ["rev-parse", "--show-toplevel"], ct);
        if (!ok) return false;

        var top = stdout.Trim();
        if (top.Length == 0) return false;

        // git answers in forward slashes whatever the platform, and either side may carry
        // a trailing separator or a differently-cased drive letter on Windows.
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
    /// Every commit the settings select, newest first, as shas, dates and subjects.
    ///
    /// This is the inventory, and it is deliberately the cheap half: no patches, no
    /// bodies. A refresh of a repository whose tip has not moved reads this, finds every
    /// sha already indexed, and stops.
    /// </summary>
    public static async Task<IReadOnlyList<GitCommit>> EnumerateAsync(
        string repoPath, GitHistoryOptions options, IReadOnlyList<string>? pathspecs,
        CancellationToken ct)
    {
        if (!IsAcceptableRef(options.Ref))
            throw new GitHistoryException(
                $"'{options.Ref}' is not a usable ref. A branch, a tag or an object name.");

        var marker = Marker();
        var args = new List<string> { "log", $"--format={marker}%H%x00%aI%x00%s" };

        if (!options.IncludeMerges) args.Add("--no-merges");
        if (options.MaxCommits is { } max) args.Add($"--max-count={max}");
        if (options.Since is { } since) args.Add($"--since={since:yyyy-MM-dd}");

        // Before the ref, so a ref beginning with a dash is a ref and not an option.
        args.Add("--end-of-options");
        args.Add(options.Ref);

        // And after it, so a pathspec cannot be read as a ref.
        if (pathspecs is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(pathspecs);
        }

        var (ok, stdout, stderr) = await RunAsync(repoPath, args, ct);
        if (!ok) throw new GitHistoryException($"git log failed: {Summarise(stderr)}");

        var commits = new List<GitCommit>();

        foreach (var record in stdout.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\0');
            if (fields.Length < 3) continue;

            if (!DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                continue;

            commits.Add(new GitCommit(fields[0].Trim(), date, fields[2].Trim('\n', '\r')));
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
        string repoPath, GitHistoryOptions options, IReadOnlyList<string> shas,
        IReadOnlyList<string>? pathspecs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var start = 0; start < shas.Count; start += ReadBatch)
        {
            ct.ThrowIfCancellationRequested();

            var batch = shas.Skip(start).Take(ReadBatch).ToList();
            foreach (var read in await ReadBatchAsync(repoPath, options, batch, pathspecs, ct))
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
        string repoPath, GitHistoryOptions options, List<string> shas,
        IReadOnlyList<string>? pathspecs, CancellationToken ct)
    {
        var marker = Marker();
        var stdin = string.Join('\n', shas);
        var known = shas.ToHashSet(StringComparer.Ordinal);

        var messages = await MessagesAsync(repoPath, marker, stdin, known, ct);
        var tails = options.IncludeStat || options.IncludeDiff
            ? await TailsAsync(repoPath, options, marker, shas, pathspecs, known, ct)
            : [];

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
    private static async Task<Dictionary<string, string[]>> MessagesAsync(
        string repoPath, string marker, string stdin, HashSet<string> known, CancellationToken ct)
    {
        var (ok, stdout, stderr) = await RunAsync(repoPath,
            ["log", "--no-walk", "--stdin", "--no-color", "--no-patch",
             $"--format={marker}%H%x00%aI%x00%an%x00%ae%x00%s%x00%b"],
            ct, stdin);

        if (!ok) throw new GitHistoryException($"git log --stdin failed: {Summarise(stderr)}");

        var messages = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? last = null;

        foreach (var record in stdout.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\0', 6);

            if (fields.Length == 6 && known.Contains(fields[0].Trim()))
            {
                last = fields[0].Trim();
                messages[last] = fields;
                continue;
            }

            // Put back what the split took out, marker included, so the body is the
            // body byte for byte.
            if (last is not null) messages[last][5] += marker + record;
        }

        return messages;
    }

    /// <summary>
    /// The stat and the patch, under a format carrying nothing but the marker and the
    /// sha. Everything between one sha and the next marker is git's own output, so there
    /// is no boundary to infer and no message text that can imitate one.
    /// </summary>
    private static async Task<Dictionary<string, string>> TailsAsync(
        string repoPath, GitHistoryOptions options, string marker, List<string> shas,
        IReadOnlyList<string>? pathspecs, HashSet<string> known, CancellationToken ct)
    {
        if (shas.Count == 0) return [];

        try
        {
            return await ReadTailsAsync(repoPath, options, marker, shas, pathspecs, known,
                CeilingFor(options, shas.Count), ct);
        }
        catch (GitOutputTooLargeException) when (shas.Count > 1)
        {
            // Which commit produced it is not reported, so halve until it is alone. The
            // same shape as the embedder's refusal handling: log2 cheap attempts rather
            // than one call per commit.
            var half = shas.Count / 2;
            var left = await TailsAsync(repoPath, options, marker,
                shas.GetRange(0, half), pathspecs, known, ct);
            var right = await TailsAsync(repoPath, options, marker,
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
            var stat = await ReadTailsAsync(repoPath, options with { IncludeDiff = false },
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
    /// Sized from the per-commit cap, so a single commit's read is bounded just above
    /// what a document may hold: a commit carrying a vendored tree is stopped rather
    /// than allocated and then discarded. The slack is the stat, the headers and the
    /// marker; the absolute ceiling is there because a batch of a hundred at a generous
    /// cap would otherwise be a bound in name only.
    /// </summary>
    private static long CeilingFor(GitHistoryOptions options, int commits)
    {
        var perCommit = options.IncludeDiff ? options.MaxDiffBytes + Slack : Slack;
        return Math.Min((long)perCommit * commits, AbsoluteCeiling);
    }

    private const int Slack = 256 * 1024;
    private const long AbsoluteCeiling = 64L * 1024 * 1024;

    private static string OverCeiling(GitHistoryOptions options) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[diff not included: over the {options.MaxDiffBytes:N0} byte limit for this source]");

    private static async Task<Dictionary<string, string>> ReadTailsAsync(
        string repoPath, GitHistoryOptions options, string marker, List<string> shas,
        IReadOnlyList<string>? pathspecs, HashSet<string> known, long ceiling, CancellationToken ct)
    {
        var args = new List<string>
        {
            "log", "--no-walk", "--stdin", "--no-color", $"--format={marker}%H",
        };

        if (options.IncludeDiff) args.Add("--patch");
        if (options.IncludeStat) args.Add("--stat");
        if (!options.IncludeDiff && !options.IncludeStat) args.Add("--no-patch");

        if (pathspecs is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(pathspecs);
        }

        var (ok, stdout, stderr) = await RunAsync(
            repoPath, args, ct, string.Join('\n', shas), ceiling);

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
    private static (string Stat, string Patch) SplitPatch(string tail)
    {
        var at = tail.StartsWith("diff --git ", StringComparison.Ordinal)
            ? 0
            : tail.IndexOf("\ndiff --git ", StringComparison.Ordinal) + 1;

        return at <= 0 ? (tail, string.Empty) : (tail[..at], tail[at..]);
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
        string workingDirectory, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null,
        long? maxChars = null)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) info.ArgumentList.Add(arg);

        // A repository someone else configured is not ours to trust with hooks, aliases
        // or a pager. -c core.hooksPath= and --no-pager keep the call to what was asked.
        info.ArgumentList.Insert(0, "--no-pager");
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";

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
        deadline.CancelAfter(Timeout);

        // Both streams read concurrently. Waiting for exit with either pipe unread is the
        // classic deadlock: git fills the buffer and blocks, and nothing drains it.
        var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
        var stdout = new StringBuilder();
        var over = false;

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

                if (maxChars is { } cap && stdout.Length + n > cap)
                {
                    over = true;
                    break;
                }

                stdout.Append(buffer, 0, n);
            }

            if (over)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new GitOutputTooLargeException(maxChars!.Value);
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
            throw new GitHistoryException($"git did not finish within {Timeout.TotalMinutes:N0} minutes.");
        }

        return (process.ExitCode == 0, stdout.ToString(), await stderrTask);
    }
}

/// <summary>
/// A git call that could not be made or could not be understood. Carried as an exception
/// rather than an empty result, because reading no commits and finding no commits are
/// different facts and the indexer records them differently.
/// </summary>
/// <summary>
/// git produced more output than the call allowed, and was killed part way through it.
///
/// Internal, and never reaches the indexer: <see cref="GitHistory.TailsAsync"/> answers
/// it by halving the batch, and for one commit by reading it again without the patch.
/// The point of killing rather than reading and measuring is that by the time it can be
/// measured it is allocated, so the per-commit cap would bound the document and nothing
/// else.
/// </summary>
internal sealed class GitOutputTooLargeException(long ceiling)
    : Exception($"git produced more than {ceiling:N0} characters.");

public sealed class GitHistoryException : Exception
{
    public GitHistoryException(string message) : base(message) { }
    public GitHistoryException(string message, Exception inner) : base(message, inner) { }
}
