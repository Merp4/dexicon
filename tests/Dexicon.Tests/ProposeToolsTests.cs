using System.Reflection;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dexicon.Tests;

/// <summary>
/// <c>propose_removal</c> as an agent calls it, and the scope that gates it.
///
/// What a request is checked against is ProposalService's and is tested there. These are about what the
/// tool decides for itself: who is shown it and who may call it, which corpora a key reaches, what the
/// agent is told, and what index_status says about its own requests afterwards.
/// </summary>
public sealed class ProposeToolsTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "docs");
        await _harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static RequestContext As(string tokenId, params string[] scopes) =>
        new() { Principal = new Principal(tokenId, "agent-" + tokenId, scopes.ToHashSet(StringComparer.Ordinal)) };

    private static RequestContext Proposer(string id = "k1") => As(id, Scopes.Search, Scopes.Propose);

    private Task<string> ProposeAsync(CatalogDbContext db, RequestContext rc, string kind, string corpus = "notes",
        string? target = null, string? reason = "no longer needed") =>
        ProposeTools.ProposeRemovalAsync(rc, new ScopeResolver(db), _harness.NewProposals(db), kind, corpus, target, reason);

    private Task<string> StatusAsync(CatalogDbContext db, RequestContext rc) =>
        DexiconTools.IndexStatusAsync(rc, new ScopeResolver(db), db, _harness.Settings, "notes");

    [Fact]
    public async Task A_key_without_propose_is_refused_by_the_tool_whatever_else_it_holds()
    {
        await using var db = _harness.NewContext();

        foreach (var rc in new[] { As("k2", Scopes.Search), As("k3", Scopes.Search, Scopes.Ingest, Scopes.Configure) })
            (await Should.ThrowAsync<McpException>(() => ProposeAsync(db, rc, "source", target: "docs")))
                .Message.ShouldContain("'propose'");

        (await db.Proposals.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public void Only_a_key_holding_the_scope_is_shown_the_tool_under_the_name_it_is_registered_by()
    {
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Configure).Principal!).ShouldContain("propose_removal");
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Propose).Principal!).ShouldNotContain("propose_removal");

        // Propose does not carry configure's tools, and configure does not carry propose's.
        ToolVisibility.HiddenFrom(As("k", Scopes.Search, Scopes.Propose).Principal!).ShouldContain("configure_source");

        // A tool renamed without this list would be shown to every key.
        typeof(ProposeTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ShouldBe(ProposeTools.Names, ignoreOrder: true);
    }

    [Fact]
    public async Task A_request_is_recorded_and_the_agent_is_told_that_nothing_has_been_removed()
    {
        await using var db = _harness.NewContext();

        var text = await ProposeAsync(db, Proposer(), "source", target: "docs", reason: "superseded");

        text.ShouldStartWith("Recorded proposal ");
        text.ShouldContain("nothing has been removed");
        text.ShouldContain("remove the source files:docs from corpus 'notes'");
        text.ShouldContain("index_status(corpus: \"notes\")");
        var row = await db.Proposals.SingleAsync();
        text.ShouldContain(row.Id);
        (row.Status, row.TokenId, row.Reason).ShouldBe((ProposalStatus.Pending, "k1", "superseded"));
        (await db.Sources.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task The_same_request_again_is_reported_as_already_waiting_and_by_whom()
    {
        await using var db = _harness.NewContext();
        await ProposeAsync(db, Proposer("k1"), "chunk_set", target: "alt-1");

        var own = await ProposeAsync(db, Proposer("k1"), "chunk_set", target: "alt-1");
        var other = await ProposeAsync(db, Proposer("k2"), "chunk-set", target: "ALT-1");

        own.ShouldStartWith("Already waiting");
        own.ShouldContain("by this key");
        other.ShouldStartWith("Already waiting");
        other.ShouldContain("by another key");
        other.ShouldNotContain("agent-k1", Case.Insensitive, "another key's name is not this key's to read");
        (await db.Proposals.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_kind_that_is_not_one_is_refused_with_the_choices_and_a_refusal_arrives_in_the_services_words()
    {
        await using var db = _harness.NewContext();

        (await Should.ThrowAsync<McpException>(() => ProposeAsync(db, Proposer(), "folder", target: "docs")))
            .Message.ShouldContain("source, chunk_set, document or corpus");

        var unknown = await Should.ThrowAsync<McpException>(() => ProposeAsync(db, Proposer(), "source", target: "papers"));
        unknown.Message.ShouldContain("files:docs, files:notes");

        var noReason = await Should.ThrowAsync<McpException>(() => ProposeAsync(db, Proposer(), "source", target: "docs", reason: ""));
        noReason.Message.ShouldContain("why");
        (await db.Proposals.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_key_mapped_to_other_corpora_cannot_ask_about_one_it_does_not_reach()
    {
        await using var db = _harness.NewContext();
        db.Corpora.Add(new Corpus { Id = "corpus-2", Name = "papers", State = CorpusState.Ready, CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var (row, _) = await new TokenService(db, TimeProvider.System).CreateAsync("mapped", [Scopes.Search, Scopes.Propose], null);
        db.TokenCorpora.Add(new TokenCorpus { TokenId = row.Id, CorpusId = "corpus-2" });
        await db.SaveChangesAsync();

        var ex = await Should.ThrowAsync<McpException>(
            () => ProposeAsync(db, As(row.Id, Scopes.Search, Scopes.Propose), "corpus", corpus: "notes"));

        ex.Message.ShouldContain("papers", Case.Insensitive, "it is told what it does reach");
        (await db.Proposals.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Index_status_shows_a_key_its_own_requests_and_how_each_was_decided_and_no_one_elses()
    {
        await using var db = _harness.NewContext();
        await ProposeAsync(db, Proposer("k1"), "source", target: "docs");
        await ProposeAsync(db, Proposer("k1"), "chunk_set", target: "alt-1");
        await ProposeAsync(db, Proposer("k2"), "source", target: "notes", reason: "the other key's");
        var svc = _harness.NewProposals(db);
        var ids = await db.Proposals.Where(p => p.TokenId == "k1").OrderBy(p => p.CreatedUtc).Select(p => p.Id).ToListAsync();
        (await svc.RejectAsync(ids[0], default)).Refusal.ShouldBeNull();

        var k1 = await StatusAsync(db, Proposer("k1"));
        var k2 = await StatusAsync(db, Proposer("k2"));
        var cannotAsk = await StatusAsync(db, As("k9", Scopes.Search));

        k1.ShouldContain("removals this key has asked for:");
        k1.ShouldContain($"{ids[1]}  chunk set alt-1  waiting since");
        k1.ShouldContain($"{ids[0]}  source files:docs  rejected");
        k1.ShouldContain("it stays");
        k1.ShouldNotContain("files:notes  waiting", Case.Sensitive, "the other key's request");
        k2.ShouldContain("source files:notes  waiting since");
        k2.ShouldNotContain("chunk set alt-1", Case.Sensitive, "the other key's request; the set itself is listed for everyone");
        cannotAsk.ShouldNotContain("removals this key has asked for");

        await svc.ApproveAsync(ids[1], default);
        (await StatusAsync(db, Proposer("k1"))).ShouldContain("approved");
        (await StatusAsync(db, Proposer("k1"))).ShouldContain("it has been removed");
    }

    [Fact]
    public async Task A_request_that_could_not_be_done_says_why_in_index_status()
    {
        await using var db = _harness.NewContext();
        await ProposeAsync(db, Proposer(), "source", target: "docs");
        await using (var admin = _harness.NewContext())
            await _harness.NewConfiguration(admin).RemoveSourceAsync(
                await admin.Corpora.SingleAsync(c => c.Id == IndexingHarness.CorpusId), "source-2", default);
        var id = await db.Proposals.Select(p => p.Id).SingleAsync();
        await _harness.NewProposals(db).ApproveAsync(id, default);

        var text = await StatusAsync(db, Proposer());

        text.ShouldContain("could not be done");
        text.ShouldContain("source-2");
    }

    [Fact]
    public async Task Propose_can_be_issued_alone_and_a_key_adopted_from_the_environment_never_holds_it()
    {
        await using var db = _harness.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);

        var (row, _) = await tokens.CreateAsync("asker", [Scopes.Propose], null);

        row.Scopes.Split(',').ShouldBe([Scopes.Propose]);
        Scopes.Issuable.ShouldContain(Scopes.Propose);
        Scopes.All.ShouldContain(Scopes.Propose);
        Scopes.Bootstrap.ShouldNotContain(Scopes.Propose, "a value sitting in .env must not carry a grant");
        Scopes.Bootstrap.ShouldNotContain(Scopes.Configure);
        Scopes.Issuable.ShouldNotContain(Scopes.Admin);
    }
}
