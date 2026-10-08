using System.Reflection;
using System.Text.Json;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.AspNetCore.Http;
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
            .ShouldBe(new[] { "index_refresh", "list_folders", "configure_corpus", "configure_source", "propose_removal" }, ignoreOrder: true);
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Configure).Principal!)
            .ShouldBe(new[] { "index_refresh", "propose_removal" }, ignoreOrder: true);
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Ingest, Scopes.Configure).Principal!)
            .ShouldBe(new[] { "propose_removal" });
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Ingest, Scopes.Configure, Scopes.Propose).Principal!).ShouldBeEmpty();

        // A key may hold configure alone, and each search tool would refuse it.
        ToolVisibility.HiddenFrom(As("k", Scopes.Configure).Principal!).ShouldBe(
            new[] { "search_index", "list_corpora", "get_context", "index_status", "index_refresh", "propose_removal" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Two_calls_adding_one_folder_at_once_leave_one_source()
    {
        // Both could read the corpus's sources before either wrote, and a duplicate cannot be
        // removed from here. Each call has its own catalogue context, as two requests do.
        Directory.CreateDirectory(Path.Combine(Workspace, "papers"));

        async Task<string> Add()
        {
            await using var db = _harness.NewContext();
            try { return await SourceAsync(db, Configurer, "papers", create: true); }
            catch (McpException ex) { return ex.Message; }
        }

        var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Add)));

        await using var check = _harness.NewContext();
        (await check.Sources.CountAsync(s => s.RootPath == "papers")).ShouldBe(1);
        replies.Count(r => r.StartsWith("Added a source", StringComparison.Ordinal)).ShouldBe(1);
        replies.Count(r => r.Contains("already has a source")).ShouldBe(3);
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

    [Theory]
    [InlineData("notes/.")]
    [InlineData("docs/../notes")]
    [InlineData("notes/")]
    public async Task Another_spelling_of_a_folder_finds_its_source_rather_than_adding_a_second(string spelling)
    {
        // A duplicate added here could not be removed from here, and would index and embed
        // the same files twice.
        await using var db = _harness.NewContext();
        var before = await db.Sources.CountAsync();

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, spelling, create: true)))
            .Message.ShouldContain("already has a source for the files under notes");
        (await SourceAsync(db, Configurer, spelling, exclude: ["**/*.tmp"])).ShouldStartWith("Changed the source for the files under notes");
        (await db.Sources.CountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_folder_outside_the_workspace_is_refused_and_one_named_with_two_dots_is_not()
    {
        await using var db = _harness.NewContext();

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "../elsewhere", create: true)))
            .Message.ShouldContain("outside the workspace");
        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "..", create: true)))
            .Message.ShouldContain("outside the workspace");

        Directory.CreateDirectory(Path.Combine(Workspace, "..data"));
        (await SourceAsync(db, Configurer, "..data", create: true)).ShouldStartWith("Added a source for the files under ..data");
    }

    [Fact]
    public void A_backslash_is_a_separator_only_where_the_operating_system_says_so()
    {
        // On Linux "x\\..\\notes" is one odd name. Read as x/../notes it skipped the duplicate check,
        // passed the existence check once collapsed, and was stored as notes: a second source
        // over the same folder that MCP cannot remove. CI runs the Linux branch.
        var canonical = WorkspaceDiscovery.Canonical(Workspace, "x\\..\\notes");

        canonical.ShouldBe(OperatingSystem.IsWindows() ? "notes" : "x\\..\\notes");
        WorkspaceDiscovery.Canonical(Workspace, canonical).ShouldBe(canonical, "canonicalising twice changes nothing");
    }

    [Fact]
    public void A_path_keeps_a_backslash_that_is_part_of_a_name_on_Unix()
    {
        // Listed as "a/b" it names a different folder than the one stored as "a\\b", and
        // the "indexed by" lookup misses. CI runs the Unix branch.
        WorkspaceDiscovery.Forward("a\\b").ShouldBe(OperatingSystem.IsWindows() ? "a/b" : "a\\b");
        WorkspaceDiscovery.Forward("a/b").ShouldBe("a/b");
    }

    [Fact]
    public async Task Gitignore_cannot_be_turned_off_over_MCP()
    {
        // .gitignore keeps Dexicon's own .env out of the index when the workspace root is the
        // checkout that holds it. A key holding configure could otherwise index it and read the
        // admin password and the bootstrap token back by search.
        await using var db = _harness.NewContext();
        var before = await db.Sources.CountAsync();

        (await Should.ThrowAsync<McpException>(() => CorpusAsync(db, Configurer, "notes", gitignore: false)))
            .Message.ShouldContain("cannot be turned off over MCP");
        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "notes", gitignore: false)))
            .Message.ShouldContain("cannot be turned off over MCP");

        (await db.Corpora.SingleAsync()).DefaultUseGitignore.ShouldBeNull("nothing was stored");
        (await db.Jobs.AnyAsync()).ShouldBeFalse();
        (await db.Sources.CountAsync()).ShouldBe(before);
        (await SourceAsync(db, Configurer, "notes", gitignore: true)).ShouldStartWith("Changed the source");
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("line\u2028separator")]
    [InlineData("esc\u001B[2J")]
    public async Task A_description_is_one_short_line(string description)
    {
        // One agent writes it and every other agent that lists corpora reads it, so a block of
        // instructions or a forged listing line is the thing to keep out.
        await using var db = _harness.NewContext();

        (await Should.ThrowAsync<McpException>(() => CorpusAsync(db, Configurer, "notes", description: description)))
            .Message.ShouldContain("one line");
        (await Should.ThrowAsync<McpException>(() => CorpusAsync(db, Configurer, "papers", create: true, description: new string('x', 501))))
            .Message.ShouldContain("at most 500");

        (await db.Corpora.CountAsync()).ShouldBe(1);
        (await CorpusAsync(db, Configurer, "notes", description: new string('x', 500))).ShouldContain("description");
    }

    [Fact]
    public async Task A_link_s_target_is_not_returned_to_the_key()
    {
        // The resolver's message says where a link leads: host text, for a key that holds
        // configure and nothing else.
        await using var db = _harness.NewContext();
        var outside = Path.Combine(_harness.DataPath, "outside-secret-place");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "away"), outside);

        var refused = await Should.ThrowAsync<McpException>(() => FoldersAsync(db, Configurer, "away/inner"));

        refused.Message.ShouldContain("passes through a link");
        refused.Message.ShouldNotContain("outside-secret-place");
    }

    [Fact]
    public async Task A_key_without_search_is_not_sent_to_index_status()
    {
        // index_status needs search, and a configure-only key is not shown it.
        await using var db = _harness.NewContext();
        var configureOnly = As("k3", Scopes.Configure);

        var reply = await CorpusAsync(db, configureOnly, "notes", exclude: ["**/*.tmp"]);

        reply.ShouldContain("A refresh is queued.");
        reply.ShouldNotContain("index_status");
        (await CorpusAsync(db, Configurer, "notes", exclude: ["**/*.bak"])).ShouldContain("index_status");
    }

    [Fact]
    public async Task A_change_says_what_a_narrower_setting_does_to_what_is_indexed()
    {
        // Nothing here removes a corpus or a source, but a filter that stops selecting a file
        // drops it from the index on the refresh, as it does in the UI.
        await using var db = _harness.NewContext();

        (await SourceAsync(db, Configurer, "notes", exclude: ["**/*.tmp"])).ShouldContain("leave the index when the refresh runs");
        (await CorpusAsync(db, Configurer, "notes", exclude: ["**/*.bak"])).ShouldContain("leave the index when the refresh runs");
    }

    [Fact]
    public async Task A_source_cannot_inherit_gitignore_off_from_the_corpus()
    {
        // The UI can turn .gitignore off for a corpus. A source an agent adds without saying
        // would follow it, which is turning it off by another route: the new source indexes
        // what .gitignore keeps out, such as a .env. Resetting a source to follow it is the same.
        await using var db = _harness.NewContext();
        await db.Corpora.ExecuteUpdateAsync(u => u.SetProperty(c => c.DefaultUseGitignore, false));
        Directory.CreateDirectory(Path.Combine(Workspace, "papers"));
        var before = await db.Sources.CountAsync();

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "papers", create: true)))
            .Message.ShouldContain("turns .gitignore off");
        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "notes", reset: ["gitignore"])))
            .Message.ShouldContain("turns .gitignore off");
        (await db.Sources.CountAsync()).ShouldBe(before);
        (await db.Jobs.AnyAsync()).ShouldBeFalse();

        // Pinned on for this one source, it is allowed.
        (await SourceAsync(db, Configurer, "papers", create: true, gitignore: true)).ShouldStartWith("Added a source");
        (await db.Sources.SingleAsync(s => s.RootPath == "papers")).UseGitignore.ShouldBe(true);
    }

    [Fact]
    public async Task A_source_still_follows_a_corpus_that_leaves_gitignore_on()
    {
        await using var db = _harness.NewContext();
        Directory.CreateDirectory(Path.Combine(Workspace, "papers"));

        (await SourceAsync(db, Configurer, "papers", create: true)).ShouldStartWith("Added a source");
        (await SourceAsync(db, Configurer, "papers", reset: ["gitignore"])).ShouldStartWith("Nothing changed");
    }

    [Fact]
    public async Task A_corpus_name_with_a_quote_or_a_backslash_is_named_back_as_a_valid_call()
    {
        // Both are allowed in a name. Written raw into the call the reply suggests, the second
        // is a string that ends early and the third escapes the quote after it.
        await using var db = _harness.NewContext();

        var reply = await CorpusAsync(db, Configurer, "a\"b\\c", create: true);

        reply.ShouldContain("configure_source(corpus: \"a\\\"b\\\\c\", ");
    }

    [Fact]
    public async Task A_corpus_name_from_before_the_rule_with_a_line_break_cannot_split_a_reply()
    {
        // Existing catalogues can hold a name the rule now refuses.
        await using var db = _harness.NewContext();
        await db.Corpora.ExecuteUpdateAsync(u => u.SetProperty(c => c.Name, "two\nlines"));

        var reply = await CorpusAsync(db, Configurer, "two\nlines", description: "kept");

        reply.ShouldContain("corpus 'two lines'");
        reply.TrimEnd().ShouldNotContain("\n");
    }

    [Fact]
    public async Task A_null_folder_or_kind_is_refused_rather_than_read_as_the_root_or_dereferenced()
    {
        // JSON can send null whatever the signature says. A null folder read as "" would add a
        // source over the whole workspace, which nothing here can remove.
        await using var db = _harness.NewContext();
        var before = await db.Sources.CountAsync();

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, null!, create: true)))
            .Message.ShouldContain("folder is required");
        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "notes", kind: null!)))
            .Message.ShouldContain("files or history");

        (await db.Sources.CountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_corpus_name_with_a_line_break_stays_on_the_folder_s_line()
    {
        // New names cannot hold one; a corpus created before that rule can.
        await using var db = _harness.NewContext();
        await db.Corpora.ExecuteUpdateAsync(u => u.SetProperty(c => c.Name, "two\nlines"));

        (await FoldersAsync(db, Configurer)).ShouldContain("  notes/  0 entries; indexed by two lines (files)\n");
    }

    [Fact]
    public void A_setting_list_of_the_wrong_shape_is_refused_in_its_own_words()
    {
        // The corpus converter meets every string[] and says "corpus"; these lists are a type of
        // their own so that a bad include is not reported as a bad corpus.
        Should.Throw<McpException>(() => JsonSerializer.Deserialize<IReadOnlyList<string>>("7", McpJson.Options))
            .Message.ShouldBe("include, exclude and reset take a string or a list of strings, not a number.");
        Should.Throw<McpException>(() => JsonSerializer.Deserialize<IReadOnlyList<string>>("[1]", McpJson.Options))
            .Message.ShouldContain("this list holds a number");
        JsonSerializer.Deserialize<IReadOnlyList<string>>("\"**/*.md\"", McpJson.Options).ShouldBe(["**/*.md"]);
        JsonSerializer.Deserialize<IReadOnlyList<string>>("[\"a\",\"b\"]", McpJson.Options).ShouldBe(["a", "b"]);
    }

    [Theory]
    [InlineData(nameof(ConfigureTools.ConfigureCorpusAsync))]
    [InlineData(nameof(ConfigureTools.ConfigureSourceAsync))]
    public void The_lists_are_advertised_as_arrays_of_strings(string method)
    {
        // A custom converter can turn the advertised schema into {} and leave a caller guessing.
        var tool = McpServerTool.Create(typeof(ConfigureTools).GetMethod(method)!,
            new McpServerToolCreateOptions { SerializerOptions = McpJson.Options });
        using var schema = JsonDocument.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        var properties = schema.RootElement.GetProperty("properties");

        foreach (var name in new[] { "include", "exclude", "reset" })
        {
            var list = properties.GetProperty(name);
            list.GetProperty("type").EnumerateArray().Select(t => t.GetString()).ShouldContain("array", name);
            list.GetProperty("items").GetProperty("type").ToString().ShouldContain("string", Case.Sensitive, name);
        }
    }

    [Fact]
    public async Task A_folder_differing_in_case_is_the_same_source_only_where_the_filesystem_says_so()
    {
        // On Windows and macOS "NOTES" is the folder notes, and adding it would be a second
        // source over the same files. On Linux it is another folder, which is not there.
        await using var db = _harness.NewContext();
        var before = await db.Sources.CountAsync();

        var refusal = await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "NOTES", create: true));

        refusal.Message.ShouldContain(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? "already has a source"
            : "There is no folder 'NOTES'");
        (await db.Sources.CountAsync()).ShouldBe(before);

        // And list_folders reads a source stored as NOTES as reading the folder notes, there.
        await db.Sources.Where(s => s.RootPath == "notes").ExecuteUpdateAsync(u => u.SetProperty(s => s.RootPath, "NOTES"));
        var listed = await FoldersAsync(db, Configurer);
        (listed.Contains("  notes/  0 entries; indexed by notes (files)\n"))
            .ShouldBe(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    }

    [Theory]
    [InlineData("docs", "docs")]
    [InlineData("docs/", "docs")]
    [InlineData("docs/.", "docs")]
    [InlineData("x/../docs", "docs")]
    [InlineData("books/./manuals/", "books/manuals")]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("../x", "../x")]
    public void A_source_root_has_one_spelling(string written, string canonical) =>
        WorkspaceDiscovery.Canonical(Workspace, written).ShouldBe(canonical);

    [Fact]
    public async Task A_source_added_through_the_api_is_stored_in_that_spelling()
    {
        await using var db = _harness.NewContext();
        Directory.CreateDirectory(Path.Combine(Workspace, "extra"));
        var corpus = await db.Corpora.SingleAsync();

        (await _harness.NewConfiguration(db).AddSourceAsync(corpus, new AddSourceRequest("extra/./"), default))
            .Refusal.ShouldBeNull();

        (await db.Sources.CountAsync(s => s.RootPath == "extra")).ShouldBe(1);
    }

    [Fact]
    public void A_null_or_unknown_scope_is_refused_and_a_padded_one_is_not()
    {
        // A null element binds from JSON, and was dereferenced: a 500 where a 400 was meant.
        ((IStatusCodeHttpResult)SystemEndpoints.UnissuableScopes([null]).ShouldNotBeNull()).StatusCode.ShouldBe(400);
        ((IStatusCodeHttpResult)SystemEndpoints.UnissuableScopes(["admin"]).ShouldNotBeNull()).StatusCode.ShouldBe(400);
        SystemEndpoints.UnissuableScopes([" Search ", "configure"]).ShouldBeNull();
    }

    [Fact]
    public async Task Changing_a_source_that_is_not_there_names_the_ones_that_are()
    {
        await using var db = _harness.NewContext();
        db.Sources.Add(new Source
        {
            Id = "history-root", CorpusId = IndexingHarness.CorpusId, Kind = SourceKind.GitHistory,
            RootPath = "", CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var miss = await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "notes", kind: "history"));
        miss.Message.ShouldContain("no source for the commit history of notes");
        miss.Message.ShouldContain("files under notes");
        miss.Message.ShouldContain("commit history of the workspace root");
        miss.Message.ShouldContain("create: true");

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "notes", create: true)))
            .Message.ShouldContain("already has a source");
    }

    [Fact]
    public async Task Two_sources_on_one_folder_are_not_changed_by_guessing_which()
    {
        // The UI allows it, as for two history sources following different branches.
        await using var db = _harness.NewContext();
        db.Sources.Add(new Source
        {
            Id = "docs-again", CorpusId = IndexingHarness.CorpusId, Kind = SourceKind.Workspace,
            RootPath = "docs", CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        (await Should.ThrowAsync<McpException>(() => SourceAsync(db, Configurer, "docs", exclude: ["**/*.tmp"])))
            .Message.ShouldContain("has 2 sources for the files under docs");

        (await db.Sources.AsNoTracking().CountAsync(s => s.RootPath == "docs" && s.ExcludeGlobs != null))
            .ShouldBe(0, "neither source was changed");
        (await db.Jobs.AnyAsync()).ShouldBeFalse();
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
    public async Task A_link_is_neither_listed_nor_looked_through()
    {
        // A link can lead out of the workspace. Counting its entries, or finding a .git behind
        // it, reads wherever it points, and it could not be added as a source anyway (D-35).
        await using var db = _harness.NewContext();
        var outside = Path.Combine(_harness.DataPath, "outside");
        Directory.CreateDirectory(Path.Combine(outside, ".git"));
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "away"), outside);
        Directory.CreateDirectory(Path.Combine(Workspace, "repo"));
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "repo", ".git"), Path.Combine(outside, ".git"));

        var listed = await FoldersAsync(db, Configurer);

        listed.ShouldNotContain("away");
        listed.ShouldContain("  repo/  1 entry\n", Case.Sensitive, "a .git that is a link does not make a repository");
        SystemEndpoints.FoldersIn(Workspace, Workspace).Select(f => f.Name).ShouldNotContain("away");
    }

    [Fact]
    public async Task A_name_in_a_change_s_log_line_can_neither_break_it_nor_reach_a_terminal()
    {
        // A key's name and a corpus's are typed text. Raw, a line break forges a log line and
        // an escape sequence clears the screen of a terminal tailing the log.
        await using var db = _harness.NewContext();

        await CorpusAsync(db, As("k\nforged\u2028too\u001B[2J", Scopes.Configure), "papers", create: true);

        var line = _logs.Lines.Single(l => l.Contains("created corpus"));
        line.ShouldNotContain("\n");
        line.ShouldNotContain("\u001B");
        line.ShouldNotContain("\u2028");
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
