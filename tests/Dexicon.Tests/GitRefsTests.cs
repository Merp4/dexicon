using System.Diagnostics;
using Dexicon.Api;
using Dexicon.Core.Indexing;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What a repository could be followed at, and how current each choice is.
///
/// Against real repositories with a local bare remote, because what matters is what git
/// actually writes: where a prefetch puts its refs differs by version (measured: 2.31
/// writes refs/prefetch/origin/main, 2.54 refs/prefetch/remotes/origin/main), and which
/// file a fetch dates itself by is a fact about git rather than a belief about it. The
/// test's own git reaches the remote by path; the code under test reaches nothing.
/// </summary>
public sealed class GitRefsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gitrefs-{Guid.NewGuid():N}");
    private readonly List<string> _elsewhere = [];

    private string Remote => Path.Combine(_root, "remote.git");
    private string Work => Path.Combine(_root, "work");
    private string Clone => Path.Combine(_root, "repo");

    public GitRefsTests()
    {
        Directory.CreateDirectory(_root);
        Git(_root, "init", "--quiet", "--bare", "remote.git");
        Git(_root, "clone", "--quiet", "remote.git", "work");
        Commit(Work, "a.txt", "one\n", "one");
        Git(Work, "push", "--quiet", "origin", "main");
        Git(_root, "clone", "--quiet", "remote.git", "repo");
    }

    /// <summary>
    /// git as the test drives it. The identity, signing and line endings are pinned here
    /// rather than read from the machine, so a developer's global config cannot decide a
    /// result.
    /// </summary>
    private static string Git(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var pin in new[]
                 {
                     "user.name=A Test", "user.email=test@example.invalid", "commit.gpgsign=false",
                     "init.defaultBranch=main", "core.autocrlf=false", "protocol.file.allow=always",
                 })
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(pin);
        }
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr.GetAwaiter().GetResult()}");

        return stdout.GetAwaiter().GetResult();
    }

    private static void Commit(string dir, string path, string content, string message)
    {
        File.AppendAllText(Path.Combine(dir, path), content);
        Git(dir, "add", path);
        Git(dir, "commit", "--quiet", "-m", message);
    }

    /// <summary>Commits pushed to the remote from elsewhere, so the clone's copy falls behind.</summary>
    private void PushedElsewhere(int commits)
    {
        for (var i = 0; i < commits; i++) Commit(Work, "a.txt", $"more {i}\n", $"elsewhere {i}");
        Git(Work, "push", "--quiet", "origin", "main");
    }

    private GitRepository Repo(string name = "repo") =>
        GitHistory.RepositoryIn(_root, name) ?? throw new InvalidOperationException($"{name} was not created");

    private Task<GitRefListing> RefsAsync(string name = "repo") => GitHistory.RefsAsync(Repo(name), default);

    [Fact]
    public async Task TheListingHasLocalRemoteTrackingAndPrefetchedRefs()
    {
        Git(Clone, "branch", "feature");
        PushedElsewhere(2);
        Git(Clone, "maintenance", "run", "--task=prefetch");

        var refs = await RefsAsync();

        refs.Head.Branch.ShouldBe("refs/heads/main");
        refs.Head.Sha.ShouldBe(Git(Clone, "rev-parse", "HEAD").Trim());

        refs.Local.Refs.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["refs/heads/feature", "refs/heads/main"]);
        refs.Local.Refs.ShouldAllBe(r => r.CommittedUtc != null && r.Sha.Length == 40);

        // origin/HEAD is a pointer to origin/main, not a second thing to follow.
        refs.RemoteTracking.Refs.Select(r => r.Name).ShouldBe(["refs/remotes/origin/main"]);

        // Wherever this git wrote it, the prefetched ref carries the remote's tip and says
        // which remote-tracking ref it copies, which a prefetch does not move.
        var prefetched = refs.Prefetched.Refs.ShouldHaveSingleItem();
        prefetched.Name.ShouldStartWith("refs/prefetch/");
        prefetched.Sha.ShouldBe(Git(Remote, "rev-parse", "main").Trim());
        prefetched.Mirrors.ShouldBe("refs/remotes/origin/main");
        prefetched.SameAsMirrored.ShouldBe(false, "a prefetch leaves origin/main where the last fetch put it");

        refs.LastFetchUtc.ShouldBeNull("a clone and a prefetch write no FETCH_HEAD, so no fetch has run");
        File.Exists(Path.Combine(Clone, ".git", "FETCH_HEAD")).ShouldBeFalse();
    }

    /// <summary>
    /// The distance a local branch is from its upstream, which is what a source following
    /// a branch nobody pulls needs to show: dexhistory sat 52 behind origin/main for three
    /// days with every count correct.
    /// </summary>
    [Fact]
    public async Task ALocalBranchReportsHowFarBehindItsUpstreamItIs()
    {
        PushedElsewhere(3);
        Git(Clone, "fetch", "--quiet");

        var behind = (await RefsAsync()).Local.Refs.Single(r => r.Name == "refs/heads/main").Upstream.ShouldNotBeNull();
        behind.Name.ShouldBe("refs/remotes/origin/main");
        behind.ShortName.ShouldBe("origin/main");
        (behind.Ahead, behind.Behind, behind.Gone).ShouldBe((0, 3, false));

        Commit(Clone, "b.txt", "mine\n", "local work");
        var both = (await RefsAsync()).Local.Refs.Single(r => r.Name == "refs/heads/main").Upstream.ShouldNotBeNull();
        (both.Ahead, both.Behind).ShouldBe((1, 3));

        Git(Clone, "branch", "orphaned");
        Git(Clone, "config", "branch.orphaned.remote", "origin");
        Git(Clone, "config", "branch.orphaned.merge", "refs/heads/deleted-upstream");
        var gone = (await RefsAsync()).Local.Refs.Single(r => r.Name == "refs/heads/orphaned").Upstream.ShouldNotBeNull();
        (gone.Ahead, gone.Behind, gone.Gone).ShouldBe((null, null, true));

        // Made from a local branch, so git gives it no upstream.
        Git(Clone, "branch", "--quiet", "loose");
        (await RefsAsync()).Local.Refs.Single(r => r.Name == "refs/heads/loose").Upstream.ShouldBeNull();
    }

    /// <summary>
    /// Only git's own C-locale words become counts. A translated or future wording must
    /// come back as no count rather than a number read out of it.
    /// </summary>
    [Fact]
    public void AnUnrecognisedTrackingPhraseIsNoCount()
    {
        GitHistory.ParseTrack("").ShouldBe((0, 0, false));
        GitHistory.ParseTrack("behind 3").ShouldBe((0, 3, false));
        GitHistory.ParseTrack("ahead 2").ShouldBe((2, 0, false));
        GitHistory.ParseTrack("ahead 1, behind 12").ShouldBe((1, 12, false));
        GitHistory.ParseTrack("gone").ShouldBe((null, null, true));

        GitHistory.ParseTrack("vor 3").ShouldBe((null, null, false));
        GitHistory.ParseTrack("hinter 3, vor 1").ShouldBe((null, null, false));
        GitHistory.ParseTrack("behind three").ShouldBe((null, null, false));
        GitHistory.ParseTrack("behind -3").ShouldBe((null, null, false));
    }

    /// <summary>
    /// When a fetch last ran is FETCH_HEAD's time, which every fetch rewrites, including one
    /// that brought nothing, and which a fetch from a linked worktree writes in that
    /// worktree's own directory. Measured on 2.31 and 2.54.
    /// </summary>
    [Fact]
    public async Task AFetchIsDatedByFetchHead()
    {
        (await RefsAsync()).LastFetchUtc.ShouldBeNull("a clone writes no FETCH_HEAD");

        Git(Clone, "fetch", "--quiet");
        var fetched = (await RefsAsync()).LastFetchUtc.ShouldNotBeNull();
        fetched.ShouldBe(File.GetLastWriteTimeUtc(Path.Combine(Clone, ".git", "FETCH_HEAD")));
        fetched.Kind.ShouldBe(DateTimeKind.Utc);

        // A linked worktree's fetch counts for the repository: it moves the refs every
        // worktree shares, and writes only its own FETCH_HEAD.
        Git(Clone, "worktree", "add", "--quiet", Path.Combine(_root, "linked"));
        var main = Path.Combine(Clone, ".git", "FETCH_HEAD");
        File.SetLastWriteTimeUtc(main, fetched.AddHours(-1));

        Git(Path.Combine(_root, "linked"), "fetch", "--quiet");
        var own = Path.Combine(Clone, ".git", "worktrees", "linked", "FETCH_HEAD");
        File.Exists(own).ShouldBeTrue();

        (await RefsAsync()).LastFetchUtc.ShouldBe(File.GetLastWriteTimeUtc(own));
    }

    /// <summary>
    /// A git directory outside the workspace is not read because git named it. A
    /// separate git directory, like a <c>.git</c> file, can put FETCH_HEAD anywhere.
    /// </summary>
    [Fact]
    public async Task FetchHeadOutsideTheWorkspaceIsNotRead()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"gitrefs-outside-{Guid.NewGuid():N}");
        _elsewhere.Add(outside);
        Directory.CreateDirectory(outside);
        Git(_root, "clone", "--quiet", $"--separate-git-dir={Path.Combine(outside, "sep.git")}", "remote.git", "split");
        Git(Path.Combine(_root, "split"), "fetch", "--quiet");
        File.Exists(Path.Combine(outside, "sep.git", "FETCH_HEAD")).ShouldBeTrue("the fetch wrote it, outside");

        (await RefsAsync("split")).LastFetchUtc.ShouldBeNull();

        // The control: the same fetch in a repository whose git directory is inside.
        Git(Clone, "fetch", "--quiet");
        (await RefsAsync()).LastFetchUtc.ShouldNotBeNull();
    }

    /// <summary>
    /// A branch git allows and the ref rule does not is listed, so it is visible, and says
    /// why it cannot be followed, in the words a typed ref is refused with.
    /// </summary>
    [Fact]
    public async Task ARefTheRuleRefusesIsListedAsUnusableWithTheReason()
    {
        Git(Clone, "branch", "feat+x");
        Git(Clone, "branch", "café");

        var local = (await RefsAsync()).Local.Refs;

        local.Single(r => r.Name == "refs/heads/feat+x").Refusal.ShouldNotBeNull().ShouldContain("not a usable ref");
        local.Single(r => r.Name == "refs/heads/café").Refusal.ShouldNotBeNull().ShouldContain("not a usable ref");
        local.Single(r => r.Name == "refs/heads/main").Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task DetachedAndUnbornHeadsAreReportedAsSuch()
    {
        Git(Clone, "checkout", "--quiet", "--detach", "HEAD");
        var detached = (await RefsAsync()).Head;
        detached.Branch.ShouldBeNull();
        detached.Sha.ShouldBe(Git(Clone, "rev-parse", "HEAD").Trim());

        Git(_root, "init", "--quiet", "empty");
        var unborn = await RefsAsync("empty");
        unborn.Head.ShouldBe(new GitHead("refs/heads/main", null));
        unborn.Local.Refs.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheListingIsCappedPerGroupAndSaysSo()
    {
        var sha = Git(Clone, "rev-parse", "HEAD").Trim();
        var info = new ProcessStartInfo("git", ["update-ref", "--stdin"])
        {
            WorkingDirectory = Clone,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        using (var p = Process.Start(info)!)
        {
            // '\n' rather than WriteLine, which ends a line with "\r\n" on Windows and
            // leaves update-ref reading a sha with a carriage return on it.
            for (var i = 0; i < GitHistory.RefsPerGroup + 5; i++)
                p.StandardInput.Write($"create refs/heads/many/{i:D4} {sha}\n");
            p.StandardInput.Close();
            p.WaitForExit();
            p.ExitCode.ShouldBe(0);
        }

        var local = (await RefsAsync()).Local;
        local.Refs.Count.ShouldBe(GitHistory.RefsPerGroup);
        local.Truncated.ShouldBeTrue();

        (await RefsAsync()).RemoteTracking.Truncated.ShouldBeFalse();
    }

    /// <summary>
    /// An alias cannot stand in for the listing's subcommands, because every one is a
    /// builtin and git ignores an alias that shadows one. The non-builtin alias is the
    /// control: it runs, so the repository's aliases are live and the first part tests
    /// something.
    /// </summary>
    [Fact]
    public async Task AnAliasCannotStandInForTheListingsSubcommands()
    {
        Git(Clone, "config", "alias.for-each-ref", "!echo PWNED");
        Git(Clone, "config", "alias.symbolic-ref", "!echo PWNED");
        Git(Clone, "config", "alias.pwned", "!echo PWNED");

        Git(Clone, "pwned").ShouldContain("PWNED");

        var refs = await RefsAsync();
        refs.Head.Branch.ShouldBe("refs/heads/main");
        refs.Local.Refs.ShouldContain(r => r.Name == "refs/heads/main");
    }

    [Fact]
    public async Task TrackingFollowsHeadToItsBranchAndItsUpstream()
    {
        PushedElsewhere(2);
        Git(Clone, "fetch", "--quiet");
        var at = DateTime.UtcNow;

        var tracking = await GitHistory.TrackingAsync(Repo(), "HEAD", at, default);

        tracking.Ref.ShouldBe("HEAD");
        tracking.Branch.ShouldBe("refs/heads/main");
        tracking.Upstream.ShouldNotBeNull().Behind.ShouldBe(2);
        tracking.LastFetchUtc.ShouldNotBeNull();
        tracking.ObservedUtc.ShouldBe(at);

        (await GitHistory.TrackingAsync(Repo(), "refs/heads/main", at, default)).Upstream.ShouldNotBeNull().Behind.ShouldBe(2);
        (await GitHistory.TrackingAsync(Repo(), "main", at, default)).Branch.ShouldBe("refs/heads/main");
    }

    [Fact]
    public async Task ARemoteTrackingRefHasAFetchTimeAndNoBranch()
    {
        Git(Clone, "fetch", "--quiet");

        foreach (var @ref in new[] { "refs/remotes/origin/main", "origin/main" })
        {
            var tracking = await GitHistory.TrackingAsync(Repo(), @ref, DateTime.UtcNow, default);
            tracking.Branch.ShouldBeNull(@ref);
            tracking.Upstream.ShouldBeNull(@ref);
            tracking.LastFetchUtc.ShouldNotBeNull(@ref);
        }
    }

    /// <summary>
    /// git resolves a short name to a tag before a branch, so a tag named <c>main</c> is
    /// what <c>main</c> walks. Reporting the branch's upstream for it would describe a
    /// history the source does not follow.
    /// </summary>
    [Fact]
    public async Task ATagNamedLikeABranchIsNotReportedAsTheBranch()
    {
        Git(Clone, "tag", "main");

        var tracking = await GitHistory.TrackingAsync(Repo(), "main", DateTime.UtcNow, default);

        tracking.Branch.ShouldBeNull();
        tracking.Upstream.ShouldBeNull();
    }

    [Fact]
    public void TrackingTimesRoundTripAsUtc()
    {
        var at = new DateTime(2026, 9, 25, 12, 30, 45, DateTimeKind.Utc);
        var tracking = new GitTracking("HEAD", "refs/heads/main",
            new GitUpstream("refs/remotes/origin/main", "origin/main", 0, 52, false), at.AddHours(-3), at);

        var back = GitTracking.FromJson(tracking.ToJson()).ShouldNotBeNull();

        back.ShouldBe(tracking);
        back.ObservedUtc.Kind.ShouldBe(DateTimeKind.Utc);
        back.LastFetchUtc.ShouldNotBeNull().Kind.ShouldBe(DateTimeKind.Utc);

        GitTracking.FromJson("{ not json").ShouldBeNull();
        GitTracking.FromJson(null).ShouldBeNull();
    }

    /// <summary>The picker's endpoint, as a source added at the same path would resolve it.</summary>
    [Fact]
    public async Task TheEndpointAnswersForAFolderAsASourceWouldResolveIt()
    {
        var listed = await SystemEndpoints.RepositoryRefsAsync(_root, "repo", default);
        var ok = listed.ShouldBeOfType<Ok<GitRefsResponse>>().Value.ShouldNotBeNull();
        ok.IsRepository.ShouldBeTrue();
        ok.Path.ShouldBe("repo");
        ok.Listing.ShouldNotBeNull().Head.Branch.ShouldBe("refs/heads/main");

        Directory.CreateDirectory(Path.Combine(Clone, "src"));
        var sub = (await SystemEndpoints.RepositoryRefsAsync(_root, "repo/src", default))
            .ShouldBeOfType<Ok<GitRefsResponse>>().Value.ShouldNotBeNull();
        sub.IsRepository.ShouldBeFalse("a source there would walk the whole repository");
        sub.Listing.ShouldBeNull();

        (await SystemEndpoints.RepositoryRefsAsync(_root, "missing", default))
            .ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(404);
        (await SystemEndpoints.RepositoryRefsAsync(_root, "../elsewhere", default))
            .ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(400);
    }

    public void Dispose()
    {
        foreach (var dir in _elsewhere.Append(_root))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { /* a temp directory left behind is not a test failure */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
