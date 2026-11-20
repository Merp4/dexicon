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
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
    private string _agentId = string.Empty;
    private byte[] _agentHash = [];
    private byte[] _agentSalt = [];

    public async ValueTask InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "history");
        await _harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await using var db = _harness.NewContext();

        // The harness's ten placeholder keys are not the subject here.
        await db.Tokens.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        // Rows are added in an order that is not the order of the file, with one capitalised name in each list, so an
        // ordering that was dropped, or that ignored case in the wrong direction, changes what is written.
        db.Sources.Add(new Source
        {
            Id = "source-zebra",
            CorpusId = IndexingHarness.CorpusId,
            Kind = SourceKind.Workspace,
            RootPath = "Zebra",
            UseGitignore = false,
            MaxFileBytes = 777,
            CreatedUtc = Now,
        });
        db.Sources.Add(new Source
        {
            Id = "source-history",
            CorpusId = IndexingHarness.CorpusId,
            Kind = SourceKind.GitHistory,
            RootPath = "history",
            CreatedUtc = Now,
        });

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

        // One flag true in each set but the default, which has them all, so a flag that is swapped or fixed shows.
        papers.ChunkSets.Add(PapersSet("set-z", "Zed", unit: true));
        papers.ChunkSets.Add(PapersSet("set-g", "gamma", heading: true));
        papers.ChunkSets.Add(PapersSet("set-p", "default", unit: true, sentence: true, heading: true, isDefault: true,
            description: "the first", size: 512, overlap: 64, boundary: "custom", pattern: "^## "));
        papers.ChunkSets.Add(PapersSet("set-b", "beta", sentence: true));
        db.Corpora.Add(papers);

        var archive = new Corpus { Id = "corpus-3", Name = "Archive", State = CorpusState.Ready, CreatedUtc = Now };
        archive.Sources.Add(new Source { Id = "source-up2", CorpusId = "corpus-3", Kind = SourceKind.Upload, CreatedUtc = Now });
        db.Corpora.Add(archive);

        foreach (var model in new[] { "Zeta", "embeddinggemma", "beta" })
            db.ModelProfiles.Add(new EmbeddingModelProfile
            {
                Provider = "ollama",
                Model = model,
                DocumentTemplate = model == "embeddinggemma" ? "title: none | text: {text}" : "doc: {text}",
                QueryTemplate = model == "embeddinggemma" ? "task: search result | query: {text}" : "q: {text}",
                Notes = model == "embeddinggemma" ? "from the model card" : null,
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });

        var tokens = new TokenService(db, TimeProvider.System);

        var (everything, _) = tokens.Build("everything", [Scopes.Search], expiresUtc: Now.AddDays(30));
        db.Tokens.Add(everything);

        var (expired, _) = tokens.Build("Expired", [Scopes.Search], expiresUtc: Now.AddDays(-1), corpusIds: []);
        db.Tokens.Add(expired);

        var (agent, issued) = tokens.Build("agent", [Scopes.Search, Scopes.Ingest], expiresUtc: null,
            corpusIds: ["corpus-3", "corpus-2", IndexingHarness.CorpusId]);
        _agentSecret = issued.Secret;
        _agentId = agent.Id;
        _agentHash = agent.TokenHash;
        _agentSalt = agent.TokenSalt;
        db.Tokens.Add(agent);

        var (revoked, _) = tokens.Build("revoked", [Scopes.Search], expiresUtc: null);
        revoked.RevokedUtc = Now;
        db.Tokens.Add(revoked);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static ChunkSet PapersSet(string id, string name, bool unit = false, bool sentence = false, bool heading = false,
        bool isDefault = false, string? description = null, int size = 256, int overlap = 32, string boundary = "language-aware",
        string? pattern = null) =>
        new()
        {
            Id = id,
            CorpusId = "corpus-2",
            Name = name,
            Description = description,
            EmbeddingProvider = "ollama",
            EmbeddingModel = "embeddinggemma",
            EmbeddingDimensions = 768,
            CollectionName = "dexicon__embeddinggemma__768",
            ChunkSize = size,
            ChunkOverlap = overlap,
            BoundaryMode = boundary,
            CustomBoundaryPattern = pattern,
            UnitAware = unit,
            SentenceAware = sentence,
            HeadingContext = heading,
            IsDefault = isDefault,
            State = CorpusState.Ready,
            CreatedUtc = Now,
        };

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
        (await ExportAsync()).Corpora.Select(c => c.Name).ShouldBe(["Archive", "notes", "Papers"]);
    }

    [Fact]
    public async Task ACorpusKeepsItsDescriptionItsFiltersAndItsChunkSets()
    {
        var papers = (await ExportAsync()).Corpora.Single(c => c.Name == "Papers");

        papers.Description.ShouldBe("Conference papers");
        Written(papers.Defaults).ShouldBe(Written(new ExportedFilters(["**/*.md"], [], UseGitignore: false, MaxFileBytes: 1000)));

        // By name ignoring case: beta, default, gamma, Zed (an ordinal order would put Zed first).
        var expected = new[]
        {
            new ExportedChunkSet("beta", null, "ollama", "embeddinggemma", 256, 32, "language-aware", null,
                UnitAware: false, SentenceAware: true, HeadingContext: false, IsDefault: false),
            new ExportedChunkSet("default", "the first", "ollama", "embeddinggemma", 512, 64, "custom", "^## ",
                UnitAware: true, SentenceAware: true, HeadingContext: true, IsDefault: true),
            new ExportedChunkSet("gamma", null, "ollama", "embeddinggemma", 256, 32, "language-aware", null,
                UnitAware: false, SentenceAware: false, HeadingContext: true, IsDefault: false),
            new ExportedChunkSet("Zed", null, "ollama", "embeddinggemma", 256, 32, "language-aware", null,
                UnitAware: true, SentenceAware: false, HeadingContext: false, IsDefault: false),
        };
        Written(papers.ChunkSets).ShouldBe(Written(expected));
    }

    [Fact]
    public async Task ACorpusThatSetsNoFiltersExportsNoneAndSoInheritsThem()
    {
        var notes = (await ExportAsync()).Corpora.Single(c => c.Name == "notes");

        Written(notes.Defaults).ShouldBe(Written(new ExportedFilters(null, null, null, null)));
        notes.ChunkSets.Select(s => s.Name).ShouldBe(["alt-1", "default"], "by name, so alt-1 is before default");

        // Workspace sources first, by root ignoring case (Zebra is last, which an ordinal order would not give), then history.
        notes.Sources.Select(s => (s.Kind, s.RootPath)).ShouldBe(
            [("workspace", "history"), ("workspace", "notes"), ("workspace", "Zebra"), ("githistory", "history")]);
    }

    [Fact]
    public async Task ASourceKeepsWhatItSetsForItselfAndAWorkspaceSourceHasNoGitOptions()
    {
        var notes = (await ExportAsync()).Corpora.Single(c => c.Name == "notes");

        var zebra = notes.Sources.Single(s => s.RootPath == "Zebra");
        Written(zebra.Filters).ShouldBe(Written(new ExportedFilters(null, null, UseGitignore: false, MaxFileBytes: 777)));
        notes.Sources.Where(s => s.Kind == "workspace").ShouldAllBe(s => s.Git == null);
        notes.Sources.Single(s => s.Kind == "githistory").Git.ShouldNotBeNull();
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
        export.Omitted.Count.ShouldBe(2);
        export.Omitted[0].ShouldContain("'Archive' holds uploaded documents");
        export.Omitted[1].ShouldContain("'Papers' holds uploaded documents");
    }

    [Fact]
    public async Task ModelProfilesKeepTheirTemplatesAndNotes()
    {
        var profiles = (await ExportAsync()).ModelProfiles;

        profiles.Select(p => p.Model).ShouldBe(["beta", "embeddinggemma", "Zeta"], "by model ignoring case");
        profiles[1].ShouldBe(new ExportedModelProfile(
            "ollama", "embeddinggemma", "title: none | text: {text}", "task: search result | query: {text}", "from the model card"));
    }

    [Fact]
    public async Task KeysCarryNameScopesReachAndExpiryAndARevokedKeyIsLeftOut()
    {
        var keys = (await ExportAsync()).Keys;

        keys.Select(k => k.Name).ShouldBe(["agent", "everything", "Expired"], "by name ignoring case");
        Written(keys[0]).ShouldBe(Written(new ExportedKey(
            "agent", [Scopes.Ingest, Scopes.Search], AllCorpora: false, ["Archive", "notes", "Papers"], ExpiresUtc: null)));
        Written(keys[1]).ShouldBe(Written(new ExportedKey("everything", [Scopes.Search], AllCorpora: true, [], Now.AddDays(30))));
        keys[2].AllCorpora.ShouldBeFalse();
        keys[2].Corpora.ShouldBeEmpty();
        keys[2].ExpiresUtc.ShouldBe(Now.AddDays(-1), "an expired key is configuration too");
    }

    [Fact]
    public async Task NoSecretNoHashNoIdAndNoStateIsInTheFile()
    {
        var text = Text(await ExportAsync());

        // Every string the file holds, decoded. A search of the written text would miss a base64 value whose '+' the writer
        // escapes, which is half of them.
        var strings = StringsOf(JsonDocument.Parse(text).RootElement).ToList();
        strings.ShouldNotBeEmpty();

        var credentials = new[]
        {
            _agentSecret, _agentId,
            Convert.ToBase64String(_agentHash), Convert.ToHexString(_agentHash), Convert.ToHexString(_agentHash).ToLowerInvariant(),
            Convert.ToBase64String(_agentSalt), Convert.ToHexString(_agentSalt), Convert.ToHexString(_agentSalt).ToLowerInvariant(),
        };
        foreach (var credential in credentials)
            strings.ShouldNotContain(v => v.Contains(credential, StringComparison.Ordinal), $"a stored credential value appears: {credential.Length} characters");

        strings.ShouldNotContain(v => v.Contains("dex_", StringComparison.Ordinal));
        foreach (var internalValue in new[] { "corpus-1", "corpus-2", "corpus-3", "source-1", "source-git", "source-up", "set-1", "set-p", "dexicon__embeddinggemma__768" })
            text.ShouldNotContain(internalValue);

        var properties = JsonDocument.Parse(text).RootElement.EnumerateObject().Select(p => p.Name).ToList();
        properties.ShouldBe(["format", "version", "exportedUtc", "dexiconVersion", "corpora", "modelProfiles", "keys", "omitted"]);
        text.ToLowerInvariant().ShouldNotContain("\"id\"");
        text.ShouldNotContain("\"state\"");
        text.ShouldNotContain("createdUtc");
        text.ShouldNotContain("tokenHash");
    }

    private static IEnumerable<string> StringsOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => StringsOf(p.Value).Append(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(StringsOf),
        _ => [],
    };

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
        read.Corpora.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AnUnsetPropertyIsWrittenAsNullAndAnEmptyListAsAnEmptyList()
    {
        var text = Text(await ExportAsync());

        text.ShouldContain("\"includeGlobs\": null");
        text.ShouldContain("\"git\": null");
        text.ShouldContain("\"expiresUtc\": null");
        text.ShouldContain("\"excludeGlobs\": []");
    }

    [Fact]
    public async Task RowsThatTieOnTheirNameAreOrderedByHowTheyAreWrittenAndNotByInsertion()
    {
        await using (var db = _harness.NewContext())
        {
            // "twin" is added before "Twin", and the key that sorts second is added first.
            db.Corpora.Add(new Corpus { Id = "corpus-t1", Name = "twin", State = CorpusState.Ready, CreatedUtc = Now });
            db.Corpora.Add(new Corpus { Id = "corpus-t2", Name = "Twin", State = CorpusState.Ready, CreatedUtc = Now });
            var tokens = new TokenService(db, TimeProvider.System);
            db.Tokens.Add(tokens.Build("dup", [Scopes.Search], expiresUtc: null).Row);
            db.Tokens.Add(tokens.Build("dup", [Scopes.Ingest], expiresUtc: null).Row);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var export = await ExportAsync();

        export.Corpora.Select(c => c.Name).ShouldBe(["Archive", "notes", "Papers", "Twin", "twin"]);
        export.Keys.Where(k => k.Name == "dup").Select(k => k.Scopes.Single()).ShouldBe([Scopes.Ingest, Scopes.Search]);
        Text(export).ShouldBe(Text(await ExportAsync()));
    }

    [Fact]
    public async Task AStoredSettingThatCannotBeReadIsReportedAndDoesNotStopTheExport()
    {
        await using (var db = _harness.NewContext())
        {
            var broken = new Corpus
            {
                Id = "corpus-b",
                Name = "Broken",
                State = CorpusState.Ready,
                CreatedUtc = Now,
                DefaultIncludeGlobs = "oops",
            };
            broken.Sources.Add(new Source
            {
                Id = "source-bad",
                CorpusId = "corpus-b",
                Kind = SourceKind.GitHistory,
                RootPath = "Bad",
                GitOptions = "{bad",
                CreatedUtc = Now,
            });
            broken.Sources.Add(new Source
            {
                Id = "source-ok",
                CorpusId = "corpus-b",
                Kind = SourceKind.Workspace,
                RootPath = "Ok",
                IncludeGlobs = "[",
                CreatedUtc = Now,
            });
            db.Corpora.Add(broken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var export = await ExportAsync();

        var corpus = export.Corpora.Single(c => c.Name == "Broken");
        corpus.Sources.ShouldHaveSingleItem().RootPath.ShouldBe("Ok", "the history source whose options could not be read is left out");
        corpus.Defaults.IncludeGlobs.ShouldBeNull();
        corpus.Sources[0].Filters.IncludeGlobs.ShouldBeNull();
        export.Omitted.ShouldContain("The githistory source 'Bad' of corpus 'Broken' has stored options that could not be read, and is not in this file.");
        export.Omitted.ShouldContain("The default include globs of corpus 'Broken' could not be read from its stored form, and is written as unset.");
        export.Omitted.ShouldContain("The include globs of the workspace source 'Ok' of corpus 'Broken' could not be read from its stored form, and is written as unset.");
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

/// <summary>
/// How the corpora are read. One joined statement over a corpus's sources and chunk sets returns each source once for every
/// chunk set, and Entity Framework Core warns about it once per query shape per process, so a test that waits for the
/// warning passes or fails by which test ran first. The statements issued are counted instead.
/// </summary>
public sealed class ConfigExportQueryTests
{
    private sealed class Statements : DbCommandInterceptor
    {
        public List<string> Text { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Text) Text.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ACorpusIsLoadedWithItsSourcesAndChunkSetsAsSeparateStatements()
    {
        var statements = new Statements();
        await using var harness = await IndexingHarness.StartAsync(statements, "a", "b");
        await harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
        await using var db = harness.NewContext();
        lock (statements.Text) statements.Text.Clear();

        var export = await ConfigEndpoints.BuildAsync(db, DateTime.UtcNow, "test", TestContext.Current.CancellationToken);

        export.Corpora.ShouldHaveSingleItem().Sources.Count.ShouldBe(2);
        export.Corpora[0].ChunkSets.Count.ShouldBe(2);
        lock (statements.Text)
        {
            // A split query reads each collection by a statement of its own, joined back to the corpora.
            statements.Text.Count(t => t.Contains("\"sources\"", StringComparison.Ordinal)).ShouldBe(1, "sources are read by one statement");
            statements.Text.Count(t => t.Contains("\"chunk_sets\"", StringComparison.Ordinal)).ShouldBe(1, "chunk sets by another");
            statements.Text.ShouldNotContain(
                t => t.Contains("\"sources\"", StringComparison.Ordinal) && t.Contains("\"chunk_sets\"", StringComparison.Ordinal),
                "one statement joining both returns each source once for every chunk set");
        }
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

        var before = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var response = await client.GetAsync("/api/config/export", TestContext.Current.CancellationToken);
        var after = DateTime.UtcNow.ToString("yyyy-MM-dd");

        response.EnsureSuccessStatusCode();
        var disposition = response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        disposition.DispositionType.ShouldBe("attachment");
        disposition.FileName.ShouldNotBeNull().Trim('"').ShouldBeOneOf($"dexicon-config-{before}.json", $"dexicon-config-{after}.json");
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue("a file of key scopes and paths is not for a cache to keep");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var root = JsonDocument.Parse(text).RootElement;
        root.GetProperty("format").GetString().ShouldBe("dexicon-config");
        root.GetProperty("version").GetInt32().ShouldBe(1);
        text.ShouldContain("\n  ", customMessage: "written indented, for a diff");
    }

    [Fact]
    public async Task AKeyWithEveryScopeButAdminIsRefusedThroughTheRoute()
    {
        await using var factory = Start();
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/session", new { password = Password }, TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        var admin = (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);

        var created = await client.PostAsJsonAsync("/api/tokens",
            new { name = "everything-but-admin", scopes = Scopes.Issuable }, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("secret").GetString();

        using var asKey = factory.CreateClient();
        asKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        (await asKey.GetAsync("/api/config/export", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
    }
}
