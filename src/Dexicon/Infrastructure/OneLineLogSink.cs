using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Dexicon.Infrastructure;

/// <summary>
/// Passes each event to the sink it wraps with its text made safe to print, so that text a caller sent
/// cannot begin a line of the console log or reach the terminal as a control sequence.
///
/// <c>{Message:j}</c> quotes a logged string and escapes U+0000 to U+001F, <c>"</c> and <c>\</c>, and leaves
/// the rest, so U+007F, the C1 controls (U+0080 to U+009F, including NEL and CSI), U+2028, U+2029, the
/// bidirectional controls and the other format characters reach the console as they were sent.
/// <c>{Exception}</c> writes <see cref="Exception.ToString"/> as it is, and the scope errors quote the corpus
/// name or path a caller sent. This sink replaces those characters (see <see cref="LogText.IsHostile"/>) in
/// every string a property holds, renders the exception with <see cref="Render"/>, and does the same to the
/// text of the message template (see <see cref="TemplateText"/>).
///
/// The template is the code's. A caller's text reaches it only when a call interpolates a value into the
/// template instead of passing it as an argument, which the build refuses (CA2254 is an error here), so the
/// work on the template is a second guard and not the first.
///
/// The event is rebuilt for the wrapped sink. Its trace and span ids are carried over when it has them.
/// Text that cannot be read (a property whose <c>ToString</c> throws, an exception whose message does) is
/// replaced by a note, so that one such value does not drop the whole entry. A string is cut at
/// <see cref="MaxProperty"/> characters, a line of an exception at <see cref="MaxLine"/>, and the whole
/// exception at <see cref="MaxTotal"/>.
/// </summary>
internal sealed class OneLineLogSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    /// <summary>How many levels of inner exceptions <see cref="Render"/> follows.</summary>
    internal const int MaxExceptionDepth = 100;

    /// <summary>How many exceptions <see cref="Render"/> reads across a chain and its aggregates.</summary>
    internal const int MaxExceptionNodes = 1_000;

    /// <summary>The most characters of a line of an exception that are written, and of an exception's message.</summary>
    internal const int MaxLine = 4_000;

    /// <summary>The most characters of an exception that are written.</summary>
    internal const int MaxTotal = 64_000;

    /// <summary>The most characters of a string property that are written.</summary>
    internal const int MaxProperty = 8_000;

    /// <summary>How deep a logged sequence, structure or dictionary is sanitised.</summary>
    internal const int MaxValueDepth = 8;

    /// <summary>Put before a line of an exception that would otherwise start at the first column.</summary>
    private const string Indent = "    ";

    private const string EndOfInnerTrace = "   --- End of inner exception stack trace ---";

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        Exception? exception = logEvent.Exception is null ? null : new RenderedException(logEvent.Exception);
        var properties = logEvent.Properties.Select(p => new LogEventProperty(p.Key, Safe(p.Value, 0))).ToList();
        var template = SafeTemplate(logEvent.MessageTemplate);

        inner.Emit(logEvent.TraceId is { } trace && logEvent.SpanId is { } span
            ? new LogEvent(logEvent.Timestamp, logEvent.Level, exception, template, properties, trace, span)
            : new LogEvent(logEvent.Timestamp, logEvent.Level, exception, template, properties));
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();

    private static MessageTemplate SafeTemplate(MessageTemplate template)
    {
        var safe = TemplateText(template.Text);
        return ReferenceEquals(safe, template.Text) ? template : new MessageTemplateParser().Parse(safe);
    }

    /// <summary>
    /// The text of a message template with the characters of <see cref="LogText.IsHostile"/> replaced, every
    /// kind of line break treated as one, and a line after the first that does not start with a space given
    /// four, so text after a line break cannot begin a line. A line that starts with a space is left, which
    /// keeps a banner written over several lines as it is. The same instance when nothing changes.
    /// </summary>
    internal static string TemplateText(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Length > 0 && lines[i][0] != ' ')
                lines[i] = Indent + lines[i];

        var rebuilt = LogText.Neutralise(string.Join('\n', lines), includeC0: false);
        // The line breaks were kept, and the other control characters are replaced with the rest.
        rebuilt = ReplaceControls(rebuilt);
        return string.Equals(rebuilt, text, StringComparison.Ordinal) ? text : rebuilt;
    }

    private static string ReplaceControls(string text)
    {
        if (!text.Any(c => c < 0x20 && c != '\n')) return text;

        return string.Concat(text.Select(c => c < 0x20 && c != '\n' ? LogText.Marker : c));
    }

    /// <summary>
    /// <see cref="Exception.ToString"/>, one line at a time. Every kind of line break ends a line, a line
    /// that would start at the first column is indented, and the control, format and separator characters in
    /// it are replaced, so text in a message, a stack frame or an override of <c>ToString</c> cannot begin a
    /// line of its own.
    ///
    /// A line is left as it is only when it is one the runtime wrote, and that is decided by comparing it with
    /// what the runtime would write for this chain: the first line starts with the exception's type name; a
    /// stack frame is a line of an exception's own <see cref="Exception.StackTrace"/> that starts with
    /// <c>   at </c> or <c>   --- </c>, with the <c>&lt;---</c> that ends a member of an aggregate; the end
    /// of an inner trace is that exact line; and an inner exception starts with <c> ---&gt; </c> and the type
    /// of an exception in the chain. A message that imitates one of these with the type of a real inner
    /// exception is not told apart.
    ///
    /// The chain is read first, without recursion, up to <see cref="MaxExceptionDepth"/> levels and
    /// <see cref="MaxExceptionNodes"/> exceptions, because <see cref="Exception.ToString"/> recurses once per
    /// inner exception and a chain a hundred thousand deep overflows the stack. Past either limit only the
    /// outermost type and message are written, with a note. A line is cut at <see cref="MaxLine"/> characters
    /// and the whole at <see cref="MaxTotal"/>; <see cref="Exception.ToString"/> still builds the text first.
    /// </summary>
    internal static string Render(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var type = exception.GetType().ToString();
        if (!Walk(exception, out var runtime))
            return Lines($"{type}: {SafeMessage(exception)}", type, new Runtime())
                   + Environment.NewLine + Indent
                   + $"(the exception chain was cut: it nests more than {MaxExceptionDepth} levels or holds more than "
                   + $"{MaxExceptionNodes} exceptions)";

        string text;
        try
        {
            text = exception.ToString();
        }
        catch (Exception)
        {
            // A message, a stack trace or an override that throws. The type is all that can be said.
            return $"{type}: (its text could not be read)";
        }

        return Lines(text, type, runtime);
    }

    private static string SafeMessage(Exception exception)
    {
        try
        {
            return LogText.Cut(exception.Message ?? string.Empty, MaxLine);
        }
        catch (Exception)
        {
            return "(its message could not be read)";
        }
    }

    /// <summary>What the runtime would write for a chain, to tell a line it wrote from one a message holds.</summary>
    private sealed class Runtime
    {
        public HashSet<string> StackLines { get; } = new(StringComparer.Ordinal);

        public List<string> InnerPrefixes { get; } = [];
    }

    /// <summary>
    /// Reads the chain without recursion. False when it is deeper or larger than the limits, and then
    /// <paramref name="runtime"/> is incomplete.
    /// </summary>
    private static bool Walk(Exception root, out Runtime runtime)
    {
        runtime = new Runtime();
        var pending = new Stack<(Exception Exception, int Depth, string? Prefix)>();
        pending.Push((root, 1, null));
        var nodes = 0;

        while (pending.TryPop(out var item))
        {
            if (++nodes > MaxExceptionNodes || item.Depth > MaxExceptionDepth) return false;

            if (item.Prefix is not null) runtime.InnerPrefixes.Add(item.Prefix + item.Exception.GetType());
            foreach (var line in StackLinesOf(item.Exception)) runtime.StackLines.Add(line);

            if (item.Exception is AggregateException aggregate)
            {
                // Without this a million members are pushed before the node limit is seen. The result is the
                // same with it removed, only slower.
                if (aggregate.InnerExceptions.Count > MaxExceptionNodes) return false;
                for (var i = 0; i < aggregate.InnerExceptions.Count; i++)
                    pending.Push((aggregate.InnerExceptions[i], item.Depth + 1,
                        ReferenceEquals(aggregate.InnerExceptions[i], aggregate.InnerException)
                            ? " ---> "
                            : $" ---> (Inner Exception #{i}) "));
            }
            else if (item.Exception.InnerException is { } next)
            {
                pending.Push((next, item.Depth + 1, " ---> "));
            }
        }

        return true;
    }

    private static string[] StackLinesOf(Exception exception)
    {
        string? trace;
        try
        {
            trace = exception.StackTrace;
        }
        catch (Exception)
        {
            return [];
        }

        return trace is null ? [] : trace.ReplaceLineEndings("\n").Split('\n');
    }

    private static string Lines(string text, string type, Runtime runtime)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var rendered = new StringBuilder(Math.Min(text.Length, MaxTotal) + Indent.Length * lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var original = LogText.Cut(lines[i], MaxLine);
            var line = LogText.Neutralise(original, includeC0: true);
            if (i > 0) rendered.Append(Environment.NewLine);
            if (NeedsIndent(original, i == 0, type, runtime)) rendered.Append(Indent);

            rendered.Append(line);
            if (rendered.Length <= MaxTotal) continue;

            rendered.Append(Environment.NewLine).Append(Indent).Append("(the rest of the exception was cut)");
            break;
        }

        return rendered.ToString();
    }

    private static bool NeedsIndent(string line, bool first, string type, Runtime runtime)
    {
        if (first) return !line.StartsWith(type, StringComparison.Ordinal);
        if (line == EndOfInnerTrace) return false;

        var frame = line.EndsWith("<---", StringComparison.Ordinal) ? line[..^4] : line;
        if ((frame.StartsWith("   at ", StringComparison.Ordinal) || frame.StartsWith("   --- ", StringComparison.Ordinal))
            && runtime.StackLines.Contains(frame))
            return false;

        return !runtime.InnerPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));
    }

    private static LogEventPropertyValue Safe(LogEventPropertyValue value, int depth)
    {
        if (depth > MaxValueDepth) return new ScalarValue("...");

        switch (value)
        {
            case ScalarValue { Value: string text }:
                var held = LogText.Neutralise(LogText.Cut(text, MaxProperty), includeC0: false);
                return ReferenceEquals(held, text) ? value : new ScalarValue(held);

            case ScalarValue { Value: var other } when IsPlain(other):
                return value;

            case ScalarValue { Value: var other }:
                // A type Serilog formats with ToString, whose text is the type's to write.
                return new ScalarValue(LogText.Neutralise(LogText.Cut(TextOf(other), MaxProperty), includeC0: false));

            case SequenceValue sequence:
                return new SequenceValue(sequence.Elements.Select(e => Safe(e, depth + 1)).ToList());

            case StructureValue structure:
                return new StructureValue(
                    structure.Properties.Select(p => new LogEventProperty(p.Name, Safe(p.Value, depth + 1))).ToList(),
                    structure.TypeTag);

            case DictionaryValue dictionary:
                return new DictionaryValue(dictionary.Elements.Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                    Safe(e.Key, depth + 1) as ScalarValue ?? new ScalarValue("..."), Safe(e.Value, depth + 1))).ToList());

            default:
                return value;
        }
    }

    private static string TextOf(object? value)
    {
        try
        {
            return value?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return $"(a value of type {value?.GetType()} whose text could not be read)";
        }
    }

    private static bool IsPlain(object? value) =>
        value is null or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double
            or decimal or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly or Guid or Enum;

    /// <summary>An exception whose <see cref="ToString"/> is text already rendered.</summary>
    private sealed class RenderedException(Exception original) : Exception(OneLineMessage(original))
    {
        private readonly string _text = Render(original);

        private static string OneLineMessage(Exception original) => LogText.OneLine(SafeMessage(original));

        public override string ToString() => _text;
    }
}
