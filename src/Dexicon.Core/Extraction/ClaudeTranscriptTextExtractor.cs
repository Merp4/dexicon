using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dexicon.Core.Extraction;

/// <summary>
/// An extractor for an extension that data files of other formats share, so the file's first
/// bytes decide whether it applies. <see cref="ExtractorRegistry.For(string)"/> never returns one;
/// <see cref="ExtractorRegistry.For(string, string)"/> asks <see cref="Matches"/>.
/// </summary>
public interface IContentGatedExtractor : ITextExtractor
{
    /// <summary>How many bytes from the start of the file <see cref="Matches"/> is given.</summary>
    int HeadBytes { get; }

    /// <summary>
    /// Whether the start of a file is in this extractor's format. A file shorter than
    /// <see cref="HeadBytes"/> is passed whole.
    /// </summary>
    bool Matches(ReadOnlySpan<byte> head);
}

/// <summary>
/// Claude Code session transcripts (<c>.jsonl</c>) as a conversation: the user's messages and
/// the assistant's replies, each under a line naming its time and speaker.
///
/// A transcript is a log of everything the harness sent and received, one JSON record per line.
/// Measured over 1,066 files on one installation (4.95 GB), user and assistant text is 1.4% of the
/// bytes; the rest is attachments, tool results, tool inputs, file snapshots and API request copies.
/// Embedding the file as it is would index mostly tool output, one record at a time, and the chunker
/// splits on blank lines, which a JSONL file does not have.
///
/// The format is not documented and changes with the Claude Code version (27 versions in the files
/// measured). The reader takes what it recognises and ignores the rest: an unknown record type, a
/// line that is not JSON (a record cut short by a write in progress) and a line over
/// <see cref="MaxLineBytes"/> are skipped, and the document is built from the lines that remain.
/// </summary>
/// <remarks>
/// What is kept: text a person typed (also when it was queued while the assistant was busy, or sent by
/// another session), assistant text, the summary of a background task, and one line per tool call
/// naming the tool and its main argument. What is dropped: tool results, thinking (2.3% of the thinking
/// blocks measured held any text), the summary Claude Code writes when it compacts a conversation (the
/// messages it summarises stay in the file), injected context (<c>system-reminder</c>, IDE state,
/// command output), attachments and every bookkeeping record.
/// </remarks>
public sealed class ClaudeTranscriptTextExtractor : IContentGatedExtractor
{
    /// <summary>The longest line parsed. A longer one is skipped without being held in memory.</summary>
    public const int MaxLineBytes = 16 * 1024 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private const int SniffLines = 20;
    private const int StubChars = 160;
    private static readonly TimeSpan SectionGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Record types that only Claude Code writes, which a file may open with before its first message (88 of the
    /// 1,066 files measured open with a queue record, 10 with a title).
    /// </summary>
    private static readonly HashSet<string> OwnTypes = new(StringComparer.Ordinal)
    {
        "queue-operation", "ai-title", "custom-title", "last-prompt", "agent-name", "bridge-session",
        "file-history-snapshot", "file-history-delta", "atis-latch", "frame-link", "tools-used", "cost-state",
        "permission-mode", "api-request", "api-request-blob", "api-request-shape",
    };

    /// <summary>Tags whose contents the harness injects into a user message; the span is removed.</summary>
    private static readonly string[] InjectedTags =
    [
        "system-reminder", "local-command-caveat", "local-command-stdout", "local-command-stderr",
        "command-message", "ide_opened_file", "ide_selection", "ide_diagnostics", "browser_instruction",
    ];

    /// <summary>The argument that names a tool call, in order of preference.</summary>
    private static readonly string[] StubArguments =
    [
        "file_path", "path", "notebook_path", "command", "pattern", "query", "url", "skill", "description",
        "prompt", "name",
    ];

    public bool CanHandle(string extension) => extension == ".jsonl";

    /// <inheritdoc/>
    public int HeadBytes => 64 * 1024;

    /// <inheritdoc/>
    public bool Matches(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Utf8Bom)) head = head[3..];

        // A full head ends part way through a line. That line is not valid JSON and is passed over.
        for (var seen = 0; !head.IsEmpty && seen < SniffLines;)
        {
            var end = head.IndexOf((byte)'\n');
            var line = TrimEnd(end < 0 ? head : head[..end]);
            head = end < 0 ? default : head[(end + 1)..];
            if (line.IsEmpty) continue;

            seen++;
            if (TryScan(line, out var top) && top.IsRecord) return true;
        }

        return false;
    }

    public ExtractedText Extract(Stream content, string fileName)
    {
        try
        {
            var transcript = new Transcript();
            var lines = new LineReader(content);
            for (var first = true; lines.TryRead(out var line); first = false)
            {
                // A byte order mark belongs to the file and not to its first record.
                transcript.Add(first && line.Span.StartsWith(Utf8Bom) ? line[Utf8Bom.Length..] : line);
            }

            return new ExtractedText(transcript.Render(), []);
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            throw ExtractionFailures.Of($"'{fileName}' is not a readable Claude Code transcript: {ex.Message}", ex);
        }
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> line)
    {
        while (!line.IsEmpty && line[^1] is (byte)'\r' or (byte)' ' or (byte)'\t') line = line[..^1];
        return line;
    }

    /// <summary>What the top level of a record says about it, read without building the whole tree.</summary>
    private readonly record struct Top(string? Type, bool HasSessionId, bool MessageIsObject)
    {
        /// <summary>
        /// A record of a transcript: a session id and either a message (a <c>user</c> or <c>assistant</c> record with
        /// a message object) or a type that only Claude Code writes. Types that other tools use too (<c>system</c>,
        /// <c>summary</c>, <c>mode</c>, <c>attachment</c>) do not count by themselves, since a file taken for a transcript
        /// that holds no message is indexed as empty and not as the text it is.
        /// </summary>
        public bool IsRecord =>
            HasSessionId && (MessageIsObject && Type is "user" or "assistant" || Type is not null && OwnTypes.Contains(Type));
    }

    private static bool TryScan(ReadOnlySpan<byte> line, out Top top)
    {
        top = default;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            string? type = null;
            var session = false;
            var message = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isType = reader.ValueTextEquals("type"u8);
                var isSession = reader.ValueTextEquals("sessionId"u8);
                var isMessage = reader.ValueTextEquals("message"u8);
                reader.Read();

                if (isType && reader.TokenType == JsonTokenType.String) type = reader.GetString();
                else if (isSession && reader.TokenType == JsonTokenType.String) session = true;
                else if (isMessage && reader.TokenType == JsonTokenType.StartObject) message = true;

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            }

            top = new Top(type, session, message);
            return reader.TokenType == JsonTokenType.EndObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private enum Speaker { User, Assistant, AgentTask, PeerSession, Coordinator, Task }

    /// <summary>One thing the conversation holds, in the order the file recorded it.</summary>
    /// <param name="Queued">
    /// A message the person typed while the assistant was busy. It is kept only when the file never
    /// delivers it as a message of its own (<see cref="Transcript.Deliver"/>).
    /// </param>
    private sealed class Entry(DateTimeOffset? at, Speaker speaker, string text, bool queued)
    {
        public DateTimeOffset? At { get; } = at;
        public Speaker Speaker { get; } = speaker;
        public string Text { get; } = text;
        public bool Queued { get; } = queued;
        public bool Delivered { get; set; }

        /// <summary>The queued message of the same text that was typed after this one, while both wait.</summary>
        public Entry? NextWaiting { get; set; }

        /// <summary>On the oldest of the waiting messages of one text: the newest, where the next one is appended.</summary>
        public Entry? LastWaiting { get; set; }
    }

    private sealed class Transcript
    {
        private readonly List<Entry> _entries = [];

        /// <summary>
        /// The oldest queued message not yet delivered, by the hash of its text; the others of that text follow it
        /// through <see cref="Entry.NextWaiting"/>. A chain and not a queue object for each text, since a file can
        /// hold millions of distinct ones.
        /// </summary>
        private readonly Dictionary<UInt128, Entry> _waiting = [];
        private string? _sessionId;
        private string? _branch;
        private string? _agentId;
        private string? _aiTitle;
        private string? _customTitle;
        private DateTimeOffset? _first;

        public void Add(ReadOnlyMemory<byte> line)
        {
            var span = line.Span;
            if (!TryScan(span, out var top) || top.Type is null) return;
            if (top.Type is not ("user" or "assistant" or "queue-operation" or "attachment" or "ai-title" or "custom-title"))
                return;

            // A line the reader cannot take is skipped and not held against the file. Strings are read one at a time by
            // Text and Pieces, which drop the one they cannot decode (a lone surrogate escape, invalid UTF-8); this is
            // for the rest, such as nesting past the parser's limit.
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var at = TimeOf(root);
                Note(root, at);

                switch (top.Type)
                {
                    case "user": AddUser(root, at); break;
                    case "assistant": AddAssistant(root, at); break;
                    case "queue-operation": AddQueued(root, at); break;
                    case "attachment": AddAttachment(root, at); break;
                    case "ai-title": _aiTitle = Text(root, "aiTitle") ?? _aiTitle; break;
                    case "custom-title": _customTitle = Text(root, "customTitle") ?? _customTitle; break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }
        }

        private void Note(JsonElement root, DateTimeOffset? at)
        {
            _sessionId ??= Text(root, "sessionId");
            _branch ??= Text(root, "gitBranch");
            _agentId ??= Text(root, "agentId");
            if (at is { } t && (_first is null || t < _first)) _first = t;
        }

        private void AddUser(JsonElement root, DateTimeOffset? at)
        {
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (!message.TryGetProperty("content", out var body)) return;

            // A compaction summary restates what the file already holds above it.
            if (Flag(root, "isMeta") || Flag(root, "isCompactSummary")) return;

            var speaker = SpeakerOf(root);
            foreach (var piece in Pieces(body)) Deliver(at, speaker, piece);
        }

        private void AddAssistant(JsonElement root, DateTimeOffset? at)
        {
            if (Flag(root, "isApiErrorMessage")) return;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (Text(message, "model") == "<synthetic>") return;
            if (!message.TryGetProperty("content", out var body) || body.ValueKind != JsonValueKind.Array) return;

            foreach (var block in body.EnumerateArray())
            {
                switch (BlockType(block))
                {
                    case "text":
                        if (Text(block, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                            _entries.Add(new Entry(at, Speaker.Assistant, text, queued: false));
                        break;

                    case "tool_use":
                        _entries.Add(new Entry(at, Speaker.Assistant, Stub(block), queued: false));
                        break;
                }
            }
        }

        /// <summary>
        /// A message the person typed while the assistant was working. Claude Code records it here when it is
        /// typed and again when it reaches the conversation, as a user record or an attachment, and in some
        /// versions only here. It is kept as a message of its own only when it is never delivered.
        /// </summary>
        private void AddQueued(JsonElement root, DateTimeOffset? at)
        {
            if (Text(root, "operation") != "enqueue" || Text(root, "content") is not { } text) return;

            var cleaned = Clean(text);
            if (cleaned.Length == 0 || IsHarnessMessage(text)) return;

            var entry = new Entry(at, Speaker.User, cleaned, queued: true);
            _entries.Add(entry);

            var key = Key(cleaned);
            if (_waiting.TryGetValue(key, out var oldest))
            {
                oldest.LastWaiting!.NextWaiting = entry;
                oldest.LastWaiting = entry;
            }
            else
            {
                entry.LastWaiting = entry;
                _waiting[key] = entry;
            }
        }

        private void AddAttachment(JsonElement root, DateTimeOffset? at)
        {
            if (!root.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object) return;
            if (Text(attachment, "type") != "queued_command" || Text(attachment, "commandMode") != "prompt") return;
            if (Flag(attachment, "isMeta") || !attachment.TryGetProperty("prompt", out var prompt)) return;

            var speaker = SpeakerOf(attachment);
            foreach (var piece in Pieces(prompt)) Deliver(at, speaker, piece);
        }

        /// <summary>Adds a message that reached the conversation, and retires its queued copy.</summary>
        private void Deliver(DateTimeOffset? at, Speaker speaker, string raw)
        {
            if (TaskSummary(raw) is { } summary)
            {
                _entries.Add(new Entry(at, Speaker.Task, summary, queued: false));
                return;
            }

            if (IsHarnessMessage(raw)) return;

            var cleaned = Clean(raw);
            if (cleaned.Length == 0) return;

            var key = Key(cleaned);
            if (_waiting.TryGetValue(key, out var queued))
            {
                queued.Delivered = true;
                if (queued.NextWaiting is { } next)
                {
                    next.LastWaiting = queued.LastWaiting;
                    _waiting[key] = next;
                }
                else
                {
                    _waiting.Remove(key);
                }
            }

            _entries.Add(new Entry(at, speaker, cleaned, queued: false));
        }

        /// <summary>The text pieces of a message body: the string itself, or the text blocks of a list.</summary>
        private static IEnumerable<string> Pieces(JsonElement body)
        {
            if (body.ValueKind == JsonValueKind.String)
            {
                if (Str(body) is { } whole) yield return whole;
                yield break;
            }

            if (body.ValueKind != JsonValueKind.Array) yield break;
            foreach (var block in body.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.String && Str(block) is { } bare) yield return bare;
                else if (BlockType(block) == "text" && Text(block, "text") is { } text) yield return text;
            }
        }

        private static Speaker SpeakerOf(JsonElement record)
        {
            if (record.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object)
            {
                switch (Text(origin, "kind"))
                {
                    case "peer": return Speaker.PeerSession;
                    case "coordinator": return Speaker.Coordinator;
                    case "task-notification": return Speaker.Task;
                }
            }

            return Flag(record, "isSidechain") ? Speaker.AgentTask : Speaker.User;
        }

        /// <summary>
        /// A message that is the harness talking, not a person: a background task's notification (kept
        /// through <see cref="TaskSummary"/>), a slash command's output, an interrupt notice.
        /// </summary>
        private static bool IsHarnessMessage(string raw)
        {
            var text = raw.AsSpan().TrimStart();
            return text.StartsWith("<task-notification", StringComparison.Ordinal)
                || text.StartsWith("[Request interrupted", StringComparison.Ordinal);
        }

        private static string? TaskSummary(string raw)
        {
            if (!raw.AsSpan().TrimStart().StartsWith("<task-notification", StringComparison.Ordinal)) return null;
            return TagText(raw, "summary") is { Length: > 0 } summary ? summary : null;
        }

        /// <summary>The text with injected spans removed, and a slash command written as it was typed.</summary>
        private static string Clean(string raw)
        {
            if (TagText(raw, "command-name") is { Length: > 0 } name)
            {
                var args = TagText(raw, "command-args");
                return string.IsNullOrWhiteSpace(args) ? name : name + " " + args;
            }

            var text = raw;
            foreach (var tag in InjectedTags) text = RemoveSpans(text, tag);
            return text.Trim();
        }

        /// <summary>
        /// The text without the spans a tag encloses, tags included, in one pass: removing them one at a time copies
        /// the text for each, which a message of many short spans turns into minutes. An opening tag that is never
        /// closed is left as it is.
        /// </summary>
        private static string RemoveSpans(string text, string tag)
        {
            var open = "<" + tag + ">";
            var close = "</" + tag + ">";
            var start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return text;

            var kept = new StringBuilder(text.Length);
            var from = 0;
            while (start >= 0)
            {
                var end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
                if (end < 0) break;

                kept.Append(text, from, start - from);
                from = end + close.Length;
                start = text.IndexOf(open, from, StringComparison.Ordinal);
            }

            return kept.Append(text, from, text.Length - from).ToString();
        }

        private static string? TagText(string text, string tag)
        {
            var open = "<" + tag + ">";
            var start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;

            start += open.Length;
            var end = text.IndexOf("</" + tag + ">", start, StringComparison.Ordinal);
            return end < 0 ? null : text[start..end].Trim();
        }

        /// <summary>One line naming a tool call and its main argument.</summary>
        private static string Stub(JsonElement toolUse)
        {
            var name = Shorten(Text(toolUse, "name") ?? "tool", 200);
            if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                return "tool: " + name;

            foreach (var key in StubArguments)
            {
                if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    && Str(value) is { } argument && !string.IsNullOrWhiteSpace(argument))
                    return "tool: " + name + " " + Shorten(key == "command" ? WithoutLeadingCd(argument) : argument, StubChars);
            }

            return "tool: " + name;
        }

        /// <summary>
        /// The command without a leading <c>cd &lt;directory&gt; &amp;&amp;</c>. Most shell commands begin with one,
        /// and the directory would fill the line that is cut to <see cref="StubChars"/> characters.
        /// </summary>
        private static string WithoutLeadingCd(string command)
        {
            var text = command.TrimStart();
            if (!text.StartsWith("cd ", StringComparison.Ordinal)) return command;

            var at = 3;
            while (at < text.Length && text[at] == ' ') at++;
            if (at < text.Length && text[at] == '"')
            {
                var close = text.IndexOf('"', at + 1);
                if (close < 0) return command;
                at = close + 1;
            }
            else
            {
                while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] != ';' && text[at] != '&') at++;
            }

            while (at < text.Length && (char.IsWhiteSpace(text[at]) || text[at] is ';' or '&')) at++;
            return at < text.Length ? text[at..] : command;
        }

        /// <summary>The text on one line, cut to <paramref name="max"/> characters without splitting a surrogate pair.</summary>
        private static string Shorten(string text, int max)
        {
            var sb = new StringBuilder(Math.Min(text.Length, max + 1));
            var space = true;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!space) sb.Append(' ');
                    space = true;
                }
                else
                {
                    sb.Append(ch);
                    space = false;
                }

                if (sb.Length >= max) break;
            }

            while (sb.Length > 0 && (sb[^1] == ' ' || char.IsHighSurrogate(sb[^1]))) sb.Length--;
            return sb.ToString();
        }

        private static UInt128 Key(string text) =>
            BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static bool Flag(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        /// <summary>
        /// The string a token holds, or null when it is not valid Unicode: a lone surrogate escape, which a JavaScript
        /// writer produces for a message cut inside an emoji, or bytes that are not UTF-8. The block that holds it is
        /// dropped, and the rest of the file is read.
        /// </summary>
        private static string? Str(JsonElement element)
        {
            try
            {
                return element.GetString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? Str(value)
                : null;

        private static string? BlockType(JsonElement block) => Text(block, "type");

        private static DateTimeOffset? TimeOf(JsonElement root) =>
            Text(root, "timestamp") is { } stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : null;

        public string Render()
        {
            // The header alone says nothing, and every such file would match a search for it.
            if (!_entries.Any(e => !e.Queued || !e.Delivered)) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("Claude Code session transcript\n");
            var title = _customTitle ?? _aiTitle;
            if (!string.IsNullOrWhiteSpace(title)) sb.Append("Title: ").Append(Shorten(title, 200)).Append('\n');
            if (_sessionId is not null) sb.Append("Session: ").Append(Shorten(_sessionId, 200)).Append('\n');
            if (_agentId is not null) sb.Append("Agent: ").Append(Shorten(_agentId, 200)).Append('\n');
            if (_branch is not null) sb.Append("Branch: ").Append(Shorten(_branch, 200)).Append('\n');
            if (_first is { } first) sb.Append("Started: ").Append(Stamp(first)).Append('\n');

            Speaker? current = null;
            DateTimeOffset? sectionStart = null;
            foreach (var entry in _entries)
            {
                if (entry.Queued && entry.Delivered) continue;

                var speaker = entry.Speaker;
                var newSection = current != speaker || entry.At is { } at && sectionStart is { } began && at - began >= SectionGap;
                if (newSection)
                {
                    sb.Append('\n').Append('[');
                    if (entry.At is { } stamp) sb.Append(Stamp(stamp)).Append(' ');
                    sb.Append(Label(speaker)).Append("]\n");
                    current = speaker;
                    sectionStart = entry.At;
                }
                else
                {
                    // A section opened by a message with no time is timed from its first message that has one.
                    sectionStart ??= entry.At;
                }

                AppendLines(sb, entry.Text);
            }

            return sb.ToString();
        }

        private static string Stamp(DateTimeOffset at) =>
            at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

        private static string Label(Speaker speaker) => speaker switch
        {
            Speaker.User => "user",
            Speaker.Assistant => "assistant",
            Speaker.AgentTask => "task for this agent",
            Speaker.PeerSession => "message from another session",
            Speaker.Coordinator => "coordinator",
            _ => "background task",
        };

        /// <summary>Appends the text with its line endings made <c>\n</c> and runs of blank lines made one.</summary>
        private static void AppendLines(StringBuilder sb, string text)
        {
            var blank = false;
            foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length == 0)
                {
                    blank = true;
                    continue;
                }

                if (blank) sb.Append('\n');
                blank = false;
                sb.Append(trimmed).Append('\n');
            }
        }
    }

    /// <summary>
    /// Lines of a stream, as bytes. A line over <see cref="MaxLineBytes"/> is skipped as it is read, so a
    /// transcript with one enormous record does not hold it in memory.
    /// </summary>
    private sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[256 * 1024];
        private byte[] _line = [];
        private int _position;
        private int _length;
        private int _lineLength;
        private bool _tooLong;

        public bool TryRead(out ReadOnlyMemory<byte> line)
        {
            line = default;
            while (true)
            {
                if (_position == _length)
                {
                    _length = stream.Read(_buffer, 0, _buffer.Length);
                    _position = 0;
                    if (_length == 0)
                    {
                        // The last line has no newline. It is returned if it was kept whole.
                        var tail = _lineLength > 0 && !_tooLong;
                        if (tail) line = _line.AsMemory(0, _lineLength);
                        _lineLength = 0;
                        _tooLong = false;
                        return tail;
                    }
                }

                var available = _buffer.AsSpan(_position, _length - _position);
                var newline = available.IndexOf((byte)'\n');
                var take = newline < 0 ? available.Length : newline;

                if (newline >= 0 && _lineLength == 0 && !_tooLong)
                {
                    // The whole line is in the buffer: no copy.
                    line = _buffer.AsMemory(_position, take);
                    _position += take + 1;
                    return true;
                }

                Append(available[..take]);
                _position += take + (newline < 0 ? 0 : 1);
                if (newline < 0) continue;

                var keep = !_tooLong;
                var held = _lineLength;
                _lineLength = 0;
                _tooLong = false;
                if (!keep) continue;

                line = _line.AsMemory(0, held);
                return true;
            }
        }

        private void Append(ReadOnlySpan<byte> piece)
        {
            if (_tooLong || piece.IsEmpty) return;
            if ((long)_lineLength + piece.Length > MaxLineBytes)
            {
                _tooLong = true;
                _lineLength = 0;
                return;
            }

            if (_lineLength + piece.Length > _line.Length)
                Array.Resize(ref _line, (int)Math.Min(MaxLineBytes, Math.Max((long)_line.Length * 2, _lineLength + piece.Length)));

            piece.CopyTo(_line.AsSpan(_lineLength));
            _lineLength += piece.Length;
        }
    }
}
