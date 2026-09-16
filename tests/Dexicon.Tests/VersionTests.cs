using System.Reflection;

namespace Dexicon.Tests;

/// <summary>
/// The version this build reports, which is derived from the nearest git tag rather than
/// written down anywhere.
///
/// Two ways that has already gone quietly wrong, both guarded here:
///
/// Reading the wrong attribute. MinVer pins <c>AssemblyVersion</c> to
/// <c>major.0.0.0</c> so a patch release cannot break assembly binding — so for a 0.x
/// project it is <c>0.0.0.0</c>, and the code that read it reported <c>0.0.0</c> as the
/// product version. Nothing failed; the OpenAPI document and every MCP client were simply
/// told the wrong number.
///
/// Putting a volatile version in a committed file. The OpenAPI document is generated at
/// build time and committed, because the container image builds the web client from it. A
/// version carrying the commit height would make that file differ on every single commit,
/// and CI's check that it matches the code would become noise.
/// </summary>
public sealed class VersionTests
{
    [Fact]
    public void ReportsARealVersion()
    {
        // The exact regression: `0.0.0` is what reading AssemblyVersion gives for a 0.x
        // project, and it looks plausible enough to survive review.
        ThisAssembly.Version.ShouldNotBe("0.0.0");
        ThisAssembly.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ReportsTheVersionMinVerDerived()
    {
        var informational = typeof(ThisAssembly).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        informational.ShouldStartWith(ThisAssembly.Version);
    }

    [Fact]
    public void DropsTheCommitHashADeterministicBuildAppends()
    {
        // `0.1.2-alpha.0.1+b7e8371…` is provenance, not a version. The image already
        // carries the commit as its own `sha-` tag, and a 40-character suffix in an MCP
        // handshake is noise.
        ThisAssembly.Version.ShouldNotContain("+");
    }

    [Fact]
    public void TheApiVersionIsTheMajorAndMinorOnly()
    {
        // What the OpenAPI document carries. Coarse on purpose: a patch does not change
        // the contract, and this file is committed.
        ThisAssembly.ApiVersion.Split('.').Length.ShouldBe(2);
        ThisAssembly.Version.ShouldStartWith(ThisAssembly.ApiVersion);
    }

    [Fact]
    public void TheApiVersionSurvivesAPrereleaseSuffix()
    {
        // An untagged commit versions itself `0.1.2-alpha.0.7`. Splitting that naively
        // would put `2-alpha` in the document.
        ThisAssembly.ApiVersion.ShouldNotContain("-");
        ThisAssembly.ApiVersion.ShouldNotContain("alpha");
    }
}
