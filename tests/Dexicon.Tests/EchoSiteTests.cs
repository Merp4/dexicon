using System.Text.RegularExpressions;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Dexicon.Tests;

/// <summary>
/// Each place in the MCP surface that puts a caller's value, or a stored name that older catalogues allow to
/// hold anything, into an error, driven with a value holding a line break, an escape, a C1 control and 5,000
/// characters. The sites a test cannot reach with such a value (a refusal from the service that quotes
/// nothing) are held by <see cref="EverySiteThatThrowsARefusalShowsItThroughTheHolders"/>, which reads the source.
/// </summary>
public sealed class EchoSiteTests : IAsyncLifetime
{
    /// <summary>The corpus name stored in the catalogue, which has no colon because a colon would split it.</summary>
    private static readonly string StoredHostile =
        "bad\n[10-00-00Z INF] forged" + (char)0x1B + "[2J" + (char)0x9B + "x" + new string('z', 5_000);

    private static readonly string Hostile =
        "bad\n[10:00:00Z INF] forged" + (char)0x1B + "[2J" + (char)0x9B + "x" + new string('z', 5_000);

    private IndexingHarness _harness = null!;

    public static TheoryData<string> Sites => new()
    {
        "configure_corpus: a refusal from the service names a stored corpus",
        "configure_corpus: the failure after the corpus is saved",
        "search_index: the resolver refuses the corpus",
        "get_context: the document has no such line",
        "get_context: the file has no content around the line",
        "file resource: the file is not indexed",
        "propose_removal: the service refuses the target",
    };

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
        await _harness.WriteFileAsync("manual.html", "<html><body><p>one</p><p>two</p></body></html>");
        await _harness.WriteFileAsync("plain.md", "one\n\ntwo\n\nthree");
        await _harness.RunIndexAsync();
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static RequestContext Holding(params string[] scopes) => new()
    {
        Principal = new Principal("k", "agent", scopes.ToHashSet(StringComparer.Ordinal)),
    };

    private async Task RenameTheCorpusAsync(string name)
    {
        await using var db = _harness.NewContext();
        await db.Corpora.ExecuteUpdateAsync(u => u.SetProperty(c => c.Name, name));
    }

    private Task<McpException> ThrownAtAsync(string site) => site switch
    {
        "configure_corpus: a refusal from the service names a stored corpus" => CreateRefusalAsync(),
        "configure_corpus: the failure after the corpus is saved" => PostSaveAsync(),
        "search_index: the resolver refuses the corpus" => SearchAsync(),
        "get_context: the document has no such line" => ContextAsync("manual.html"),
        "get_context: the file has no content around the line" => ContextAsync("plain.md"),
        "file resource: the file is not indexed" => ResourceAsync(),
        "propose_removal: the service refuses the target" => ProposeAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, "no driver"),
    };

    private async Task<McpException> CreateRefusalAsync()
    {
        // A name equal to another corpus's id is refused, and the refusal quotes that corpus's stored name.
        await RenameTheCorpusAsync(StoredHostile);
        await using var db = _harness.NewContext();
        return await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            Holding(Scopes.Configure), new ScopeResolver(db), db, _harness.NewConfiguration(db),
            new RecordingLoggerFactory(), IndexingHarness.CorpusId, create: true));
    }

    private async Task<McpException> PostSaveAsync()
    {
        await using var db = _harness.NewContext();
        // Runs after the corpus is saved. The name is changed on the tracked entity the tool holds, which
        // is how an older catalogue's name looks to it.
        _harness.Vectors.OnEnsureCollection = () =>
        {
            db.Corpora.Local.Single(c => c.Name == "papers").Name = StoredHostile;
            throw new InvalidOperationException("the vector store is not answering");
        };
        return await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            Holding(Scopes.Configure), new ScopeResolver(db), db, _harness.NewConfiguration(db),
            new RecordingLoggerFactory(), "papers", create: true));
    }

    private async Task<McpException> SearchAsync()
    {
        await using var db = _harness.NewContext();
        var search = new SearchService(new ScopeResolver(db), _harness.Vectors, _harness.Embedder,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SearchService>.Instance);
        return await Should.ThrowAsync<McpException>(() => DexiconTools.SearchIndexAsync(
            Holding(Scopes.Search), search, "anything", corpus: [Hostile]));
    }

    private async Task<McpException> ContextAsync(string path)
    {
        await RenameTheCorpusAsync(StoredHostile);
        await using var db = _harness.NewContext();
        return await Should.ThrowAsync<McpException>(() => DexiconTools.GetContextAsync(
            Holding(Scopes.Search), new ScopeResolver(db), _harness.Vectors, new DocumentReader(db),
            StoredHostile, path, aroundLine: 100_000, before: 0, after: 0));
    }

    private async Task<McpException> ResourceAsync()
    {
        await RenameTheCorpusAsync(StoredHostile);
        await using var db = _harness.NewContext();
        return await Should.ThrowAsync<McpException>(() => DexiconResources.FileAsync(
            Holding(Scopes.Search), new ScopeResolver(db), _harness.Vectors, new DocumentReader(db),
            StoredHostile, "missing.md"));
    }

    private async Task<McpException> ProposeAsync()
    {
        await using var db = _harness.NewContext();
        return await Should.ThrowAsync<McpException>(() => ProposeTools.ProposeRemovalAsync(
            Holding(Scopes.Propose), new ScopeResolver(db), _harness.NewProposals(db), "chunk_set", "notes",
            "no longer needed", target: Hostile));
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task EachSiteShowsAHostileValueOnOneLineAndCut(string site)
    {
        var thrown = await ThrownAtAsync(site);

        thrown.Message.Any(c => char.IsControl(c) || c is (char)0x2028 or (char)0x2029)
            .ShouldBeFalse("a line break or an escape sequence would begin a line of its own");
        thrown.Message.ShouldContain("bad", Case.Sensitive, "the start of the value still says what was refused");
        thrown.Message.Length.ShouldBeLessThan(DexiconTools.MessageMax + 100, "the 5,000 characters sent are not all repeated");
        thrown.Message.ShouldNotContain(new string('z', 4_500));
    }

    [Fact]
    public void EverySiteThatThrowsARefusalShowsItThroughTheHolders()
    {
        var files = Directory.GetFiles(Path.GetDirectoryName(SourceFiles.Find("src", "Dexicon", "Mcp", "ConfigureTools.cs"))!, "*.cs");
        files.Length.ShouldBeGreaterThanOrEqualTo(5, "the scan has to have found the tool files");
        var all = files.ToDictionary(f => Path.GetFileName(f)!, File.ReadAllText);

        // A refusal or a resolver message reaches the caller only through Refused or DexiconTools.Refusal.
        foreach (var (file, text) in all)
        {
            // The two exception types whose messages the repository wrote whole (an unknown search mode and a
            // dimension mismatch) are passed on as they are, and are not matched here.
            Regex.IsMatch(text, @"new McpException\(\s*(refused|update|added|created|updated)\.(Detail|Message)")
                .ShouldBeFalse($"{file} passes a refusal to the caller as it is");

            foreach (var line in text.Split('\n').Where(l => l.Contains(".Refusal is {", StringComparison.Ordinal)))
                line.ShouldMatch(@"throw (Refused|DexiconTools\.Refusal)\(", $"{file}: {line.Trim()}");

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("catch (ScopeResolutionException", StringComparison.Ordinal)) continue;

                string.Join("\n", lines.Skip(i).Take(8)).ShouldMatch(
                    @"throw (DexiconTools\.)?Refusal\(", $"{file}:{i + 1}: {lines[i].Trim()}");
            }
        }

        // The folder text in the source messages is built once, through Echo.
        all["ConfigureTools.cs"].ShouldContain("DexiconTools.Echo(root))}\";");
        all["ConfigureTools.cs"].ShouldContain("var what = ");

        // The sites that call the holders: four refusals and one resolver message in ConfigureTools, four
        // resolver messages in DexiconTools, one in the file resource, and one refusal in ProposeTools.
        Regex.Count(all["ConfigureTools.cs"], @"throw Refused\(refused, logs, principal\)").ShouldBe(4);
        Regex.Count(all["DexiconTools.cs"], @"throw Refusal\(ex\.Message\)").ShouldBe(4);
        all["DexiconResources.cs"].ShouldContain("throw DexiconTools.Refusal(ex.Message)");
        all["ProposeTools.cs"].ShouldContain("throw DexiconTools.Refusal(refused.Detail)");
        all["ConfigureTools.cs"].ShouldContain("throw DexiconTools.Refusal($\"{ex.Message} {hint}\".TrimEnd())");
    }
}
