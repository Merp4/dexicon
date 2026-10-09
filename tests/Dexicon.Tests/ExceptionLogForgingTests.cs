using System.Globalization;
using Dexicon.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace Dexicon.Tests;

/// <summary>
/// The console template renders <c>{Message:j}</c> escaped and <c>{Exception}</c> raw, so an exception
/// message that holds a line break could begin a line that reads as a log entry. The scope errors
/// (<c>ScopeResolver</c>) quote a caller's corpus name or path in theirs, and
/// <c>ScopeExceptionHandler</c> logs them at Debug. See <see cref="LogForgingTests"/> for the message half.
/// </summary>
public sealed class ExceptionLogForgingTests
{
    private const string ForgedLine = "[10:00:00Z INF] forged";

    // UtcTime is the property UtcTimestampEnricher adds, which the template's timestamp reads.
    private static LogEvent EventFor(Exception? exception, string template) =>
        new(DateTimeOffset.UnixEpoch, LogEventLevel.Debug, exception,
            new MessageTemplateParser().Parse(template),
            [
                new LogEventProperty("Title", new ScalarValue("Unknown or unreadable corpus")),
                new LogEventProperty("UtcTime", new ScalarValue(DateTime.UnixEpoch)),
            ]);

    /// <summary>Formats what reaches it with the console template, as the console sink does.</summary>
    private sealed class TemplateSink(StringWriter writer) : ILogEventSink
    {
        private readonly MessageTemplateTextFormatter _template =
            new(LogOutput.ConsoleTemplate, CultureInfo.InvariantCulture);

        public void Emit(LogEvent logEvent) => _template.Format(logEvent, writer);
    }

    /// <summary>Renders as the application's console does: <see cref="OneLineExceptionSink"/> in front of the template.</summary>
    private static string Render(Exception exception)
    {
        var writer = new StringWriter();
        new OneLineExceptionSink(new TemplateSink(writer)).Emit(EventFor(exception, "Refused: {Title}"));
        return writer.ToString();
    }

    /// <summary>The ways a reader ends a line: \n, \r, \r\n, NEL, and the Unicode separators.</summary>
    private static string[] Lines(string rendered) =>
        rendered.Split(["\r\n", "\n", "\r", "\u0085", "\u2028", "\u2029"], StringSplitOptions.None);

    private static Exception Thrown(Func<Exception> make)
    {
        try { throw make(); }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void AnExceptionMessageHoldingALineBreakCannotStartALogLine()
    {
        var thrown = Thrown(() => new InvalidOperationException($"Unknown corpus 'x\n{ForgedLine}'."));

        var lines = Lines(Render(thrown));

        lines.ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
        lines[0].ShouldStartWith("[00:00:00Z DBG] ");
        lines.ShouldContain(l => l.Contains("forged", StringComparison.Ordinal), "the text is still reported");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u0085")]
    public void EveryKindOfLineBreakInAMessageIsHeld(string lineBreak)
    {
        var thrown = Thrown(() => new InvalidOperationException($"bad{lineBreak}{ForgedLine}"));

        var lines = Lines(Render(thrown));

        lines.ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("forged", StringComparison.Ordinal), "the text is still reported");
    }

    [Fact]
    public void AnEscapeSequenceInAMessageIsHeld()
    {
        var thrown = Thrown(() => new InvalidOperationException("clears the screen: \u001B[2J"));

        Render(thrown).ShouldNotContain("\u001B");
    }

    [Fact]
    public void AnInnerExceptionsMessageIsHeldToo()
    {
        var thrown = Thrown(() => new InvalidOperationException("outer", new ArgumentException($"inner\n{ForgedLine}")));

        Lines(Render(thrown)).ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
    }

    [Fact]
    public void EachExceptionOfAnAggregateHasItsMessageHeld()
    {
        var thrown = Thrown(() => new AggregateException(
            "several",
            new InvalidOperationException($"first\n{ForgedLine}"),
            new InvalidOperationException($"second\n{ForgedLine}")));

        var rendered = Render(thrown);

        Lines(rendered).ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
        rendered.ShouldContain("(Inner Exception #1)");
    }

    [Fact]
    public void AnExceptionWithOrdinaryTextRendersAsItDoesWithoutTheSink()
    {
        // The stack trace, the inner exception and its end marker are the runtime's own format.
        var thrown = Thrown(() => new InvalidOperationException("outer", Thrown(() => new ArgumentException("inner"))));

        var lines = Lines(Render(thrown));

        string.Join(Environment.NewLine, lines.Skip(1)).TrimEnd().ShouldBe(thrown.ToString());
        lines.ShouldContain(l => l.Contains("   at Dexicon.Tests.ExceptionLogForgingTests.Thrown", StringComparison.Ordinal));
        lines.ShouldContain("   --- End of inner exception stack trace ---");
    }

    [Fact]
    public void AnAggregateWithOrdinaryTextRendersAsItDoesWithoutTheSink()
    {
        var thrown = Thrown(() => new AggregateException(
            "several", Thrown(() => new InvalidOperationException("first")), Thrown(() => new ArgumentException("second"))));

        var lines = Lines(Render(thrown));

        string.Join(Environment.NewLine, lines.Skip(1)).TrimEnd().ShouldBe(thrown.ToString().TrimEnd());
    }

    [Fact]
    public void AnEventWithNoExceptionIsUnchanged()
    {
        var writer = new StringWriter();

        new OneLineExceptionSink(new TemplateSink(writer)).Emit(EventFor(null, "Workers started"));

        writer.ToString().TrimEnd().ShouldBe("[00:00:00Z DBG] Workers started");
    }

    [Fact]
    public void AnEventKeepsItsPropertiesAndLevelWhenItsExceptionIsRendered()
    {
        var writer = new StringWriter();

        new OneLineExceptionSink(new TemplateSink(writer))
            .Emit(EventFor(Thrown(() => new InvalidOperationException("x")), "Refused: {Title}"));

        writer.ToString().ShouldStartWith("[00:00:00Z DBG] Refused: \"Unknown or unreadable corpus\"");
    }

    [Fact]
    public void TheConsoleSinkTheApplicationUsesPrintsAForgedMessageOnOneLine()
    {
        // The real console sink behind the real wrapper, as Program.cs builds it, with Console.Out replaced.
        // Not parallel with other tests that write to the console.
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        try
        {
            using var logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.With<UtcTimestampEnricher>()
                .WriteTo.OneLineConsole()
                .CreateLogger();
            logger.Debug(Thrown(() => new InvalidOperationException($"bad\n{ForgedLine}")), "Refused: {Title}", "t");
        }
        finally
        {
            Console.SetOut(original);
        }

        var lines = Lines(captured.ToString());
        lines.Length.ShouldBeGreaterThan(1);
        lines.ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("forged", StringComparison.Ordinal));
    }
}
