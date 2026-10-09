using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace Dexicon.Tests;

/// <summary>
/// Text an agent sends that a tool repeats in an error. The SDK returns the message of an
/// <see cref="McpException"/> to the caller and a log can carry it, so a line break in the repeated value could
/// begin a line that reads as another entry, and a very long value was repeated in full.
/// </summary>
public sealed class McpEchoTests : IAsyncLifetime
{
    /// <summary>A line break, a forged log line after it, an escape sequence, then 5,000 characters.</summary>
    private static readonly string Hostile =
        "bad\n[10:00:00Z INF] forged\u001B[2J\u2028x" + new string('z', 5_000);

    private IndexingHarness _harness = null!;
    private readonly RecordingLoggerFactory _logs = new();

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "docs");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static RequestContext Configurer => new()
    {
        Principal = new Principal("k1", "agent-one",
            new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Configure }),
    };

    private static RequestContext Reader => new()
    {
        Principal = new Principal("k2", "reader", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search }),
    };

    /// <summary>One line, with none of the characters that end one or move a terminal, and not the whole value.</summary>
    private static void ShouldEchoSafely(McpException thrown, string startsWithValue = "bad")
    {
        thrown.Message.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')
            .ShouldBeFalse("a line break or an escape sequence would begin a line of its own");
        thrown.Message.Length.ShouldBeLessThan(1_200, "the 5,000 characters sent are not all repeated");
        thrown.Message.ShouldContain(startsWithValue, Case.Sensitive, "the start of the value still says what was refused");
    }

    private Task<string> SourceAsync(CatalogDbContext db, string folder, string kind = "files", bool create = false,
        HistorySettings? history = null, string[]? reset = null) =>
        ConfigureTools.ConfigureSourceAsync(Configurer, new ScopeResolver(db), db, _harness.NewConfiguration(db),
            _harness.Settings, _logs, "notes", folder, kind, create, null, null, null, null, history, reset);

    private Task<string> CorpusAsync(CatalogDbContext db, string[] reset) =>
        ConfigureTools.ConfigureCorpusAsync(Configurer, new ScopeResolver(db), db, _harness.NewConfiguration(db), _logs,
            "notes", reset: reset);

    [Fact]
    public async Task ANullEntryInResetIsRefusedByBothConfigureToolsAsAnMcpException()
    {
        // A JSON null in the list is a null element. Trim threw on it, which an agent sees only as
        // "An error occurred".
        await using var db = _harness.NewContext();

        var corpus = await Should.ThrowAsync<McpException>(() => CorpusAsync(db, [null!]));
        var source = await Should.ThrowAsync<McpException>(() => SourceAsync(db, "docs", reset: [null!]));

        corpus.Message.ShouldBe("reset takes include, exclude, gitignore, maxFileKb; a null entry is not one of them.");
        source.Message.ShouldBe(corpus.Message);
    }

    [Fact]
    public async Task AnUnknownResetNameWithALineBreakIsRepeatedOnOneLineByBothConfigureTools()
    {
        await using var db = _harness.NewContext();
        const string name = "bad\n[10:00:00Z INF] forged";

        var corpus = await Should.ThrowAsync<McpException>(() => CorpusAsync(db, [name]));
        var source = await Should.ThrowAsync<McpException>(() => SourceAsync(db, "docs", reset: [name]));

        foreach (var thrown in new[] { corpus, source })
        {
            thrown.Message.ShouldEndWith("'bad [10:00:00Z INF] forged' is not one of them here.");
            ShouldEchoSafely(thrown);
        }
    }

    [Fact]
    public async Task AKindThatIsNeitherFilesNorHistoryIsRepeatedOnOneLineAndCut()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => SourceAsync(db, "docs", kind: Hostile));

        thrown.Message.ShouldStartWith("kind is files or history, not 'bad");
        ShouldEchoSafely(thrown);
    }

    [Fact]
    public async Task AFolderWithNoSourceIsRepeatedOnOneLineAndCutWhenTheSourceIsToBeChanged()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => SourceAsync(db, Hostile));

        thrown.Message.ShouldStartWith("Corpus 'notes' has no source for the files under bad");
        ShouldEchoSafely(thrown);
    }

    [Fact]
    public async Task TwoSourcesOnOneFolderAreRepeatedOnOneLineAndCutWhenTheSourceIsToBeChanged()
    {
        await using var db = _harness.NewContext();
        var folder = "same\n[10:00:00Z INF] forged\u001B[2J" + new string('y', 500);
        foreach (var id in new[] { "dup-1", "dup-2" })
            db.Sources.Add(new Source
            {
                Id = id,
                CorpusId = IndexingHarness.CorpusId,
                Kind = SourceKind.Workspace,
                RootPath = folder,
                CreatedUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var thrown = await Should.ThrowAsync<McpException>(() => SourceAsync(db, folder));

        thrown.Message.ShouldContain("has 2 sources for the files under same");
        ShouldEchoSafely(thrown, "same");
    }

    [Fact]
    public void AnEchoedValueIsCutBeforeItIsScannedAndNotBetweenTheHalvesOfACharacterPair()
    {
        // 199 characters, then a surrogate pair that would straddle the cut at 200.
        var pair = new string('a', 199) + "\U0001F600" + "tail";
        // 199 characters, then a CRLF that the cut would split.
        var crlf = new string('a', 199) + "\r\n" + "tail";

        DexiconTools.Echo(pair).ShouldBe(new string('a', 199) + "...");
        DexiconTools.Echo(crlf).ShouldBe(new string('a', 199) + " ...");
        DexiconTools.Echo(new string('z', 10_000_000)).Length.ShouldBe(DexiconTools.EchoMax + 3);
        DexiconTools.Echo(null).ShouldBe(string.Empty);
        DexiconTools.Echo("a\r\nb").ShouldBe("a b");
    }

    [Fact]
    public async Task AFolderOutsideTheWorkspaceIsRepeatedOnOneLineAndCut()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => SourceAsync(db, "../" + Hostile));

        thrown.Message.ShouldContain("is outside the workspace");
        ShouldEchoSafely(thrown, "../bad");
    }

    [Fact]
    public async Task AMissingFolderInListFoldersIsRepeatedOnOneLineAndCut()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ListFoldersAsync(
            Configurer, new ScopeResolver(db), db, _harness.Settings, Hostile));

        thrown.Message.ShouldStartWith("There is no folder 'bad");
        ShouldEchoSafely(thrown);
    }

    [Fact]
    public async Task AFolderThatLeavesTheWorkspaceInListFoldersIsRepeatedOnOneLineAndCut()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ListFoldersAsync(
            Configurer, new ScopeResolver(db), db, _harness.Settings, "../../" + Hostile));

        thrown.Message.ShouldContain("is outside the workspace, or passes through a link");
        ShouldEchoSafely(thrown, "'../../bad");
    }

    [Fact]
    public async Task AProposalKindThatIsNotOneOfTheChoicesIsRepeatedOnOneLineAndCut()
    {
        await using var db = _harness.NewContext();
        var proposer = new RequestContext
        {
            Principal = new Principal("k4", "proposer", new HashSet<string>(StringComparer.Ordinal) { Scopes.Propose }),
        };

        var thrown = await Should.ThrowAsync<McpException>(() => ProposeTools.ProposeRemovalAsync(
            proposer, new ScopeResolver(db), _harness.NewProposals(db), Hostile, "notes", "no longer needed"));

        thrown.Message.ShouldContain(", not 'bad");
        ShouldEchoSafely(thrown);
    }

    [Fact]
    public async Task AResetNameThatIsNotOneOfTheOnesAllowedIsRepeatedOnOneLineAndCut()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            Configurer, new ScopeResolver(db), db, _harness.NewConfiguration(db), _logs,
            "notes", reset: [Hostile]));

        thrown.Message.ShouldStartWith("reset takes ");
        ShouldEchoSafely(thrown);
    }

    [Fact]
    public async Task ASinceThatIsNotADateIsRepeatedOnOneLineAndCut()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() =>
            SourceAsync(db, "docs", kind: "history", create: true, history: new HistorySettings(Since: Hostile)));

        thrown.Message.ShouldStartWith("since is a date written yyyy-MM-dd, not 'bad");
        ShouldEchoSafely(thrown);
        (await db.Sources.CountAsync(s => s.Kind == SourceKind.GitHistory)).ShouldBe(0, "nothing is added for a refused call");
    }

    [Fact]
    public async Task AFileThatIsNotIndexedIsRepeatedOnOneLineAndCutByGetContext()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => DexiconTools.GetContextAsync(
            Reader, new ScopeResolver(db), _harness.Vectors, new DocumentReader(db), "notes", Hostile, aroundLine: 1));

        thrown.Message.ShouldStartWith("No indexed file 'bad");
        ShouldEchoSafely(thrown);
    }

    [Fact]
    public async Task AFileThatIsNotIndexedIsRepeatedOnOneLineAndCutByTheFileResource()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<McpException>(() => DexiconResources.FileAsync(
            Reader, new ScopeResolver(db), _harness.Vectors, new DocumentReader(db), "notes", Hostile));

        thrown.Message.ShouldStartWith("No indexed file 'bad");
        ShouldEchoSafely(thrown);
    }

    [Fact]
    public async Task ACorpusThatDoesNotExistIsRepeatedOnOneLineAndCutWhichEverToolIsAskedAboutIt()
    {
        await using var db = _harness.NewContext();
        var scopes = new ScopeResolver(db);

        var context = await Should.ThrowAsync<McpException>(() => DexiconTools.GetContextAsync(
            Reader, scopes, _harness.Vectors, new DocumentReader(db), Hostile, "a.md", aroundLine: 1));
        var resource = await Should.ThrowAsync<McpException>(() => DexiconResources.FileAsync(
            Reader, scopes, _harness.Vectors, new DocumentReader(db), Hostile, "a.md"));
        var refresh = await Should.ThrowAsync<McpException>(() => DexiconTools.IndexRefreshAsync(
            new RequestContext
            {
                Principal = new Principal("k3", "ingester",
                    new HashSet<string>(StringComparer.Ordinal) { Scopes.Ingest }),
            },
            scopes, new IndexJobQueue(db, new WorkScheduler(_harness.Settings),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IndexJobQueue>.Instance), Hostile));
        var status = await Should.ThrowAsync<McpException>(() => DexiconTools.IndexStatusAsync(
            Reader, scopes, db, _harness.Settings, Hostile));

        foreach (var thrown in new[] { context, resource, refresh, status })
        {
            ShouldEchoSafely(thrown, "'bad");
        }
    }

    [Fact]
    public async Task AFailureAfterTheCorpusIsSavedDoesNotRepeatTheErrorTextToTheAgentAndLogsIt()
    {
        // A client error from the vector store is the transport's own text, which can name the address of
        // Dexicon's own service, and the agent holding configure has no need of it.
        await using var db = _harness.NewContext();
        _harness.Vectors.OnEnsureCollection = () => throw new InvalidOperationException(
            "Status(StatusCode=\"Unavailable\", Detail=\"Error connecting to subchannel.\", "
            + "DebugException=\"System.Net.Http.HttpRequestException: Connection refused (qdrant:6334)\")");

        var thrown = await Should.ThrowAsync<McpException>(() => ConfigureTools.ConfigureCorpusAsync(
            Configurer, new ScopeResolver(db), db, _harness.NewConfiguration(db), _logs, "papers", create: true));

        thrown.Message.ShouldStartWith("Corpus 'papers' was created, but preparing its index failed");
        thrown.Message.ShouldNotContain("qdrant:6334");
        thrown.Message.ShouldNotContain("Unavailable");
        (await db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeTrue("the corpus is saved");
        _logs.Exceptions.ShouldContain(e => e.Message.Contains("qdrant:6334", StringComparison.Ordinal),
            "the operator can still read the cause");
    }
}
