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

        var expected = new (LogEventPropertyValue Value, string Written)[]
        {
            (sequence, "a�"),
            (structure, "b�"),
            (dictionary, "k�"),
        };
        foreach (var (value, written) in expected)
        {
            var rendered = Render(EventFor(null, "Refused: {Title}", value));

            rendered.ShouldNotContain(hostile);
            rendered.ShouldContain(written);
        }
    }

    /// <summary>The scalar sits at the depth of the sequences around it, and the limit is 8.</summary>
    [Theory]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    [InlineData(20, false)]
    public void AValueNestedPastTheDepthLimitIsWrittenAsDots(int levels, bool written)
    {
        var hostile = ((char)0x2028).ToString();
        LogEventPropertyValue value = new ScalarValue("deep" + hostile);
        for (var i = 0; i < levels; i++) value = new SequenceValue([value]);

        var rendered = Render(EventFor(null, "Refused: {Title}", value));

        rendered.ShouldNotContain(hostile);
        rendered.Contains("deep�", StringComparison.Ordinal).ShouldBe(written);
        rendered.Contains("\"...\"", StringComparison.Ordinal).ShouldBe(!written);
    }

    /// <summary>The invisible characters: format characters, in the BMP and past it, and a lone surrogate.</summary>
    [Theory]
    [InlineData(0x200B)]
    [InlineData(0x200D)]
    [InlineData(0xFEFF)]
    [InlineData(0x00AD)]
    [InlineData(0x2062)]
    [InlineData(0x180E)]
    [InlineData(0xE0001)]
    [InlineData(0xE0041)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void AnInvisibleCharacterIsReplacedWithOneMarkerInAnArgumentAndInAnException(int codePoint)
    {
        var hostile = codePoint < 0x10000 ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);
        var marker = ((char)0xFFFD).ToString();

        var argument = Render(EventFor(null, "Refused: {Title}", "a" + hostile + "b"));
        var exception = Render(Thrown(() => new InvalidOperationException("a" + hostile + "b")));

        argument.ShouldContain("a" + marker + "b");
        argument.ShouldNotContain(hostile);
        exception.ShouldContain("InvalidOperationException: a" + marker + "b");
    }

    [Fact]
    public void ACharacterThatIsNotHostileIsKept()
    {
        // Letters, an accent, an emoji past the BMP and a CJK character.
        var text = "caf" + (char)0xE9 + " " + char.ConvertFromUtf32(0x1F600) + " " + (char)0x4E2D;

        Render(EventFor(null, "Refused: {Title}", text)).ShouldContain(text);
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
    public void ANumberIsLoggedAsItWas()
    {
        Render(EventFor(null, "Refused: {Title}", 42)).ShouldContain("Refused: 42");
    }

    public static TheoryData<object> PlainValues => new()
    {
        Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), LogEventLevel.Warning, 42, 4_000_000_000L, 1.5, 2.5m, true,
        TimeSpan.FromSeconds(90), new DateTimeOffset(2026, 10, 9, 8, 30, 0, TimeSpan.Zero), new DateOnly(2026, 10, 9),
        new TimeOnly(8, 30),
    };

    [Theory]
    [MemberData(nameof(PlainValues))]
    public void ANumberADateAGuidAndAnEnumReachTheWrappedSinkAsTheTypeTheyAre(object value)
    {
        var sink = new CaptureSink();

        new OneLineLogSink(sink).Emit(EventFor(null, "t {Title}", value));

        var scalar = sink.Last!.Properties["Title"].ShouldBeOfType<ScalarValue>();
        scalar.Value.ShouldBe(value);
        scalar.Value!.GetType().ShouldBe(value.GetType());
    }

    [Fact]
    public void TheMessageOfTheExceptionTheSinkPassesOnIsOnOneLine()
    {
        var sink = new CaptureSink();

        new OneLineLogSink(sink).Emit(EventFor(Thrown(() => new InvalidOperationException($"a\n{ForgedLine}")), "t"));

        sink.Last!.Exception!.Message.ShouldBe("a " + ForgedLine);
    }

    [Fact]
    public void ADateIsLoggedAsItWas()
    {
        var date = new DateTime(2026, 10, 9, 8, 30, 15, DateTimeKind.Utc);

        Render(EventFor(null, "Refused: {Title}", date)).ShouldContain("\"2026-10-09T08:30:15.0000000Z\"");
    }

    [Fact]
    public void AMessageTemplateThatHoldsAForgedLineAnEscapeAndABidiControlIsHeld()
    {
        var template = $"Started {Esc}[2J{(char)0x202E} {{Title}}\n{ForgedLine}\r{ForgedLine} again";

        var rendered = Render(EventFor(null, template, "corpus"));

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        rendered.ShouldNotContain(Esc);
        rendered.ShouldNotContain(((char)0x202E).ToString());
        rendered.ShouldContain("Started �[2J� \"corpus\"");
        Lines(rendered).Count(l => l.StartsWith("    " + ForgedLine, StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void ABannerTemplateWhoseLinesStartWithASpaceIsUnchanged()
    {
        const string banner = "Dexicon\n  listening on {Title}\n  ready";

        OneLineLogSink.TemplateText(banner).ShouldBeSameAs(banner);
        Lines(Render(EventFor(null, banner, "port 80"))).ShouldContain("  listening on \"port 80\"");
    }

    [Fact]
    public void ATemplateKeepsItsPropertyTokensWhileItsTextIsReplaced()
    {
        var sink = new CaptureSink();
        new OneLineLogSink(sink).Emit(EventFor(null, $"a{Esc}b {{Title}} {{{{literal}}}}", "corpus"));

        sink.Last!.MessageTemplate.Tokens.OfType<PropertyToken>().Select(t => t.PropertyName).ShouldBe(["Title"]);
        sink.Last.MessageTemplate.Text.ShouldBe("a�b {Title} {{literal}}");
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
    public void AStackFrameThatHoldsAForgedLineAnEscapeAndABidiControlIsHeld()
    {
        var thrown = new HostileTextException();

        var rendered = OneLineLogSink.Render(thrown);

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        Lines(rendered).ShouldContain(l => l.StartsWith("    " + ForgedLine + " frame", StringComparison.Ordinal));
        rendered.ShouldContain("   at A.B�[2J�()");
        rendered.ShouldNotContain(((char)0x202E).ToString());
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x2003)]
    [InlineData(0x3000)]
    [InlineData(0x09)]
    public void AContinuationLineThatStartsWithWhitespaceStillGetsAnIndent(int lead)
    {
        var rendered = Render(Thrown(() => new InvalidOperationException($"x\n{(char)lead}{ForgedLine}")));

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        Lines(rendered).Single(l => l.Contains("forged", StringComparison.Ordinal)).ShouldStartWith("    ");
    }

    public static TheoryData<string> LineBreaks => new()
    {
        "\n", "\r", "\r\n", "\f",
        char.ConvertFromUtf32(0x85), char.ConvertFromUtf32(0x2028), char.ConvertFromUtf32(0x2029),
    };

    [Theory]
    [MemberData(nameof(LineBreaks))]
    public void AMessageThatImitatesAStackFrameIsIndentedWhicheverBreakStartsIt(string lineBreak)
    {
        var rendered = Render(Thrown(() => new InvalidOperationException($"first{lineBreak}   at Forged.Frame() in x.cs:line 1")));

        Lines(rendered).ShouldContain("       at Forged.Frame() in x.cs:line 1", "four spaces added to the three it starts with");
        Lines(rendered).ShouldNotContain(l => l.StartsWith("   at Forged", StringComparison.Ordinal));
    }

    [Fact]
    public void AFrameTheRuntimeWroteIsLeftAsItIs()
    {
        var thrown = Thrown(() => new InvalidOperationException("first"));

        var lines = Lines(Render(thrown));

        lines.ShouldContain(l => l.StartsWith("   at Dexicon.Tests.ExceptionLogForgingTests.Thrown(", StringComparison.Ordinal));
    }

    [Fact]
    public void ALineThatOnlyStartsLikeAFrameOrTheEndOfAnInnerTraceIsIndented()
    {
        var thrown = Thrown(() => new InvalidOperationException("first"));
        var frame = thrown.StackTrace!.ReplaceLineEndings("\n").Split('\n')[0];
        var forged = Thrown(() => new InvalidOperationException(
            $"first\n{frame} forged\n   --- End of inner exception stack trace --- forged\n   --- forged\n   at Forged() <---"));

        var lines = Lines(Render(forged));

        lines.ShouldContain("    " + frame + " forged");
        lines.ShouldContain("       --- End of inner exception stack trace --- forged");
        lines.ShouldContain("       --- forged");
        lines.ShouldContain("       at Forged() <---");
        lines.ShouldNotContain(l => l.StartsWith("   at Forged", StringComparison.Ordinal)
                                    || l.StartsWith("   --- ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInnerExceptionMarkerIsLeftOnlyBeforeTheTypeOfAnExceptionInTheChain()
    {
        var aggregate = Thrown(() => new AggregateException(
            "first\n ---> (Inner Exception #1) System.ArgumentException: forged<---",
            new InvalidOperationException("a"), new FormatException("b")));

        var lines = Lines(Render(aggregate));

        lines.ShouldContain(l => l.StartsWith(" ---> (Inner Exception #1) System.FormatException: b", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("     ---> (Inner Exception #1) System.ArgumentException: forged", StringComparison.Ordinal));
    }

    [Fact]
    public void AFirstLineThatDoesNotStartWithTheTypeNameIsIndentedToo()
    {
        var rendered = Render(new ForgedFirstLineException());

        ShouldNotStartAnyLineWithTheForgedEntry(rendered);
        Lines(rendered).ShouldContain(l => l.StartsWith("    " + ForgedLine, StringComparison.Ordinal));
    }

    [Fact]
    public void ABlankLineInAMessageIsFourSpaces()
    {
        var rendered = Render(Thrown(() => new InvalidOperationException("a\n\nb")));

        Lines(rendered).ShouldContain("    ");
        Lines(rendered).ShouldContain(l => l == "    b");
    }

    private sealed class ForgedFirstLineException : Exception
    {
        public override string ToString() => ForgedLine + " and then the rest";
    }

    /// <summary>An exception whose own text, including its stack trace, is the attacker's.</summary>
    private sealed class HostileTextException : Exception
    {
        public override string? StackTrace =>
            $"   at A.B{Esc}[2J{(char)0x202E}()\n{ForgedLine} frame\n   at C.D()";

        public override string ToString() => $"{GetType()}: ordinary\n{ForgedLine} extra {Esc}[2J{Environment.NewLine}{StackTrace}";
    }

    /// <summary>
    /// A tree of 20 aggregates whose members are the same aggregate twice, so that 21 objects hold a message
    /// that would be megabytes if written whole.
    /// </summary>
    private static AggregateException Diamond(int levels)
    {
        var node = new AggregateException(new string('m', 100), new InvalidOperationException("leaf"));
        for (var i = 0; i < levels; i++) node = new AggregateException(new string('m', 100), node, node);
        return node;
    }

    [Fact]
    public void AnAggregateWhoseMessageHoldsEveryMessageUnderItIsCutAtFourThousandCharacters()
    {
        var diamond = Diamond(20);
        diamond.Message.Length.ShouldBeGreaterThan(1_000_000, "the graph is what makes the message long");

        var rendered = OneLineLogSink.Render(diamond);

        rendered.ShouldContain("the exception chain was cut");
        rendered.Length.ShouldBeLessThan(OneLineLogSink.MaxLine + 400);
        var sink = new CaptureSink();
        new OneLineLogSink(sink).Emit(EventFor(diamond, "t"));
        sink.Last!.Exception!.Message.Length.ShouldBeLessThanOrEqualTo(OneLineLogSink.MaxLine + 3);
    }

    [Fact]
    public void TheLimitsAreTheNumbersTheDocumentationGives()
    {
        (OneLineLogSink.MaxLine, OneLineLogSink.MaxTotal, OneLineLogSink.MaxProperty).ShouldBe((4_000, 64_000, 8_000));
        (OneLineLogSink.MaxExceptionDepth, OneLineLogSink.MaxExceptionNodes, OneLineLogSink.MaxValueDepth).ShouldBe((100, 1_000, 8));
    }

    [Fact]
    public void ALineOfAnExceptionIsCutAtFourThousandCharactersWhateverTheLengthOfItsMessage()
    {
        // The first line holds the type name and ": " before the message.
        var room = 4_000 - (typeof(InvalidOperationException).ToString().Length + 2);

        foreach (var (length, cut) in new[] { (room, false), (room + 1, true), (10_000_000, true) })
        {
            var rendered = Render(Thrown(() => new InvalidOperationException(new string('q', length))));

            var first = Lines(rendered)[1];
            first.Length.ShouldBe(cut ? 4_003 : 4_000);
            first.EndsWith("...", StringComparison.Ordinal).ShouldBe(cut);
        }
    }

    [Fact]
    public void ALineOfAChainTooDeepToReadIsCutAtFourThousandCharactersToo()
    {
        var whole = new string('q', 4_000);

        var first = Lines(OneLineLogSink.Render(DeepWith(whole)))[0];

        first.Length.ShouldBe(4_003);
        first.EndsWith("qq...", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void AnExceptionOfTenMillionCharactersInManyLinesIsWrittenToSixtyFourThousandAndANote()
    {
        var message = string.Concat(Enumerable.Repeat("a line of the message\n", 500_000));

        var rendered = OneLineLogSink.Render(Thrown(() => new InvalidOperationException(message)));

        rendered.Length.ShouldBeLessThan(64_000 + 200);
        rendered.Length.ShouldBeGreaterThan(64_000);
        rendered.ShouldEndWith("(the rest of the exception was cut)");
    }

    [Fact]
    public void AStringPropertyOfTenMillionCharactersIsWrittenToEightThousandAndThreeDots()
    {
        var rendered = Render(EventFor(null, "Refused: {Title}", new string('p', 10_000_000)));

        rendered.Length.ShouldBeLessThan(8_100);
        rendered.ShouldContain(new string('p', 8_000) + "...");
        rendered.ShouldNotContain(new string('p', 8_001));
    }

    /// <summary>A chain deep enough to be cut, with a message on its outermost exception.</summary>
    private static InvalidOperationException DeepWith(string message)
    {
        Exception chain = new InvalidOperationException("root");
        for (var i = 0; i < OneLineLogSink.MaxExceptionDepth + 1; i++) chain = new InvalidOperationException("x", chain);
        return new InvalidOperationException(message, chain);
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");
    }

    private sealed class ThrowingToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("no text");
    }

    private sealed class ThrowingStackTraceException() : Exception("fine")
    {
        public override string? StackTrace => throw new InvalidOperationException("no trace");
    }

    private sealed class ThrowingText
    {
        public override string ToString() => throw new InvalidOperationException("no text");
    }

    [Fact]
    public void AnExceptionWhoseTextCannotBeReadIsStillWrittenWithANote()
    {
        foreach (Exception thrown in new Exception[] { new ThrowingMessageException(), new ThrowingToStringException(), new ThrowingStackTraceException() })
        {
            var rendered = Render(thrown);

            rendered.ShouldStartWith("[00:00:00Z DBG] Refused:");
            rendered.ShouldContain(thrown.GetType().ToString());
        }
    }

    [Fact]
    public void ADeepChainWhoseOutermostMessageCannotBeReadIsStillWritten()
    {
        Exception chain = new InvalidOperationException("root");
        for (var i = 0; i < OneLineLogSink.MaxExceptionDepth + 1; i++) chain = new InvalidOperationException("x", chain);

        var rendered = OneLineLogSink.Render(new WrappingThrowingMessage(chain));

        rendered.ShouldContain("the exception chain was cut");
        rendered.ShouldContain("(its message could not be read)");
    }

    private sealed class WrappingThrowingMessage(Exception inner) : Exception("x", inner)
    {
        public override string Message => throw new InvalidOperationException("no message");
    }

    [Fact]
    public void APropertyWhoseTextCannotBeReadIsStillWrittenWithANote()
    {
        var rendered = Render(EventFor(null, "Refused: {Title}", new ThrowingText()));

        rendered.ShouldContain("(a value of type " + typeof(ThrowingText) + " whose text could not be read)");
    }

    [Fact]
    public void ADisposeOfTheSinkIsPassedOnToTheSinkItWraps()
    {
        var wrapped = new DisposableSink();

        new OneLineLogSink(wrapped).Dispose();

        wrapped.Disposed.ShouldBe(1);
    }

    private sealed class DisposableSink : ILogEventSink, IDisposable
    {
        public int Disposed { get; private set; }

        public void Emit(LogEvent logEvent)
        {
        }

        public void Dispose() => Disposed++;
    }

    [Fact]
    public void AnEventKeepsItsTimestampLevelTemplateAndTypeTag()
    {
        var capture = new CaptureSink();
        var when = new DateTimeOffset(2026, 10, 9, 12, 30, 15, TimeSpan.Zero);
        var tagged = new StructureValue([new LogEventProperty("Name", new ScalarValue("n"))], "Corpus");
        var source = new LogEvent(when, LogEventLevel.Warning, new InvalidOperationException("x"),
            new MessageTemplateParser().Parse("Refused {Title}"), [new LogEventProperty("Title", tagged)],
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom());

        new OneLineLogSink(capture).Emit(source);

        capture.Last!.Timestamp.ShouldBe(when);
        capture.Last.Level.ShouldBe(LogEventLevel.Warning);
        capture.Last.MessageTemplate.Text.ShouldBe("Refused {Title}");
        capture.Last.Properties["Title"].ShouldBeOfType<StructureValue>().TypeTag.ShouldBe("Corpus");
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
    public void TheApplicationsConfigurationKeepsItsLevelItsFrameworkOverridesAndItsUtcTimestamp()
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        try
        {
            using var logger = LogOutput.Configuration(LogEventLevel.Information).CreateLogger();
            logger.Debug("debug-line");
            logger.Information("info-line");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting").Information("framework-info");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting").Warning("framework-warning");
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Query").Information("ef-info");
            logger.ForContext("SourceContext", "Dexicon.Other").Information("other-info");
        }
        finally
        {
            Console.SetOut(original);
        }

        var written = captured.ToString();
        written.ShouldContain("info-line");
        written.ShouldContain("framework-warning");
        written.ShouldContain("other-info");
        written.ShouldNotContain("debug-line");
        written.ShouldNotContain("framework-info");
        written.ShouldNotContain("ef-info");
        System.Text.RegularExpressions.Regex.IsMatch(written, @"^\[\d\d:\d\d:\d\dZ INF\] info-line", System.Text.RegularExpressions.RegexOptions.Multiline)
            .ShouldBeTrue("the timestamp is the UTC time the enricher adds");
    }
}
