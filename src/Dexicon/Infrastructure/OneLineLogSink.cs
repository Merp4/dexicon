using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Dexicon.Infrastructure;

/// <summary>
/// Passes each event to the sink it wraps with its text made safe to print, so that nothing a caller sent
/// can begin a line of the console log.
///
/// <c>{Message:j}</c> quotes a logged string and escapes U+0000 to U+001F, <c>"</c> and <c>\</c>, and leaves
/// the rest, so U+007F, the C1 controls (U+0080 to U+009F, including NEL and CSI), U+2028, U+2029, the
/// bidirectional controls and the other format characters reach the console as they were sent.
/// <c>{Exception}</c> writes <see cref="Exception.ToString"/> as it is, and the scope errors quote the corpus
/// name or path a caller sent. This sink replaces those characters (see <see cref="LogText.IsHostile"/>) in
/// every string a property holds, and renders the exception with <see cref="Render"/>.
///
/// The event is rebuilt for the wrapped sink. Its trace and span ids are carried over when it has them.
/// Text that cannot be read (a property whose <c>ToString</c> throws, an exception whose message does) is
/// replaced by a note, so that one such value does not drop the whole entry.
/// </summary>
internal sealed class OneLineLogSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    /// <summary>How many levels of inner exceptions <see cref="Render"/> follows.</summary>
    internal const int MaxExceptionDepth = 100;

    /// <summary>How many exceptions <see cref="Render"/> reads across a chain and its aggregates.</summary>
    internal const int MaxExceptionNodes = 1_000;

    /// <summary>The most characters of an exception's message that are written.</summary>
    internal const int MaxMessage = 4_000;

    /// <summary>How deep a logged sequence, structure or dictionary is sanitised.</summary>
    internal const int MaxValueDepth = 8;

    /// <summary>Put before a line of an exception that would otherwise start at the first column.</summary>
    private const string Indent = "    ";

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        Exception? exception = logEvent.Exception is null ? null : new RenderedException(logEvent.Exception);
        var properties = logEvent.Properties.Select(p => new LogEventProperty(p.Key, Safe(p.Value, 0))).ToList();

        inner.Emit(logEvent.TraceId is { } trace && logEvent.SpanId is { } span
            ? new LogEvent(logEvent.Timestamp, logEvent.Level, exception, logEvent.MessageTemplate, properties, trace, span)
            : new LogEvent(logEvent.Timestamp, logEvent.Level, exception, logEvent.MessageTemplate, properties));
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();

    /// <summary>
    /// <see cref="Exception.ToString"/>, one line at a time. Every kind of line break ends a line, a line
    /// that would start at the first column is indented, and the control, format and separator characters in
    /// it are replaced, so text in a message, a stack frame or an override of <c>ToString</c> cannot begin a
    /// line of its own. A line is left as it is only when it starts the way the runtime starts its own:
    /// the first line with the exception's type name, a stack frame with <c>   at </c>, the end of an inner
    /// exception's trace with <c>   --- </c>, and an inner exception with <c> ---&gt; </c>.
    ///
    /// The chain is read first, without recursion, up to <see cref="MaxExceptionDepth"/> levels and
    /// <see cref="MaxExceptionNodes"/> exceptions, because <see cref="Exception.ToString"/> recurses once per
    /// inner exception and a chain a hundred thousand deep overflows the stack. Past either limit only the
    /// outermost type and message are written, with a note. A message is cut at <see cref="MaxMessage"/>
    /// characters, since an aggregate's message holds those of everything under it.
    /// </summary>
    internal static string Render(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var type = exception.GetType().ToString();
        if (!WithinBudget(exception))
            return Lines($"{type}: {SafeMessage(exception)}", type)
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

        return Lines(text, type);
    }

    private static string SafeMessage(Exception exception)
    {
        try
        {
            return LogText.Cut(exception.Message ?? string.Empty, MaxMessage);
        }
        catch (Exception)
        {
            return "(its message could not be read)";
        }
    }

    private static bool WithinBudget(Exception root)
    {
        var pending = new Stack<(Exception Exception, int Depth)>();
        pending.Push((root, 1));
        var nodes = 0;

        while (pending.TryPop(out var item))
        {
            if (++nodes > MaxExceptionNodes || item.Depth > MaxExceptionDepth) return false;

            if (item.Exception is AggregateException aggregate)
            {
                // Without this a million members are pushed before the node limit is seen. The result is the
                // same with it removed, only slower.
                if (aggregate.InnerExceptions.Count > MaxExceptionNodes) return false;
                foreach (var member in aggregate.InnerExceptions) pending.Push((member, item.Depth + 1));
            }
            else if (item.Exception.InnerException is { } next)
            {
                pending.Push((next, item.Depth + 1));
            }
        }

        return true;
    }

    private static string Lines(string text, string type)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var rendered = new StringBuilder(text.Length + Indent.Length * lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = LogText.Neutralise(lines[i], includeC0: true);
            if (i > 0) rendered.Append(Environment.NewLine);
            if (NeedsIndent(line, i == 0, type)) rendered.Append(Indent);

            rendered.Append(line);
        }

        return rendered.ToString();
    }

    private static bool NeedsIndent(string line, bool first, string type) => first
        ? !line.StartsWith(type, StringComparison.Ordinal)
        : !(line.StartsWith("   at ", StringComparison.Ordinal)
            || line.StartsWith("   --- ", StringComparison.Ordinal)
            || line.StartsWith(" ---> ", StringComparison.Ordinal));

    private static LogEventPropertyValue Safe(LogEventPropertyValue value, int depth)
    {
        if (depth > MaxValueDepth) return new ScalarValue("...");

        switch (value)
        {
            case ScalarValue { Value: string text }:
                var held = LogText.Neutralise(text, includeC0: false);
                return ReferenceEquals(held, text) ? value : new ScalarValue(held);

            case ScalarValue { Value: var other } when IsPlain(other):
                return value;

            case ScalarValue { Value: var other }:
                // A type Serilog formats with ToString, whose text is the type's to write.
                return new ScalarValue(LogText.Neutralise(TextOf(other), includeC0: false));

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
