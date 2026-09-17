using Dexicon.Api;
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
        IReadOnlyList<ChunkSetSummary>? sets = null) =>
        new(
            Id: "01ABC", Name: name, Description: description, TenantId: "default",
            Owned: true, Visibility: "private", State: state,
            CreatedUtc: DateTime.UnixEpoch, LastIndexedUtc: DateTime.UnixEpoch,
            SourceCount: 1, FileCount: files, ChunkCount: chunks,
            SkippedCount: 0, FailedCount: failed,
            Sources: [],
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
        DexiconTools.RenderCorpus(Corpus(failed: 6)).ShouldContain("6 file(s) failed");
    }
}
