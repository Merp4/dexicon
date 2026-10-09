using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Dexicon.Infrastructure;

/// <summary>
/// Passes each event to the sink it wraps with its text made safe to print, so that nothing a caller sent
/// can begin a line of the console log.
///
/// <c>{Message:j}</c> escapes U+0000 to U+001F in a logged string and leaves the rest, so U+007F, the C1
/// controls (U+0080 to U+009F, including NEL and CSI), U+2028 and U+2029, and the bidirectional controls
/// reach the console as they were sent. <c>{Exception}</c> writes <see cref="Exception.ToString"/> as it
/// is, and the scope errors quote the corpus name or path a caller sent. This sink replaces those
/// characters in every string a property holds, and renders the exception with <see cref="Render"/>.
///
/// The event is rebuilt for the wrapped sink. Its trace and span ids are carried over when it has them.
/// </summary>
internal sealed class OneLineLogSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    /// <summary>How many levels of inner exceptions <see cref="Render"/> follows.</summary>
    internal const int MaxExceptionDepth = 100;

    /// <summary>How many exceptions <see cref="Render"/> reads across a chain and its aggregates.</summary>
    internal const int MaxExceptionNodes = 1_000;

    /// <summary>How deep a logged sequence, structure or dictionary is sanitised.</summary>
    private const int MaxValueDepth = 8;

    /// <summary>What a replaced character becomes.</summary>
    private const char Marker = (char)0xFFFD;

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
    /// <see cref="Exception.ToString"/>, one line at a time. Every kind of line break ends a line (the ones
    /// <see cref="string.ReplaceLineEndings(string)"/> knows), a line that would start at the first column is
    /// indented, and the control, C1, bidirectional and separator characters in it are replaced, so text
    /// in a message, a stack frame or an override of <c>ToString</c> cannot begin a line of its own. A
    /// stack trace line already starts with spaces and is left as the runtime wrote it.
    ///
    /// The chain is read first, without recursion, up to <see cref="MaxExceptionDepth"/> levels and
    /// <see cref="MaxExceptionNodes"/> exceptions, because <see cref="Exception.ToString"/> recurses once per
    /// inner exception and a chain a hundred thousand deep overflows the stack. Past either limit only the
    /// outermost type and message are written, with a note.
    /// </summary>
    internal static string Render(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (WithinBudget(exception)) return Lines(exception.ToString());

        return Lines($"{exception.GetType()}: {exception.Message}")
            + Environment.NewLine + Indent
            + $"(the exception chain was cut: it nests more than {MaxExceptionDepth} levels or holds more than "
            + $"{MaxExceptionNodes} exceptions)";
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

    private static string Lines(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var rendered = new StringBuilder(text.Length + Indent.Length * lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = Neutralise(lines[i], includeC0: true);
            if (i > 0)
            {
                rendered.Append(Environment.NewLine);
                if (line.Length == 0 || !char.IsWhiteSpace(line[0])) rendered.Append(Indent);
            }

            rendered.Append(line);
        }

        return rendered.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> with the characters that can start or hide a line replaced by U+FFFD.
    /// The C0 controls are included only when <paramref name="includeC0"/> is set, because
    /// <c>{Message:j}</c> already escapes them in a property value.
    /// </summary>
    internal static string Neutralise(string text, bool includeC0)
    {
        var first = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsHostile(text[i], includeC0)) continue;
            first = i;
            break;
        }

        if (first < 0) return text;

        return string.Create(text.Length, (text, includeC0), static (span, state) =>
        {
            for (var i = 0; i < state.text.Length; i++)
                span[i] = IsHostile(state.text[i], state.includeC0) ? Marker : state.text[i];
        });
    }

    private static bool IsHostile(char c, bool includeC0)
    {
        int code = c;
        return code switch
        {
            < 0x20 => includeC0,
            0x7F => true,
            >= 0x80 and <= 0x9F => true,
            0x061C or 0x200E or 0x200F or 0x2028 or 0x2029 => true,
            >= 0x202A and <= 0x202E => true,
            >= 0x2066 and <= 0x2069 => true,
            _ => false,
        };
    }

    private static LogEventPropertyValue Safe(LogEventPropertyValue value, int depth)
    {
        if (depth > MaxValueDepth) return new ScalarValue("...");

        switch (value)
        {
            case ScalarValue { Value: string text }:
                var held = Neutralise(text, includeC0: false);
                return ReferenceEquals(held, text) ? value : new ScalarValue(held);

            case ScalarValue { Value: var other } when IsPlain(other):
                return value;

            case ScalarValue { Value: var other }:
                // A type Serilog formats with ToString, whose text is the type's to write.
                return new ScalarValue(Neutralise(other?.ToString() ?? string.Empty, includeC0: false));

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

    private static bool IsPlain(object? value) =>
        value is null or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double
            or decimal or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly or Guid or Enum;

    /// <summary>An exception whose <see cref="ToString"/> is text already rendered.</summary>
    private sealed class RenderedException(Exception original)
        : Exception(DexiconAuthMiddleware.OneLine(original.Message))
    {
        private readonly string _text = Render(original);

        public override string ToString() => _text;
    }
}
