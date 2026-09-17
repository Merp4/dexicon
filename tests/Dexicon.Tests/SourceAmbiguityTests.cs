using Dexicon.Core.Search;
using Dexicon.Core.Vectors;

namespace Dexicon.Tests;

/// <summary>
/// A file path is not a unique name for a file.
///
/// Chunks record `file_path` relative to their SOURCE root, not to the corpus. That is
/// invisible with one source and wrong with two: this repository's own book corpus has
/// sources `orly/AI` and `orly/Philosophy`, and BOTH contain "Logic For Dummies, 2nd
/// Edition.pdf" and "Intensional First-Order Logic.pdf". Two different files, one path,
/// one corpus, one chunk set.
///
/// Everything downstream assumed paths were unique, and `source_id` — written into every
/// point's payload and indexed as a keyword since chunk sets landed — was never read.
/// </summary>
public class SourceAmbiguityTests
{
    private static SearchHit Chunk(string sourceId, int index, int startLine, string content) =>
        new()
        {
            CorpusId = "corpus", SourceId = sourceId,
            FilePath = "Logic For Dummies, 2nd Edition.pdf",
            StartLine = startLine, EndLine = startLine + 9,
            ChunkIndex = index, Content = content, Score = 0f,
        };

    [Fact]
    public void Deleting_one_source_file_does_not_take_the_other_sources_copy()
    {
        // The filter decides which points die. Without source_id, refreshing the AI copy
        // deletes the Philosophy copy too — and an incremental refresh only rewrites the
        // file that changed, so the other silently vanishes until a full reindex. Lost
        // content, no error, no log line.
        var scoped = QdrantVectorStore.FileChunksFilter("set-1", "source-ai", "Logic For Dummies.pdf");

        var keys = scoped.Must
            .Select(c => c.Field?.Key)
            .Where(k => k is not null)
            .ToList();

        keys.ShouldContain("chunk_set_id");
        keys.ShouldContain("file_path");
        keys.ShouldContain("source_id");
    }

    [Fact]
    public void Reading_a_file_can_still_span_every_source_when_that_is_asked_for()
    {
        // The read path keeps the old behaviour available; only the DELETE is unconditional.
        var unscoped = QdrantVectorStore.FileChunksFilter("set-1", null, "Logic For Dummies.pdf");

        unscoped.Must.Select(c => c.Field?.Key).ShouldNotContain("source_id");
    }

    [Fact]
    public void Two_books_with_one_path_group_into_two_sources()
    {
        // The property get_context relies on to notice the problem at all.
        var chunks = new[]
        {
            Chunk("source-ai", 0, 1, "Propositional logic, as an AI text presents it."),
            Chunk("source-philosophy", 0, 1, "Propositional logic, as a philosophy text presents it."),
            Chunk("source-ai", 1, 11, "Predicate calculus follows."),
        };

        var bySource = chunks.GroupBy(c => c.SourceId ?? "").ToList();

        bySource.Count.ShouldBe(2);
        // Interleaved by chunk index — which is exactly how they used to be stitched.
        chunks.OrderBy(c => c.ChunkIndex).Select(c => c.SourceId).ShouldBe(
            ["source-ai", "source-philosophy", "source-ai"]);
    }

    [Fact]
    public void Stitching_one_source_reads_as_one_document()
    {
        // The fix, stated as the property that matters: the passage a model quotes must
        // come from a single file. Mixed, it reads as a coherent argument that no book
        // makes, with line numbers inviting it to be quoted.
        var chunks = new[]
        {
            Chunk("source-ai", 0, 1, "Propositional logic, as an AI text presents it."),
            Chunk("source-philosophy", 0, 1, "Propositional logic, as a philosophy text presents it."),
            Chunk("source-ai", 1, 11, "Predicate calculus follows."),
        };

        var chosen = chunks
            .GroupBy(c => c.SourceId ?? "")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .First()
            .OrderBy(c => c.ChunkIndex)
            .ToList();

        chosen.Select(c => c.SourceId).Distinct().Count().ShouldBe(1);
        chosen.Count.ShouldBe(2);
        chosen[0].Content.ShouldContain("AI text");
        chosen.ShouldAllBe(c => c.SourceId == "source-ai");
    }

    [Fact]
    public void The_choice_is_deterministic_when_sources_are_the_same_size()
    {
        // Two copies of equal length must not alternate between calls: an agent that reads
        // a passage twice and gets two different books has no way to notice.
        var a = new[] { Chunk("source-b", 0, 1, "b"), Chunk("source-a", 0, 1, "a") };

        string Pick(IEnumerable<SearchHit> hits) => hits
            .GroupBy(c => c.SourceId ?? "")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;

        Pick(a).ShouldBe("source-a");
        Pick(a.Reverse()).ShouldBe("source-a");
    }
}
