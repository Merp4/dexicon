using System.Reflection;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dexicon.Tests;

/// <summary>
/// The configure tools as an agent calls them.
///
/// The rules for a corpus or a source are CorpusConfiguration's and are tested there. These
/// are about what the tools decide for themselves: the scope, creating only when asked, a
/// folder that must exist, a mapped key's new corpus, history settings merged rather than
/// replaced, and what the agent is told.
/// </summary>
public sealed class ConfigureToolsTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;
    private readonly RecordingLoggerFactory _logs = new();

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "docs");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private string Workspace => Path.Combine(_harness.DataPath, "workspace");

    private static RequestContext As(string tokenId, params string[] scopes) =>
        new() { Principal = new Principal(tokenId, "agent-" + tokenId, scopes.ToHashSet(StringComparer.Ordinal)) };

    private static RequestContext Configurer => As("k1", Scopes.Search, Scopes.Configure);

    private Task<string> FoldersAsync(CatalogDbContext db, RequestContext rc, string? path = null) =>
        ConfigureTools.ListFoldersAsync(rc, new ScopeResolver(db), db, _harness.Settings, path);

    private Task<string> CorpusAsync(CatalogDbContext db, RequestContext rc, string corpus, bool create = false,
        string? description = null, string[]? include = null, string[]? exclude = null, bool? gitignore = null,
        int? maxFileKb = null, string[]? reset = null) =>
        ConfigureTools.ConfigureCorpusAsync(rc, new ScopeResolver(db), db, _harness.NewConfiguration(db), _logs,
            corpus, create, description, include, exclude, gitignore, maxFileKb, reset);

    private Task<string> SourceAsync(CatalogDbContext db, RequestContext rc, string folder, string kind = "files",
        bool create = false, string[]? include = null, string[]? exclude = null, bool? gitignore = null,
        int? maxFileKb = null, HistorySettings? history = null, string[]? reset = null) =>
        ConfigureTools.ConfigureSourceAsync(rc, new ScopeResolver(db), db, _harness.NewConfiguration(db),
            _harness.Settings, _logs, "notes", folder, kind, create, include, exclude, gitignore, maxFileKb, history, reset);

    [Fact]
    public async Task A_key_without_configure_is_refused_by_every_configure_tool()
    {
        await using var db = _harness.NewContext();
        var searcher = As("k2", Scopes.Search, Scopes.Ingest);

        (await Should.ThrowAsync<McpException>(() => FoldersAsync(db, searcher))).Message.ShouldContain("'configure'");
        (await Should.ThrowAsync<McpException>(() => CorpusAsync(db, searcher, "papers", create: true)))
            .Message.ShouldContain("'configure'");
        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, searcher, "docs", exclude: ["x"])))
            .Message.ShouldContain("'configure'");

        (await db.Corpora.CountAsync()).ShouldBe(1);
        (await db.Jobs.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public void Only_a_key_holding_the_scope_is_shown_the_tools()
    {
        ToolVisibility.HiddenFrom(As("k", Scopes.Search).Principal!)
            .ShouldBe(new[] { "index_refresh", "list_folders", "configure_corpus", "configure_source" }, ignoreOrder: true);
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Configure).Principal!)
            .ShouldBe(new[] { "index_refresh" });
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Ingest, Scopes.Configure).Principal!).ShouldBeEmpty();
    }

    [Fact]
    public void The_names_hidden_are_the_names_the_tools_are_registered_under()
    {
        // A tool renamed without this list would be shown to every key. It would still refuse
        // the call, but the listing is what keeps an agent from spending a turn on it.
        var registered = typeof(ConfigureTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>();

        registered.ShouldBe(ConfigureTools.Names, ignoreOrder: true);
    }

    [Fact]
    public async Task A_corpus_is_created_only_when_asked_and_a_mapped_key_then_reaches_it()
    {
        await using var db = _harness.NewContext();
        var (row, _) = await new TokenService(db, TimeProvider.System)
            .CreateAsync("mapped", [Scopes.Search, Scopes.Configure], null);
        db.TokenCorpora.Add(new TokenCorpus { TokenId = row.Id, CorpusId = IndexingHarness.CorpusId });
        await db.SaveChangesAsync();
        var rc = As(row.Id, Scopes.Search, Scopes.Configure);

        (await Should.ThrowAsync<McpException>(() => CorpusAsync(db, rc, "papers"))).Message.ShouldContain("create: true");
        (await db.Corpora.CountAsync()).ShouldBe(1, "a name that matched nothing is not a request to create it");

        var text = await CorpusAsync(db, rc, "papers", create: true, description: "Conference papers");

        text.ShouldStartWith("Created corpus 'papers'");
        var papers = await db.Corpora.SingleAsync(c => c.Name == "papers");
        papers.Description.ShouldBe("Conference papers");
        (await new ScopeResolver(db).VisibleAsync(rc.Principal!)).Select(c => c.Name)
            .ShouldBe(new[] { "notes", "papers" }, ignoreOrder: true, "a mapped key reaches only its corpora, so the new one joins them");
    }

    [Fact]
    public async Task A_corpus_s_filters_are_merged_with_what_it_has_and_reset_by_name()
    {
        await using var db = _harness.NewContext();

        await CorpusAsync(db, Configurer, "notes", exclude: ["**/bin/**"], maxFileKb: 128);
        var text = await CorpusAsync(db, Configurer, "notes", include: ["**/*.md"], reset: ["maxFileKb"]);

        text.ShouldContain("the filters its sources inherit");
        text.ShouldContain("A refresh is queued");
        var defaults = (await db.Corpora.SingleAsync()).DefaultsOf();
        defaults.ShouldBe(new CorpusDefaults(null, null, ["**/*.md"], ["**/bin/**"]), new CorpusDefaultsComparer());
    }

    [Fact]
    public async Task A_folder_is_added_only_if_it_exists_and_the_reply_shows_it_as_index_status_does()
    {
        await using var db = _harness.NewContext();
        var before = await db.Sources.CountAsync();

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "papers", create: true)))
            .Message.ShouldContain("list_folders");
        (await db.Sources.CountAsync()).ShouldBe(before, "a typo cannot be removed from here, so it is not added");

        Directory.CreateDirectory(Path.Combine(Workspace, "papers"));
        var text = await SourceAsync(db, Configurer, "papers", create: true, exclude: ["**/draft/**"], maxFileKb: 512);

        text.ShouldStartWith("Added a source for the files under papers to corpus 'notes', and queued a refresh as job ");
        text.ShouldContain(
            "  files under papers: 0 files found; .gitignore respected; .dexiconignore respected; "
            + "code and text up to 512 KB; not **/draft/**\n");
        (await db.Jobs.CountAsync(j => j.Kind == JobKind.Refresh)).ShouldBe(1);
    }

    [Fact]
    public async Task Changing_a_source_that_is_not_there_names_the_ones_that_are()
    {
        await using var db = _harness.NewContext();

        var miss = await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "notes", kind: "history"));
        miss.Message.ShouldContain("no source for the commit history of notes");
        miss.Message.ShouldContain("files under notes");
        miss.Message.ShouldContain("create: true");

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "notes", create: true)))
            .Message.ShouldContain("already has a source");
    }

    [Fact]
    public async Task A_change_that_moves_nothing_queues_nothing()
    {
        await using var db = _harness.NewContext();

        (await SourceAsync(db, Configurer, "notes", exclude: ["**/*.tmp"])).ShouldStartWith("Changed the source for the files under notes");
        var jobs = await db.Jobs.CountAsync();

        (await SourceAsync(db, Configurer, "notes", exclude: ["**/*.tmp"])).ShouldStartWith("Nothing changed");
        (await db.Jobs.CountAsync()).ShouldBe(jobs);
    }

    [Theory]
    [InlineData("both", "both set and reset")]
    [InlineData("file-only on history", "does not take exclude")]
    [InlineData("history on files", "history source")]
    [InlineData("history reset on files", "reset takes")]
    [InlineData("unknown kind", "files or history")]
    [InlineData("zero cap", "greater than zero")]
    public async Task Settings_that_contradict_or_do_not_apply_are_refused_before_anything_changes(string @case, string expected)
    {
        await using var db = _harness.NewContext();

        Func<Task<string>> call = @case switch
        {
            "both" => () => SourceAsync(db, Configurer, "notes", exclude: ["a"], reset: ["exclude"]),
            "file-only on history" => () => SourceAsync(db, Configurer, "notes", kind: "history", create: true, exclude: ["a"]),
            "history on files" => () => SourceAsync(db, Configurer, "notes", history: new HistorySettings(Diff: true)),
            "history reset on files" => () => SourceAsync(db, Configurer, "notes", reset: ["since"]),
            "unknown kind" => () => SourceAsync(db, Configurer, "notes", kind: "commits"),
            "zero cap" => () => SourceAsync(db, Configurer, "notes", maxFileKb: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        (await Should.ThrowAsync<McpException>(call)).Message.ShouldContain(expected);
        (await db.Jobs.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public void History_settings_sent_are_laid_over_the_current_ones_and_reset_ones_return_to_default()
    {
        // The API replaces a history source's settings whole. Sending only diff must not
        // quietly return the ref, the limit and the date to their defaults.
        var current = new GitHistoryOptions
        {
            Ref = "origin/main", MaxCommits = 100, Since = new DateOnly(2026, 1, 1), IncludeStat = false,
        };

        var merged = ConfigureTools.Merge(current, new HistorySettings(Diff: true, MaxDiffKb: 16), ["since", "stat"]);

        merged.ShouldBe(current with { IncludeDiff = true, MaxDiffBytes = 16 * 1024, Since = null, IncludeStat = true });
    }

    [Fact]
    public void A_since_that_is_not_a_date_is_refused()
    {
        Should.Throw<McpException>(() =>
                ConfigureTools.Merge(new GitHistoryOptions(), new HistorySettings(Since: "last week"), []))
            .Message.ShouldContain("yyyy-MM-dd");
    }

    [Fact]
    public async Task Every_change_is_logged_with_the_key_that_made_it()
    {
        await using var db = _harness.NewContext();

        await CorpusAsync(db, Configurer, "papers", create: true, description: "Conference papers");
        await SourceAsync(db, Configurer, "docs", exclude: ["**/*.bak"]);

        _logs.Lines.ShouldContain(l => l.Contains("agent-k1") && l.Contains("created corpus papers; set: description"));
        _logs.Lines.ShouldContain(l => l.Contains("agent-k1") && l.Contains("changed the source for the files under docs"));
    }

    [Fact]
    public async Task Folders_show_repositories_and_what_reads_them_but_only_from_corpora_the_key_reaches()
    {
        await using var db = _harness.NewContext();
        Directory.CreateDirectory(Path.Combine(Workspace, "repo", ".git"));

        var all = await FoldersAsync(db, Configurer);
        all.ShouldContain("  notes/  0 entries; indexed by notes (files)\n");
        all.ShouldContain("  repo/  1 entry; git repository\n");
        (await FoldersAsync(db, Configurer, "repo")).ShouldStartWith("Folders in repo (git repository):\n  none\n");
        (await Should.ThrowAsync<McpException>(() => FoldersAsync(db, Configurer, "..")))
            .Message.ShouldNotBeNullOrWhiteSpace();

        // A key mapped elsewhere is not told which corpus reads the folder.
        var papers = (await _harness.NewConfiguration(db).CreateCorpusAsync(new CreateCorpusRequest("papers"), default)).Value!;
        var (row, _) = await new TokenService(db, TimeProvider.System).CreateAsync("elsewhere", [Scopes.Configure], null);
        db.TokenCorpora.Add(new TokenCorpus { TokenId = row.Id, CorpusId = papers.Id });
        await db.SaveChangesAsync();

        var mapped = await FoldersAsync(db, As(row.Id, Scopes.Configure));
        mapped.ShouldContain("  notes/  0 entries\n");
        mapped.ShouldNotContain("indexed by notes");
    }

    private sealed class CorpusDefaultsComparer : IEqualityComparer<CorpusDefaults>
    {
        public bool Equals(CorpusDefaults? a, CorpusDefaults? b) =>
            a is not null && b is not null
            && a.UseGitignore == b.UseGitignore && a.MaxFileBytes == b.MaxFileBytes
            && (a.IncludeGlobs ?? []).SequenceEqual(b.IncludeGlobs ?? [])
            && (a.ExcludeGlobs ?? []).SequenceEqual(b.ExcludeGlobs ?? [])
            && (a.IncludeGlobs is null) == (b.IncludeGlobs is null)
            && (a.ExcludeGlobs is null) == (b.ExcludeGlobs is null);

        public int GetHashCode(CorpusDefaults d) => 0;
    }
}

/// <summary>Every message logged, formatted, for a test that asserts what was recorded.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger(RecordingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines) owner.Lines.Add(formatter(state, exception));
        }
    }
}
