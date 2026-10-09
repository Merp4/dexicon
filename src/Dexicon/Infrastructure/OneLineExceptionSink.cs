using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Dexicon.Infrastructure;

/// <summary>
/// Passes each event to the sink it wraps with its exception rendered by <see cref="Render"/>, so that
/// <c>{Exception}</c> in <see cref="LogOutput.ConsoleTemplate"/> cannot be made to start a line.
///
/// The template escapes the message (<c>{Message:j}</c>) and writes the exception as
/// <see cref="Exception.ToString"/> does, which holds each exception's message as it is. The scope errors
/// quote the corpus name or path a caller sent, and <c>ScopeExceptionHandler</c> logs them, so a line
/// break in that text began a line that read as a log entry.
/// </summary>
internal sealed class OneLineExceptionSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Exception is null)
        {
            inner.Emit(logEvent);
            return;
        }

        inner.Emit(new LogEvent(
            logEvent.Timestamp, logEvent.Level, new RenderedException(logEvent.Exception), logEvent.MessageTemplate,
            logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value))));
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();

    /// <summary>
    /// The text <see cref="Exception.ToString"/> gives, with each message held to one line by
    /// <see cref="DexiconAuthMiddleware.OneLine"/>: the type and message, the inner exception and the end
    /// marker of its stack trace, the stack trace, and for an <see cref="AggregateException"/> the others it
    /// holds. Nothing else an exception's own <c>ToString</c> may add is written, such as the file name of
    /// a <see cref="FileNotFoundException"/>, which is text the caller may have supplied and which its
    /// message repeats.
    /// </summary>
    internal static string Render(Exception exception)
    {
        var text = new StringBuilder();
        Append(text, exception);
        return text.ToString();
    }

    private static void Append(StringBuilder text, Exception exception)
    {
        text.Append(exception.GetType());
        if (!string.IsNullOrEmpty(exception.Message))
            text.Append(": ").Append(DexiconAuthMiddleware.OneLine(exception.Message));

        if (exception.InnerException is { } inner)
        {
            text.Append(Environment.NewLine).Append(" ---> ");
            Append(text, inner);
            text.Append(Environment.NewLine).Append("   --- End of inner exception stack trace ---");
        }

        if (exception.StackTrace is { } trace)
        {
            // The runtime writes one frame per line. Each goes through OneLine as well, so a frame
            // cannot be the way in if a runtime ever put text of the caller's into one.
            foreach (var frame in trace.Split('\n'))
                text.Append(Environment.NewLine).Append(DexiconAuthMiddleware.OneLine(frame.TrimEnd('\r')));
        }

        if (exception is AggregateException aggregate)
        {
            for (var i = 0; i < aggregate.InnerExceptions.Count; i++)
            {
                if (ReferenceEquals(aggregate.InnerExceptions[i], aggregate.InnerException)) continue;

                text.Append(Environment.NewLine).Append(" ---> (Inner Exception #").Append(i).Append(") ");
                Append(text, aggregate.InnerExceptions[i]);
                text.Append("<---").Append(Environment.NewLine);
            }
        }
    }

    /// <summary>An exception whose <see cref="ToString"/> is text already rendered.</summary>
    private sealed class RenderedException(Exception original)
        : Exception(DexiconAuthMiddleware.OneLine(original.Message))
    {
        private readonly string _text = Render(original);

        public override string ToString() => _text;
    }
}
