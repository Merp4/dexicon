using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace Dexicon.Mcp;

/// <summary>
/// The options the MCP tools bind their arguments with, and generate their input schemas
/// from. One converter is the whole of the customisation.
/// </summary>
internal static class McpJson
{
    internal static readonly JsonSerializerOptions Options = Build();

    static JsonSerializerOptions Build()
    {
        // The SDK's own defaults, so nothing else about binding changes.
        var options = new JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        options.Converters.Add(new CorpusNamesConverter());
        options.Converters.Add(new SettingListConverter());
        return options;
    }
}

/// <summary>
/// Reads <c>search_index</c>'s <c>corpus</c> argument from either a single name or a list
/// of them.
///
/// It is the only <c>string[]</c> on the MCP surface, which is what lets its messages name
/// the argument; the configuration tools' lists are another type with a converter of their
/// own (<see cref="SettingListConverter"/>). Every other filter — <c>source</c>,
/// <c>language</c>, <c>symbol</c>, <c>pathPrefix</c> — is a scalar, and scoping to one
/// corpus is the common call. A client that sends <c>"corpus": "docs"</c> was refused
/// during argument binding, before the tool body ran, and the refusal reached the caller
/// as the SDK's generic message. Both halves measured against a live instance — the
/// response body:
///
///   {"content":[{"type":"text","text":"An error occurred invoking 'search_index'."}],
///    "isError":true}
///
/// and the server log behind it:
///
///   [14:50:35Z ERR] "search_index" threw an unhandled exception.
///   System.Text.Json.JsonException: The JSON value could not be converted to
///   System.String[]. Path: $ | LineNumber: 0 | BytePositionInLine: 12.
///
/// An agent that sees that has no way to tell a wrong argument shape from a broken
/// feature, and the one reported here concluded scoped search did not work and searched
/// everything instead for the rest of its session.
///
/// McpException rather than JsonException for a shape that is wrong however it is read:
/// the SDK renders that one's message to the caller and swallows everything else into
/// the generic sentence above.
///
/// The advertised schema stays <c>array of string</c>, which is the contract worth
/// describing; this only widens what is accepted.
///
/// One place it is NARROWER: the generated schema says <c>items: ["string", "null"]</c>,
/// the SDK describing a nullable reference type, and a null element is refused. It never
/// meant anything — <c>ScopeResolver.Split</c> does <c>nameOrId.IndexOf(':')</c>, so one
/// arrived as a NullReferenceException and came back as the generic sentence above.
/// </summary>
internal sealed class CorpusNamesConverter : JsonConverter<string[]>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return [reader.GetString()!];

            case JsonTokenType.StartArray:
                var names = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                        throw new McpException(
                            $"corpus takes a name or a list of names; this list holds {Describe(reader.TokenType)}.");
                    names.Add(reader.GetString()!);
                }

                return [.. names];

            default:
                throw new McpException(
                    $"corpus takes a name or a list of names, not {Describe(reader.TokenType)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartArray();
        foreach (var name in value) writer.WriteStringValue(name);
        writer.WriteEndArray();
    }

    internal static string Describe(JsonTokenType token) => token switch
    {
        JsonTokenType.Number => "a number",
        JsonTokenType.True or JsonTokenType.False => "a boolean",
        JsonTokenType.StartObject => "an object",
        JsonTokenType.StartArray => "a nested list",
        JsonTokenType.Null => "null",
        _ => token.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// Reads the configuration tools' <c>include</c>, <c>exclude</c> and <c>reset</c> from a single
/// string or a list, as <see cref="CorpusNamesConverter"/> reads <c>corpus</c>, with messages
/// that name these arguments rather than that one. A type of their own,
/// <c>IReadOnlyList&lt;string&gt;</c>, is what keeps the two converters apart: one registered for
/// <c>string[]</c> meets every string array, and its wording would be wrong for the others.
/// </summary>
internal sealed class SettingListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return [reader.GetString()!];

            case JsonTokenType.StartArray:
                var values = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                        throw new McpException(
                            "include, exclude and reset take a string or a list of strings; this list holds "
                            + $"{CorpusNamesConverter.Describe(reader.TokenType)}.");
                    values.Add(reader.GetString()!);
                }

                return values;

            default:
                throw new McpException(
                    "include, exclude and reset take a string or a list of strings, not "
                    + $"{CorpusNamesConverter.Describe(reader.TokenType)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
