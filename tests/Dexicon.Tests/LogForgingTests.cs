using System.Globalization;
using Dexicon.Infrastructure;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// A request path is decoded before it reaches a log call, so `%0A` arrives as a real
/// newline. Rendered literally into a text sink it ends the line, and everything after it
/// reads as a separate entry: an unauthenticated caller can write a plausible audit line
/// of their own choosing, because the rejected request is logged before the 401 is
/// returned. docs/07 names the container logs as the audit trail, so what was forgeable
/// was the audit trail.
///
/// These run against <see cref="LogOutput.ConsoleTemplate"/> itself rather than a copy of
/// it, so restoring the `l` flag fails the build rather than quietly reopening this.
/// </summary>
public class LogForgingTests
{
    /// <summary>A path carrying a complete, plausible audit line after a newline.</summary>
    private const string ForgedPath =
        "/x\n[19:05:31Z INF] DELETE /api/corpora/books -> 200 (token bootstrap)";

    private static string Render(string outputTemplate, string path)
    {
        var template = new MessageTemplateParser().Parse("Rejected request to {Path}: token invalid");
        var evt = new LogEvent(
            DateTimeOffset.UnixEpoch, LogEventLevel.Information, exception: null, template,
            [new LogEventProperty("Path", new ScalarValue(path))]);

        var writer = new StringWriter();
        new MessageTemplateTextFormatter(outputTemplate, CultureInfo.InvariantCulture)
            .Format(evt, writer);
        return writer.ToString().TrimEnd('\r', '\n');
    }

    [Fact]
    public void TheConfiguredTemplate_KeepsAForgedPathOnOneLine()
    {
        var rendered = Render(LogOutput.ConsoleTemplate, ForgedPath);

        rendered.ShouldNotContain("\n");
        rendered.Split('\n').Length.ShouldBe(1);

        // The text is still reported, which is the point of an audit line. It simply can
        // no longer end the entry it appears in.
        rendered.ShouldContain("DELETE /api/corpora/books");
    }

    [Fact]
    public void TheConfiguredTemplate_DoesNotRenderStringsLiterally()
    {
        // The whole defect was one flag. Naming it here makes the reason a later reader
        // finds before they "tidy" it back.
        LogOutput.ConsoleTemplate.ShouldContain("{Message:j}");
        LogOutput.ConsoleTemplate.ShouldNotContain("{Message:lj}");
    }

    [Fact]
    public void LiteralRendering_IsWhatTheDefectLookedLike()
    {
        // Kept as the statement of the defect, so the difference is visible in one file.
        var rendered = Render("[{Level:u3}] {Message:lj}", ForgedPath);

        rendered.Split('\n').Length.ShouldBe(2);
    }

    /// <summary>
    /// The rejection line now carries the caller's own user agent, which is the one
    /// field on it an unauthenticated caller writes. Two things keep it honest: the
    /// template escapes it, as it does the path, and it is capped before it is logged.
    /// </summary>
    [Fact]
    public void AForgedUserAgentStaysOnOneLine()
    {
        var rendered = Render(LogOutput.ConsoleTemplate, DexiconAuthMiddleware.Agent(ForgedPath));

        rendered.Split('\n').Length.ShouldBe(1);
        rendered.ShouldContain("DELETE /api/corpora/books");
    }

    [Fact]
    public void AUserAgentIsCapped()
    {
        // A rejected request is the one path an unauthenticated caller can reach, and it
        // is logged. Uncapped, a kilobyte per poll is theirs to write into the audit
        // trail.
        var huge = new string('x', 5_000);

        var agent = DexiconAuthMiddleware.Agent(huge);

        agent.Length.ShouldBeLessThan(200);
        DexiconAuthMiddleware.Agent(null).ShouldBe("no user agent");
        DexiconAuthMiddleware.Agent("curl/8.0").ShouldBe("curl/8.0");
    }

    [Fact]
    public void AnOrdinaryPathStaysReadable()
    {
        // The cost of dropping `l`: string values are quoted. Worth pinning in a test
        // rather than discovering in production logs.
        var rendered = Render("[{Level:u3}] {Message:j}", "/api/corpora");

        rendered.ShouldBe("""[INF] Rejected request to "/api/corpora": token invalid""");
    }
}
