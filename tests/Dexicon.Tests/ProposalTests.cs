using System.Text;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Removals an agent asks for and a person decides.
///
/// What is judged here is the service: what a request is checked against and told, that a decision
/// and the removal it names are one save, and that two decisions cannot both stand. The removals
/// themselves are CorpusConfiguration's and are tested there; these show that approving reaches them.
/// </summary>
public sealed class ProposalTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes", "docs");
        // Two sets, so a removal that reached only the default one would be seen.
        await _harness.SeedCorpusAsync(SourceKind.Workspace, sets: 2);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static Principal Key(string id = "k1", string name = "research-agent") =>
        new(id, name, new HashSet<string>(StringComparer.Ordinal) { Scopes.Search, Scopes.Propose });

    private static Task<Corpus> CorpusAsync(CatalogDbContext db) =>
        db.Corpora.SingleAsync(c => c.Id == IndexingHarness.CorpusId);

    private async Task<ConfigOutcome<ProposalAsked>> TryAskAsync(
        CatalogDbContext db, ProposalKind kind, string? target, string? reason = "no longer needed", Principal? by = null) =>
        await _harness.NewProposals(db).ProposeAsync(by ?? Key(), await CorpusAsync(db), kind, target, reason, default);

    private async Task<Proposal> AskAsync(
        CatalogDbContext db, ProposalKind kind, string? target, string? reason = "no longer needed", Principal? by = null)
    {
        var asked = await TryAskAsync(db, kind, target, reason, by);
        asked.Refusal.ShouldBeNull();
        return asked.Value!.Proposal;
    }

    /// <summary>Long enough to fall into several chunks, where <see cref="IndexingHarness.Prose"/> is one.</summary>
    private static string Long(string word) => string.Join("\n\n", Enumerable.Range(1, 200)
        .Select(i => $"Paragraph {i} about {word}: " + string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 12))));

    /// <summary>One file in each source, indexed into both sets.</summary>
    private async Task IndexAsync()
    {
        await _harness.WriteFileAsync("a.md", IndexingHarness.Prose("alpha"), source: 0);
        await _harness.WriteFileAsync("b.md", IndexingHarness.Prose("beta"), source: 1);
        await _harness.RunIndexAsync(JobKind.Full);
    }

    private async Task<Proposal> StoredAsync(string id)
    {
        await using var fresh = _harness.NewContext();
        return await fresh.Proposals.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    // ---- asking -------------------------------------------------------------------------------

    [Fact]
    public async Task A_key_asks_for_a_source_by_its_folder_and_the_request_is_recorded_by_id()
    {
        await using var db = _harness.NewContext();

        var asked = await TryAskAsync(db, ProposalKind.Source, "notes", "added by mistake");

        asked.Refusal.ShouldBeNull();
        asked.Value!.AlreadyPending.ShouldBeFalse();
        var stored = await StoredAsync(asked.Value.Proposal.Id);
        stored.TargetId.ShouldBe(IndexingHarness.SourceIdFor(0));
        stored.TargetLabel.ShouldBe("files:notes");
        (stored.Status, stored.Kind, stored.Reason).ShouldBe((ProposalStatus.Pending, ProposalKind.Source, "added by mistake"));
        (stored.TokenId, stored.TokenName, stored.CorpusId, stored.CorpusName)
            .ShouldBe(("k1", "research-agent", IndexingHarness.CorpusId, "notes"));
        stored.DecidedUtc.ShouldBeNull();
        (await db.Sources.CountAsync()).ShouldBe(2, "asking removes nothing");
    }

    [Theory]
    [InlineData("files:docs", "source-2")]
    [InlineData("./docs/", "source-2")]
    [InlineData("x/../docs", "source-2")]
    [InlineData("source-2", "source-2")]
    public async Task A_source_is_found_by_prefix_by_id_or_by_a_path_that_collapses_to_its_folder(string asked, string expected)
    {
        await using var db = _harness.NewContext();

        var proposal = await AskAsync(db, ProposalKind.Source, asked);

        proposal.TargetId.ShouldBe(expected);
        proposal.TargetLabel.ShouldBe("files:docs");
    }

    [Fact]
    public async Task Two_sources_on_one_folder_must_be_told_apart()
    {
        await using var db = _harness.NewContext();
        db.Sources.Add(new Source
        {
            Id = "source-h", CorpusId = IndexingHarness.CorpusId, Kind = SourceKind.GitHistory,
            RootPath = "notes", CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var ambiguous = await TryAskAsync(db, ProposalKind.Source, "notes");
        var refusal = ambiguous.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Detail.ShouldContain("files:notes");
        refusal.Detail.ShouldContain("history:notes");

        (await AskAsync(db, ProposalKind.Source, "history:notes")).TargetId.ShouldBe("source-h");
        (await AskAsync(db, ProposalKind.Source, "files:notes")).TargetId.ShouldBe("source-1");
    }

    [Fact]
    public async Task A_source_that_is_not_there_is_named_back_with_the_ones_that_are()
    {
        await using var db = _harness.NewContext();

        var refused = await TryAskAsync(db, ProposalKind.Source, "papers");

        var refusal = refused.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(404);
        refusal.Detail.ShouldContain("'papers'");
        refusal.Detail.ShouldContain("files:docs, files:notes");
        (await db.Proposals.AnyAsync()).ShouldBeFalse("a refusal records nothing");
    }

    [Fact]
    public async Task A_chunk_set_is_asked_for_by_name_ignoring_case_or_by_id()
    {
        await using var db = _harness.NewContext();

        (await AskAsync(db, ProposalKind.ChunkSet, "ALT-1")).TargetId.ShouldBe("set-2");

        await using var other = _harness.NewContext();
        // The same set again, by id, is the one already waiting.
        var again = await TryAskAsync(other, ProposalKind.ChunkSet, "set-2");
        again.Value!.AlreadyPending.ShouldBeTrue();
    }

    [Fact]
    public async Task The_default_set_and_an_unknown_one_are_refused_when_asked()
    {
        await using var db = _harness.NewContext();

        var isDefault = (await TryAskAsync(db, ProposalKind.ChunkSet, "default")).Refusal.ShouldNotBeNull();
        isDefault.Status.ShouldBe(409);
        isDefault.Title.ShouldContain("default chunk set");

        var unknown = (await TryAskAsync(db, ProposalKind.ChunkSet, "set-9")).Refusal.ShouldNotBeNull();
        unknown.Status.ShouldBe(404);
        unknown.Detail.ShouldContain("alt-1, default");
        (await db.Proposals.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task The_only_chunk_set_is_refused_and_the_corpus_is_named_as_the_way_out()
    {
        await using var single = await IndexingHarness.StartAsync("notes");
        await single.SeedCorpusAsync(SourceKind.Workspace, sets: 1);
        await using var db = single.NewContext();

        var refused = await single.NewProposals(db).ProposeAsync(
            Key(), await CorpusAsync(db), ProposalKind.ChunkSet, "default", "unused", default);

        var refusal = refused.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Detail.ShouldContain("removing the corpus");
    }

    [Fact]
    public async Task A_corpus_is_asked_for_without_a_target_and_a_wrong_one_is_refused()
    {
        await using var db = _harness.NewContext();

        var wrong = (await TryAskAsync(db, ProposalKind.Corpus, "papers")).Refusal.ShouldNotBeNull();
        wrong.Status.ShouldBe(400);
        wrong.Detail.ShouldContain("leave target out");

        var proposal = await AskAsync(db, ProposalKind.Corpus, null);
        proposal.TargetId.ShouldBe(IndexingHarness.CorpusId);
        proposal.TargetLabel.ShouldBe("notes");

        // The name is accepted too, ignoring case, for an agent that fills the field in anyway.
        (await TryAskAsync(db, ProposalKind.Corpus, "NOTES")).Value!.AlreadyPending.ShouldBeTrue();
    }

    [Fact]
    public async Task A_reason_is_required_short_and_one_line()
    {
        await using var db = _harness.NewContext();

        (await TryAskAsync(db, ProposalKind.Source, "docs", reason: null)).Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await TryAskAsync(db, ProposalKind.Source, "docs", reason: "  \n ")).Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        (await TryAskAsync(db, ProposalKind.Source, "docs", reason: new string('x', 301))).Refusal
            .ShouldNotBeNull().Detail.ShouldContain("301");
        (await db.Proposals.AnyAsync()).ShouldBeFalse();

        var kept = await AskAsync(db, ProposalKind.Source, "docs", reason: "  superseded\nby the new export  ");
        kept.Reason.ShouldBe("superseded by the new export", "a line break could begin a line that reads as another entry");
    }

    [Fact]
    public async Task Asking_twice_returns_the_one_waiting_and_records_nothing_new()
    {
        await using var db = _harness.NewContext();
        var first = await TryAskAsync(db, ProposalKind.Source, "docs", "first reason");

        // Another key asking for the same thing is told it is already waiting; it is not recorded twice.
        var second = await TryAskAsync(db, ProposalKind.Source, "docs", "second reason", by: Key("k2", "other-agent"));

        second.Refusal.ShouldBeNull();
        second.Value!.AlreadyPending.ShouldBeTrue();
        second.Value.Proposal.Id.ShouldBe(first.Value!.Proposal.Id);
        (await db.Proposals.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_key_may_have_ten_waiting_and_another_key_is_not_held_to_its_count()
    {
        await using var db = _harness.NewContext();
        for (var i = 0; i < 11; i++)
            db.Sources.Add(new Source
            {
                Id = $"x{i}", CorpusId = IndexingHarness.CorpusId, Kind = SourceKind.Workspace,
                RootPath = $"extra/{i}", CreatedUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        for (var i = 0; i < ProposalService.MaxPendingPerKey; i++)
            await AskAsync(db, ProposalKind.Source, $"x{i}");

        var over = (await TryAskAsync(db, ProposalKind.Source, "x10")).Refusal.ShouldNotBeNull();
        over.Status.ShouldBe(429);
        over.Detail.ShouldContain("10");

        // Asking again for one already waiting is not another, so it is not turned away.
        (await TryAskAsync(db, ProposalKind.Source, "x0")).Value!.AlreadyPending.ShouldBeTrue();

        (await TryAskAsync(db, ProposalKind.Source, "x10", by: Key("k2", "other-agent"))).Refusal.ShouldBeNull();
    }

    // ---- deciding -----------------------------------------------------------------------------

    [Fact]
    public async Task Approving_a_source_removes_it_from_every_set_and_records_the_approval()
    {
        await IndexAsync();
        _harness.Vectors.CountFor("a.md", "set-1").ShouldBeGreaterThan(0, "the premise");
        _harness.Vectors.CountFor("a.md", "set-2").ShouldBeGreaterThan(0, "the premise");
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.Source, "notes");

        var approved = await _harness.NewProposals(db).ApproveAsync(proposal.Id, default);

        approved.Refusal.ShouldBeNull();
        approved.Value!.Status.ShouldBe("approved");
        foreach (var set in new[] { "set-1", "set-2" })
        {
            _harness.Vectors.CountFor("a.md", set).ShouldBe(0, $"gone from {set}");
            _harness.Vectors.CountFor("b.md", set).ShouldBeGreaterThan(0, $"the other source stays in {set}");
        }
        var stored = await StoredAsync(proposal.Id);
        stored.Status.ShouldBe(ProposalStatus.Approved);
        stored.DecidedUtc.ShouldNotBeNull();
        await using var fresh = _harness.NewContext();
        (await fresh.Sources.AnyAsync(s => s.Id == "source-1")).ShouldBeFalse();
    }

    [Fact]
    public async Task Approving_a_chunk_set_removes_its_vectors_and_its_row_and_leaves_the_default()
    {
        await IndexAsync();
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.ChunkSet, "alt-1");

        var approved = await _harness.NewProposals(db).ApproveAsync(proposal.Id, default);

        approved.Refusal.ShouldBeNull();
        _harness.Vectors.CountFor("a.md", "set-2").ShouldBe(0);
        _harness.Vectors.CountFor("a.md", "set-1").ShouldBeGreaterThan(0);
        await using var fresh = _harness.NewContext();
        (await fresh.ChunkSets.Select(s => s.Id).ToListAsync()).ShouldBe(["set-1"]);
        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Approved);
    }

    [Fact]
    public async Task Approving_a_corpus_removes_everything_under_it_and_the_record_of_it_stays()
    {
        await IndexAsync();
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.Corpus, null);

        var approved = await _harness.NewProposals(db).ApproveAsync(proposal.Id, default);

        approved.Refusal.ShouldBeNull();
        foreach (var set in new[] { "set-1", "set-2" })
            _harness.Vectors.CountFor("a.md", set).ShouldBe(0);
        await using var fresh = _harness.NewContext();
        (await fresh.Corpora.CountAsync()).ShouldBe(0);
        (await fresh.Sources.CountAsync()).ShouldBe(0);
        var kept = await fresh.Proposals.AsNoTracking().SingleAsync();
        (kept.Status, kept.CorpusName).ShouldBe((ProposalStatus.Approved, "notes"));
    }

    [Fact]
    public async Task A_removal_that_fails_leaves_the_proposal_waiting_and_the_target_in_place()
    {
        await IndexAsync();
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.Source, "notes");

        // The decision and the removal are one save, and the removal starts with the vectors. A store
        // that cannot be reached must not leave a source described as removed that is still there.
        _harness.Vectors.DeletesThrow = true;
        await Should.ThrowAsync<InvalidOperationException>(() => _harness.NewProposals(db).ApproveAsync(proposal.Id, default));
        _harness.Vectors.DeletesThrow = false;

        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Pending);
        await using var fresh = _harness.NewContext();
        (await fresh.Sources.AnyAsync(s => s.Id == "source-1")).ShouldBeTrue();

        // And it can be approved once the store is back.
        await using var retry = _harness.NewContext();
        (await _harness.NewProposals(retry).ApproveAsync(proposal.Id, default)).Refusal.ShouldBeNull();
        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Approved);
    }

    [Fact]
    public async Task A_chunk_set_a_job_is_working_on_stays_waiting_until_the_job_has_finished()
    {
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.ChunkSet, "alt-1");
        db.Jobs.Add(new IndexJob
        {
            Id = "job-1", CorpusId = IndexingHarness.CorpusId, ChunkSetId = "set-2",
            Kind = JobKind.Refresh, State = JobState.Queued, QueuedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var refused = await _harness.NewProposals(db).ApproveAsync(proposal.Id, default);

        refused.Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Pending);

        // Marked decided on the tracked row before the removal ran, and put back when it was refused.
        // Any later save on the same context must not write the approval that did not happen.
        await db.SaveChangesAsync();
        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Pending, "a refused approval is not left on the row");

        await db.Jobs.ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobState.Succeeded));
        await using var later = _harness.NewContext();
        (await _harness.NewProposals(later).ApproveAsync(proposal.Id, default)).Refusal.ShouldBeNull();
        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Approved);
    }

    [Fact]
    public async Task A_target_that_is_already_gone_fails_the_proposal_with_the_reason()
    {
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.Source, "docs");

        // Removed in the UI between the ask and the decision.
        await using (var admin = _harness.NewContext())
            (await _harness.NewConfiguration(admin).RemoveSourceAsync(await CorpusAsync(admin), "source-2", default)).Refusal.ShouldBeNull();

        var decided = await _harness.NewProposals(db).ApproveAsync(proposal.Id, default);

        decided.Refusal.ShouldBeNull("it is recorded, not refused");
        decided.Value!.Status.ShouldBe("failed");
        decided.Value.Error.ShouldNotBeNull().ShouldContain("source-2");
        var stored = await StoredAsync(proposal.Id);
        (stored.Status, stored.DecidedUtc is not null).ShouldBe((ProposalStatus.Failed, true));
    }

    [Fact]
    public async Task A_corpus_that_is_gone_fails_the_proposal_for_what_was_in_it()
    {
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.Source, "docs");
        await using (var admin = _harness.NewContext())
            await _harness.NewConfiguration(admin).RemoveCorpusAsync(await CorpusAsync(admin), default);

        var decided = await _harness.NewProposals(db).ApproveAsync(proposal.Id, default);

        decided.Value!.Status.ShouldBe("failed");
        decided.Value.Error.ShouldNotBeNull().ShouldContain("corpus");
    }

    [Fact]
    public async Task Rejecting_leaves_the_target_and_a_decision_cannot_be_made_twice()
    {
        await IndexAsync();
        await using var db = _harness.NewContext();
        var proposal = await AskAsync(db, ProposalKind.Source, "notes");
        var svc = _harness.NewProposals(db);

        var rejected = await svc.RejectAsync(proposal.Id, default);

        rejected.Value!.Status.ShouldBe("rejected");
        _harness.Vectors.CountFor("a.md", "set-1").ShouldBeGreaterThan(0, "rejecting removes nothing");
        (await svc.ApproveAsync(proposal.Id, default)).Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        (await svc.RejectAsync(proposal.Id, default)).Refusal.ShouldNotBeNull().Status.ShouldBe(409);
        (await svc.ApproveAsync("no-such", default)).Refusal.ShouldNotBeNull().Status.ShouldBe(404);
        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Rejected);
    }

    [Fact]
    public async Task Two_decisions_made_from_the_same_read_cannot_both_stand()
    {
        await using var a = _harness.NewContext();
        await using var b = _harness.NewContext();
        var proposal = await AskAsync(a, ProposalKind.Source, "docs");
        _ = await b.Proposals.SingleAsync();    // read while it was waiting, as a second click's request would

        (await _harness.NewProposals(a).ApproveAsync(proposal.Id, default)).Refusal.ShouldBeNull();
        var late = await _harness.NewProposals(b).RejectAsync(proposal.Id, default);

        var refusal = late.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(409);
        refusal.Detail.ShouldContain("someone else");
        (await StoredAsync(proposal.Id)).Status.ShouldBe(ProposalStatus.Approved, "the first decision stands");
    }

    [Fact]
    public async Task A_document_is_detached_on_approval_and_its_blob_stays()
    {
        await using var uploads = await IndexingHarness.StartAsync();
        await uploads.SeedCorpusAsync(SourceKind.Upload);
        await using var db = uploads.NewContext();
        var documents = uploads.NewDocumentService(db);
        var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
            .FirstAsync(c => c.Id == IndexingHarness.CorpusId);
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(IndexingHarness.Prose("uploads")));
        var stored = await documents.StoreAsync(bytes, "paper.md");
        var file = await documents.AttachAsync(corpus, stored.Sha256, "paper.md");
        await uploads.RunIndexAsync();
        uploads.Vectors.CountFor("paper.md").ShouldBeGreaterThan(0, "the premise");

        var svc = uploads.NewProposals(db);
        var asked = await svc.ProposeAsync(Key(), corpus, ProposalKind.Document, "paper.md", "obsolete", default);
        asked.Refusal.ShouldBeNull();
        asked.Value!.Proposal.TargetId.ShouldBe(file.Id);

        var approved = await svc.ApproveAsync(asked.Value.Proposal.Id, default);

        approved.Refusal.ShouldBeNull();
        uploads.Vectors.CountFor("paper.md").ShouldBe(0);
        await using var fresh = uploads.NewContext();
        (await fresh.Files.AnyAsync(f => f.Id == file.Id)).ShouldBeFalse("detached from the corpus");
        (await fresh.Blobs.AnyAsync(b => b.Sha256 == stored.Sha256)).ShouldBeTrue("another corpus may still hold the document");
        (await fresh.Proposals.AsNoTracking().SingleAsync()).Status.ShouldBe(ProposalStatus.Approved);
    }

    [Fact]
    public async Task A_document_that_is_not_attached_is_named_back_with_the_ones_that_are()
    {
        await using var uploads = await IndexingHarness.StartAsync();
        await uploads.SeedCorpusAsync(SourceKind.Upload);
        await using var db = uploads.NewContext();
        var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
            .FirstAsync(c => c.Id == IndexingHarness.CorpusId);
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("text"));
        var stored = await uploads.NewDocumentService(db).StoreAsync(bytes, "kept.md");
        await uploads.NewDocumentService(db).AttachAsync(corpus, stored.Sha256, "kept.md");

        var refused = await uploads.NewProposals(db).ProposeAsync(Key(), corpus, ProposalKind.Document, "other.md", "x", default);

        var refusal = refused.Refusal.ShouldNotBeNull();
        refusal.Status.ShouldBe(404);
        refusal.Detail.ShouldContain("kept.md");
    }

    // ---- reading ------------------------------------------------------------------------------

    [Fact]
    public async Task Waiting_proposals_are_listed_oldest_first_with_what_each_would_take()
    {
        await IndexAsync();
        await using var db = _harness.NewContext();
        var first = await AskAsync(db, ProposalKind.Source, "notes");
        await Task.Delay(5);
        var second = await AskAsync(db, ProposalKind.ChunkSet, "alt-1");

        var waiting = await _harness.NewProposals(db).ListAsync(decided: false, 50, default);

        waiting.Select(w => w.Id).ShouldBe([first.Id, second.Id]);
        var source = waiting[0];
        source.Kind.ShouldBe("source");
        source.KeyName.ShouldBe("research-agent");
        source.Gone.ShouldBeFalse();
        var facts = source.Facts.ShouldNotBeNull();
        facts.Files.ShouldBe(1);
        facts.ChunkSets.ShouldBe(2);
        facts.Chunks.ShouldBe(await db.FileChunkStates.Where(s => s.File!.SourceId == "source-1").SumAsync(s => s.ChunkCount));
        facts.Chunks.ShouldBeGreaterThan(0);
        waiting[1].Facts.ShouldNotBeNull().Chunks.ShouldBe(
            await db.FileChunkStates.Where(s => s.ChunkSetId == "set-2").SumAsync(s => s.ChunkCount));
    }

    [Fact]
    public async Task A_proposal_whose_target_has_gone_is_listed_as_gone_and_a_blocked_one_says_why()
    {
        await using var db = _harness.NewContext();
        var gone = await AskAsync(db, ProposalKind.Source, "docs");
        var set = await AskAsync(db, ProposalKind.ChunkSet, "alt-1");

        // The source goes in the UI, and the set becomes the default.
        await using (var admin = _harness.NewContext())
            await _harness.NewConfiguration(admin).RemoveSourceAsync(await CorpusAsync(admin), "source-2", default);
        await db.ChunkSets.Where(s => s.Id == "set-1").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, false));
        await db.ChunkSets.Where(s => s.Id == "set-2").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, true));

        var listed = (await _harness.NewProposals(db).ListAsync(decided: false, 50, default)).ToDictionary(v => v.Id);

        listed[gone.Id].Gone.ShouldBeTrue();
        listed[gone.Id].Facts.ShouldBeNull();
        listed[set.Id].Facts.ShouldNotBeNull().Blocker.ShouldBe("it is the default chunk set");
    }

    [Fact]
    public async Task A_page_of_requests_gets_each_ones_own_figures()
    {
        // The first source holds more than the second, so figures read for the wrong one would differ.
        await _harness.WriteFileAsync("a.md", Long("alpha"), source: 0);
        await _harness.WriteFileAsync("b.md", IndexingHarness.Prose("beta"), source: 1);
        await _harness.RunIndexAsync(JobKind.Full);
        await using var db = _harness.NewContext();
        var notes = await AskAsync(db, ProposalKind.Source, "notes");
        var docs = await AskAsync(db, ProposalKind.Source, "docs");
        var set = await AskAsync(db, ProposalKind.ChunkSet, "alt-1");
        var corpus = await AskAsync(db, ProposalKind.Corpus, null);

        var notesChunks = await db.FileChunkStates.Where(s => s.File!.SourceId == "source-1").SumAsync(s => s.ChunkCount);
        var docsChunks = await db.FileChunkStates.Where(s => s.File!.SourceId == "source-2").SumAsync(s => s.ChunkCount);
        var setChunks = await db.FileChunkStates.Where(s => s.ChunkSetId == "set-2").SumAsync(s => s.ChunkCount);
        var setFiles = await db.FileChunkStates.CountAsync(s => s.ChunkSetId == "set-2" && s.ChunkCount > 0);
        notesChunks.ShouldBeGreaterThan(docsChunks, "the premise: the two are told apart by their size");

        var listed = (await _harness.NewProposals(db).ListAsync(decided: false, 50, default)).ToDictionary(v => v.Id);

        listed[notes.Id].Facts.ShouldBe(new ProposalFacts(1, 1, notesChunks, 2, null));
        listed[docs.Id].Facts.ShouldBe(new ProposalFacts(1, 1, docsChunks, 2, null));
        listed[set.Id].Facts.ShouldBe(new ProposalFacts(0, setFiles, setChunks, 1, null));
        listed[corpus.Id].Facts.ShouldBe(new ProposalFacts(2, 2, notesChunks + docsChunks, 2, null));
        listed.Values.ShouldAllBe(v => !v.Gone);
    }

    [Fact]
    public async Task A_document_request_is_listed_with_the_chunks_of_that_document()
    {
        await using var uploads = await IndexingHarness.StartAsync();
        await uploads.SeedCorpusAsync(SourceKind.Upload);
        await using var db = uploads.NewContext();
        var documents = uploads.NewDocumentService(db);
        var corpus = await db.Corpora.Include(c => c.Sources).Include(c => c.ChunkSets)
            .FirstAsync(c => c.Id == IndexingHarness.CorpusId);

        async Task<string> AttachAsync(string name, string text)
        {
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var stored = await documents.StoreAsync(bytes, name);
            return (await documents.AttachAsync(corpus, stored.Sha256, name)).Id;
        }

        var bigId = await AttachAsync("big.md", Long("one"));
        var smallId = await AttachAsync("small.md", IndexingHarness.Prose("three"));
        await uploads.RunIndexAsync();
        var bigChunks = await db.FileChunkStates.Where(s => s.FileId == bigId).SumAsync(s => s.ChunkCount);
        var smallChunks = await db.FileChunkStates.Where(s => s.FileId == smallId).SumAsync(s => s.ChunkCount);
        bigChunks.ShouldBeGreaterThan(smallChunks, "the premise: the two are told apart by their size");

        var svc = uploads.NewProposals(db);
        var askedBig = (await svc.ProposeAsync(Key(), corpus, ProposalKind.Document, "big.md", "obsolete", default)).Value!.Proposal;
        var askedSmall = (await svc.ProposeAsync(Key(), corpus, ProposalKind.Document, "small.md", "obsolete", default)).Value!.Proposal;

        var listed = (await svc.ListAsync(decided: false, 50, default)).ToDictionary(v => v.Id);

        var sets = corpus.ChunkSets.Count;
        listed[askedBig.Id].Facts.ShouldBe(new ProposalFacts(0, 1, bigChunks, sets, null));
        listed[askedSmall.Id].Facts.ShouldBe(new ProposalFacts(0, 1, smallChunks, sets, null));
    }

    [Fact]
    public async Task A_second_corpus_is_counted_by_itself_and_a_target_in_another_corpus_is_gone()
    {
        await IndexAsync();
        await using var db = _harness.NewContext();
        db.Corpora.Add(new Corpus { Id = "corpus-2", Name = "papers", State = CorpusState.Ready, CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var mine = await AskAsync(db, ProposalKind.Corpus, null);

        // Written by hand: the service never records a target outside the corpus it was asked about.
        Proposal Raw(string id, ProposalKind kind, string targetId) => new()
        {
            Id = id, CreatedUtc = DateTime.UtcNow, TokenId = "k1", TokenName = "research-agent",
            CorpusId = "corpus-2", CorpusName = "papers", Kind = kind, TargetId = targetId, TargetLabel = targetId,
            Reason = "x", Status = ProposalStatus.Pending,
        };
        db.Proposals.AddRange(
            Raw("p-empty", ProposalKind.Corpus, "corpus-2"),
            Raw("p-source", ProposalKind.Source, "source-1"),
            Raw("p-set", ProposalKind.ChunkSet, "set-2"));
        await db.SaveChangesAsync();

        var listed = (await _harness.NewProposals(db).ListAsync(decided: false, 50, default)).ToDictionary(v => v.Id);

        listed[mine.Id].Facts.ShouldNotBeNull().Sources.ShouldBe(2);
        listed["p-empty"].Facts.ShouldBe(new ProposalFacts(0, 0, 0, 0, null), "nothing of the other corpus's is counted");
        foreach (var strayed in new[] { "p-source", "p-set" })
            (listed[strayed].Gone, listed[strayed].Facts).ShouldBe((true, null), $"{strayed} names a target in a different corpus");
    }

    [Fact]
    public async Task A_chunk_set_that_has_become_the_only_one_or_has_a_job_on_it_says_so_in_the_listing()
    {
        await using var db = _harness.NewContext();
        var set = await AskAsync(db, ProposalKind.ChunkSet, "alt-1");
        var svc = _harness.NewProposals(db);

        db.Jobs.Add(new IndexJob
        {
            Id = "job-1", CorpusId = IndexingHarness.CorpusId, ChunkSetId = "set-2",
            Kind = JobKind.Refresh, State = JobState.Queued, QueuedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        (await svc.ListAsync(decided: false, 50, default)).Single().Facts.ShouldNotBeNull().Blocker.ShouldBe("a job is working on it");

        await db.Jobs.ExecuteUpdateAsync(u => u.SetProperty(j => j.State, JobState.Succeeded));
        (await svc.ListAsync(decided: false, 50, default)).Single().Facts.ShouldNotBeNull().Blocker.ShouldBeNull();

        // The other set goes, after the default has moved off it, and this one is all that is left.
        await db.ChunkSets.Where(s => s.Id == "set-1").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, false));
        await db.ChunkSets.Where(s => s.Id == "set-2").ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefault, true));
        await using (var admin = _harness.NewContext())
            (await _harness.NewConfiguration(admin).RemoveChunkSetAsync(await CorpusAsync(admin), "default", default)).Refusal.ShouldBeNull();
        (await svc.ListAsync(decided: false, 50, default)).Single(v => v.Id == set.Id)
            .Facts.ShouldNotBeNull().Blocker.ShouldBe("it is the only chunk set");
    }

    [Fact]
    public async Task Decided_proposals_are_listed_newest_first_without_facts_and_the_count_is_what_is_waiting()
    {
        await using var db = _harness.NewContext();
        var a = await AskAsync(db, ProposalKind.Source, "docs");
        var b = await AskAsync(db, ProposalKind.ChunkSet, "alt-1");
        var c = await AskAsync(db, ProposalKind.Source, "notes");
        var svc = _harness.NewProposals(db);
        (await svc.PendingCountAsync(default)).ShouldBe(3);

        await svc.RejectAsync(a.Id, default);
        await Task.Delay(5);
        await svc.RejectAsync(b.Id, default);

        (await svc.PendingCountAsync(default)).ShouldBe(1);
        var decided = await svc.ListAsync(decided: true, 50, default);
        decided.Select(d => d.Id).ShouldBe([b.Id, a.Id]);
        decided.ShouldAllBe(d => d.Facts == null && d.DecidedUtc != null);
        (await svc.ListAsync(decided: false, 50, default)).Select(w => w.Id).ShouldBe([c.Id]);
    }
}
