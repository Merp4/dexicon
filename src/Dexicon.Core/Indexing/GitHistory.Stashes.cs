using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dexicon.Core.Indexing;

/// <summary>
/// One document of a history source: a commit, a stash, or a file a stash held.
///
/// The indexer and the sweep work on these through <see cref="RelativePath"/>, which is the
/// document's key in the catalogue, and <see cref="ContentKey"/>, which settles its text
/// without reading it.
/// </summary>
public abstract record GitUnit
{
    /// <summary>The path this unit is indexed under, relative to the source.</summary>
    public abstract string RelativePath { get; }

    /// <summary>
    /// What the unit's text is a function of, so a unit already indexed can be skipped without
    /// being read. <paramref name="settings"/> is <see cref="GitHistoryOptions.ContentFingerprint"/>.
    /// </summary>
    public abstract string ContentKey(GitHistoryOptions options, string settings);
}

/// <summary>One untracked file a stash recorded, as the stash's untracked-files commit lists it.</summary>
/// <param name="Path">The path in the repository, as git prints it.</param>
/// <param name="BlobSha">The object name of the file's content.</param>
/// <param name="SizeBytes">The size of the content in bytes, from git.</param>
/// <param name="Binary">Git's own verdict: <c>--numstat</c> prints <c>-</c> for a file with a NUL byte in its first 8,000 bytes.</param>
public sealed record GitUntrackedFile(string Path, string BlobSha, long SizeBytes, bool Binary);

/// <summary>One stash as the reflog of <c>refs/stash</c> lists it.</summary>
/// <param name="Sha">The stash commit. The identity of the stash and of its document.</param>
/// <param name="AuthorDate">When the stash was made.</param>
/// <param name="BaseSha">The commit the stash was made on, or empty when the stash commit has no parent.</param>
/// <param name="Subject">The stash's message, such as <c>On main: park notes</c>.</param>
/// <param name="Untracked">The first <see cref="GitHistory.MaxUntrackedPerStash"/> untracked files, in git's order.</param>
/// <param name="UntrackedTotal">How many untracked regular files the stash holds under the include filters.</param>
public sealed record GitStash(
    string Sha, DateTimeOffset AuthorDate, string BaseSha, string Subject,
    IReadOnlyList<GitUntrackedFile> Untracked, int UntrackedTotal) : GitUnit
{
    /// <summary>
    /// Raised when the layout of a stash's document changes, or <see cref="GitHistory.MaxUntrackedPerStash"/>
    /// does, so that stashes already indexed are read again. Commits do not carry it: their
    /// documents are unchanged.
    /// </summary>
    internal const int DocumentVersion = 1;

    public override string RelativePath =>
        $"stashes/{AuthorDate:yyyy-MM-dd}-{Sha[..Math.Min(12, Sha.Length)]}";

    public override string ContentKey(GitHistoryOptions options, string settings) =>
        $"stash{DocumentVersion}|{Sha}|{settings}";
}

/// <summary>
/// One version of one untracked file, held by one or more stashes. Indexed once however many
/// stashes hold it, under the oldest of them.
/// </summary>
/// <param name="Path">The path in the repository.</param>
/// <param name="BlobSha">The object name of this version of the content.</param>
/// <param name="SizeBytes">The size of the content in bytes.</param>
/// <param name="HolderSha">The oldest stash holding this version at this path.</param>
/// <param name="HolderDate">When that stash was made.</param>
/// <param name="HolderSubject">That stash's message.</param>
public sealed record GitStashFile(
    string Path, string BlobSha, long SizeBytes, string HolderSha, DateTimeOffset HolderDate, string HolderSubject)
    : GitUnit
{
    /// <summary>Raised when the layout of this document changes. See <see cref="GitStash.DocumentVersion"/>.</summary>
    internal const int DocumentVersion = 1;

    public override string RelativePath =>
        $"stashes/untracked/{BlobSha[..Math.Min(12, BlobSha.Length)]}/{Path}";

    /// <summary>
    /// The stash and its message are in the text, so they are in the key. The message only when
    /// the source indexes messages. Nothing else about the source's settings reaches this
    /// document, so changing the diff or stat settings does not read it again.
    /// </summary>
    public override string ContentKey(GitHistoryOptions options, string settings) =>
        string.Create(CultureInfo.InvariantCulture,
            $"stashfile{DocumentVersion}|{BlobSha}|{Path}|{HolderSha}|{(options.IncludeMessage ? "m" : "-")}");
}

/// <summary>What <see cref="GitHistory.StashesAsync"/> found.</summary>
/// <param name="Stashes">One unit per stash.</param>
/// <param name="Files">One unit per distinct untracked file version that is worth indexing.</param>
/// <param name="Skipped">File versions left out, with the reason, recorded as skipped rows like a file walk does.</param>
public sealed record GitStashInventory(
    IReadOnlyList<GitStash> Stashes, IReadOnlyList<GitStashFile> Files,
    IReadOnlyList<WorkspaceWalker.Skipped> Skipped)
{
    public static readonly GitStashInventory None = new([], [], []);

    /// <summary>Every unit to index: the stashes, then the files.</summary>
    public IEnumerable<GitUnit> Units => Stashes.Cast<GitUnit>().Concat(Files);
}

public static partial class GitHistory
{
    /// <summary>
    /// Untracked files listed and indexed per stash, taken in git's path order. A stash made with
    /// <c>--all</c> also holds every ignored file, and one made in a tree whose build output is not
    /// ignored holds that: either can be tens of thousands of paths, and each is a document. The
    /// stash's own document says how many it left out.
    /// </summary>
    internal const int MaxUntrackedPerStash = 500;

    /// <summary>Untracked-files commits listed per git call, so the output of one call is bounded by this and <see cref="MaxUntrackedPerStash"/>.</summary>
    private const int UntrackedListBatch = 50;

    /// <summary>The most a list of stashes, or of blob sizes, may print before the read is refused.</summary>
    private const long StashListCeiling = 16L * 1024 * 1024;

    private static readonly Regex NumstatEntry = new(@"^(\d+|-)\t(\d+|-)\t", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ObjectName = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled);

    /// <summary>
    /// The stashes of the repository and the untracked files they hold, as units to index.
    ///
    /// A stash is a commit with up to three parents: the commit it was made on, a commit of the
    /// index, and, for <c>git stash -u</c>, a commit holding only the untracked files. The
    /// untracked-files commit is not shown by <c>git show stash@{n}</c> and is not reached by
    /// walking <c>refs/stash</c> past the newest stash, so stashes are listed from the reflog and
    /// the untracked commit is read from the third parent. Measured on git 2.31.1 and 2.54.0.
    ///
    /// Untracked files are deduplicated by path and content: the same note unchanged across thirty
    /// stashes is one document, held by the oldest of them. A file is left out when git's ignore
    /// rules cover its path (<c>git stash --all</c> records ignored files such as <c>.env</c>), when
    /// it is empty, over <paramref name="maxFileBytes"/>, or binary.
    /// </summary>
    /// <param name="pathspecs">The source's include globs. They select untracked files as they select a commit's files.</param>
    public static async Task<GitStashInventory> StashesAsync(
        GitRepository repo, GitHistoryOptions options, IReadOnlyList<string>? pathspecs, long maxFileBytes,
        CancellationToken ct)
    {
        if (!options.IncludeStashes) return GitStashInventory.None;

        // Exact name, because a pattern also matches refs beneath it.
        var (probed, names, probeError) = await BoundedAsync(
            repo, ["for-each-ref", "--format=%(refname)", "refs/stash"], ct);
        if (!probed) throw new GitHistoryException($"git for-each-ref failed: {Summarise(probeError)}");
        if (!names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("refs/stash"))
            return GitStashInventory.None;

        var listed = await ListStashesAsync(repo, options, ct);
        if (listed.Count == 0) return GitStashInventory.None;

        var untracked = await UntrackedAsync(repo, listed, pathspecs, ct);

        // One stash per path, the newest of any that share a date and twelve characters of a sha,
        // for the reason commits are (OnePerPath): two units on one path delete each other's chunks.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var stashes = new List<GitStash>(listed.Count);
        foreach (var s in listed)
        {
            var files = s.UntrackedSha is not null && untracked.TryGetValue(s.UntrackedSha, out var held)
                ? held
                : [];
            var stash = new GitStash(
                s.Sha, s.Date, s.BaseSha, s.Subject, [.. files.Take(MaxUntrackedPerStash)], files.Count);

            if (paths.Add(stash.RelativePath)) stashes.Add(stash);
        }

        var (units, skipped) = await FilesAsync(repo, stashes, maxFileBytes, ct);
        return new GitStashInventory(stashes, units, skipped);
    }

    /// <summary>
    /// A git call whose output is bounded, with output past the bound reported as a
    /// <see cref="GitHistoryException"/>, which is what the indexer and the sweep handle. Left as
    /// <see cref="GitOutputTooLargeException"/> it reached the job's generic catch and failed the
    /// job, an outage written as a terminal state for the corpus.
    /// </summary>
    private static async Task<(bool Ok, string Stdout, string Stderr)> BoundedAsync(
        GitRepository repo, List<string> args, CancellationToken ct, string? stdin = null,
        long ceiling = StashListCeiling, int? alsoOk = null)
    {
        try
        {
            return await RunAsync(repo, args, ct, stdin, ceiling, alsoOk: alsoOk);
        }
        catch (GitOutputTooLargeException)
        {
            throw new GitHistoryException($"git {args[0]} printed more than {ceiling:N0} bytes.");
        }
    }

    private sealed record Listed(string Sha, DateTimeOffset Date, string BaseSha, string? UntrackedSha, string Subject);

    private static async Task<List<Listed>> ListStashesAsync(GitRepository repo, GitHistoryOptions options, CancellationToken ct)
    {
        var marker = Marker();
        var (ok, stdout, stderr) = await BoundedAsync(repo,
            ["log", "-g", "--no-color", $"--format={marker}%H%x00%aI%x00%P%x00%s", "--end-of-options", "refs/stash"], ct);
        if (!ok) throw new GitHistoryException($"git log -g failed: {Summarise(stderr)}");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stashes = new List<Listed>();

        foreach (var record in stdout.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            // The subject last, with the rest of the record, so a NUL in it stays in it.
            var fields = record.Split('\0', 4);
            if (fields.Length < 4) continue;

            var sha = fields[0].Trim();
            if (!ObjectName.IsMatch(sha) || !seen.Add(sha)) continue;

            if (!DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            // Midnight UTC, as for commits, so a date means the same for both.
            if (options.Since is { } since && date.UtcDateTime < since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                continue;

            var parents = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            stashes.Add(new Listed(
                sha, date,
                parents.Length > 0 ? parents[0] : string.Empty,
                parents.Length > 2 && ObjectName.IsMatch(parents[2]) ? parents[2] : null,
                fields[3].TrimEnd('\n')));
        }

        return stashes;
    }

    /// <summary>
    /// The regular files of each untracked-files commit, with git's verdict on whether each is
    /// binary. Raw output for the object names and numstat for the verdict: numstat prints
    /// <c>-</c> for both counts of a file git treats as binary. Symbolic links and submodule
    /// entries are not files with content and are left out.
    /// </summary>
    private static async Task<Dictionary<string, List<GitUntrackedFile>>> UntrackedAsync(
        GitRepository repo, List<Listed> stashes, IReadOnlyList<string>? pathspecs, CancellationToken ct)
    {
        var commits = stashes.Select(s => s.UntrackedSha).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var found = new Dictionary<string, List<(string Path, string Blob, bool Binary)>>(StringComparer.Ordinal);

        for (var start = 0; start < commits.Count; start += UntrackedListBatch)
        {
            ct.ThrowIfCancellationRequested();

            var batch = commits.Skip(start).Take(UntrackedListBatch).ToList();
            var marker = Marker();
            var args = new List<string>
            {
                "log", "--no-walk", "--stdin", "--root", "--no-color", "--no-renames", "--no-textconv",
                "--raw", "--abbrev=40", "--numstat", "-z", $"--format={marker}%H",
            };

            if (Pathspecs(pathspecs) is { Count: > 0 } specs)
            {
                args.Add("--");
                args.AddRange(specs);
            }

            var (ok, stdout, stderr) = await BoundedAsync(repo, args, ct, string.Join('\n', batch), AbsoluteCeiling);
            if (!ok) throw new GitHistoryException($"git log --stdin failed: {Summarise(stderr)}");

            foreach (var (sha, files) in ParseUntrackedListing(stdout, marker, [.. batch]))
                found[sha] = files;
        }

        var sizes = await SizesAsync(repo, found.Values.SelectMany(f => f.Select(e => e.Blob)), ct);

        return found.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(e => new GitUntrackedFile(e.Path, e.Blob, sizes.GetValueOrDefault(e.Blob, -1), e.Binary)).ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads <c>git log --raw --numstat -z</c> for root commits. Per commit the sha opens the record,
    /// then every raw entry (<c>:old new oldsha newsha status</c>, a NUL, the path, a NUL), then every
    /// numstat entry (<c>added TAB deleted TAB path</c>, a NUL). A path is read by position after its
    /// raw entry and never classified by its text, so a path that looks like either is still a path.
    /// </summary>
    internal static Dictionary<string, List<(string Path, string Blob, bool Binary)>> ParseUntrackedListing(
        string stdout, string marker, HashSet<string> known)
    {
        var result = new Dictionary<string, List<(string Path, string Blob, bool Binary)>>(StringComparer.Ordinal);

        foreach (var record in stdout.Split(marker, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = record.Split('\0');
            var sha = tokens[0].Trim();
            if (!known.Contains(sha)) continue;

            var entries = new List<(string Path, string Blob, string Mode)>();
            var binary = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 1; i < tokens.Length; i++)
            {
                var token = tokens[i].TrimStart('\n');

                if (token.StartsWith(':'))
                {
                    var meta = token.Split(' ');
                    if (meta.Length < 5 || i + 1 >= tokens.Length)
                        throw new GitHistoryException("git printed a raw entry for an untracked file in a form that could not be read.");

                    entries.Add((tokens[i + 1], meta[3], meta[1]));
                    i++;
                }
                else if (NumstatEntry.Match(token) is { Success: true } numstat)
                {
                    var rest = token[numstat.Length..];
                    if (numstat.Groups[1].Value == "-" && numstat.Groups[2].Value == "-") binary.Add(rest);
                }
            }

            result[sha] = [.. entries
                .Where(e => e.Mode is "100644" or "100755" && ObjectName.IsMatch(e.Blob))
                .Select(e => (e.Path, e.Blob, binary.Contains(e.Path)))];
        }

        return result;
    }

    /// <summary>The size in bytes of each object, by name. An object git cannot find is left out.</summary>
    private static async Task<Dictionary<string, long>> SizesAsync(
        GitRepository repo, IEnumerable<string> blobs, CancellationToken ct)
    {
        var distinct = blobs.Distinct(StringComparer.Ordinal).ToList();
        var sizes = new Dictionary<string, long>(distinct.Count, StringComparer.Ordinal);
        if (distinct.Count == 0) return sizes;

        var (ok, stdout, stderr) = await BoundedAsync(repo,
            ["cat-file", "--batch-check=%(objectname) %(objectsize)"], ct, string.Join('\n', distinct));
        if (!ok) throw new GitHistoryException($"git cat-file failed: {Summarise(stderr)}");

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ');
            if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                sizes[parts[0]] = size;
        }

        return sizes;
    }

    /// <summary>
    /// One unit per distinct (path, content) among the stashes' untracked files, held by the oldest
    /// stash that has it, and a skipped record for each that is left out for a reason a person can
    /// act on. A path git's ignore rules cover is left out without a record, as a file walk leaves it.
    /// </summary>
    private static async Task<(List<GitStashFile> Files, List<WorkspaceWalker.Skipped> Skipped)> FilesAsync(
        GitRepository repo, List<GitStash> stashes, long maxFileBytes, CancellationToken ct)
    {
        // Oldest first, so the holder named in a document does not change when a newer stash is
        // made with the same file. The reflog lists the newest first, in the order the stashes were
        // made, which dates alone cannot give: two stashes made in one second have one date.
        var firstHolders = new Dictionary<(string Path, string Blob), (GitStash Stash, GitUntrackedFile File)>();
        foreach (var stash in Enumerable.Reverse(stashes))
            foreach (var file in stash.Untracked)
                firstHolders.TryAdd((file.Path, file.BlobSha), (stash, file));

        // Asked about only the paths that could become documents: git's ignore rules for a path
        // are the same whichever stash holds it.
        var candidates = firstHolders.Values
            .Where(h => !h.File.Binary && h.File.SizeBytes > 0 && h.File.SizeBytes <= maxFileBytes)
            .Select(h => h.File.Path).Distinct(StringComparer.Ordinal).ToList();
        var ignored = await IgnoredAsync(repo, candidates, ct);

        var files = new List<GitStashFile>();
        var skipped = new List<WorkspaceWalker.Skipped>();
        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (stash, file) in firstHolders.Values)
        {
            if (ignored.Contains(file.Path)) continue;

            // The path is in the document's key, which file lists show on one line. A control
            // character in it would end the line, so the key carries it as a replacement
            // character and the file is not indexed.
            if (file.Path.Any(EndsALine))
            {
                var key = new GitStashFile(
                    Printable(file.Path), file.BlobSha, file.SizeBytes, stash.Sha, stash.AuthorDate, stash.Subject).RelativePath;
                if (paths.Add(key))
                    skipped.Add(new WorkspaceWalker.Skipped(key, "path holds a control character", Math.Max(0, file.SizeBytes)));
                continue;
            }

            var unit = new GitStashFile(file.Path, file.BlobSha, file.SizeBytes, stash.Sha, stash.AuthorDate, stash.Subject);
            if (!paths.Add(unit.RelativePath)) continue;

            string? reason = file.SizeBytes < 0 ? "object missing from the repository"
                : file.SizeBytes == 0 ? "empty file"
                : file.SizeBytes > maxFileBytes
                    ? string.Create(CultureInfo.InvariantCulture, $"over the {maxFileBytes:N0} byte size cap ({file.SizeBytes:N0} bytes)")
                : file.Binary ? "binary content (git treats it as binary)"
                : null;

            if (reason is null) files.Add(unit);
            else skipped.Add(new WorkspaceWalker.Skipped(unit.RelativePath, reason, Math.Max(0, file.SizeBytes)));
        }

        return (files, skipped);
    }

    /// <summary>
    /// The paths git's ignore rules cover, judged as if they were not in the index. The files are in a
    /// stash and not in the working tree, so what applies is the repository's <c>.gitignore</c> files,
    /// <c>.git/info/exclude</c> and the user's global excludes. Exit status 1 is "none ignored".
    /// </summary>
    private static async Task<HashSet<string>> IgnoredAsync(GitRepository repo, List<string> paths, CancellationToken ct)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Count == 0) return ignored;

        var (ok, stdout, stderr) = await BoundedAsync(repo,
            ["check-ignore", "--no-index", "--stdin", "-z"], ct,
            string.Concat(paths.Select(p => p + '\0')), alsoOk: 1);
        if (!ok) throw new GitHistoryException($"git check-ignore failed: {Summarise(stderr)}");

        foreach (var path in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) ignored.Add(path);
        return ignored;
    }

    /// <summary>
    /// The documents of these stashes. The same layout as a commit's, with <c>stash</c> for the
    /// first word, the commit the stash was made on, and the diff taken against it: a stash is a
    /// merge, and git prints no diff for a merge unless asked for the first parent's. The untracked
    /// files follow, as names and sizes; their contents are documents of their own.
    /// </summary>
    public static async IAsyncEnumerable<(string Sha, string Text)> ReadStashesAsync(
        GitRepository repo, GitHistoryOptions options, IReadOnlyList<GitStash> stashes,
        IReadOnlyList<string>? pathspecs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        RequireUsableOptions(options);

        var bySha = stashes.ToDictionary(s => s.Sha, StringComparer.Ordinal);

        for (var start = 0; start < stashes.Count; start += ReadBatch)
        {
            ct.ThrowIfCancellationRequested();

            var batch = stashes.Skip(start).Take(ReadBatch).Select(s => s.Sha).ToList();
            foreach (var read in await ReadBatchAsync(repo, options, batch, pathspecs, ct, bySha))
                yield return read;
        }
    }

    /// <summary>
    /// The documents of these untracked file versions: a header naming the stash, then the file.
    /// One <c>git cat-file</c> per file, which returns the content as stored: no filter or text
    /// conversion runs. The ceiling is the file's size with room for the decoding of invalid
    /// bytes, which count as three each.
    /// </summary>
    public static async IAsyncEnumerable<(GitStashFile File, string Text)> ReadStashFilesAsync(
        GitRepository repo, GitHistoryOptions options, IReadOnlyList<GitStashFile> files,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Given to git as an argument. The inventory only produces object names, and this is
            // a public entry point: a unit built by hand must not become an option.
            if (!ObjectName.IsMatch(file.BlobSha))
                throw new GitHistoryException("An untracked file's object name is not one git could have printed.");

            string content;
            try
            {
                var (ok, stdout, stderr) = await RunAsync(repo, ["cat-file", "blob", file.BlobSha], ct,
                    maxBytes: file.SizeBytes * 3 + 4096);
                if (!ok) throw new GitHistoryException($"git cat-file failed: {Summarise(stderr)}");
                content = stdout;
            }
            catch (GitOutputTooLargeException ex)
            {
                throw new GitHistoryException($"An untracked file of a stash could not be read: {ex.Message}", ex);
            }

            yield return (file, StashFileDocument(options, file, content));
        }
    }

    internal static string StashFileDocument(GitHistoryOptions options, GitStashFile file, string content)
    {
        var sb = new StringBuilder();
        sb.Append("untracked file ").Append(Printable(file.Path)).Append('\n');
        sb.Append("Stash:  ").Append(file.HolderSha[..Math.Min(12, file.HolderSha.Length)]);
        if (options.IncludeMessage && file.HolderSubject.Length > 0)
            sb.Append(" \"").Append(Printable(file.HolderSubject)).Append('"');
        sb.Append('\n');
        sb.Append("Date:   ").Append(file.HolderDate.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Blob:   ").Append(file.BlobSha).Append(CultureInfo.InvariantCulture, $" ({file.SizeBytes:N0} bytes)\n");

        // A NUL byte past the 8,000 bytes git looked at. A text file does not have one, and the
        // catalogue's text column ends a value at the first.
        if (content.Contains('\0'))
            return sb.Append("\n[binary content, not indexed]\n").ToString();

        return sb.Append('\n').Append(content.TrimStart('﻿').TrimEnd()).Append('\n').ToString();
    }

    /// <summary>The untracked files of a stash as names and sizes, ending the stash's document.</summary>
    internal static string UntrackedSection(GitStash stash)
    {
        if (stash.UntrackedTotal == 0) return string.Empty;

        var sb = new StringBuilder("\n");
        sb.Append(CultureInfo.InvariantCulture, $"Untracked files in this stash ({stash.UntrackedTotal:N0}):\n");

        foreach (var file in stash.Untracked)
            sb.Append("  ").Append(Printable(file.Path))
              .Append(CultureInfo.InvariantCulture,
                  $"  ({(file.SizeBytes < 0 ? "size unknown" : $"{file.SizeBytes:N0} bytes")}, blob {file.BlobSha[..Math.Min(7, file.BlobSha.Length)]})\n");

        if (stash.UntrackedTotal > stash.Untracked.Count)
            sb.Append(CultureInfo.InvariantCulture,
                $"  [{stash.UntrackedTotal - stash.Untracked.Count:N0} more not listed or indexed: the limit is {MaxUntrackedPerStash:N0} per stash]\n");

        return sb.ToString();
    }

    /// <summary>
    /// Text from a path or a message for a line of a document. A control character, such as a
    /// newline in a file name, would start a line the document did not have.
    /// </summary>
    private const char Replacement = (char)0xFFFD;

    private static string Printable(string text) =>
        text.Any(EndsALine) ? new string([.. text.Select(c => EndsALine(c) ? Replacement : c)]) : text;

    /// <summary>
    /// A control character, which includes <c>\r</c>, <c>\n</c> and U+0085, or the line and paragraph
    /// separators U+2028 and U+2029, which a control test misses and a reader may break a line at.
    /// </summary>
    private static bool EndsALine(char c) => char.IsControl(c) || c is (char)0x2028 or (char)0x2029;
}
