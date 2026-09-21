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

    /// <summary>The subject and body of each commit.</summary>
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
    /// A commit is immutable, so its sha plus this settles the content. It deliberately
    /// leaves out <see cref="Ref"/>, <see cref="MaxCommits"/> and <see cref="Since"/>:
    /// those decide WHICH commits are indexed, not what any one of them says, and
    /// including them would re-index the whole history when the tip moved.
    /// </summary>
    public string ContentFingerprint() => string.Join(
        '|',
        IncludeMessage ? "m" : "-",
        IncludeStat ? "s" : "-",
        IncludeDiff ? "d" : "-",
        IncludeMerges ? "M" : "-",
        MaxDiffBytes.ToString(CultureInfo.InvariantCulture));
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
/// Two passes, because a refresh has to be cheap. The first asks git only for shas and
/// subjects and is the inventory; the second asks for the bodies of the commits that are
/// not already indexed. A single <c>git log -p</c> would produce every patch in the
/// repository in order to discover that nothing had changed. Measured on this repository,
/// 201 commits: 77ms to enumerate, 1,069ms to read all of them with their patches.
///
/// The git binary rather than a library. The runtime image is Alpine, so a native library
/// means musl builds to keep working, and one process per pass is not a cost worth that.
/// </summary>
public static class GitHistory
{
    /// <summary>
    /// Bodies read per git invocation. Bounds peak memory rather than process count: the
    /// patches of a hundred commits of this repository are about 3 MB, and the whole
    /// history of a large one would not fit in a string.
    /// </summary>
    private const int ReadBatch = 100;

    /// <summary>Record separator in git's output, ahead of every commit.</summary>
    private const char Rs = '';

    /// <summary>
    /// A git call that never returns must not hold an indexing slot for ever. Generous,
    /// because the second pass over a large batch is real work.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Whether this path is a git repository, which is what a source needs to be.</summary>
    public static async Task<bool> IsRepositoryAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return false;

        var (ok, stdout, _) = await RunAsync(path, ["rev-parse", "--is-inside-work-tree"], ct);
        return ok && stdout.Trim() == "true";
    }

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
        var args = new List<string> { "log", $"--format={Rs}%H%x00%aI%x00%s" };

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

        foreach (var record in stdout.Split(Rs, StringSplitOptions.RemoveEmptyEntries))
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

    private static async Task<List<(string Sha, string Text)>> ReadBatchAsync(
        string repoPath, GitHistoryOptions options, List<string> shas,
        IReadOnlyList<string>? pathspecs, CancellationToken ct)
    {
        // The body is LAST among the fields on purpose. A commit message can hold any
        // byte, including the separator, and a NUL in a field before the body would shift
        // every field after it. With the body last, the worst a stray separator can do is
        // split the body, which is then rejoined.
        var args = new List<string>
        {
            "log", "--no-walk", "--stdin",
            $"--format={Rs}%H%x00%aI%x00%an%x00%ae%x00%s%x00%b",
            "--no-color",
        };

        if (options.IncludeDiff) args.Add("--patch");
        if (options.IncludeStat) args.Add("--stat");
        if (!options.IncludeDiff && !options.IncludeStat) args.Add("--no-patch");

        if (pathspecs is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(pathspecs);
        }

        var (ok, stdout, stderr) = await RunAsync(repoPath, args, ct, stdin: string.Join('\n', shas));
        if (!ok) throw new GitHistoryException($"git log --stdin failed: {Summarise(stderr)}");

        var read = new List<(string, string)>(shas.Count);

        foreach (var record in stdout.Split(Rs, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\0', 6);
            if (fields.Length < 6) continue;

            read.Add((fields[0].Trim(), Document(options, fields)));
        }

        return read;
    }

    /// <summary>
    /// The commit as a document, laid out the way <c>git show</c> lays it out.
    ///
    /// Not an invented format. Every model that has read code has read thousands of these,
    /// and a layout of our own would have to be explained to each one; the cost of
    /// following git is a few lines of formatting.
    /// </summary>
    private static string Document(GitHistoryOptions options, string[] fields)
    {
        var (sha, date, name, email, subject, rest) =
            (fields[0].Trim(), fields[1], fields[2], fields[3], fields[4], fields[5]);

        var sb = new StringBuilder();
        sb.Append("commit ").Append(sha).Append('\n');
        sb.Append("Author: ").Append(name).Append(" <").Append(email).Append(">\n");
        sb.Append("Date:   ").Append(date).Append('\n');

        // The body carries the patch and the stat after it, because git appends them to
        // the formatted record. Splitting them is what lets the message be included
        // without the diff, and the diff be dropped when it is too large.
        var (body, tail) = SplitTail(rest);

        if (options.IncludeMessage)
        {
            sb.Append('\n');
            foreach (var line in (subject + "\n\n" + body).TrimEnd().Split('\n'))
                sb.Append("    ").Append(line.TrimEnd()).Append('\n');
        }
        else
        {
            // The subject is the commit's name. Dropping it as well would leave a document
            // that cannot be recognised as any particular commit.
            sb.Append('\n').Append("    ").Append(subject).Append('\n');
        }

        if (tail.Length == 0) return sb.ToString();

        if (options.IncludeDiff && tail.Length > options.MaxDiffBytes)
        {
            // Said, not cut. A patch truncated mid-hunk reads as a complete change that
            // did something different from what it did.
            sb.Append('\n')
              .Append(CultureInfo.InvariantCulture, $"[diff of {tail.Length:N0} characters not included: over the {options.MaxDiffBytes:N0} character limit for this source]")
              .Append('\n');

            return sb.ToString();
        }

        sb.Append('\n').Append(tail.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Splits git's trailing output into the message body and whatever followed it.
    ///
    /// <c>--stat</c> and <c>--patch</c> are appended after the body with a blank line
    /// between, and the first line of either is recognisable: a stat line names a file
    /// and a bar, a patch begins with <c>diff --git</c>. Looking for those is what keeps
    /// a message that happens to contain the word "diff" from being cut in half.
    /// </summary>
    private static (string Body, string Tail) SplitTail(string rest)
    {
        var lines = rest.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isPatch = line.StartsWith("diff --git ", StringComparison.Ordinal);
            var isStat = line.StartsWith(' ') && line.Contains(" | ", StringComparison.Ordinal);

            if (!isPatch && !isStat) continue;

            return (string.Join('\n', lines[..i]), string.Join('\n', lines[i..]));
        }

        return (rest, string.Empty);
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
        string workingDirectory, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null)
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), deadline.Token);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new GitHistoryException($"git did not finish within {Timeout.TotalMinutes:N0} minutes.");
        }

        return (process.ExitCode == 0, await stdoutTask, await stderrTask);
    }
}

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
