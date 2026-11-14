using System.Text;
using System.Text.Json;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A transcript on a workspace source is a document, and any other <c>.jsonl</c> file is the text it was.
///
/// Mounting <c>~/.claude/projects</c> without this recorded 1,054 of 1,065 transcripts as over the
/// 256 KB text cap (the median file is 1.8 MB). With the cap raised, a file has no blank lines and its
/// records are long lines of tool output, so the chunks are JSON. The walk takes a transcript under the
/// document cap and the extractor writes the conversation; a dataset or a log that happens to share the
/// extension keeps the text cap, the binary check and the plain-text read.
/// </summary>
public sealed class TranscriptIndexingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"transcript-walk-{Guid.NewGuid():N}");

    public TranscriptIndexingTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A transcript of about <paramref name="bytes"/> bytes: user and assistant turns around bulky tool output.</summary>
    private static string Transcript(int bytes, string topic = "stash handling")
    {
        var lines = new List<string>();
        var size = 0;
        for (var turn = 0; size < bytes; turn++)
        {
            var at = new DateTime(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc).AddMinutes(turn);
            lines.Add(Json(new
            {
                type = "user",
                sessionId = "s-1",
                timestamp = at,
                message = new { role = "user", content = $"Question {turn} about {topic}." },
            }));
            lines.Add(Json(new
            {
                type = "assistant",
                sessionId = "s-1",
                timestamp = at.AddSeconds(5),
                message = new { role = "assistant", model = "m", content = new object[] { new { type = "text", text = $"Answer {turn} about {topic}." } } },
            }));
            lines.Add(Json(new
            {
                type = "user",
                sessionId = "s-1",
                timestamp = at.AddSeconds(9),
                message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t", content = "BULKY-TOOL-OUTPUT " + new string('z', 3_000) } } },
            }));
            size = lines.Sum(l => l.Length + 1);
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static string Dataset(int bytes) =>
        string.Join('\n', Enumerable.Range(0, Math.Max(1, bytes / 60)).Select(i => Json(new { id = i, text = $"row {i} of a dataset" }))) + "\n";

    private void Write(string relative, string content) => File.WriteAllText(Path.Combine(_root, relative), content);

    private WorkspaceWalker.WalkResult Walk(long maxFileBytes, long? documentMaxBytes = null) =>
        WorkspaceWalker.Walk(_root, useGitignore: true, null, null, maxFileBytes, documentMaxBytes);

    [Fact]
    public void ATranscriptOverTheTextCapIsWalkedUnderTheDocumentCap()
    {
        Write("session.jsonl", Transcript(150_000));

        var walk = Walk(maxFileBytes: 100_000);

        walk.Files.Select(f => f.RelativePath).ShouldBe(["session.jsonl"]);
        walk.SkippedFiles.ShouldBeEmpty();
    }

    [Fact]
    public void AJsonLinesFileThatIsNotATranscriptKeepsTheTextCap()
    {
        Write("dataset.jsonl", Dataset(150_000));

        var walk = Walk(maxFileBytes: 100_000);

        walk.Files.ShouldBeEmpty();
        var skipped = walk.SkippedFiles.ShouldHaveSingleItem();
        skipped.Reason.ShouldStartWith("over the 100,000 byte size cap");
    }

    [Fact]
    public void ATranscriptOverTheDocumentCapIsSkippedWithThatCap()
    {
        Write("session.jsonl", Transcript(150_000));

        var walk = Walk(maxFileBytes: 100_000, documentMaxBytes: 120_000);

        walk.Files.ShouldBeEmpty();
        walk.SkippedFiles.ShouldHaveSingleItem().Reason.ShouldStartWith("document over the 120,000 byte cap");
    }

    [Fact]
    public void AJsonLinesFileThatIsNotATranscriptIsStillCheckedForBinaryContent()
    {
        Write("small.jsonl", Dataset(1_000));
        File.WriteAllBytes(Path.Combine(_root, "binary.jsonl"), [.. Encoding.ASCII.GetBytes("{\"a\":1}\n"), 0, 1, 2, 3]);

        var walk = Walk(maxFileBytes: 100_000);

        walk.Files.Select(f => f.RelativePath).ShouldBe(["small.jsonl"]);
        walk.SkippedFiles.ShouldHaveSingleItem().Reason.ShouldStartWith("binary content");
    }

    [Fact]
    public void AnEmptyJsonLinesFileIsSkippedAsEmpty()
    {
        Write("empty.jsonl", "");

        Walk(maxFileBytes: 100_000).SkippedFiles.ShouldHaveSingleItem().Reason.ShouldBe("empty file");
    }

    private sealed class RecordingEmbedder : IEmbeddingService
    {
        private readonly IndexingHarness.FixedEmbedder _inner = new();

        public List<string> Texts { get; } = [];

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            if (!IndexingHarness.IsCanary(inputs)) lock (Texts) Texts.AddRange(inputs);
            return _inner.EmbedAsync(target, purpose, inputs, source, ct);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            _inner.CountTokensAsync(t, text, ct);
        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            _inner.ProbeDimensionsAsync(t, ct);
        public int KnownDimensions(EmbeddingTarget t) => _inner.KnownDimensions(t);
    }

    private static async Task<(IndexingHarness Harness, RecordingEmbedder Embedder)> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync();
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var embedder = new RecordingEmbedder();
        harness.Embedder = embedder;
        return (harness, embedder);
    }

    [Fact]
    public async Task ATranscriptIsIndexedAsItsConversation()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.WriteFileAsync("session.jsonl", Transcript(300_000, "stash handling"));

        var job = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        job.FilesDone.ShouldBe(1);
        job.FilesFailed.ShouldBe(0);
        var state = await harness.StateOfAsync("session.jsonl");
        state.Status.ShouldBe(FileStatus.Indexed);

        var embedded = string.Concat(embedder.Texts);
        embedded.ShouldContain("Question 0 about stash handling.");
        embedded.ShouldContain("Answer 0 about stash handling.");
        embedded.ShouldNotContain("BULKY-TOOL-OUTPUT");
        embedded.ShouldNotContain("sessionId");
    }

    [Fact]
    public async Task TheDocumentAReaderGetsBackIsTheConversationAndNotTheFile()
    {
        var (harness, _) = await StartAsync();
        await using var _ = harness;
        await harness.WriteFileAsync("session.jsonl", Transcript(60_000));
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        var state = await harness.StateOfAsync("session.jsonl");

        await using var db = harness.NewContext();
        var body = await new DocumentReader(db).ForAsync(
            IndexingHarness.CorpusId, "set-1", "session.jsonl", IndexingHarness.SourceIdFor(0), TestContext.Current.CancellationToken);

        state.SourceSha256.ShouldNotBeNullOrEmpty();
        var document = body.ShouldNotBeNull();
        document.Store.ShouldBe("file_texts");
        document.Text.ShouldStartWith("Claude Code session transcript\n");
        document.Text.ShouldContain("Question 1 about stash handling.");
        document.Text.ShouldNotContain("BULKY-TOOL-OUTPUT");
    }

    [Fact]
    public async Task ATranscriptThatGrewIsIndexedAgainWithTheNewTurns()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.WriteFileAsync("session.jsonl", Transcript(30_000, "first topic"));
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        var before = embedder.Texts.Count;

        var path = Path.Combine(harness.SourceDirectory, "session.jsonl");
        await File.AppendAllTextAsync(path, Json(new
        {
            type = "user",
            sessionId = "s-1",
            timestamp = new DateTime(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc),
            message = new { role = "user", content = "A later question about the appended topic." },
        }) + "\n", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var second = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        second.FilesDone.ShouldBe(1);
        embedder.Texts.Count.ShouldBeGreaterThan(before);
        string.Concat(embedder.Texts.Skip(before)).ShouldContain("A later question about the appended topic.");
    }

    [Fact]
    public async Task ATranscriptThatGrewByRecordsWithNoConversationIsReadAndNotEmbeddedAgain()
    {
        // Most of what Claude Code appends is tool output and bookkeeping. The text of the file does not
        // change, so the pass finds the fingerprint it already holds and embeds nothing; a header line
        // that moved with every record (a "last activity" time) made the whole file stale each time.
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.WriteFileAsync("session.jsonl", Transcript(30_000));
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        var before = embedder.Texts.Count;

        var later = new DateTime(2026, 11, 12, 19, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(harness.SourceDirectory, "session.jsonl");
        await File.AppendAllLinesAsync(path,
        [
            Json(new { type = "attachment", sessionId = "s-1", timestamp = later, attachment = new { type = "hook_success", content = "output" } }),
            Json(new { type = "file-history-snapshot", sessionId = "s-1", timestamp = later, snapshot = new { files = 3 } }),
            Json(new
            {
                type = "user", sessionId = "s-1", timestamp = later,
                message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t", content = "more output" } } },
            }),
        ], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var second = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        second.FilesSkipped.ShouldBe(1);
        second.FilesDone.ShouldBe(0);
        embedder.Texts.Count.ShouldBe(before);
    }

    [Fact]
    public async Task ATranscriptWithNoConversationIsRecordedAsEmpty()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.WriteFileAsync("session.jsonl", Json(new
        {
            type = "user",
            sessionId = "s-1",
            timestamp = new DateTime(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc),
            message = new { role = "user", content = "<system-reminder>Context and nothing else.</system-reminder>" },
        }) + "\n");

        var job = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        var state = await harness.StateOfAsync("session.jsonl");
        state.Status.ShouldBe(FileStatus.Empty);
        state.StatusDetail.ShouldBe("no extractable text content");
        embedder.Texts.ShouldBeEmpty();
        job.FilesFailed.ShouldBe(0);
    }

    [Fact]
    public async Task AFileThatBecameATranscriptAtTheSameSizeAndTimeIsReadAgain()
    {
        // The size and the time stand in for the bytes of a settled file, so the key has to name the extractor too:
        // a file indexed as text before the extractor existed is a transcript to the walk now and has neither changed.
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var transcript = Transcript(2_000);
        var data = Dataset(1_000);
        data.Length.ShouldBeLessThan(transcript.Length);
        await harness.WriteFileAsync("session.jsonl", data + new string(' ', transcript.Length - data.Length));
        File.SetLastWriteTimeUtc(Path.Combine(harness.SourceDirectory, "session.jsonl"), stamp);
        await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);
        string.Concat(embedder.Texts).ShouldContain("of a dataset");
        var before = embedder.Texts.Count;

        await harness.WriteFileAsync("session.jsonl", transcript);
        File.SetLastWriteTimeUtc(Path.Combine(harness.SourceDirectory, "session.jsonl"), stamp);
        var second = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        second.FilesDone.ShouldBe(1);
        string.Concat(embedder.Texts.Skip(before)).ShouldContain("Question 0 about stash handling.");
    }

    [Fact]
    public async Task AJsonLinesFileThatIsNotATranscriptIsIndexedAsTextAndHasNoStoredDocument()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.WriteFileAsync("dataset.jsonl", Dataset(20_000));

        var job = await harness.RunIndexAsync(cancel: TestContext.Current.CancellationToken);

        job.FilesDone.ShouldBe(1);
        string.Concat(embedder.Texts).ShouldContain("\"text\":\"row 0 of a dataset\"");

        await using var db = harness.NewContext();
        var body = await new DocumentReader(db).ForAsync(
            IndexingHarness.CorpusId, "set-1", "dataset.jsonl", IndexingHarness.SourceIdFor(0), TestContext.Current.CancellationToken);
        body.ShouldBeNull();
    }
}
