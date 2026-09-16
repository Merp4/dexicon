using Dexicon.Infrastructure;

namespace Dexicon.Tests;

/// <summary>
/// Which paths are served without a token.
///
/// The distinction is easy to get wrong in the direction that matters. The container
/// probes must answer before anyone has a token, so <c>/healthz/live</c> and
/// <c>/healthz/ready</c> are open — but bare <c>/healthz</c> reports endpoints, model
/// names and job state, so it authenticates like everything else. Prefix-matching the
/// "/healthz" family would open the detailed one; prefix-matching nothing would make the
/// probes unreachable and the container permanently unhealthy.
///
/// The OpenAPI document asks this same question when it decides which operations carry a
/// security requirement, so a change here changes both — which is the point of there
/// being one list.
/// </summary>
public sealed class AnonymousPathTests
{
    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/favicon.ico")]
    [InlineData("/robots.txt")]
    [InlineData("/assets/index-abc123.js")]
    [InlineData("/assets/nested/thing.css")]
    public void ServedWithoutAToken(string path) =>
        Assert.True(DexiconAuthMiddleware.IsAnonymous(path), $"{path} should be anonymous");

    [Theory]
    // The detailed health report. Open probes, closed report.
    [InlineData("/healthz")]
    [InlineData("/api/corpora")]
    [InlineData("/api/search")]
    [InlineData("/api/tokens")]
    [InlineData("/api/workspaces")]
    [InlineData("/mcp")]
    // Not a prefix match: nothing may open the family by looking like a member of it.
    [InlineData("/healthz/live/../../api/corpora")]
    [InlineData("/assets")]
    public void RequiresAToken(string path) =>
        Assert.False(DexiconAuthMiddleware.IsAnonymous(path), $"{path} should require a token");

    [Theory]
    [InlineData("/HEALTHZ/LIVE")]
    [InlineData("/Index.html")]
    public void MatchesRegardlessOfCase(string path) =>
        Assert.True(DexiconAuthMiddleware.IsAnonymous(path), $"{path} should be anonymous");
}
