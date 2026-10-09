namespace Dexicon.Api;

/// <summary>
/// A search mode that is not one of the three. An <see cref="ArgumentException"/> with no parameter name,
/// because the message goes to the caller: the exception handler answers 400 and the MCP tools raise it
/// as a tool error.
/// </summary>
public sealed class UnknownSearchModeException(string message) : ArgumentException(message);
