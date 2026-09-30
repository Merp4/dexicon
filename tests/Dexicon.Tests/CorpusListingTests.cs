using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Indexing;
using Dexicon.Mcp;

namespace Dexicon.Tests;

/// <summary>
/// `list_corpora` as an agent reads it.
///
/// An agent calls this to answer exactly one question: which of these should I search?
/// Everything else in the output is operational detail it cannot act on. So the tests here
/// are about order and about warnings rather than content: the facts were all present
/// before, arranged so the answer came last.
/// </summary>
public class CorpusListingTests
{
    private static CorpusSummary Corpus(
        string name = "books",
        string? description = "19 architecture books, PDF and EPUB",
        int files = 19,
        int chunks = 2531,
        string state = "ready",
        int failed = 0,
        IReadOnlyList<SourceSummary>? sources = null,
        IReadOnlyList<ChunkSetSummary>? sets = null) =>
        new(
            Id: "01ABC", Name: name, Description: description, State: state,
            CreatedUtc: DateTime.UnixEpoch, LastIndexedUtc: DateTime.UnixEpoch,
            SourceCount: 1, FileCount: files, ChunkCount: chunks,
            SkippedCount: 0, FailedCount: failed, PendingCount: 0,
            Sources: sources ?? [],
            ChunkSets: sets ?? [Set("default", chunks)]);

    private static ChunkSetSummary Set(string name, int chunks, bool isDefault = true) =>
        new(Id: $"set-{name}", Name: name, Description: null, EmbeddingProvider: "ollama",
            EmbeddingModel: "embeddinggemma", EmbeddingDimensions: 768,
            CollectionName: $"dexicon_{name}", ChunkSize: 2065, ChunkOverlap: 258,
            BoundaryMode: "blank-line", CustomBoundaryPattern: null,
            UnitAware: true, SentenceAware: true, HeadingContext: true,
            IsDefault: isDefault, State: "ready", FileCount: 19, ChunkCount: chunks,
            PendingCount: 0, FailedCount: 0,
            CreatedUtc: DateTime.UnixEpoch, LastIndexedUtc: DateTime.UnixEpoch);

    [Fact]
    public void The_description_comes_before_the_machinery()
    {
        // It is the only line that answers "should I search here". It used to sit under
        // the state, the counts, the chunk sets, the embedding dimensions and the overlap.
        var text = DexiconTools.RenderCorpus(Corpus());

        var description = text.IndexOf("19 architecture books", StringComparison.Ordinal);
        var state = text.IndexOf("state:", StringComparison.Ordinal);
        var model = text.IndexOf("embeddinggemma", StringComparison.Ordinal);

        description.ShouldBeGreaterThan(0);
        description.ShouldBeLessThan(state);
        description.ShouldBeLessThan(model);
    }

    [Fact]
    public void A_corpus_with_no_description_says_so_rather_than_saying_nothing()
    {
        // Silence reads as "no information"; an agent cannot tell an undescribed corpus
        // from one that is genuinely unsuitable. It also nudges the human who reads it.
        var text = DexiconTools.RenderCorpus(Corpus(description: null));

        text.ShouldContain("no description");
    }

    [Fact]
    public void An_empty_corpus_is_marked_as_unable_to_answer()
    {
        // Listed identically to a full one, an empty corpus reads as a reasonable place to
        // look, and the agent spends a whole call finding out otherwise.
        var text = DexiconTools.RenderCorpus(
            Corpus(name: "corp2", description: "test corpus", files: 0, chunks: 0,
                   sets: [Set("default", 0)]));

        text.ShouldContain("NOT SEARCHABLE");
    }

    [Fact]
    public void A_corpus_still_indexing_is_told_apart_from_one_that_is_simply_empty()
    {
        // Different problems with different responses: wait and retry, versus never bother.
        var indexing = DexiconTools.RenderCorpus(
            Corpus(files: 0, chunks: 0, state: "indexing", sets: [Set("default", 0)]));

        indexing.ShouldContain("NOT SEARCHABLE YET");
        indexing.ShouldContain("index_status");
    }

    [Fact]
    public void A_corpus_with_content_carries_no_warning()
    {
        DexiconTools.RenderCorpus(Corpus()).ShouldNotContain("NOT SEARCHABLE");
    }

    [Fact]
    public void Every_chunk_set_is_named_with_the_address_that_reaches_it()
    {
        // A set that is not the default is only reachable as `corpus:set`. An agent told
        // only the corpus name cannot get to it, so the address has to be in the listing.
        var text = DexiconTools.RenderCorpus(
            Corpus(sets: [Set("default", 136), Set("gemma", 136, isDefault: false)]));

        text.ShouldContain("books:default");
        text.ShouldContain("books:gemma");
        text.ShouldContain("corpus:set");
    }

    [Fact]
    public void Failed_files_are_reported_because_a_gap_in_a_corpus_is_invisible_otherwise()
    {
        // Six of nineteen books failed to extract once, and search simply returned less.
        // Nothing in a result set says "and there were six books I could not read".
        DexiconTools.RenderCorpus(Corpus(failed: 6)).ShouldContain("6 files failed");
    }

    /// <summary>
    /// The failure line counts in the corpus's own unit, like the line above it.
    ///
    /// The count and the unit were fixed together everywhere else and this line kept
    /// "file(s)", so a history corpus reported "8 file(s) failed" directly beneath "201
    /// commits" — an agent reading both has to decide which one is lying.
    /// </summary>
    [Fact]
    public void A_history_corpus_reports_commits_failed_not_files()
    {
        var sources = new[]
        {
            new SourceSummary("s", nameof(SourceKind.GitHistory).ToLowerInvariant(), "repo",
                true, 1024, [], []),
        };

        var rendered = DexiconTools.RenderCorpus(Corpus(failed: 8, sources: sources));

        rendered.ShouldContain("8 commits failed");
        rendered.ShouldNotContain("file");
    }

    /// <summary>
    /// The failure line sends an agent to a tool it can call. It ended "see the UI for why",
    /// which an agent cannot do.
    /// </summary>
    [Fact]
    public void Failed_files_point_to_where_an_agent_can_read_why()
    {
        var text = DexiconTools.RenderCorpus(Corpus(failed: 6));

        text.ShouldContain("index_status(\"books\")");
        text.ShouldNotContain("see the UI");
    }

    [Fact]
    public void The_suggested_call_survives_a_name_with_a_quote_in_it()
    {
        // Only a blank name is refused, and `a"b` rendered as index_status("a"b").
        DexiconTools.RenderCorpus(Corpus(name: "a\"b", failed: 1)).ShouldContain("index_status(\"a\\\"b\")");
    }
}

/// <summary>
/// `index_status` for one corpus: what each source reads, and the files it left out and why.
/// It stopped at counts, so an agent asked why a file is not found could only send the user
/// to the UI.
/// </summary>
public class CorpusDiagnosisTests
{
    private static SourceSummary Files(string root, int files = 12, bool gitignore = true, int cap = 262_144,
        string[]? include = null, string[]? exclude = null, bool ownExclude = true) =>
        new("f-" + root, "workspace", root, gitignore, cap, include ?? [], exclude ?? [], files,
            OwnUseGitignore: gitignore, OwnMaxFileBytes: cap,
            OwnExcludeGlobs: ownExclude ? exclude : null);

    [Fact]
    public void A_file_source_says_how_it_is_filtered_and_which_filters_are_the_corpus_s()
    {
        var text = DexiconTools.RenderSources(
            [Files("docs", gitignore: false, cap: 2 * 1024 * 1024, include: ["**/*.md"], exclude: ["**/draft/**"], ownExclude: false)],
            new CorpusDefaults(null, null, null, ["**/draft/**"]));

        text.ShouldContain("files under docs: 12 files found; .gitignore ignored; .dexiconignore respected; code and text up to 2 MB; only **/*.md; not **/draft/**");
        text.ShouldContain("(from the corpus defaults: not)");
    }

    [Fact]
    public void An_empty_list_from_the_corpus_is_not_credited_for_a_filter_that_is_not_shown()
    {
        var source = new SourceSummary("f", "workspace", "docs", true, 262_144, [], [], 3);

        DexiconTools.RenderSources([source], new CorpusDefaults(null, null, [], []))
            .ShouldNotContain("from the corpus defaults");
    }

    [Fact]
    public void A_history_source_s_paths_from_the_corpus_are_named_as_the_corpus_s()
    {
        var history = new SourceSummary("h", "githistory", "repo", true, 262_144, ["src/**"], [], 10,
            Git: new Dexicon.Core.Indexing.GitHistoryOptions());

        DexiconTools.RenderSources([history], new CorpusDefaults(null, null, ["src/**"], null))
            .ShouldContain("only paths src/** (from the corpus defaults)");
        DexiconTools.RenderSources([history with { OwnIncludeGlobs = ["src/**"] }], new CorpusDefaults(null, null, ["src/**"], null))
            .ShouldNotContain("from the corpus defaults");
    }

    [Fact]
    public void One_file_is_one_file()
    {
        // Found live, on a source holding one book: "1 files".
        DexiconTools.RenderSources([Files("books/manuals", files: 1)], null).ShouldContain("files under books/manuals: 1 file found;");
    }

    [Fact]
    public void A_source_that_sets_its_own_filters_names_no_corpus_default()
    {
        var text = DexiconTools.RenderSources([Files("docs", exclude: ["x"])], new CorpusDefaults(null, null, null, ["y"]));

        text.ShouldNotContain("from the corpus defaults");
        text.ShouldContain("files under docs: 12 files found; .gitignore respected; .dexiconignore respected; code and text up to 256 KB; not x");
    }

    [Fact]
    public void A_history_source_says_what_it_follows_how_current_it_is_and_what_each_commit_holds()
    {
        var history = new SourceSummary("h", "githistory", "", true, 262_144, ["src/**"], [], 256,
            Git: new Dexicon.Core.Indexing.GitHistoryOptions { Ref = "refs/heads/main", IncludeDiff = true, MaxCommits = 500, KeepIndexed = true },
            NewestCommit: new CommitSummary("24664acfa47e5ff3199457a2f8c43ed6219a033f", new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc)),
            Tracking: new Dexicon.Core.Indexing.GitTracking("refs/heads/main", "refs/heads/main",
                new Dexicon.Core.Indexing.GitUpstream("refs/remotes/origin/main", "origin/main", 0, 52, false),
                null, DateTime.UtcNow));

        var text = DexiconTools.RenderSources([history], null);

        // No fetch time was recorded, so none is claimed.
        text.ShouldContain("commit history of the workspace root: 256 commits, follows refs/heads/main (52 behind origin/main);");
        text.ShouldContain("holds message, stat, diff");
        text.ShouldContain("newest 500, kept once indexed");
        text.ShouldContain("only paths src/**");
        text.ShouldContain("newest 24664ac, 2026-09-26");
        // The file settings mean nothing to a commit, and saying them would claim it obeys them.
        text.ShouldNotContain(".gitignore");
    }

    /// <summary>
    /// Up to date is said, so it reads differently from a branch with no tracking recorded,
    /// and the fetch is dated only when its time is known.
    /// </summary>
    [Theory]
    [InlineData(0, 0, false, null, "up to date with origin/main")]
    [InlineData(0, 3, false, null, "3 ahead of origin/main")]
    [InlineData(52, 3, false, null, "52 behind and 3 ahead of origin/main")]
    [InlineData(null, null, true, null, "its upstream origin/main is gone")]
    [InlineData(52, 0, false, "2026-09-27T08:15:00Z", "52 behind origin/main as of the fetch at 2026-09-27 08:15 UTC")]
    public void The_distance_from_upstream_says_what_was_observed(int? behind, int? ahead, bool gone, string? fetched, string expected)
    {
        var tracking = new Dexicon.Core.Indexing.GitTracking("refs/heads/main", "refs/heads/main",
            new Dexicon.Core.Indexing.GitUpstream("refs/remotes/origin/main", "origin/main", ahead, behind, gone),
            fetched is null ? null : DateTime.Parse(fetched, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTime.UtcNow);

        DexiconTools.Distance(tracking).ShouldBe(expected);
    }

    [Fact]
    public void Nothing_is_said_where_nothing_was_observed()
    {
        DexiconTools.Distance(new Dexicon.Core.Indexing.GitTracking("HEAD", null, null, null, DateTime.UtcNow)).ShouldBeNull();
        DexiconTools.Distance(new Dexicon.Core.Indexing.GitTracking("refs/heads/main", "refs/heads/main",
            new Dexicon.Core.Indexing.GitUpstream("refs/remotes/origin/main", "origin/main", null, null, false),
            null, DateTime.UtcNow)).ShouldBeNull();
    }

    [Fact]
    public void Uploaded_documents_are_not_described_as_a_walk()
    {
        var upload = new SourceSummary("u", "upload", null, false, int.MaxValue, [], [], 3);

        var text = DexiconTools.RenderSources([upload], null);

        text.ShouldContain("uploaded documents: 3");
        text.ShouldNotContain("GB");
    }

    [Fact]
    public void Problem_files_are_listed_with_their_reason_on_one_line_and_the_rest_counted()
    {
        var reason = "Extraction failed:\n" + new string('x', 400);
        var text = DexiconTools.RenderProblemFiles(
        [
            new DexiconTools.ProblemFiles("failed", 2, [("books/a.pdf", reason), ("books/b.pdf", null)]),
            new DexiconTools.ProblemFiles("skipped", 37, [("books/c.bin", "binary content")]),
        ]);

        text.ShouldContain("  failed: 2\n");
        text.ShouldContain("    books/a.pdf — Extraction failed: xxx");
        text.ShouldContain("...\n");
        text.Split('\n').ShouldAllBe(line => line.Length <= 200, "a reason is cut to one short line");
        text.ShouldContain("    books/b.pdf\n");
        text.ShouldContain("  skipped: 37\n    books/c.bin — binary content\n    ... and 36 more\n");
    }

    /// <summary>
    /// A file name can hold a line break on Linux. Printed as it is, it ended its entry early
    /// and began a line that could read as a status of its own.
    /// </summary>
    [Fact]
    public void A_path_holding_a_line_break_stays_on_its_own_line()
    {
        var problems = DexiconTools.RenderProblemFiles(
            [new DexiconTools.ProblemFiles("failed", 1, [("notes/a\n  skipped: 0\r\nb.md", "unreadable")])]);
        var sources = DexiconTools.RenderSources([Files("notes/x\ny")], null);
        var gaps = DexiconTools.RenderCoverage([new SourceCoverage.Gap("notes\ny", ["c\nd.md"])]);

        problems.ShouldBe("  failed: 1\n    notes/a   skipped: 0 b.md — unreadable\n");
        sources.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2, "the heading and one source");
        gaps.ShouldContain("in notes y are covered");
        gaps.ShouldContain("    c d.md\n");
    }
}
