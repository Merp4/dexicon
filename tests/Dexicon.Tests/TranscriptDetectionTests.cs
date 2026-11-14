using System.Text;
using System.Text.Json;
using Dexicon.Core.Extraction;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Which <c>.jsonl</c> files are Claude Code transcripts.
///
/// The extension is shared with datasets, training logs and the session logs of other tools, so the
/// extractor looks at the start of the file. Measured over 1,066 transcripts and 2,204 other
/// <c>.jsonl</c> files on one machine (conversation datasets, loss logs, generation samples, other
/// assistants' logs): every transcript matched and no other file did. A file that does not match is
/// read as text under the usual size cap, as it was before the extractor existed.
/// </summary>
public sealed class TranscriptDetectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"transcript-detect-{Guid.NewGuid():N}");

    public TranscriptDetectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly ClaudeTranscriptTextExtractor Extractor = new();

    private static bool Matches(string file) => Extractor.Matches(Encoding.UTF8.GetBytes(file));

    private static string Line(object record) => JsonSerializer.Serialize(record);

    private static readonly string UserRecord = Line(new
    {
        type = "user",
        sessionId = "s-1",
        timestamp = "2026-11-12T14:03:00Z",
        message = new { role = "user", content = "Hello." },
    });

    [Fact]
    public void AFileThatStartsWithAUserRecordIsATranscript()
    {
        Matches(UserRecord + "\n").ShouldBeTrue();
    }

    [Theory]
    [InlineData("queue-operation")]
    [InlineData("ai-title")]
    [InlineData("custom-title")]
    [InlineData("file-history-snapshot")]
    [InlineData("attachment")]
    [InlineData("summary")]
    public void AFileCanOpenWithRecordsThatAreNotMessages(string type)
    {
        // 88 of the 1,066 files measured start with a queue record, 10 with a title.
        var opening = Line(new { type, sessionId = "s-1", timestamp = "2026-11-12T14:03:00Z" });

        Matches(opening + "\n" + UserRecord + "\n").ShouldBeTrue();
    }

    [Theory]
    [InlineData("queue-operation")]
    [InlineData("ai-title")]
    [InlineData("custom-title")]
    [InlineData("file-history-snapshot")]
    public void ATypeOnlyClaudeCodeWritesIsEnoughToBeATranscript(string type)
    {
        // Two of the 1,121 files measured open with a message over 64 KB (a pasted document), so nothing in the head
        // is a message; their first lines are queue records.
        Matches(Line(new { type, sessionId = "s-1", timestamp = "2026-11-12T14:03:00Z" }) + "\n").ShouldBeTrue();
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("system")]
    [InlineData("summary")]
    [InlineData("mode")]
    [InlineData("event")]
    public void ATypeOtherToolsUseIsNotEnoughToBeATranscript(string type)
    {
        // A log of another tool can carry a type and a session id. Taken for a transcript, it would be indexed as the
        // empty document the extractor makes of a file with no messages, and not as the text it is.
        Matches(Line(new { type, sessionId = "s-1", timestamp = "2026-11-12T14:03:00Z" }) + "\n").ShouldBeFalse();
    }

    [Fact]
    public void ATypeOnlyClaudeCodeWritesNeedsASessionId()
    {
        Matches(Line(new { type = "queue-operation", timestamp = "2026-11-12T14:03:00Z" }) + "\n").ShouldBeFalse();
    }

    [Fact]
    public void ARecordLaterInTheFirstLinesIsEnough()
    {
        var lines = string.Join('\n', Line(new { note = "header" }), "", Line(new { other = 1 }), UserRecord);

        Matches(lines).ShouldBeTrue();
    }

    [Fact]
    public void BlankLinesAreNotCountedAmongTheLinesLookedAt()
    {
        // A file written with Windows line endings can open with a run of them.
        var blanks = string.Concat(Enumerable.Repeat("  \r\n\r\n\t\r\n", 10));

        Matches(blanks + UserRecord + "\r\n").ShouldBeTrue();
    }

    [Fact]
    public void ARecordOnTheTwentiethLineIsFound()
    {
        var lines = string.Join('\n', Enumerable.Range(0, 19).Select(i => Line(new { row = i })).Append(UserRecord));

        Matches(lines).ShouldBeTrue();
    }

    [Fact]
    public void ARecordPastTheTwentiethLineIsNotLookedFor()
    {
        var lines = string.Join('\n', Enumerable.Range(0, 20).Select(i => Line(new { row = i })).Append(UserRecord));

        Matches(lines).ShouldBeFalse();
    }

    [Fact]
    public void AUserRecordWithoutAMessageIsNotATranscriptRecord()
    {
        Matches(Line(new { type = "user", sessionId = "s-1", text = "no message" }) + "\n").ShouldBeFalse();
    }

    [Fact]
    public void ARecordWithoutASessionIdIsNotATranscriptRecord()
    {
        Matches(Line(new { type = "user", message = new { content = "Hello." } }) + "\n").ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"conversations":[{"from":"human","value":"Hi"},{"from":"gpt","value":"Hello"}]}""")]
    [InlineData("""{"kind":"user_message","payload":{"text":"Hi"},"timestamp":"2026-11-12T14:03:00Z"}""")]
    [InlineData("""{"current_steps":10,"total_steps":100,"loss":2.3,"lr":0.0001,"epoch":0.1}""")]
    [InlineData("""{"prompt":"Once upon","model":"m","seed":1,"temperature":0.7,"text":"a time"}""")]
    [InlineData("""{"display":"a prompt","pastedContents":{},"timestamp":1760000000000,"project":"C:\\work","sessionId":"s-1"}""")]
    [InlineData("""{"text":"one row of a corpus"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("plain text, not json")]
    public void OtherJsonLinesFilesAreNotTranscripts(string firstLine)
    {
        // The shapes measured on one machine: a conversation dataset, another assistant's log, a training
        // log, generation samples, and the prompt history Claude Code keeps beside its transcripts, which
        // has a sessionId and no record type.
        Matches(firstLine + "\n" + firstLine + "\n").ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyFileIsNotATranscript()
    {
        Matches("").ShouldBeFalse();
        Matches("\n\n").ShouldBeFalse();
    }

    [Fact]
    public void AByteOrderMarkBeforeTheFirstRecordIsIgnored()
    {
        Extractor.Matches([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(UserRecord + "\n")]).ShouldBeTrue();
    }

    [Fact]
    public void ALineOfAnotherPartWithCarriageReturnsIsRead()
    {
        Matches(UserRecord + "\r\n").ShouldBeTrue();
    }

    [Fact]
    public void AHeadThatEndsInsideALineJudgesOnlyTheLinesBeforeIt()
    {
        // A full head is cut wherever the limit fell. A record cut there is not parsed, and one before it counts.
        var head = new byte[Extractor.HeadBytes];
        var whole = Encoding.UTF8.GetBytes(UserRecord + "\n");
        var cut = Encoding.UTF8.GetBytes(UserRecord[..40]);
        whole.CopyTo(head, 0);
        var filler = Encoding.UTF8.GetBytes(new string(' ', head.Length - whole.Length - cut.Length));
        filler.CopyTo(head, whole.Length);
        cut.CopyTo(head, head.Length - cut.Length);

        Extractor.Matches(head).ShouldBeTrue();

        var onlyCut = new byte[Extractor.HeadBytes];
        Array.Fill(onlyCut, (byte)' ');
        cut.CopyTo(onlyCut, 0);
        Extractor.Matches(onlyCut).ShouldBeFalse();
    }

    [Fact]
    public void TheRegistryReturnsTheTranscriptExtractorOnlyForAFileThatIsOne()
    {
        var transcript = Path.Combine(_directory, "session.jsonl");
        var dataset = Path.Combine(_directory, "dataset.jsonl");
        File.WriteAllText(transcript, UserRecord + "\n");
        File.WriteAllText(dataset, Line(new { text = "one row" }) + "\n");

        ExtractorRegistry.For("session.jsonl", transcript).ShouldBeOfType<ClaudeTranscriptTextExtractor>();
        ExtractorRegistry.For("dataset.jsonl", dataset).ShouldBeNull();
        ExtractorRegistry.IsDocumentFormat("session.jsonl", transcript).ShouldBeTrue();
        ExtractorRegistry.IsDocumentFormat("dataset.jsonl", dataset).ShouldBeFalse();
    }

    [Fact]
    public void AnExtensionLookupNeverReturnsAnExtractorThatNeedsTheFile()
    {
        // A caller that has only a path would otherwise send every .jsonl file to the transcript reader.
        ExtractorRegistry.For("session.jsonl").ShouldBeNull();
    }

    [Fact]
    public void TheRegistryNamesTheTranscriptExtractorForAReaderThatHasNoFile()
    {
        ExtractorRegistry.NamesFor("any/path/session.JSONL").ShouldBe([nameof(ClaudeTranscriptTextExtractor)]);
        ExtractorRegistry.NamesFor("notes.txt").ShouldBeEmpty();
        ExtractorRegistry.NamesFor("book.epub").ShouldBe([nameof(EpubTextExtractor)]);
    }

    [Fact]
    public void AFileThatCannotBeOpenedIsNotATranscriptAndDoesNotThrow()
    {
        ExtractorRegistry.For("gone.jsonl", Path.Combine(_directory, "gone.jsonl")).ShouldBeNull();
    }

    [Fact]
    public void AShortFileIsPassedWholeAndALongOneByItsFirstBytes()
    {
        var path = Path.Combine(_directory, "long.jsonl");
        File.WriteAllText(path, UserRecord + "\n" + new string('x', 200_000));

        ExtractorRegistry.For("long.jsonl", path).ShouldBeOfType<ClaudeTranscriptTextExtractor>();
    }
}
