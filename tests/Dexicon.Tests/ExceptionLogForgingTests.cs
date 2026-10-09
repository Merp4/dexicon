using System.Diagnostics;
using System.Globalization;
using Dexicon.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace Dexicon.Tests;

/// <summary>Tests that replace <see cref="Console.Out"/> run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Console output";
}

/// <summary>
/// The console template renders <c>{Message:j}</c> with U+0000 to U+001F escaped and the rest as sent, and
/// <c>{Exception}</c> raw, so a logged string or an exception message that holds a line break could begin a
/// line that reads as a log entry. The scope errors (<c>ScopeResolver</c>) quote a caller's corpus name or
/// path in theirs, and <c>ScopeExceptionHandler</c> logs them at Debug. See <see cref="LogForgingTests"/>
/// for the message half. Every case here runs the sink that Program.cs puts in front of the console.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class ExceptionLogForgingTests
{
    private const string ForgedLine = "[10:00:00Z INF] forged";

    private static readonly string Esc = ((char)0x1B).ToString();

    // UtcTime is the property UtcTimestampEnricher adds, which the template's timestamp reads.
    private static LogEvent EventFor(Exception? exception, string template, object? title = null) =>
        new(DateTimeOffset.UnixEpoch, LogEventLevel.Debug, exception,
            new MessageTemplateParser().Parse(template),
            [
                new LogEventProperty("Title", title as LogEventPropertyValue ?? new ScalarValue(title ?? "Unknown or unreadable corpus")),
                new LogEventProperty("UtcTime", new ScalarValue(DateTime.UnixEpoch)),
            ]);

    /// <summary>Formats what reaches it with the console template, as the console sink does.</summary>
    private sealed class TemplateSink(StringWriter writer) : ILogEventSink
    {
        private readonly MessageTemplateTextFormatter _template =
            new(LogOutput.ConsoleTemplate, CultureInfo.InvariantCulture);

        public void Emit(LogEvent logEvent) => _template.Format(logEvent, writer);
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public LogEvent? Last { get; private set; }

        public void Emit(LogEvent logEvent) => Last = logEvent;
    }

    /// <summary>Renders as the application's console does: <see cref="OneLineLogSink"/> in front of the template.</summary>
    private static string Render(LogEvent logEvent)
    {
        var writer = new StringWriter();
        new OneLineLogSink(new TemplateSink(writer)).Emit(logEvent);
        return writer.ToString();
    }

    private static string Render(Exception exception) => Render(EventFor(exception, "Refused: {Title}"));

    /// <summary>The ways a reader ends a line: \n, \r, \r\n, NEL, and the Unicode separators.</summary>
    private static string[] Lines(string rendered) =>
        rendered.Split(["\r\n", "\n", "\r", "\u0085", "\u2028", "\u2029"], StringSplitOptions.None);

    private static Exception Thrown(Func<Exception> make)
    {
        try { throw make(); }
        catch (Exception ex) { return ex; }
    }

    private static void ShouldNotStartAnyLineWithTheForgedEntry(string rendered) =>
        Lines(rendered).ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));

    [Fact]
    public void AnExceptionMessageHoldingALineBreakCannotStartALogLine()
    {
        var thrown = Thrown(() => new InvalidOperationException($"Unknown corpus 'x\n{ForgedLine}'."));

        var lines = Lines(Render(thrown));

        lines.ShouldNotContain(l => l.StartsWith(ForgedLine, StringComparison.Ordinal));
        lines[0].ShouldStartWith("[00:00:00Z DBG] ");
        lines.ShouldContain(l => l.StartsWith("    " + ForgedLine, StringComparison.Ordinal), "reported, indented");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u0085")]
    [InlineData("\f")]
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
        var thrown = Thrown(() => new InvalidOperationException("clears the screen: " + Esc + "[2J"));

        Render(thrown).ShouldNotContain(Esc);
    }

    [Fact]
    public void AControlCharacterInAMessageIsReplacedAndTheRestOfTheTextIsKept()
    {
        var thrown = Thrown(() => new InvalidOperationException("a" + (char)0x7F + "b" + (char)0x9B + "c" + (char)0x202E + "d\te"));

        Render(thrown).ShouldContain("a�b�c�d�e");
    }

    [Fact]
    public void AnInnerExceptionsMessageIsHeldAndStillReported()
    {
        var thrown = Thrown(() => new InvalidOperationException("outer", new ArgumentException($"inner\n{ForgedLine}")));

        var rendered = Render(thrown);

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        Lines(rendered).ShouldContain(l => l.Contains("ArgumentException: inner", StringComparison.Ordinal));
        Lines(rendered).ShouldContain(l => l.StartsWith("    " + ForgedLine, StringComparison.Ordinal));
    }

    [Fact]
    public void ADeeplyNestedInnerExceptionsMessageIsHeldAndStillReported()
    {
        var thrown = Thrown(() => new InvalidOperationException(
            "one", new ArgumentException("two", new FormatException("three", new IOException($"four\r\n{ForgedLine}")))));

        var rendered = Render(thrown);

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        Lines(rendered).ShouldContain(l => l.Contains("IOException: four", StringComparison.Ordinal));
    }

    [Fact]
    public void EachExceptionOfAnAggregateHasItsMessageHeldAndStillReported()
    {
        var thrown = Thrown(() => new AggregateException(
            "several",
            new InvalidOperationException($"first\n{ForgedLine}"),
            new InvalidOperationException($"second\n{ForgedLine}")));

        var rendered = Render(thrown);

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        rendered.ShouldContain("(Inner Exception #1)");
        Lines(rendered).ShouldContain(l => l.Contains("InvalidOperationException: second", StringComparison.Ordinal));
    }

    [Fact]
    public void AForgedLineInALoggedArgumentCannotStartALogLine()
    {
        var rendered = Render(EventFor(null, "Refused: {Title}", $"x\n{ForgedLine}"));

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        rendered.ShouldContain("forged");
    }

    [Theory]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0x85)]
    [InlineData(0x9B)]
    [InlineData(0x9F)]
    [InlineData(0x061C)]
    [InlineData(0x200E)]
    [InlineData(0x200F)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x202A)]
    [InlineData(0x202B)]
    [InlineData(0x202C)]
    [InlineData(0x202D)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0x2067)]
    [InlineData(0x2068)]
    [InlineData(0x2069)]
    public void ACharacterTheTemplateLeavesAloneIsReplacedInALoggedArgument(int codePoint)
    {
        var hostile = ((char)codePoint).ToString();

        var rendered = Render(EventFor(null, "Refused: {Title}", $"bad{hostile}{ForgedLine}"));

        rendered.ShouldNotContain(hostile);
        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        rendered.ShouldContain("bad�" + ForgedLine);
    }

    [Fact]
    public void ACharacterInsideASequenceAStructureAndADictionaryIsReplacedToo()
    {
        var hostile = ((char)0x2028).ToString();
        var sequence = new SequenceValue([new ScalarValue("a" + hostile), new ScalarValue(1)]);
        var structure = new StructureValue([new LogEventProperty("Name", new ScalarValue("b" + hostile))], "T");
        var dictionary = new DictionaryValue([
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("k" + hostile), new ScalarValue("v" + hostile)),
        ]);

        foreach (var value in new LogEventPropertyValue[] { sequence, structure, dictionary })
        {
            var rendered = Render(EventFor(null, "Refused: {Title}", value));

            rendered.ShouldNotContain(hostile);
        }
    }

    [Fact]
    public void AValueNestedPastTheDepthLimitIsNotWritten()
    {
        var hostile = ((char)0x2028).ToString();
        LogEventPropertyValue value = new ScalarValue("deep" + hostile);
        for (var i = 0; i < 20; i++) value = new SequenceValue([value]);

        var rendered = Render(EventFor(null, "Refused: {Title}", value));

        rendered.ShouldNotContain(hostile);
        rendered.ShouldContain("...");
    }

    [Fact]
    public void ATypeLoggedByItsToStringHasThatTextReplaced()
    {
        var hostile = ((char)0x2028).ToString();

        var rendered = Render(EventFor(null, "Refused: {Title}", new Uri("http://example.test/a" + "b")));
        var custom = Render(EventFor(null, "Refused: {Title}", new Hostile("x" + hostile)));

        rendered.ShouldContain("http://example.test/ab");
        custom.ShouldNotContain(hostile);
        custom.ShouldContain("x�");
    }

    private sealed class Hostile(string text)
    {
        public override string ToString() => text;
    }

    [Fact]
    public void ANumberAndADateAreLoggedAsTheyWere()
    {
        Render(EventFor(null, "Refused: {Title}", 42)).ShouldContain("Refused: 42");
    }

    [Fact]
    public void AnExceptionWhoseToStringIsHostileCannotStartALogLine()
    {
        var thrown = new HostileTextException();

        var rendered = Render(thrown);

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        Lines(rendered).ShouldContain(l => l.Contains("forged", StringComparison.Ordinal));
        rendered.ShouldNotContain(Esc);
    }

    [Fact]
    public void AStackFrameThatHoldsAForgedLineIsHeld()
    {
        var thrown = new HostileTextException();

        var rendered = OneLineLogSink.Render(thrown);

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        Lines(rendered).ShouldContain(l => l.StartsWith("    " + ForgedLine + " frame", StringComparison.Ordinal));
    }

    /// <summary>An exception whose own text, including its stack trace, is the attacker's.</summary>
    private sealed class HostileTextException : Exception
    {
        public override string? StackTrace => $"   at A.B()\n{ForgedLine} frame\n   at C.D()";

        public override string ToString() => $"{GetType()}: ordinary\n{ForgedLine} extra {Esc}[2J{Environment.NewLine}{StackTrace}";
    }

    [Fact]
    public void AnExceptionWithOrdinaryTextRendersAsItsToStringDoes()
    {
        var thrown = Thrown(() => new InvalidOperationException("outer", Thrown(() => new ArgumentException("inner"))));

        var lines = Lines(Render(thrown));

        string.Join(Environment.NewLine, lines.Skip(1)).TrimEnd().ShouldBe(thrown.ToString());
        lines.ShouldContain(l => l.Contains("   at Dexicon.Tests.ExceptionLogForgingTests.Thrown", StringComparison.Ordinal));
        lines.ShouldContain("   --- End of inner exception stack trace ---");
    }

    [Fact]
    public void AnAggregateWithOrdinaryTextRendersAsItsToStringDoes()
    {
        var thrown = Thrown(() => new AggregateException(
            "several", Thrown(() => new InvalidOperationException("first")), Thrown(() => new ArgumentException("second"))));

        var lines = Lines(Render(thrown));

        string.Join(Environment.NewLine, lines.Skip(1)).TrimEnd().ShouldBe(thrown.ToString().TrimEnd());
    }

    [Fact]
    public void WhatAnExceptionTypeAddsToItsToStringIsKept()
    {
        var thrown = Thrown(() => new FileNotFoundException("missing", "reports/q3.csv"));

        var lines = Lines(Render(thrown));

        lines.ShouldContain(l => l.StartsWith("File name: 'reports/q3.csv'", StringComparison.Ordinal) || l.StartsWith("    File name: 'reports/q3.csv'", StringComparison.Ordinal));
    }

    [Fact]
    public void AChainOfAHundredThousandIsCutWithoutRecursingIntoIt()
    {
        Exception chain = new InvalidOperationException("root");
        for (var i = 0; i < 100_000; i++) chain = new InvalidOperationException($"level {i}\n{ForgedLine}", chain);

        var rendered = OneLineLogSink.Render(chain);

        rendered.ShouldStartWith("System.InvalidOperationException: level 99999");
        rendered.ShouldContain("the exception chain was cut");
        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        rendered.Length.ShouldBeLessThan(1_000);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void AChainIsCutPastAHundredLevels(int levels, bool cut)
    {
        Exception chain = new InvalidOperationException("level 1");
        for (var i = 2; i <= levels; i++) chain = new InvalidOperationException($"level {i}", chain);

        OneLineLogSink.Render(chain).Contains("the exception chain was cut", StringComparison.Ordinal).ShouldBe(cut);
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1_000, true)]
    public void AnAggregateIsCutPastAThousandExceptions(int members, bool cut)
    {
        var aggregate = new AggregateException(Enumerable.Range(0, members).Select(i => new InvalidOperationException($"m{i}")));

        OneLineLogSink.Render(aggregate).Contains("the exception chain was cut", StringComparison.Ordinal).ShouldBe(cut);
    }

    [Fact]
    public void AnEventWithNoExceptionIsUnchanged()
    {
        var writer = new StringWriter();

        new OneLineLogSink(new TemplateSink(writer)).Emit(EventFor(null, "Workers started"));

        writer.ToString().TrimEnd().ShouldBe("[00:00:00Z DBG] Workers started");
    }

    [Fact]
    public void AnEventKeepsItsPropertiesAndLevelWhenItsExceptionIsRendered()
    {
        var writer = new StringWriter();

        new OneLineLogSink(new TemplateSink(writer))
            .Emit(EventFor(Thrown(() => new InvalidOperationException("x")), "Refused: {Title}"));

        writer.ToString().ShouldStartWith("[00:00:00Z DBG] Refused: \"Unknown or unreadable corpus\"");
    }

    [Fact]
    public void AnEventKeepsItsTraceAndSpanIds()
    {
        var trace = ActivityTraceId.CreateRandom();
        var span = ActivitySpanId.CreateRandom();
        var source = new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Debug, new InvalidOperationException("x"),
            new MessageTemplateParser().Parse("t"), [], trace, span);
        var capture = new CaptureSink();

        new OneLineLogSink(capture).Emit(source);

        (capture.Last!.TraceId, capture.Last.SpanId).ShouldBe((trace, span));
    }

    [Fact]
    public void TheConsoleSinkTheApplicationUsesPrintsAForgedMessageOnOneLine()
    {
        // The real console sink behind the real wrapper, from the configuration Program.cs builds its logger
        // from, with Console.Out replaced.
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        try
        {
            using var logger = LogOutput.Configuration(LogEventLevel.Debug).CreateLogger();
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

    [Fact]
    public void ProgramBuildsItsLoggerFromTheConfigurationTheTestsUse()
    {
        var program = File.ReadAllText(SourceFiles.Find("src", "Dexicon", "Program.cs"));

        program.ShouldContain("Log.Logger = LogOutput.Configuration(");
        program.ShouldNotContain("WriteTo.Console(");
        program.ShouldNotContain("new LoggerConfiguration()");
    }
}
