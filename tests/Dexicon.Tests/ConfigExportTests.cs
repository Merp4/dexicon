using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The configuration as a file (D-53). The expectations are written from the rows that were seeded, not from what the
/// exporter returns, so a field the exporter dropped or renamed shows as a difference.
/// </summary>
public sealed class ConfigExportTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private IndexingHarness _harness = null!;
    private string _agentSecret = string.Empty;
    private byte[] _agentHash = [];
    private byte[] _agentSalt = [];

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "history");
        await _harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await using var db = _harness.NewContext();

        // The harness's ten placeholder keys are not the subject here.
        await db.Tokens.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        var papers = new Corpus
        {
            Id = "corpus-2",
            Name = "Papers",
            Description = "Conference papers",
            State = CorpusState.Ready,
            CreatedUtc = Now,
            DefaultIncludeGlobs = "[\"**/*.md\"]",
            DefaultExcludeGlobs = "[]",
            DefaultUseGitignore = false,
            DefaultMaxFileBytes = 1000,
        };
        papers.Sources.Add(new Source { Id = "source-up", CorpusId = "corpus-2", Kind = SourceKind.Upload, CreatedUtc = Now });
        papers.Sources.Add(new Source
        {
            Id = "source-git",
            CorpusId = "corpus-2",
            Kind = SourceKind.GitHistory,
            RootPath = "repo",
            ExcludeGlobs = "[]",
            GitOptions = new GitHistoryOptions { Ref = "main", IncludeDiff = true, MaxCommits = 100 }.ToJson(),
            CreatedUtc = Now,
        });
        papers.ChunkSets.Add(new ChunkSet
        {
            Id = "set-p",
            CorpusId = "corpus-2",
            Name = "default",
            Description = "the first",
            EmbeddingProvider = "ollama",
            EmbeddingModel = "embeddinggemma",
            EmbeddingDimensions = 768,
            CollectionName = "dexicon__embeddinggemma__768",
            ChunkSize = 512,
            ChunkOverlap = 64,
            BoundaryMode = "custom",
            CustomBoundaryPattern = "^## ",
            UnitAware = true,
            SentenceAware = true,
            HeadingContext = true,
            IsDefault = true,
            State = CorpusState.Ready,
            CreatedUtc = Now,
        });
        db.Corpora.Add(papers);

        db.ModelProfiles.Add(new EmbeddingModelProfile
        {
            Provider = "ollama",
            Model = "embeddinggemma",
            DocumentTemplate = "title: none | text: {text}",
            QueryTemplate = "task: search result | query: {text}",
            Notes = "from the model card",
            CreatedUtc = Now,
            UpdatedUtc = Now,
        });

        var tokens = new TokenService(db, TimeProvider.System);
        var (agent, issued) = tokens.Build("agent", [Scopes.Search, Scopes.Ingest], expiresUtc: null, corpusIds: [IndexingHarness.CorpusId]);
        _agentSecret = issued.Secret;
        _agentHash = agent.TokenHash;
        _agentSalt = agent.TokenSalt;
        db.Tokens.Add(agent);

        var (everything, _) = tokens.Build("everything", [Scopes.Search], expiresUtc: Now.AddDays(30));
        db.Tokens.Add(everything);

        var (expired, _) = tokens.Build("expired", [Scopes.Search], expiresUtc: Now.AddDays(-1), corpusIds: []);
        db.Tokens.Add(expired);

        var (revoked, _) = tokens.Build("revoked", [Scopes.Search], expiresUtc: null);
        revoked.RevokedUtc = Now;
        db.Tokens.Add(revoked);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private async Task<ConfigExport> ExportAsync()
    {
        await using var db = _harness.NewContext();
        return await ConfigEndpoints.BuildAsync(db, Now, "9.9.9-test", TestContext.Current.CancellationToken);
    }

    private static string Text(ConfigExport export) => JsonSerializer.Serialize(export, ConfigEndpoints.FileJson);

    /// <summary>Records that hold lists are not equal to each other by value, so two of them are compared as the file would write them.</summary>
    private static string Written<T>(T value) => JsonSerializer.Serialize(value, ConfigEndpoints.FileJson);

    [Fact]
    public async Task TheFileSaysWhatItIsAndWhenItWasMade()
    {
        var export = await ExportAsync();

        export.Format.ShouldBe("dexicon-config");
        export.Version.ShouldBe(1);
        export.ExportedUtc.ShouldBe(Now);
        export.DexiconVersion.ShouldBe("9.9.9-test");
    }

    [Fact]
    public async Task CorporaAreInNameOrderIgnoringCase()
    {
        (await ExportAsync()).Corpora.Select(c => c.Name).ShouldBe(["notes", "Papers"]);
    }

    [Fact]
    public async Task ACorpusKeepsItsDescriptionItsFiltersAndItsChunkSets()
    {
        var papers = (await ExportAsync()).Corpora.Single(c => c.Name == "Papers");

        papers.Description.ShouldBe("Conference papers");
        Written(papers.Defaults).ShouldBe(Written(new ExportedFilters(["**/*.md"], [], UseGitignore: false, MaxFileBytes: 1000)));
        Written(papers.ChunkSets.ShouldHaveSingleItem()).ShouldBe(Written(new ExportedChunkSet(
            "default", "the first", "ollama", "embeddinggemma", 512, 64, "custom", "^## ",
            UnitAware: true, SentenceAware: true, HeadingContext: true, IsDefault: true)));
    }

    [Fact]
    public async Task ACorpusThatSetsNoFiltersExportsNoneAndSoInheritsThem()
    {
        var notes = (await ExportAsync()).Corpora.Single(c => c.Name == "notes");

        Written(notes.Defaults).ShouldBe(Written(new ExportedFilters(null, null, null, null)));
        notes.ChunkSets.Select(s => s.Name).ShouldBe(["alt-1", "default"], "by name, so alt-1 is before default");
        notes.Sources.Select(s => s.RootPath).ShouldBe(["history", "notes"], "by root, ignoring case");
    }

    [Fact]
    public async Task AnEmptyListOfGlobsIsKeptAndIsNotTheSameAsNone()
    {
        var git = (await ExportAsync()).Corpora.Single(c => c.Name == "Papers").Sources.ShouldHaveSingleItem();

        git.Filters.ExcludeGlobs.ShouldNotBeNull().ShouldBeEmpty();
        git.Filters.IncludeGlobs.ShouldBeNull();
        Text(await ExportAsync()).ShouldContain("\"excludeGlobs\": []");
    }

    [Fact]
    public async Task AGitHistorySourceKeepsItsOptionsAndAnUploadSourceIsLeftOutWithAnExplanation()
    {
        var export = await ExportAsync();
        var git = export.Corpora.Single(c => c.Name == "Papers").Sources.ShouldHaveSingleItem();

        git.Kind.ShouldBe("githistory");
        git.RootPath.ShouldBe("repo");
        git.Git.ShouldNotBeNull().Ref.ShouldBe("main");
        git.Git.IncludeDiff.ShouldBeTrue();
        git.Git.MaxCommits.ShouldBe(100);
        export.Omitted.ShouldHaveSingleItem().ShouldContain("'Papers' holds uploaded documents");
    }

    [Fact]
    public async Task ModelProfilesKeepTheirTemplatesAndNotes()
    {
        (await ExportAsync()).ModelProfiles.ShouldHaveSingleItem().ShouldBe(new ExportedModelProfile(
            "ollama", "embeddinggemma", "title: none | text: {text}", "task: search result | query: {text}", "from the model card"));
    }

    [Fact]
    public async Task KeysCarryNameScopesReachAndExpiryAndARevokedKeyIsLeftOut()
    {
        var keys = (await ExportAsync()).Keys;

        keys.Select(k => k.Name).ShouldBe(["agent", "everything", "expired"]);
        Written(keys[0]).ShouldBe(Written(new ExportedKey("agent", [Scopes.Ingest, Scopes.Search], AllCorpora: false, ["notes"], ExpiresUtc: null)));
        Written(keys[1]).ShouldBe(Written(new ExportedKey("everything", [Scopes.Search], AllCorpora: true, [], Now.AddDays(30))));
        keys[2].AllCorpora.ShouldBeFalse();
        keys[2].Corpora.ShouldBeEmpty();
        keys[2].ExpiresUtc.ShouldBe(Now.AddDays(-1), "an expired key is configuration too");
    }

    [Fact]
    public async Task NoSecretNoHashNoIdAndNoStateIsInTheFile()
    {
        var text = Text(await ExportAsync());

        text.ShouldNotContain(_agentSecret);
        text.ShouldNotContain(Convert.ToBase64String(_agentHash));
        text.ShouldNotContain(Convert.ToBase64String(_agentSalt));
        text.ShouldNotContain("dex_");
        foreach (var internalValue in new[] { "corpus-1", "corpus-2", "source-1", "source-git", "set-1", "set-p", "dexicon__embeddinggemma__768" })
            text.ShouldNotContain(internalValue);

        var properties = JsonDocument.Parse(text).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        properties.ShouldBe(["format", "version", "exportedUtc", "dexiconVersion", "corpora", "modelProfiles", "keys", "omitted"]);
        text.ToLowerInvariant().ShouldNotContain("\"id\"");
        text.ShouldNotContain("\"state\"");
        text.ShouldNotContain("createdUtc");
        text.ShouldNotContain("tokenHash");
    }

    [Fact]
    public async Task TwoExportsOfOneStateAreTheSameText()
    {
        Text(await ExportAsync()).ShouldBe(Text(await ExportAsync()));
    }

    [Fact]
    public async Task TheFileReadsBackAsTheSameFile()
    {
        var text = Text(await ExportAsync());

        var read = JsonSerializer.Deserialize<ConfigExport>(text, ConfigEndpoints.FileJson).ShouldNotBeNull();

        Text(read).ShouldBe(text);
        read.Corpora.Count.ShouldBe(2);
    }

    [Fact]
    public async Task APropertyThatIsNullIsNotWritten()
    {
        var text = Text(await ExportAsync());

        text.ShouldNotContain(": null");
    }

    [Fact]
    public async Task AKeyWithEveryScopeButAdminIsRefused()
    {
        var rc = new RequestContext
        {
            Principal = new Principal("k", "agent", new HashSet<string>(StringComparer.Ordinal)
            {
                Scopes.Search, Scopes.Ingest, Scopes.Configure, Scopes.Propose, Scopes.Destroy,
            }),
        };
        await using var db = _harness.NewContext();

        var result = await ConfigEndpoints.ExportAsync(new DefaultHttpContext(), rc, db, TimeProvider.System, TestContext.Current.CancellationToken);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(403);
    }
}

/// <summary>The route through the running application: admin only, written as a download, and valid JSON.</summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class ConfigExportHostTests : IDisposable
{
    private const string Password = "a-test-password-for-this-run";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"dexicon-configexport-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = [];
    private readonly TextWriter _console = Console.Out;

    private void Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private WebApplicationFactory<Program> Start()
    {
        Directory.CreateDirectory(_data);
        Set("DEXICON__STORAGE__DATAPATH", _data);
        Set("DEXICON__QDRANT__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__ENDPOINT", "http://127.0.0.1:1");
        Set("DEXICON__OLLAMA__MAXRETRIES", "0");
        Set("DEXICON__INDEXING__REFRESHMINUTES", "0");
        Set("DEXICON__ADMIN__PASSWORD", Password);
        return new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        Console.SetOut(_console);
        Serilog.Log.CloseAndFlush();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={new StorageOptions { DataPath = _data }.CatalogPath}"));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    [Fact]
    public async Task AnAnonymousCallerIsRefusedAndTheAdministratorGetsTheFileAsADownload()
    {
        await using var factory = Start();
        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/config/export", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/session", new { password = Password }, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());

        var response = await client.GetAsync("/api/config/export", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentDisposition.ShouldNotBeNull().FileName.ShouldNotBeNull().ShouldMatch(@"^""?dexicon-config-\d{4}-\d{2}-\d{2}\.json""?$");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var root = JsonDocument.Parse(text).RootElement;
        root.GetProperty("format").GetString().ShouldBe("dexicon-config");
        root.GetProperty("version").GetInt32().ShouldBe(1);
        text.ShouldContain("\n  ", customMessage: "written indented, for a diff");
    }
}
