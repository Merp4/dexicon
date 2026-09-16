using Dexicon.Core.Indexing;
using Dexicon.Core.Search;

namespace Dexicon.Tests;

public class SparseEncoderTests
{
    [Fact]
    public void Hash_IsStableAcrossProcesses()
    {
        // THE critical property. string.GetHashCode() is randomised per process in
        // .NET, so using it would make an index built in one run unqueryable after a
        // restart — silently, with every keyword search simply returning nothing.
        // These are the FNV-1a values; if this test fails, every existing index is
        // invalidated and the change needs a migration, not a fix to the test.
        SparseEncoder.Hash("token").ShouldBe(SparseEncoder.Hash("token"));
        SparseEncoder.Hash("token").ShouldNotBe(SparseEncoder.Hash("tokens"));

        // Pinned literals: a refactor that changes the hash function must be deliberate.
        SparseEncoder.Hash("a").ShouldBe(0xe40c292cu & 0x7FFFFFFF);
        SparseEncoder.Hash("refresh").ShouldBe(SparseEncoder.Hash("refresh"));
    }

    [Fact]
    public void Encode_SplitsIdentifiersSoTokenRefreshReachesTokenServiceRefreshAsync()
    {
        // The reason identifier splitting earns its place on code.
        var doc = SparseEncoder.Encode("public async Task<TokenPair> RefreshAsync(string refreshToken)");
        var query = SparseEncoder.Encode("token refresh");

        doc.IsEmpty.ShouldBeFalse();
        query.IsEmpty.ShouldBeFalse();

        var shared = doc.Indices.Intersect(query.Indices).ToList();
        shared.ShouldNotBeEmpty("a query for 'token refresh' must share terms with TokenService.RefreshAsync");
        shared.ShouldContain(SparseEncoder.Hash("token"));
        shared.ShouldContain(SparseEncoder.Hash("refresh"));
    }

    [Theory]
    [InlineData("TokenService", new[] { "tokenservice", "token", "service" })]
    [InlineData("refresh_token", new[] { "refresh", "token" })]
    [InlineData("kebab-case-name", new[] { "kebab", "case", "name" })]
    [InlineData("HTTPServer", new[] { "httpserver", "http", "server" })]
    [InlineData("base64Encode", new[] { "base", "64", "encode" })]
    public void Tokenize_ProducesWholeIdentifierAndItsParts(string input, string[] expected)
    {
        var tokens = SparseEncoder.Tokenize(input).ToList();
        foreach (var e in expected) tokens.ShouldContain(e);
    }

    [Fact]
    public void Encode_TermFrequency_CountsRepeats()
    {
        var v = SparseEncoder.Encode("alpha alpha alpha beta");
        var idx = Array.IndexOf(v.Indices, SparseEncoder.Hash("alpha"));
        idx.ShouldBeGreaterThanOrEqualTo(0);
        v.Values[idx].ShouldBe(3f, "Qdrant applies IDF; we supply raw term frequency");
    }

    [Fact]
    public void Encode_EmptyInput_IsEmptyNotNull()
    {
        SparseEncoder.Encode(null).IsEmpty.ShouldBeTrue();
        SparseEncoder.Encode("").IsEmpty.ShouldBeTrue();
        SparseEncoder.Encode("   ").IsEmpty.ShouldBeTrue();
        SparseEncoder.Encode("a").IsEmpty.ShouldBeTrue("single characters fall under the minimum term length");
    }

    [Fact]
    public void Encode_StoplistIsShort_BecauseCodeUsesCommonWordsAsIdentifiers()
    {
        // An aggressive stoplist hurts code search: `for`, `in` and `is` are frequently
        // part of an identifier that matters.
        var v = SparseEncoder.Encode("for in is");
        v.IsEmpty.ShouldBeFalse("'for' and 'in' must survive — they appear in real identifiers");
    }
}

public class GitignoreFilterTests
{
    [Theory]
    [InlineData("*.dll", "bin/Debug/App.dll", true)]
    [InlineData("*.dll", "src/App.cs", false)]
    [InlineData("node_modules/", "node_modules/react/index.js", true)]
    [InlineData("bin/", "src/bin/Debug/x.txt", true)]
    [InlineData("/root-only.txt", "root-only.txt", true)]
    [InlineData("/root-only.txt", "nested/root-only.txt", false)]
    [InlineData("**/generated/**", "a/b/generated/c.cs", true)]
    [InlineData("docs/*.md", "docs/readme.md", true)]
    [InlineData("docs/*.md", "docs/nested/readme.md", false)]
    public void IsIgnored_FollowsGitignoreSemantics(string pattern, string path, bool expected)
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns([pattern], "test");
        rules.IsIgnored(path, isDirectory: false).ShouldBe(expected);
    }

    [Fact]
    public void Negation_LaterPatternWins()
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns(["*.log", "!keep.log"], "test");
        rules.IsIgnored("debug.log", false).ShouldBeTrue();
        rules.IsIgnored("keep.log", false).ShouldBeFalse();
    }

    [Fact]
    public void CommentsAndBlankLines_AreIgnored()
    {
        var rules = new IgnoreRuleSet();
        rules.AddPatterns(["# a comment", "", "   ", "*.tmp"], "test");
        rules.Count.ShouldBe(1);
    }
}
