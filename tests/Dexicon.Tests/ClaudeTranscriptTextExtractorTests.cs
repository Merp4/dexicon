using System.Text;
using System.Text.Json;
using Dexicon.Core.Extraction;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A Claude Code transcript is read as the conversation it records.
///
/// The file is a log of everything the harness sent and received, and user and assistant text is
/// 1.4% of its bytes (4.95 GB measured over 1,066 files). Embedding it as it is would index tool
/// output and bookkeeping records, one JSON line at a time. The records here are built by the
/// tests from the shapes those files have, and carry no real conversation.
/// </summary>
public sealed class ClaudeTranscriptTextExtractorTests
{
    private const string Session = "5b6b8f0e-1c3e-4a52-9d51-0a0a3c1f7c11";

    private static Dictionary<string, object?> Record(
        string type, string at, object? message = null, params (string Key, object? Value)[] extra)
    {
        var record = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["sessionId"] = Session,
            ["timestamp"] = at,
            ["isSidechain"] = false,
        };
        if (message is not null) record["message"] = message;
        foreach (var (key, value) in extra) record[key] = value;
        return record;
    }

    private static Dictionary<string, object?> User(string text, string at = "2026-11-12T14:03:00.000Z",
        params (string Key, object? Value)[] extra) =>
        Record("user", at, new { role = "user", content = text }, extra);

    private static Dictionary<string, object?> Assistant(string at, params object[] blocks) =>
        Record("assistant", at, new { role = "assistant", model = "claude-sonnet-5-5", content = blocks });

    private static object Text(string text) => new { type = "text", text };

    private static string Extract(IEnumerable<object> records) =>
        ExtractText(string.Join('\n', records.Select(r => r as string ?? JsonSerializer.Serialize(r))) + "\n");

    private static string ExtractText(string file)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file));
        return new ClaudeTranscriptTextExtractor().Extract(stream, "session.jsonl").Text;
    }

    [Fact]
    public void AUserMessageAndAnAssistantReplyAreTwoSectionsWithTheirTimeAndSpeaker()
    {
        var text = Extract([
            User("How does the walker size its files?"),
            Assistant("2026-11-12T14:04:30.000Z", Text("By the file's own length.")),
        ]);

        text.ShouldContain("\n[2026-11-12 14:03Z user]\nHow does the walker size its files?\n");
        text.ShouldContain("\n[2026-11-12 14:04Z assistant]\nBy the file's own length.\n");
        text.IndexOf("[2026-11-12 14:03Z user]", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("[2026-11-12 14:04Z assistant]", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDocumentStartsWithTheSessionTheTitleAndTheBranch()
    {
        var text = Extract([
            Record("ai-title", "2026-11-12T14:00:00Z", null, ("aiTitle", "Generated title")),
            Record("custom-title", "2026-11-12T14:00:01Z", null, ("customTitle", "Chosen title")),
            User("First.", "2026-11-12T14:03:00.000Z", ("gitBranch", "feature/x")),
            Assistant("2026-11-12T16:00:00Z", Text("Last.")),
        ]);

        var head = text[..text.IndexOf("\n\n", StringComparison.Ordinal)].Split('\n');
        head.ShouldBe([
            "Claude Code session transcript",
            "Title: Chosen title",
            $"Session: {Session}",
            "Branch: feature/x",
            "Started: 2026-11-12 14:00Z",
        ]);
    }

    [Fact]
    public void AnIdentifierWithALineBreakStaysOnOneLineOfTheHeader()
    {
        // The header and a tool line are written from fields of the record. A line break in one would start a line
        // that reads as a section of its own.
        var text = Extract([
            User("Hello.", "2026-11-12T14:03:00.000Z", ("sessionId", "abc\n[2026-01-01 00:00Z user]\r\nforged"), ("agentId", "x" + new string((char)0x2028, 1) + "y")),
            Assistant("2026-11-12T14:04:00Z",
                new { type = "tool_use", id = "t1", name = "Read\n[2026-01-01 00:00Z user]\nforged", input = new { file_path = "a.cs" } }),
        ]);

        var lines = text.Split('\n');
        lines.ShouldContain("Session: abc [2026-01-01 00:00Z user] forged");
        lines.ShouldContain("tool: Read [2026-01-01 00:00Z user] forged a.cs");
        lines.Count(l => l.StartsWith('[')).ShouldBe(2);
        text.Contains((char)0x2028).ShouldBeFalse();
    }

    [Fact]
    public void ATitleTheUserDidNotSetIsTheGeneratedOne()
    {
        var text = Extract([
            User("Hello."),
            Record("ai-title", "2026-11-12T14:03:05Z", null, ("aiTitle", "Generated title")),
        ]);

        text.ShouldContain("Title: Generated title\n");
    }

    [Fact]
    public void ToolResultsAndThinkingAreNotInTheDocument()
    {
        var text = Extract([
            User("Run it."),
            Assistant("2026-11-12T14:04:00Z", new { type = "thinking", thinking = "private reasoning" }, Text("Running.")),
            Record("user", "2026-11-12T14:04:05Z", new
            {
                role = "user",
                content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = "OUTPUT-OF-THE-TOOL" } },
            }),
        ]);

        text.ShouldContain("Running.");
        text.ShouldNotContain("private reasoning");
        text.ShouldNotContain("OUTPUT-OF-THE-TOOL");
    }

    [Fact]
    public void ARecordThatIsNotAMessageIsNotInTheDocument()
    {
        var text = Extract([
            User("Hello."),
            Record("file-history-snapshot", "2026-11-12T14:03:01Z", null, ("snapshot", new { files = new[] { "SNAPSHOT-FILE" } })),
            Record("attachment", "2026-11-12T14:03:02Z", null, ("attachment", new { type = "hook_success", content = "HOOK-OUTPUT" })),
            Record("api-request", "2026-11-12T14:03:03Z", null, ("params", new { blob = "REQUEST-COPY" })),
            Record("some-future-type", "2026-11-12T14:03:04Z", null, ("content", "FUTURE-CONTENT")),
        ]);

        text.ShouldContain("Hello.");
        text.ShouldNotContain("SNAPSHOT-FILE");
        text.ShouldNotContain("HOOK-OUTPUT");
        text.ShouldNotContain("REQUEST-COPY");
        text.ShouldNotContain("FUTURE-CONTENT");
    }

    [Fact]
    public void ContextTheHarnessInjectedIntoAUserMessageIsRemoved()
    {
        var text = Extract([
            User("<system-reminder>\nStanding instructions from a file.\n</system-reminder>Fix the build.<ide_opened_file>The user opened a.cs</ide_opened_file>"),
        ]);

        text.ShouldContain("\nFix the build.\n");
        text.ShouldNotContain("Standing instructions");
        text.ShouldNotContain("opened a.cs");
        text.ShouldNotContain("system-reminder");
    }

    [Fact]
    public void AMessageOfInjectedContextAloneIsNotASection()
    {
        var text = Extract([
            User("<system-reminder>Only a reminder.</system-reminder>"),
            User("<local-command-stdout>Output of a slash command</local-command-stdout>"),
        ]);

        text.ShouldNotContain("[");
        text.ShouldNotContain("reminder");
    }

    [Fact]
    public void ASlashCommandIsWrittenAsTheUserTypedIt()
    {
        var text = Extract([
            User("<command-message>review is running</command-message>\n<command-name>/review</command-name>\n<command-args>src/Dexicon.Core</command-args>"),
        ]);

        text.ShouldContain("\n[2026-11-12 14:03Z user]\n/review src/Dexicon.Core\n");
    }

    [Fact]
    public void MessagesTheHarnessMarkedAsMetaAreNotInTheDocument()
    {
        var text = Extract([
            User("An expanded skill prompt.", "2026-11-12T14:03:00.000Z", ("isMeta", true)),
            User("The real question."),
        ]);

        text.ShouldNotContain("expanded skill");
        text.ShouldContain("The real question.");
    }

    [Fact]
    public void AnApiErrorAndASyntheticReplyAreNotAssistantText()
    {
        var text = Extract([
            User("Hello."),
            Record("assistant", "2026-11-12T14:04:00Z", new { role = "assistant", model = "<synthetic>", content = new[] { Text("No response requested.") } }),
            Record("assistant", "2026-11-12T14:04:01Z", new { role = "assistant", model = "claude-sonnet-5-5", content = new[] { Text("API Error: 529") } },
                ("isApiErrorMessage", true)),
        ]);

        text.ShouldNotContain("No response requested");
        text.ShouldNotContain("API Error");
    }

    [Fact]
    public void ASubagentsFirstMessageIsATaskForTheAgentAndNotAUserMessage()
    {
        var text = Extract([
            User("Review the matcher.", "2026-11-12T14:03:00.000Z", ("isSidechain", true)),
            Assistant("2026-11-12T14:05:00Z", Text("Reviewing.")),
        ]);

        text.ShouldContain("[2026-11-12 14:03Z task for this agent]\nReview the matcher.\n");
        text.ShouldNotContain("user]");
    }

    [Fact]
    public void AMessageFromAnotherSessionIsNamedAsSuch()
    {
        var text = Extract([
            User("Please check the branch.", "2026-11-12T14:03:00.000Z", ("origin", new { kind = "peer", from = "uds:pipe-1" })),
        ]);

        text.ShouldContain("[2026-11-12 14:03Z message from another session]\nPlease check the branch.\n");
        text.ShouldNotContain("uds:pipe-1");
    }

    [Fact]
    public void ABackgroundTaskIsItsSummary()
    {
        var text = Extract([
            User("<task-notification>\n<task-id>b1</task-id>\n<status>completed</status>\n<summary>Background command \"run tests\" completed</summary>\n<output-file>C:\\tmp\\out.txt</output-file>\n</task-notification>",
                "2026-11-12T14:03:00.000Z", ("origin", new { kind = "task-notification" })),
        ]);

        text.ShouldContain("[2026-11-12 14:03Z background task]\nBackground command \"run tests\" completed\n");
        text.ShouldNotContain("out.txt");
        text.ShouldNotContain("task-id");
    }

    [Fact]
    public void AnInterruptNoticeIsNotAMessage()
    {
        var text = Extract([
            User("Start."),
            User("[Request interrupted by user for tool use]", "2026-11-12T14:04:00Z"),
        ]);

        text.ShouldNotContain("interrupted");
    }

    [Fact]
    public void ANotificationQueuedForTheAssistantIsNotAUserMessage()
    {
        // The notification is recorded when queued and again when delivered; only the delivery is read, as the
        // background task it is. Read from the queue record it would be a message the user typed.
        var notification = "<task-notification>\n<summary>Background command \"build\" completed</summary>\n</task-notification>";

        var text = Extract([
            Record("queue-operation", "2026-11-12T14:04:00Z", null, ("operation", "enqueue"), ("content", notification)),
        ]);

        text.ShouldNotContain("user]");
        text.ShouldNotContain("build");
    }

    [Fact]
    public void AToolCallIsOneLineWithTheToolAndItsMainArgument()
    {
        var text = Extract([
            User("Look."),
            Assistant("2026-11-12T14:04:00Z", Text("Reading."),
                new { type = "tool_use", id = "t1", name = "Read", input = new { file_path = "src/Dexicon.Core/Foo.cs", offset = 10 } },
                new { type = "tool_use", id = "t2", name = "Grep", input = new { pattern = "TODO", path = "src" } }),
        ]);

        text.ShouldContain("Reading.\ntool: Read src/Dexicon.Core/Foo.cs\ntool: Grep src\n");
    }

    [Fact]
    public void AShellCommandIsShownWithoutItsLeadingChangeOfDirectory()
    {
        var text = Extract([
            Assistant("2026-11-12T14:04:00Z",
                new { type = "tool_use", id = "t1", name = "Bash", input = new { command = "cd \"C:/Users/x/source/repo\" && dotnet test --filter Foo" } },
                new { type = "tool_use", id = "t2", name = "Bash", input = new { command = "cd /tmp/work; git log --oneline -3" } },
                new { type = "tool_use", id = "t3", name = "Bash", input = new { command = "cd /only/this" } }),
        ]);

        text.ShouldContain("tool: Bash dotnet test --filter Foo\n");
        text.ShouldContain("tool: Bash git log --oneline -3\n");
        text.ShouldContain("tool: Bash cd /only/this\n");
    }

    [Fact]
    public void ALongToolArgumentIsCutAtOneHundredAndSixtyCharacters()
    {
        var text = Extract([
            Assistant("2026-11-12T14:04:00Z",
                new { type = "tool_use", id = "t1", name = "Read", input = new { file_path = new string('a', 300) } }),
        ]);

        text.ShouldContain("\ntool: Read " + new string('a', 160) + "\n");
    }

    [Fact]
    public void AToolArgumentOverSeveralLinesIsOneLine()
    {
        var command = "echo one\n\n   two\t three\r\nfour";

        var text = Extract([
            Assistant("2026-11-12T14:04:00Z", new { type = "tool_use", id = "t1", name = "Bash", input = new { command } }),
        ]);

        text.ShouldContain("\ntool: Bash echo one two three four\n");
    }

    [Fact]
    public void ACompactionSummaryIsNotInTheDocument()
    {
        // Claude Code writes a summary of the conversation when it compacts it, as a user record. What it
        // summarises is still in the file above it.
        var text = Extract([
            User("The original question."),
            User("This session is being continued. SUMMARY-OF-EARLIER-TURNS", "2026-11-12T15:00:00Z", ("isCompactSummary", true)),
            Assistant("2026-11-12T15:01:00Z", Text("The answer.")),
        ]);

        text.ShouldContain("The original question.");
        text.ShouldContain("The answer.");
        text.ShouldNotContain("SUMMARY-OF-EARLIER-TURNS");
    }

    [Fact]
    public void AToolArgumentIsNotCutInsideASurrogatePair()
    {
        // 159 ASCII characters then an emoji, which is two UTF-16 units: the cut at 160 would split it.
        var command = new string('a', 159) + "\U0001F600 more";

        var text = Extract([
            Assistant("2026-11-12T14:04:00Z", new { type = "tool_use", id = "t1", name = "Bash", input = new { command } }),
        ]);

        // Asserted on a flag: a failure message that held the lone surrogate cannot be written out as UTF-8.
        HasLoneSurrogate(text).ShouldBeFalse();
        text.ShouldContain("\ntool: Bash " + new string('a', 159) + "\n");
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else if (char.IsSurrogate(text[i])) return true;
        }

        return false;
    }

    [Fact]
    public void AMessageTypedWhileTheAssistantWorkedIsOneSectionAtItsDelivery()
    {
        // Recorded when typed, then again as the user record that delivers it.
        var text = Extract([
            User("Start."),
            Record("queue-operation", "2026-11-12T14:04:00Z", extra: [("operation", "enqueue"), ("content", "Also check the tests.")]),
            Assistant("2026-11-12T14:04:30Z", Text("Working.")),
            User("Also check the tests.", "2026-11-12T14:05:00Z", ("origin", new { kind = "human" })),
        ]);

        Occurrences(text, "Also check the tests.").ShouldBe(1);
        text.ShouldContain("[2026-11-12 14:05Z user]\nAlso check the tests.\n");
    }

    [Fact]
    public void AMessageIsRetiredByItsDeliveryWhateverTheHarnessAddedToIt()
    {
        // The queue record holds what was typed, and the delivering record the same text with context around it.
        var text = Extract([
            Record("queue-operation", "2026-11-12T14:04:00Z", null, ("operation", "enqueue"), ("content", "Also check the tests.")),
            User("<system-reminder>Context.</system-reminder>Also check the tests.", "2026-11-12T14:05:00Z"),
        ]);

        Occurrences(text, "Also check the tests.").ShouldBe(1);
    }

    [Fact]
    public void ATagTheHarnessInjectsIsOnlyRemovedWhereItIsClosed()
    {
        // A message that talks about the tag is a person's text, not context the harness added.
        var text = Extract([User("Why does a <system-reminder> tag reach the model?")]);

        text.ShouldContain("\nWhy does a <system-reminder> tag reach the model?\n");
    }

    [Fact]
    public void AMessageTypedWhileTheAssistantWorkedAndNeverDeliveredIsKeptWhereItWasTyped()
    {
        var text = Extract([
            User("Start."),
            Record("queue-operation", "2026-11-12T14:04:00Z", extra: [("operation", "enqueue"), ("content", "Only recorded here.")]),
            Assistant("2026-11-12T14:05:00Z", Text("Done.")),
        ]);

        Occurrences(text, "Only recorded here.").ShouldBe(1);
        text.IndexOf("Only recorded here.", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("Done.", StringComparison.Ordinal));
    }

    [Fact]
    public void AMessageDeliveredByAnAttachmentIsOneSectionWithTheOriginOfTheDelivery()
    {
        var text = Extract([
            Record("queue-operation", "2026-11-12T14:04:00Z", extra: [("operation", "enqueue"), ("content", "From the other session.")]),
            Record("attachment", "2026-11-12T14:04:10Z", extra: [
                ("attachment", new { type = "queued_command", commandMode = "prompt", prompt = "From the other session." }),
                ("origin", new { kind = "peer", from = "uds:pipe-2" }),
            ]),
        ]);

        Occurrences(text, "From the other session.").ShouldBe(1);
    }

    [Fact]
    public void AnAttachmentThatIsNotAQueuedPromptIsNotInTheDocument()
    {
        var text = Extract([
            Record("attachment", "2026-11-12T14:04:10Z", null,
                ("attachment", new { type = "queued_command", commandMode = "task-notification", prompt = "NOTIFICATION-BODY" })),
            Record("attachment", "2026-11-12T14:04:11Z", null,
                ("attachment", new { type = "hook_success", prompt = "HOOK-BODY" })),
        ]);

        text.ShouldNotContain("NOTIFICATION-BODY");
        text.ShouldNotContain("HOOK-BODY");
    }

    [Fact]
    public void TheSameTextTypedTwiceIsTwoSections()
    {
        // Two prompts that happen to read alike are two messages. Only a queued copy is retired.
        var text = Extract([
            User("continue", "2026-11-12T14:03:00Z"),
            Assistant("2026-11-12T14:04:00Z", Text("Done.")),
            User("continue", "2026-11-12T15:03:00Z"),
        ]);

        Occurrences(text, "\ncontinue\n").ShouldBe(2);
    }

    [Fact]
    public void AnotherSectionStartsWhenTheSameSpeakerResumesAfterTenMinutes()
    {
        var text = Extract([
            Assistant("2026-11-12T14:00:00Z", Text("Start.")),
            Assistant("2026-11-12T14:05:00Z", Text("Middle.")),
            Assistant("2026-11-12T14:20:00Z", Text("Later.")),
        ]);

        text.ShouldContain("[2026-11-12 14:00Z assistant]\nStart.\nMiddle.\n");
        text.ShouldContain("[2026-11-12 14:20Z assistant]\nLater.\n");
    }

    [Theory]
    [InlineData("2026-11-12T14:09:59Z", false)]
    [InlineData("2026-11-12T14:10:00Z", true)]
    public void ASectionIsMadeOfMessagesLessThanTenMinutesFromItsFirst(string second, bool newSection)
    {
        var text = Extract([
            Assistant("2026-11-12T14:00:00Z", Text("First.")),
            Assistant(second, Text("Second.")),
        ]);

        Occurrences(text, "\n\n[").ShouldBe(newSection ? 2 : 1);
    }

    [Fact]
    public void BlankLinesInAMessageAreKeptAsOneSoTheChunkerHasParagraphs()
    {
        var text = Extract([User("First paragraph.\r\n\r\n\r\n\r\nSecond paragraph.   \r\nStill second.")]);

        text.ShouldContain("First paragraph.\n\nSecond paragraph.\nStill second.\n");
        text.ShouldNotContain("\r");
    }

    [Fact]
    public void ALineThatIsNotJsonAndARecordCutShortAreSkipped()
    {
        var file = string.Join('\n',
            JsonSerializer.Serialize(User("Before.")),
            "this line is not json",
            "{\"type\":\"user\",\"sessionId\":\"x\",\"message\":{\"content\":\"cut",
            JsonSerializer.Serialize(User("After.", "2026-11-12T14:06:00Z")));

        var text = ExtractText(file);

        text.ShouldContain("Before.");
        text.ShouldContain("After.");
    }

    [Fact]
    public void ARecordCutShortAtTheEndOfTheFileIsSkipped()
    {
        // A transcript is read while Claude Code is still writing it.
        var file = JsonSerializer.Serialize(User("Whole.")) + "\n{\"type\":\"assistant\",\"sessionId\":\"x\",\"message\":{\"con";

        ExtractText(file).ShouldContain("Whole.");
    }

    [Fact]
    public void ALastLineWithoutANewlineIsRead()
    {
        ExtractText(JsonSerializer.Serialize(User("No newline at the end."))).ShouldContain("No newline at the end.");
    }

    [Fact]
    public void ALineLongerThanTheBufferIsRead()
    {
        // The reader holds 256 KB at a time; a long reply spans several reads.
        var reply = string.Join(' ', Enumerable.Repeat("sentence", 100_000));
        reply.Length.ShouldBeGreaterThan(700_000);

        var text = Extract([User("Q."), Assistant("2026-11-12T14:04:00Z", Text(reply))]);

        text.ShouldContain(reply);
    }

    [Fact]
    public void ALineOverTheLimitIsSkippedAndTheLinesAfterItAreRead()
    {
        var huge = new string('x', ClaudeTranscriptTextExtractor.MaxLineBytes + 10);
        var file = JsonSerializer.Serialize(User("Before.")) + "\n"
            + JsonSerializer.Serialize(User(huge, "2026-11-12T14:04:00Z")) + "\n"
            + JsonSerializer.Serialize(User("After.", "2026-11-12T14:05:00Z")) + "\n";

        var text = ExtractText(file);

        text.ShouldContain("Before.");
        text.ShouldContain("After.");
        text.Length.ShouldBeLessThan(1_000);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ALineOfExactlyTheLimitIsReadAndOneByteMoreIsNot(int extra, bool read)
    {
        var record = JsonSerializer.Serialize(User("AT-THE-LIMIT", "2026-11-12T14:04:00Z"));
        var padding = ClaudeTranscriptTextExtractor.MaxLineBytes + extra - Encoding.UTF8.GetByteCount(record);
        var line = new string(' ', padding) + record;
        Encoding.UTF8.GetByteCount(line).ShouldBe(ClaudeTranscriptTextExtractor.MaxLineBytes + extra);

        ExtractText(line + "\n").Contains("AT-THE-LIMIT", StringComparison.Ordinal).ShouldBe(read);
    }

    [Fact]
    public void ARecordAtTheEndOfALineOverTheLimitIsNotRead()
    {
        // JSON allows white space before a value, so the last part of this line is a whole record. It is part of
        // a line that was skipped, and reading it would make the limit a limit on where a record may start.
        var inside = JsonSerializer.Serialize(User("INSIDE-THE-LONG-LINE", "2026-11-12T14:04:00Z"));
        var file = JsonSerializer.Serialize(User("Before.")) + "\n"
            + new string(' ', ClaudeTranscriptTextExtractor.MaxLineBytes + 1_000_000) + inside + "\n"
            + JsonSerializer.Serialize(User("After.", "2026-11-12T14:05:00Z")) + "\n";

        var text = ExtractText(file);

        text.ShouldContain("After.");
        text.ShouldNotContain("INSIDE-THE-LONG-LINE");
    }

    [Fact]
    public void AnEmptyFileHasNoText()
    {
        ExtractText("").ShouldBe("");
    }

    [Fact]
    public void ATranscriptWithNoMessageHasNoText()
    {
        // Only the header would be left, and every such file would be a chunk that matches "Claude Code session transcript".
        var text = Extract([
            Record("file-history-snapshot", "2026-11-12T14:03:00Z", null, ("snapshot", new { files = 1 })),
            Record("ai-title", "2026-11-12T14:03:01Z", null, ("aiTitle", "A title")),
            User("<system-reminder>Only context.</system-reminder>"),
        ]);

        text.ShouldBe("");
    }

    [Fact]
    public void AByteOrderMarkAtTheStartOfTheFileDoesNotHideTheFirstRecord()
    {
        var first = JsonSerializer.Serialize(User("First message.", "2026-11-12T14:03:00Z"));
        var second = JsonSerializer.Serialize(User("Second message.", "2026-11-12T14:04:00Z"));

        var text = ExtractText((char)0xFEFF + first + "\n" + second + "\n");

        text.ShouldContain("First message.");
        text.ShouldContain("Started: 2026-11-12 14:03Z");
    }

    [Fact]
    public void ASectionOpenedByAMessageWithNoTimeIsTimedFromItsFirstMessageThatHasOne()
    {
        var untimed = Assistant("2026-11-12T14:00:30Z", Text("No time."));
        untimed.Remove("timestamp");

        var text = Extract([
            User("Start.", "2026-11-12T14:00:00Z"),
            untimed,
            Assistant("2026-11-12T14:01:00Z", Text("Timed.")),
            Assistant("2026-11-12T14:12:00Z", Text("Eleven minutes on.")),
        ]);

        text.ShouldContain("\n[assistant]\nNo time.\nTimed.\n");
        text.ShouldContain("\n[2026-11-12 14:12Z assistant]\nEleven minutes on.\n");
    }

    [Fact]
    public void MessagesWithNoTimestampAreStillSections()
    {
        var record = Record("user", "2026-11-12T14:03:00Z", new { role = "user", content = "No time." });
        record.Remove("timestamp");

        Extract([record]).ShouldContain("\n[user]\nNo time.\n");
    }

    [Fact]
    public void TheTextOfAMessageIsNotCutOrChanged()
    {
        const string message = "Keep `code`, <b>tags</b>, \"quotes\", \\backslashes\\, and unicode: \u00e9\u4e2d\U0001F600.";

        Extract([User(message)]).ShouldContain("\n" + message + "\n");
    }

    [Fact]
    public void AStringThatIsNotValidUnicodeDropsOnlyTheMessageThatHoldsIt()
    {
        // A JavaScript writer escapes half of a surrogate pair on its own when a message is cut inside an emoji. The
        // parser accepts it and reading the string throws, which was taken for a file that cannot be read.
        var backslash = new string((char)92, 1);
        var broken = "{\"type\":\"user\",\"sessionId\":\"s\",\"timestamp\":\"2026-11-12T14:04:00Z\","
            + "\"message\":{\"role\":\"user\",\"content\":\"cut in an emoji " + backslash + "ud83d\"}}";

        var text = ExtractText(string.Join('\n',
            JsonSerializer.Serialize(User("Before.", "2026-11-12T14:03:00Z")),
            broken,
            JsonSerializer.Serialize(User("After.", "2026-11-12T14:05:00Z"))));

        text.ShouldContain("Before.");
        text.ShouldContain("After.");
        text.ShouldNotContain("cut in an emoji");
    }

    [Fact]
    public void AStringThatIsNotValidUnicodeDropsOnlyTheBlockThatHoldsIt()
    {
        // The block after it in the same reply is kept.
        var backslash = new string((char)92, 1);
        var broken = "{\"type\":\"assistant\",\"sessionId\":\"s\",\"timestamp\":\"2026-11-12T14:04:00Z\",\"message\":{\"role\":\"assistant\","
            + "\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"The first is cut " + backslash + "ud83d\"},"
            + "{\"type\":\"text\",\"text\":\"The second block is fine.\"}]}}";

        var text = ExtractText(broken);

        text.ShouldContain("The second block is fine.");
        text.ShouldNotContain("The first");
    }

    [Fact]
    public void TheChainOfWaitingMessagesSurvivesADeliveryInTheMiddle()
    {
        // Three copies of one text are queued, one after a delivery has already retired the oldest.
        var queue = (string at) => Record("queue-operation", at, null, ("operation", "enqueue"), ("content", "again"));

        var text = Extract([
            queue("2026-11-12T14:04:00Z"),
            queue("2026-11-12T14:05:00Z"),
            User("again", "2026-11-12T14:06:00Z"),
            queue("2026-11-12T14:07:00Z"),
            User("again", "2026-11-12T14:08:00Z"),
            User("again", "2026-11-12T14:09:00Z"),
        ]);

        Occurrences(text, "again").ShouldBe(3);
    }

    [Fact]
    public void BytesThatAreNotUtf8InsideAStringDropOnlyTheMessageThatHoldsThem()
    {
        var before = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(User("Before.", "2026-11-12T14:03:00Z")) + "\n");
        var broken = new List<byte>(Encoding.UTF8.GetBytes(
            "{\"type\":\"user\",\"sessionId\":\"s\",\"timestamp\":\"2026-11-12T14:04:00Z\",\"message\":{\"role\":\"user\",\"content\":\"bad "));
        broken.AddRange([0xFF, 0xFE]);
        broken.AddRange(Encoding.UTF8.GetBytes(" bytes\"}}\n"));
        var after = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(User("After.", "2026-11-12T14:05:00Z")) + "\n");

        using var stream = new MemoryStream([.. before, .. broken, .. after]);
        var text = new ClaudeTranscriptTextExtractor().Extract(stream, "session.jsonl").Text;

        text.ShouldContain("Before.");
        text.ShouldContain("After.");
        text.ShouldNotContain("bad ");
    }

    [Fact]
    public void ManyInjectedSpansInOneMessageAreRemovedInOnePass()
    {
        // Removing the spans one at a time copied the message for each: 100,000 of them took 71 s.
        var spans = string.Concat(Enumerable.Repeat("<system-reminder>x</system-reminder>", 100_000));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var text = Extract([User(spans + "The real message.")]);
        clock.Stop();

        text.ShouldContain("\nThe real message.\n");
        text.ShouldNotContain("system-reminder");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void TwoQueuedCopiesOfOneTextAreRetiredOneForEachDelivery()
    {
        var queued = new[]
        {
            Record("queue-operation", "2026-11-12T14:04:00Z", null, ("operation", "enqueue"), ("content", "again")),
            Record("queue-operation", "2026-11-12T14:05:00Z", null, ("operation", "enqueue"), ("content", "again")),
        };

        var oneDelivery = Extract([queued[0], queued[1], User("again", "2026-11-12T14:06:00Z")]);
        var twoDeliveries = Extract([queued[0], queued[1], User("again", "2026-11-12T14:06:00Z"), User("again", "2026-11-12T14:07:00Z")]);

        // The first delivery retires the older copy, and the other stays where it was typed.
        Occurrences(oneDelivery, "again").ShouldBe(2);
        oneDelivery.ShouldContain("[2026-11-12 14:05Z user]\nagain\nagain\n");
        Occurrences(twoDeliveries, "again").ShouldBe(2);
        twoDeliveries.ShouldContain("[2026-11-12 14:06Z user]\nagain\nagain\n");
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
