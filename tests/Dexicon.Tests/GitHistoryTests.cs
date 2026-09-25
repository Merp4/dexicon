using System.Diagnostics;
using System.Text;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Reading a repository's history as one document per commit.
///
/// Against a real repository built in the test rather than against canned output. What
/// this code has to get right is the shape of what git actually prints — where the stat
/// starts, what a merge looks like, what an empty body leaves behind — and canned output
/// is a record of what we believed git prints.
/// </summary>
public sealed class GitHistoryTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"githist-{Guid.NewGuid():N}");

    public GitHistoryTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "A Test");
        Git("config", "commit.gpgsign", "false");
    }

    private string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = Process.Start(info)!;

        // Both pipes drained at once, which is the deadlock the code under test comments
        // about and this helper originally walked into. `git add` over 900 files emits a
        // line-ending warning per file to STDERR; reading stdout to the end first filled
        // that buffer, git blocked writing it, stdout never closed, and the test hung
        // until it was killed.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        p.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");

        return stdout;
    }

    private void Commit(string path, string content, string message)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Git("add", path);
        Git("commit", "-m", message);
    }

    /// <summary>
    /// The sha of the commit just made. The inventory carries no subject, so this is
    /// what a test names a particular commit by.
    /// </summary>
    private string Head() => Git("rev-parse", "HEAD").Trim();

    /// <summary>
    /// A path as the code under test takes one: through the factory that holds it against
    /// a workspace root, because that is the only way to make a <see cref="GitRepository"/>.
    /// The root here is the directory above it, which is what the temporary trees are.
    ///
    /// Non-null asserted rather than suppressed: every caller of this passes a directory
    /// it has just created, so null here is the test setup being wrong and should say so
    /// at the line that made the assumption.
    /// </summary>
    private static GitRepository Repo(string path) =>
        GitHistory.RepositoryIn(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        ?? throw new InvalidOperationException($"{path} does not exist, so there is no repository to resolve");

    private Task<IReadOnlyList<GitCommit>> EnumerateAsync(GitHistoryOptions? options = null,
        IReadOnlyList<string>? paths = null) =>
        GitHistory.EnumerateAsync(Repo(_repo), options ?? new GitHistoryOptions(), paths, default);

    private async Task<Dictionary<string, string>> ReadAsync(
        GitHistoryOptions options, IReadOnlyList<GitCommit> commits, IReadOnlyList<string>? paths = null)
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var (sha, text) in
                       GitHistory.ReadAsync(Repo(_repo), options, [.. commits.Select(c => c.Sha)], paths))
            read[sha] = text;
        return read;
    }

    /// <summary>
    /// The diff cap is operator input, stored as JSON on the source and read back on
    /// every pass, so a value that arrived once sizes every later read. Refused where a
    /// bad ref is refused, and for the same reason: the pass reports it and stops rather
    /// than computing a ceiling from it.
    /// </summary>
    [Fact]
    public async Task ADiffCapThatCannotSizeAReadIsRefused()
    {
        Commit("a.txt", "one\n", "first");

        await Should.ThrowAsync<GitHistoryException>(
            () => EnumerateAsync(new GitHistoryOptions { MaxDiffBytes = -1 }));

        // Overflowed the ceiling's addition when it was done in int.
        await Should.ThrowAsync<GitHistoryException>(
            () => EnumerateAsync(new GitHistoryOptions { MaxDiffBytes = int.MaxValue }));

        (await EnumerateAsync()).Count.ShouldBe(1, "the default cap is a usable one");

        // The boundary, because the interesting value is the largest one that works
        // rather than the silly ones above. A cap equal to the read ceiling is refused:
        // the read has to fit the stat as well, so such a cap would kill its own patch
        // and then report the diff as over a limit it was under.
        var largest = (int)GitHistory.MaxDiffCap;

        await Should.ThrowAsync<GitHistoryException>(
            () => EnumerateAsync(new GitHistoryOptions { MaxDiffBytes = largest + 1 }));
        (await EnumerateAsync(new GitHistoryOptions { MaxDiffBytes = largest })).Count.ShouldBe(1);
    }

    /// <summary>
    /// The commit limit, refused for the same reason and in the same place.
    ///
    /// git accepts both bad values and does something silently unhelpful with each,
    /// measured: `--max-count=0` returns no commits and exits 0, so the source indexes
    /// nothing and reports no error, and `--max-count=-1` is treated as unlimited, so a
    /// negative limit quietly means the opposite of a limit. Neither is a failure the
    /// operator would ever see without this.
    /// </summary>
    [Fact]
    public async Task ACommitLimitThatIsNotALimitIsRefused()
    {
        Commit("a.txt", "one", "first");
        Commit("b.txt", "two", "second");

        await Should.ThrowAsync<GitHistoryException>(
            () => EnumerateAsync(new GitHistoryOptions { MaxCommits = 0 }));
        await Should.ThrowAsync<GitHistoryException>(
            () => EnumerateAsync(new GitHistoryOptions { MaxCommits = -1 }));

        (await EnumerateAsync(new GitHistoryOptions { MaxCommits = 1 })).Count.ShouldBe(1);
        (await EnumerateAsync(new GitHistoryOptions())).Count.ShouldBe(2, "absent means all of them");
    }

    /// <summary>
    /// The message pass had no bound at all, and a commit message is arbitrary text.
    ///
    /// Both halves matter: that the read is stopped, and that what comes out is the
    /// exception the caller handles. Left as the raw too-large kind it escaped the
    /// source's catch and killed the job, which writes an outage as a decision about
    /// the corpus.
    /// </summary>
    [Fact]
    public async Task ACommitMessageTooLargeToReadFailsTheBatchWithAReason()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(_repo, "msg.txt"), new string('m', 2 * 1024 * 1024));
        Git("add", "a.txt");
        Git("commit", "-F", "msg.txt");

        var commits = await EnumerateAsync();
        commits.Count.ShouldBe(1);

        var ex = await Should.ThrowAsync<GitHistoryException>(
            () => ReadAsync(new GitHistoryOptions(), commits));

        ex.Message.ShouldContain("could not be read");
    }

    /// <summary>
    /// The ceilings are named in bytes, so they are counted in bytes.
    ///
    /// Enforced against <c>StringBuilder.Length</c> they were UTF-16 code units, and a
    /// character outside the ASCII range is one code unit and up to four bytes: 400,000
    /// of these is 1.2 MB of UTF-8 against a 1 MB ceiling, and 400,000 code units, so
    /// the read passed a limit it was over. Document already measures the patch in UTF-8
    /// bytes for the same reason, one measurement further up the same file.
    /// </summary>
    [Fact]
    public async Task AReadIsMeasuredInTheBytesItsCeilingIsNamedIn()
    {
        const int Count = 400_000;
        var message = new string('漢', Count);   // three bytes each in UTF-8

        Encoding.UTF8.GetByteCount(message).ShouldBe(Count * 3, "the premise of this test");
        message.Length.ShouldBeLessThan(1024 * 1024, "and under the ceiling as code units");

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(_repo, "msg.txt"), message, new UTF8Encoding(false));
        Git("add", "a.txt");
        Git("commit", "-F", "msg.txt");

        var commits = await EnumerateAsync();

        await Should.ThrowAsync<GitHistoryException>(
            () => ReadAsync(new GitHistoryOptions(), commits));
    }

    /// <summary>
    /// The same repository, the same commit, and the opposite outcome from the setting
    /// alone. A source that has turned messages off must not be stopped by a message:
    /// it is not asked for, so its size is not a fact about this read.
    /// </summary>
    [Fact]
    public async Task AMessageTooLargeToReadIsNoObstacleWhenMessagesAreOff()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(_repo, "msg.txt"), new string('m', 2 * 1024 * 1024));
        Git("add", "a.txt");
        Git("commit", "-F", "msg.txt");

        var commits = await EnumerateAsync();
        var read = await ReadAsync(new GitHistoryOptions { IncludeMessage = false }, commits);

        var document = read.Values.ShouldHaveSingleItem();
        document.ShouldNotContain("mmmm", Case.Sensitive);
        document.ShouldContain("commit " + commits[0].Sha);
    }

    /// <summary>
    /// "No patch here" and "the patch starts at the beginning" are different answers.
    ///
    /// They used to be the same value: a tail beginning with the header gave 0, and so
    /// did the not-found branch, so the whole patch came back as the stat and the cap
    /// was measured against a patch of zero bytes. The tail as git emits it opens with a
    /// newline, so the collision is not reachable today — it is in the function rather
    /// than in the format, and anything that trims the tail would turn a silently
    /// unenforced cap into the behaviour with nothing failing to say so.
    /// </summary>
    [Fact]
    public void APatchAtTheStartOfATailIsStillAPatch()
    {
        var (stat, patch) = GitHistory.SplitPatch("diff --git a/x b/x\n-one\n+two\n");
        stat.ShouldBeEmpty();
        patch.ShouldStartWith("diff --git");

        var (leading, afterNewline) = GitHistory.SplitPatch("\ndiff --git a/x b/x\n+two\n");
        leading.ShouldBe("\n", "the tail as git emits it: the sha line's own newline, then the header");
        afterNewline.ShouldStartWith("diff --git");

        var (onlyStat, none) = GitHistory.SplitPatch(" x.txt | 1 +\n 1 file changed\n");
        onlyStat.ShouldBe(" x.txt | 1 +\n 1 file changed\n");
        none.ShouldBeEmpty();
    }

    /// <summary>
    /// Two commits that land on one path are one document, and it is the newer.
    ///
    /// A real collision needs two shas sharing twelve hex characters on one day, which
    /// is a birthday search rather than something a test can commit its way to, so this
    /// builds the pair directly. What matters is not the odds but that the outcome is
    /// decided: carried forward, both were processed under the one path, each deleting
    /// the other's chunks and upserting its own, so which document survived fell out of
    /// the order they were read in.
    /// </summary>
    [Fact]
    public void TwoCommitsOnOnePathAreOneDocument()
    {
        var day = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
        var newer = new GitCommit(new string('a', 12) + new string('1', 28), day.AddHours(2));
        var older = new GitCommit(new string('a', 12) + new string('2', 28), day);
        var other = new GitCommit(new string('b', 40), day);

        newer.RelativePath.ShouldBe(older.RelativePath, "the premise: these collide");

        // git log order, newest first.
        var kept = GitHistory.OnePerPath([newer, older, other]);

        kept.Count.ShouldBe(2);
        kept[0].Sha.ShouldBe(newer.Sha, "the newer of a colliding pair is the one kept");
        kept[1].Sha.ShouldBe(other.Sha);
    }

    /// <summary>
    /// The largest limit the settings accept, beside a held commit. The capacity hint was
    /// the limit plus the held count, which overflows to a negative capacity there, so a
    /// limit the endpoints accept made every later pass throw.
    /// </summary>
    [Fact]
    public void SelectingWithTheLargestLimitAndAHeldCommitDoesNotOverflow()
    {
        var inventory = new[]
        {
            new GitCommit(new string('a', 40), DateTimeOffset.UnixEpoch),
            new GitCommit(new string('b', 40), DateTimeOffset.UnixEpoch),
        };
        var options = new GitHistoryOptions { MaxCommits = int.MaxValue, KeepIndexed = true };
        var held = new HashSet<string>(StringComparer.Ordinal) { inventory[1].RelativePath };

        GitHistory.Select(inventory, options, held).Count.ShouldBe(2);
    }

    /// <summary>
    /// A repository cannot change what its own commits say by changing its config.
    ///
    /// The fingerprint settles a document from the sha and the source's settings, so
    /// anything else that alters the text makes the pre-read skip hold a document git
    /// would no longer produce — and the skip is the path that avoids looking, so
    /// nothing notices. Two settings are checked: `core.quotePath`, the one most likely to
    /// be set for real, and `diff.noprefix`, both visible in the diff header.
    /// `diff.context` is set as well, but a one-line file has no context for it to widen,
    /// so nothing here observes it.
    /// </summary>
    [Fact]
    public async Task RepositoryConfigDoesNotChangeWhatACommitSays()
    {
        var name = "漢.txt";
        File.WriteAllText(Path.Combine(_repo, name), "one\n");
        Git("add", "--all");
        Git("commit", "-m", "a path outside ASCII");

        Git("config", "core.quotePath", "true");
        Git("config", "diff.noprefix", "true");
        Git("config", "diff.context", "7");

        var commits = await EnumerateAsync();
        var read = await ReadAsync(new GitHistoryOptions { IncludeDiff = true }, commits);
        var document = read[commits[0].Sha];

        document.ShouldContain(name, Case.Sensitive);
        // The octal escape quotePath produces for 漢 (UTF-8 E6 BC A2). The backslashes are
        // what a sha or a date cannot contain; "346" alone appeared in one by chance.
        document.ShouldNotContain(@"\346\274\242", Case.Sensitive);
        document.ShouldContain($"diff --git a/{name} b/{name}", Case.Sensitive);
    }

    /// <summary>
    /// Signature verification is a second way a repository's own config runs a program.
    ///
    /// `log.showSignature=true` plus `gpg.program=&lt;anything&gt;`, both settable in the
    /// repository being read, make `git log` execute that program for any commit
    /// carrying a gpgsig header. Measured on a scratch repository: with the two set, a
    /// fake gpg left its marker file behind, and with `-c log.showSignature=false` it did
    /// not. An unsigned commit does not trigger it, which is why this test has to build
    /// the signed object by hand — there is no key involved and none is needed, because
    /// git runs the program to find out whether the signature is any good.
    /// </summary>
    [Fact]
    public async Task ASignatureVerifierInTheRepositoryIsNotRun()
    {
        Commit("a.txt", "one", "first");

        var marker = Path.Combine(_repo, "gpg-ran.txt");
        Git("config", "gpg.program", FakeGpg(marker));
        Git("config", "log.showSignature", "true");

        var tree = Git("rev-parse", "HEAD^{tree}").Trim();
        var commit =
            $"tree {tree}\n"
            + "author A Test <test@example.invalid> 1700000000 +0000\n"
            + "committer A Test <test@example.invalid> 1700000000 +0000\n"
            + "gpgsig -----BEGIN PGP SIGNATURE-----\n \n ZmFrZQ==\n -----END PGP SIGNATURE-----\n"
            + "\nsigned commit\n";

        File.WriteAllText(Path.Combine(_repo, "object.txt"), commit);
        var sha = Git("hash-object", "-t", "commit", "-w", "object.txt").Trim();
        sha.Length.ShouldBe(40, "the signed object has to exist for this to test anything");

        await ReadAsync(new GitHistoryOptions(), [new GitCommit(sha, DateTimeOffset.UtcNow)]);

        File.Exists(marker).ShouldBeFalse("the repository's own gpg.program was executed");
    }

    /// <summary>A program that records having been run, executable on this platform.</summary>
    private string FakeGpg(string marker)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_repo, "fakegpg.cmd");
            File.WriteAllText(cmd, $"@echo off\r\necho RAN > \"{marker}\"\r\nexit /b 1\r\n");
            return cmd;
        }

        var sh = Path.Combine(_repo, "fakegpg.sh");
        File.WriteAllText(sh, $"#!/bin/sh\necho RAN > '{marker}'\nexit 1\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    /// <summary>
    /// A sha settles the content, which is the assumption the whole cheap refresh rests
    /// on, and `git replace` makes it false.
    ///
    /// A replacement is honoured by every read by default, so the same sha yields
    /// different text and the pre-read skip keeps a document nobody would recognise.
    /// Indexing the object the sha names is what makes the sha an identity at all.
    /// </summary>
    [Fact]
    public async Task AReplaceRefDoesNotChangeWhatAShaSays()
    {
        Commit("a.txt", "one", "ORIGINAL MESSAGE");
        var original = Head();

        Git("checkout", "-q", "--detach", original);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one");
        Git("add", "a.txt");
        Git("commit", "--amend", "-m", "REPLACEMENT MESSAGE");
        var replacement = Head();

        Git("replace", original, replacement);

        var read = await ReadAsync(
            new GitHistoryOptions(), [new GitCommit(original, DateTimeOffset.UtcNow)]);

        var document = read.Values.ShouldHaveSingleItem();
        document.ShouldContain("ORIGINAL MESSAGE");
        document.ShouldNotContain("REPLACEMENT MESSAGE");
    }

    /// <summary>
    /// A repository's own settings do not get to run a command.
    ///
    /// A <c>diff=&lt;driver&gt;</c> attribute and a <c>diff.&lt;driver&gt;.textconv</c> in
    /// the config both live inside the repository being read, and together they make git
    /// run that command on every blob it diffs. Whoever can write to a repository under
    /// the workspace root therefore gets a command executed by the indexer, with its
    /// output indexed as the file's content.
    ///
    /// Both halves are asserted: the driver's output is absent, and the real content is
    /// present. Absent alone would pass against a read that returned nothing at all.
    /// </summary>
    [Fact]
    public async Task ATextconvDriverInTheRepositoryIsNotRun()
    {
        Commit(".gitattributes", "*.bin diff=evil\n", "attributes");
        Commit("a.bin", "the real content\n", "first");
        Commit("a.bin", "the real content, changed\n", "second");
        Git("config", "diff.evil.textconv", "echo PWNED-BY-TEXTCONV");

        var commits = await EnumerateAsync();
        var read = await ReadAsync(new GitHistoryOptions { IncludeDiff = true }, commits);

        var patches = string.Join('\n', read.Values);
        patches.ShouldNotContain("PWNED-BY-TEXTCONV");
        patches.ShouldContain("the real content, changed");
    }

    /// <summary>
    /// The workspace boundary, held by the code that starts the process.
    ///
    /// A source's root path is operator input that reaches Process.Start as a working
    /// directory. The API and the sweep refuse an escaping path already, and that is
    /// what is offered rather than what is enforced: the decision is made again here,
    /// and a <see cref="GitRepository"/> cannot be made any other way.
    /// </summary>
    [Fact]
    public void APathOutsideTheWorkspaceRootNeverBecomesARepository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "inside"));
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => GitHistory.RepositoryIn(root, "../elsewhere"));
            Should.Throw<UnauthorizedAccessException>(() => GitHistory.RepositoryIn(root, "a/../../b"));
            Should.Throw<UnauthorizedAccessException>(
                () => GitHistory.RepositoryIn(root, Path.Combine(Path.GetTempPath(), "somewhere-else")));

            GitHistory.RepositoryIn(root, "inside")!.FullPath
                .ShouldBe(Path.Combine(Path.GetFullPath(root), "inside"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A link is the same escape as `..`, by a mechanism the string never shows.
    ///
    /// <c>Path.GetFullPath</c> canonicalises separators and dots and resolves no links,
    /// so `root/link` passes the boundary check on its spelling and then IS the outside
    /// directory. Links are not followed, so one that stays inside is refused as well; the
    /// directory it points at can be named instead.
    /// </summary>
    [Fact]
    public void ALinkInARepositoryPathIsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "inward"), Path.Combine(root, "real"));

        try
        {
            var refused = Should.Throw<UnauthorizedAccessException>(
                () => GitHistory.RepositoryIn(root, "link"));
            refused.Message.ShouldContain(outside, Case.Insensitive);

            Should.Throw<UnauthorizedAccessException>(() => GitHistory.RepositoryIn(root, "inward"));

            GitHistory.RepositoryIn(root, "real")!.FullPath
                .ShouldBe(Path.Combine(Path.GetFullPath(root), "real"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>
    /// The path handed to git is the filesystem's own string, not the caller's.
    ///
    /// On Windows and macOS a differently-cased request resolves to the same directory,
    /// and what comes back is spelled the way the directory is. That is what keeps the
    /// request's text out of the working directory a process is started in.
    /// </summary>
    [Fact]
    public void TheResolvedPathIsTheOneTheFilesystemReports()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "MixedCase"));
        try
        {
            GitHistory.RepositoryIn(root, "MixedCase")!.FullPath
                .ShouldEndWith("MixedCase");

            var asked = GitHistory.RepositoryIn(root, "mixedcase");
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                asked!.FullPath.ShouldEndWith("MixedCase");
            else
                asked.ShouldBeNull("a case-sensitive filesystem has no such directory");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Absent is not the same answer as refused. A mount that is away is an operational
    /// condition the callers report without touching what they indexed last time; a path
    /// that escapes the root is a refusal. Returning a string for both made them one.
    /// </summary>
    [Fact]
    public async Task ADirectoryThatIsNotARepositoryIsNotOne()
    {
        (await GitHistory.IsRepositoryAsync(Repo(_repo), default)).ShouldBeTrue();

        var plain = Path.Combine(Path.GetTempPath(), $"plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);
        try
        {
            (await GitHistory.IsRepositoryAsync(Repo(plain), default)).ShouldBeFalse();

            GitHistory.RepositoryIn(plain, "nope").ShouldBeNull();
        }
        finally { Directory.Delete(plain, recursive: true); }
    }

    /// <summary>
    /// git's stderr is drained to the end, so git never blocks on a full pipe, and kept to
    /// a bound, so a flood of diagnostics cannot allocate without limit. The output
    /// ceilings covered stdout alone.
    /// </summary>
    [Fact]
    public async Task StderrIsDrainedWholeAndKeptToABound()
    {
        var flood = new string('x', GitHistory.StderrKept * 4);
        using var reader = new StringReader(flood + "tail");

        var kept = await GitHistory.DrainAsync(reader, GitHistory.StderrKept, default);

        kept.Length.ShouldBe(GitHistory.StderrKept);
        (await reader.ReadToEndAsync()).ShouldBeEmpty("the rest was read, so a pipe would not block");

        using var short_ = new StringReader("fatal: not a git repository\n");
        (await GitHistory.DrainAsync(short_, GitHistory.StderrKept, default)).ShouldBe("fatal: not a git repository\n");
    }

    /// <summary>
    /// Adding a history source at a folder: 400 when the folder has no repository, 503
    /// when git could not be asked. The second was a 500, and a 400 would send someone to
    /// check a path that is right.
    /// </summary>
    [Fact]
    public async Task AGitThatCannotBeAskedIsNotReportedAsTheWrongFolder()
    {
        var root = Path.GetDirectoryName(_repo)!;
        var name = Path.GetFileName(_repo);

        (await Dexicon.Api.CorpusEndpoints.NotAHistoryRootAsync(root, name, GitHistory.IsRepositoryAsync, default))
            .ShouldBeNull("the folder holds a repository");

        var plain = Path.Combine(Path.GetTempPath(), $"plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);
        try
        {
            var notOne = await Dexicon.Api.CorpusEndpoints.NotAHistoryRootAsync(
                Path.GetDirectoryName(plain)!, Path.GetFileName(plain), GitHistory.IsRepositoryAsync, default);
            notOne.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>()
                .StatusCode.ShouldBe(400);
        }
        finally { Directory.Delete(plain, recursive: true); }

        var unasked = await Dexicon.Api.CorpusEndpoints.NotAHistoryRootAsync(root, name,
            (_, _) => throw new GitHistoryException("git could not be started. A git-history source needs the git binary on PATH."),
            default);
        var problem = unasked.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>();
        problem.StatusCode.ShouldBe(503);
        problem.ProblemDetails.Detail.ShouldNotBeNull().ShouldContain("could not be started");
    }

    /// <summary>
    /// A subdirectory of a repository is not a repository.
    ///
    /// `rev-parse --is-inside-work-tree` says true from `/repo/src`, and a source
    /// accepted there would have walked the whole of `/repo`: every commit of the parent
    /// indexed under a source the operator scoped to one folder, including commits that
    /// never touched it.
    /// </summary>
    [Fact]
    public async Task ASubdirectoryOfARepositoryIsNotOne()
    {
        Commit("src/a.txt", "one", "first");

        (await GitHistory.IsRepositoryAsync(Repo(_repo), default)).ShouldBeTrue();
        (await GitHistory.IsRepositoryAsync(Repo(Path.Combine(_repo, "src")), default)).ShouldBeFalse();
    }

    /// <summary>
    /// A commit message is arbitrary text, and it used to be parsed out of the same
    /// stream as git's own output. A body holding the record separator split a record
    /// in two and the fragment was dropped, which loses a commit — and a commit the
    /// reader drops is one the shared reconcile sees as vanished and deletes the
    /// vectors of. A body line beginning `diff --git ` was read as the start of a patch.
    /// </summary>
    [Fact]
    public async Task ACommitMessageThatLooksLikeGitOutputIsStillAMessage()
    {
        var hostile = "Not a patch:\ndiff --git a/x b/x\n and a stat | line\n and a separator";

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one");
        Git("add", "a.txt");
        Git("commit", "-m", "the subject", "-m", hostile);

        var commits = await EnumerateAsync();
        commits.ShouldHaveSingleItem();

        var text = (await ReadAsync(new GitHistoryOptions { IncludeDiff = true }, commits))[commits[0].Sha];

        text.ShouldContain("Not a patch:", Case.Sensitive);
        text.ShouldContain("and a separator", Case.Sensitive, "the body survives the separator inside it");
        text.ShouldContain("+one", Case.Sensitive, "and the real patch is still there");
    }

    [Fact]
    public async Task TheInventoryIsShasDatesAndSubjectsNewestFirst()
    {
        Commit("a.txt", "one", "first change");
        var first = Head();
        Commit("b.txt", "two", "second change");
        var second = Head();

        var commits = await EnumerateAsync();

        commits.Count.ShouldBe(2);
        commits[0].Sha.ShouldBe(second, "newest first, as git log gives them");
        commits[1].Sha.ShouldBe(first);
        commits.ShouldAllBe(c => c.Sha.Length == 40);
        commits[0].AuthorDate.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    /// <summary>
    /// Dated first, so the file list reads chronologically rather than by hash.
    /// </summary>
    [Fact]
    public async Task ThePathIsTheDateAndTheShortSha()
    {
        Commit("a.txt", "one", "first change");
        var commit = (await EnumerateAsync())[0];

        commit.RelativePath.ShouldBe($"commits/{commit.AuthorDate:yyyy-MM-dd}-{commit.Sha[..12]}");
    }

    [Fact]
    public async Task TheDocumentReadsLikeGitShow()
    {
        Commit("a.txt", "one\n", "a subject line");

        var commits = await EnumerateAsync();
        var text = (await ReadAsync(new GitHistoryOptions(), commits))[commits[0].Sha];

        text.ShouldStartWith($"commit {commits[0].Sha}");
        text.ShouldContain("Author: A Test <test@example.invalid>");
        text.ShouldContain("    a subject line");
        text.ShouldContain("a.txt", Case.Sensitive, "the stat names the file that changed");
    }

    [Fact]
    public async Task TheMessageBodyIsKept()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "x.txt"), "x");
        Git("add", "src/x.txt");
        Git("commit", "-m", "the subject", "-m", "The body explains why,\nover two lines.");

        var commits = await EnumerateAsync();
        var text = (await ReadAsync(new GitHistoryOptions(), commits))[commits[0].Sha];

        text.ShouldContain("    the subject");
        text.ShouldContain("    The body explains why,");
        text.ShouldContain("    over two lines.");
    }

    [Fact]
    public async Task TheDiffIsLeftOutUnlessItIsAskedFor()
    {
        Commit("a.txt", "the quick brown fox\n", "add a line");
        var commits = await EnumerateAsync();

        var without = (await ReadAsync(new GitHistoryOptions(), commits))[commits[0].Sha];
        var with = (await ReadAsync(new GitHistoryOptions { IncludeDiff = true }, commits))[commits[0].Sha];

        without.ShouldNotContain("the quick brown fox", Case.Sensitive,
            "the stat says which files changed, not what they now say");
        with.ShouldContain("+the quick brown fox");
        with.ShouldContain("diff --git");
    }

    [Fact]
    public async Task AStatCanBeLeftOutToo()
    {
        Commit("a.txt", "one\n", "add a line");
        var commits = await EnumerateAsync();

        var text = (await ReadAsync(
            new GitHistoryOptions { IncludeStat = false }, commits))[commits[0].Sha];

        text.ShouldContain("    add a line");
        text.ShouldNotContain("1 file changed");
    }

    /// <summary>
    /// A patch over the limit is reported, not cut. A diff truncated mid-hunk reads as a
    /// complete change that did something other than what it did.
    /// </summary>
    [Fact]
    public async Task AnOversizedDiffIsStatedRatherThanCut()
    {
        Commit("big.txt", string.Join('\n', Enumerable.Range(0, 2_000).Select(i => $"line {i}")), "a big change");
        var commits = await EnumerateAsync();

        var text = (await ReadAsync(
            new GitHistoryOptions { IncludeDiff = true, MaxDiffBytes = 500 }, commits))[commits[0].Sha];

        text.ShouldContain("not included");
        text.ShouldContain("byte limit for this source");
        text.ShouldNotContain("+line 1999", Case.Sensitive);
        text.ShouldContain("    a big change", Case.Sensitive, "the message survives the diff being dropped");
        text.ShouldContain("big.txt", Case.Sensitive, "and so does the stat, which is the cheap half");
    }

    /// <summary>
    /// The cap bounds the read, not only the document.
    ///
    /// Reading a batch whole and measuring afterwards is not a bound: by then the patch
    /// is allocated, and one commit carrying a vendored tree could exhaust the indexer
    /// before the limit dropped it. git is killed at a ceiling instead, the batch is
    /// halved to find which commit did it, and that one is read again without a patch.
    ///
    /// Two commits, so the halving runs as well as the fallback.
    /// </summary>
    [Fact]
    public async Task APatchTooLargeToReadIsNeverRead()
    {
        Commit("small.txt", "one\n", "a small change");

        // Over the read ceiling, which is the stat's own allowance plus the diff cap.
        // Long lines rather than many, so git has less to diff for the same bytes.
        var wide = new string('x', 100);
        Commit("big.txt", string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"{i} {wide}")),
            "a huge change");

        var commits = await EnumerateAsync();
        commits.Count.ShouldBe(2);

        // Below the smallest thing git will emit for the big commit, so the ceiling is
        // certain to be hit rather than merely likely.
        var read = await ReadAsync(new GitHistoryOptions { IncludeDiff = true, MaxDiffBytes = 2_000 }, commits);

        read.Count.ShouldBe(2, "the batch is halved, so the other commit is not lost with it");

        var big = read[commits[0].Sha];
        big.ShouldContain("    a huge change", Case.Sensitive);
        big.ShouldContain("not included");
        big.ShouldNotContain(wide, Case.Sensitive, "no part of the patch reached the document");
        big.ShouldContain("big.txt", Case.Sensitive, "the stat survives, which is the point of keeping it");

        // Which path dropped it, and the distinction is the whole test. A patch read
        // and then measured says how large it was — "diff of 1,234,567 bytes" — and a
        // patch never read cannot. Without this the assertions above pass whether the
        // ceiling fired or the document cap did, which is the same test twice.
        big.ShouldNotContain("diff of ", Case.Sensitive,
            "a size here would mean the patch was read before being dropped");

        read[commits[1].Sha].ShouldContain("+one", Case.Sensitive,
            "a small commit in the same batch still gets its patch");
    }

    /// <summary>
    /// A commit that touches many files keeps its stat, and the source does not fail.
    ///
    /// The ceiling measured the stat against the diff's budget and then handed the
    /// stat-only retry that same budget, so a commit whose stat alone exceeded it made
    /// the retry throw as well — losing the one part the cap promises to keep, and
    /// failing the whole source with it. The stat has its own allowance now.
    ///
    /// Note what this does NOT claim: that a large stat can coexist with a small patch.
    /// It cannot, because a stat line implies a diff header, so the patch is always the
    /// larger of the two. The reachable half of the problem is the retry.
    /// </summary>
    [Fact]
    public async Task ACommitThatTouchesManyFilesKeepsItsStat()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "many"));
        for (var i = 0; i < 900; i++)
            File.WriteAllText(Path.Combine(_repo, "many", $"f{i}.txt"), "a\n");

        Git("add", "many");
        Git("commit", "-m", "touch many files");

        var commits = await EnumerateAsync();

        var text = (await ReadAsync(
            new GitHistoryOptions { IncludeDiff = true, MaxDiffBytes = 32_000 }, commits))[commits[0].Sha];

        text.ShouldContain("900 files changed", Case.Sensitive,
            "the stat is kept whatever happens to the patch");
        text.ShouldContain("    touch many files", Case.Sensitive);
        text.ShouldContain("not included", Case.Sensitive, "and the patch is over its own limit");
    }

    [Fact]
    public async Task MergesAreLeftOutUnlessTheyAreAskedFor()
    {
        Commit("a.txt", "one", "base");
        Git("checkout", "-b", "side");
        Commit("b.txt", "two", "on the side");
        Git("checkout", "main");
        Commit("c.txt", "three", "on main");
        Git("merge", "--no-ff", "side", "-m", "merge the side branch");
        var merge = Head();

        var without = await EnumerateAsync();
        var with = await EnumerateAsync(new GitHistoryOptions { IncludeMerges = true });

        without.ShouldNotContain(c => c.Sha == merge);
        with.ShouldContain(c => c.Sha == merge);
    }

    [Fact]
    public async Task TheWalkCanBeBoundedByCountAndByDate()
    {
        Commit("a.txt", "one", "first");
        Commit("b.txt", "two", "second");
        Commit("c.txt", "three", "third");

        (await EnumerateAsync(new GitHistoryOptions { MaxCommits = 2 })).Count.ShouldBe(2);

        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        (await EnumerateAsync(new GitHistoryOptions { Since = tomorrow })).ShouldBeEmpty();
    }

    /// <summary>
    /// The source's own filters become pathspecs, so they mean "whose history" and narrow
    /// the diff at the same time.
    /// </summary>
    [Fact]
    public async Task PathspecsSelectWhoseHistoryIsIndexed()
    {
        Commit("src/a.txt", "one", "touch src");
        var touchedSrc = Head();
        Commit("docs/b.txt", "two", "touch docs");

        var all = await EnumerateAsync();
        var srcOnly = await EnumerateAsync(paths: ["src"]);

        all.Count.ShouldBe(2);
        srcOnly.ShouldHaveSingleItem().Sha.ShouldBe(touchedSrc);
    }

    /// <summary>
    /// The ref decides which commits, and must not be able to become a git option: a
    /// value starting with a dash arrives after `--end-of-options`.
    /// </summary>
    [Fact]
    public async Task ARefThatLooksLikeAnOptionIsTreatedAsARef()
    {
        Commit("a.txt", "one", "first");

        var boom = await Should.ThrowAsync<GitHistoryException>(
            EnumerateAsync(new GitHistoryOptions { Ref = "--output=/tmp/pwned" }));

        boom.Message.ShouldContain("not a usable ref");
        File.Exists("/tmp/pwned").ShouldBeFalse();

        // What the check admits, stated, so that narrowing it later cannot quietly break
        // ordinary use — and what it refuses, so widening it cannot quietly stop refusing.
        foreach (var ok in new[] { "HEAD", "main", "release/1.0", "v1.2.3", "HEAD~3", "HEAD^", "@{u}" })
            GitHistory.IsAcceptableRef(ok).ShouldBeTrue(ok);

        foreach (var no in new[] { "--upload-pack=x", "-n", "a b", "a;b", "a$(x)", "main..other", "", "  " })
            GitHistory.IsAcceptableRef(no).ShouldBeFalse(no);
    }

    /// <summary>
    /// The fingerprint decides whether an indexed commit has to be read again. It covers
    /// what the document SAYS, and deliberately not which commits are selected: a moved
    /// tip must not re-index the history behind it.
    /// </summary>
    [Fact]
    public void TheFingerprintCoversContentAndNotSelection()
    {
        var baseline = new GitHistoryOptions();

        // Selection, not content: these choose WHICH commits are indexed. Including one
        // would re-read and re-embed every commit in the repository the first time it
        // changed, for documents not one of which had moved.
        (baseline with { Ref = "other" }).ContentFingerprint().ShouldBe(baseline.ContentFingerprint());
        (baseline with { MaxCommits = 10 }).ContentFingerprint().ShouldBe(baseline.ContentFingerprint());
        (baseline with { Since = new DateOnly(2020, 1, 1) }).ContentFingerprint()
            .ShouldBe(baseline.ContentFingerprint());
        (baseline with { IncludeMerges = true }).ContentFingerprint()
            .ShouldBe(baseline.ContentFingerprint(), "turning merges on ADDS documents; it alters none");

        // The cap matters only when there is a patch for it to cap.
        (baseline with { MaxDiffBytes = 1 }).ContentFingerprint()
            .ShouldBe(baseline.ContentFingerprint(), "no diff, so no cap to apply");
        (baseline with { IncludeDiff = true, MaxDiffBytes = 1 }).ContentFingerprint()
            .ShouldNotBe((baseline with { IncludeDiff = true }).ContentFingerprint());

        (baseline with { IncludeDiff = true }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
        (baseline with { IncludeStat = false }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());
        (baseline with { IncludeMessage = false }).ContentFingerprint().ShouldNotBe(baseline.ContentFingerprint());

        // Pathspecs reach git, so they decide what the stat lists and what the patch
        // holds: the same commit under a narrower filter is a different document. Their
        // order does not, because the same set is the same filter.
        baseline.ContentFingerprint(["src"]).ShouldNotBe(baseline.ContentFingerprint());
        baseline.ContentFingerprint(["src", "docs"]).ShouldBe(baseline.ContentFingerprint(["docs", "src"]));

        // A pathspec is a caller's text and may hold any character, so a separator alone
        // cannot encode the list: joined on a comma these two are the same string, and
        // they are different filters. One of them would skip a commit whose stat had
        // been cut to the other's paths.
        baseline.ContentFingerprint(["a,b"]).ShouldNotBe(baseline.ContentFingerprint(["a", "b"]));
        baseline.ContentFingerprint(["a;b"]).ShouldNotBe(baseline.ContentFingerprint(["a", "b"]));
        baseline.ContentFingerprint(["2:ab"]).ShouldNotBe(baseline.ContentFingerprint(["ab"]));
    }

    [Fact]
    public void OptionsRoundTripThroughJson()
    {
        var options = new GitHistoryOptions
        {
            Ref = "release/1.0", IncludeDiff = true, MaxDiffBytes = 1234,
            IncludeMerges = true, MaxCommits = 50, Since = new DateOnly(2026, 1, 2),
        };

        GitHistoryOptions.FromJson(options.ToJson()).ShouldBe(options);
        GitHistoryOptions.FromJson(null).ShouldBe(new GitHistoryOptions());
        GitHistoryOptions.FromJson("   ").ShouldBe(new GitHistoryOptions());
    }

    public void Dispose()
    {
        try
        {
            // git leaves read-only objects behind, which Directory.Delete will not remove.
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
