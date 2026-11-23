using Dexicon.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dexicon.Mcp;

/// <summary>
/// Answers a tool's refusal of what the caller sent without an error-level log entry.
///
/// The tools throw <see cref="McpException"/> for a refusal the caller can act on: an unknown corpus or source,
/// a missing scope, an argument of the wrong shape. SDK 2.4.0 turns it into an error result that carries the
/// message, and also logs it at Error with its stack trace, as <c>"search_index" threw an unhandled exception.</c>
/// A corpus name mistyped in a client then read in the log as a fault in the server, and an alert on error
/// entries fired on it. The REST surface answers the same refusals with a 4xx and a Debug entry
/// (<c>ScopeExceptionHandler</c>); this does the same for MCP.
///
/// The filter wraps the tool, the binding of its arguments included, and returns the result the SDK would have
/// built (<c>CreateToolCallErrorResult</c> in its <c>McpServerImpl</c>), so the caller reads the same text. The SDK
/// then logs <c>"search_index" completed. IsError = true.</c> at Information.
///
/// Not covered:
/// - Any other exception still reaches the SDK and is logged at Error, because it is a fault. Where a tool turns
///   a fault into a refusal (<c>configure_corpus</c> when preparing the index fails), the tool logs the cause first.
/// - <see cref="McpProtocolException"/> is rethrown, because the SDK answers it as a JSON-RPC error.
/// - A resource read (<see cref="DexiconResources"/>) keeps the SDK's Error entry. The SDK logs a resource's
///   exception and rethrows it as the JSON-RPC error, and a filter cannot catch it without changing that answer.
/// </summary>
internal static class McpRefusals
{
    /// <summary>The call-tool filter Program.cs registers.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, ct) => RunAsync(context.Params?.Name, context.Services, () => next(context, ct));

    /// <summary>
    /// Runs the tool, and answers a refusal with <see cref="AsResult"/> and a Debug entry in the category
    /// <c>Dexicon.Mcp</c>. Any other exception goes on to the SDK.
    /// </summary>
    internal static async ValueTask<CallToolResult> RunAsync(
        string? tool, IServiceProvider? services, Func<ValueTask<CallToolResult>> next)
    {
        try
        {
            return await next();
        }
        catch (Exception ex) when (AsResult(tool, ex) is { } refused)
        {
            services?.GetService<ILoggerFactory>()?.CreateLogger("Dexicon.Mcp")
                .LogDebug("Refused {Tool}: {Reason}", LogText.OneLine(tool ?? ""), LogText.OneLine(ex.Message));
            return refused;
        }
    }

    /// <summary>
    /// The error result for a refusal, with the text the SDK writes for one, or null for an exception that is not
    /// a refusal and goes on to the SDK.
    ///
    /// Both values are held to one line by <see cref="LogText.OneLine"/>, which changes nothing a caller reads
    /// today: the name is a registered tool's (the SDK answers an unknown one with a <see cref="McpProtocolException"/>
    /// before any tool runs), and the tools build each message on one line with <c>Echo</c>.
    /// </summary>
    internal static CallToolResult? AsResult(string? tool, Exception exception) =>
        exception is McpException and not McpProtocolException
            ? new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"An error occurred invoking '{LogText.OneLine(tool ?? "")}': {LogText.OneLine(exception.Message)}",
                    },
                ],
            }
            : null;
}
