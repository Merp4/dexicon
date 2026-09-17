using Serilog.Core;
using Serilog.Events;

namespace Dexicon.Infrastructure;

/// <summary>
/// Adds <c>{UtcTime}</c> so the console template can print UTC.
///
/// Serilog's built-in <c>{Timestamp}</c> is a <see cref="DateTimeOffset"/> rendered in the
/// host's local time, and it has no "as UTC" format specifier. Printing that next to a
/// hard-coded "Z" would be incorrect, which is worse than no label, so the value is
/// converted here and the template uses this instead.
///
/// Dexicon's rule: timestamps are UTC in the database, in the API and in the log. The
/// only place a local time appears is the browser, rendering for its own viewer.
/// </summary>
public sealed class UtcTimestampEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("UtcTime", logEvent.Timestamp.UtcDateTime));
}
