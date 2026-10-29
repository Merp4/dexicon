using System.Text.RegularExpressions;

namespace Dexicon.Tests;

/// <summary>
/// The release workflow stamps an untagged build of main as <c>0.0.0-edge.&lt;seven characters of the commit hash&gt;</c>. A prerelease
/// identifier of digits only must not start with a zero (SemVer 2.0), and NuGet rejects such a version while restoring, without logging
/// an error. The verification build of the 0.6.12 changelog commit (hash 0504641) failed that way, so the version has to be valid for every
/// hash, including seven digits that start with 0.
/// </summary>
public sealed class ReleaseWorkflowVersionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string EdgeVersionTemplate()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));
        var match = Regex.Match(workflow, @"echo ""value=(?<version>0\.0\.0-edge\.[^""]*)"" >> ""\$GITHUB_OUTPUT""");
        match.Success.ShouldBeTrue("release.yml no longer stamps an untagged build as 0.0.0-edge.<hash>; change this test with it");
        return match.Groups["version"].Value;
    }

    // SemVer 2.0: a numeric identifier has no leading zero; an identifier with a letter or a hyphen may.
    private static bool IsValidSemVer(string version) =>
        Regex.IsMatch(version, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$");

    [Theory]
    [InlineData("0504641")]
    [InlineData("0000000")]
    [InlineData("0123456")]
    [InlineData("1504641")]
    [InlineData("ae160f7")]
    [InlineData("00000a0")]
    public void TheEdgeVersionOfAnyCommitHashIsValidSemVer(string shortHash)
    {
        var version = EdgeVersionTemplate().Replace("${GITHUB_SHA::7}", shortHash, StringComparison.Ordinal);

        IsValidSemVer(version).ShouldBeTrue($"{version} is not a valid SemVer 2.0 version");
    }

    [Fact]
    public void TheValidityCheckRejectsADigitOnlyIdentifierWithALeadingZero()
    {
        IsValidSemVer("0.0.0-edge.0504641").ShouldBeFalse();
        IsValidSemVer("0.0.0-edge.g0504641").ShouldBeTrue();
        IsValidSemVer("0.0.0-edge.1504641").ShouldBeTrue();
        IsValidSemVer("0.6.12").ShouldBeTrue();
    }
}
