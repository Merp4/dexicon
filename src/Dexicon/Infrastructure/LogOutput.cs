namespace Dexicon.Infrastructure;

/// <summary>
/// The console output template, as a constant so that the test which proves it safe is
/// testing the value the application actually uses rather than a copy of it.
/// </summary>
public static class LogOutput
{
    /// <summary>
    /// UTC, and labelled. Rendering the log in local time while the API returns UTC makes
    /// the two impossible to line up, which cost time once already, reading a job that
    /// "started an hour ago" when it had started three minutes before. Timestamps are UTC
    /// everywhere; only the UI localises, for its viewer.
    ///
    /// <c>{Message:j}</c>, not <c>:lj</c>. The <c>l</c> means "literal": string values are
    /// written raw, so a decoded <c>%0A</c> in a request path ends the line and everything
    /// after it reads as a separate entry. An unauthenticated caller could forge a
    /// plausible audit line that way, and docs/07 names these logs as the audit trail.
    /// Without <c>l</c> the values are rendered as JSON, which quotes them and escapes the
    /// newline. The quotes are the cost, and they also make the boundaries of a logged
    /// value explicit. See <c>LogForgingTests</c>.
    /// </summary>
    public const string ConsoleTemplate =
        "[{UtcTime:HH:mm:ss}Z {Level:u3}] {Message:j}{NewLine}{Exception}";
}
