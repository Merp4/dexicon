using System.Reflection;
using System.Text.Json;
using Dexicon.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dexicon.Tests;

/// <summary>
/// `search_index`'s corpus argument, and the shapes a client sends it in.
///
/// It is the only array on the MCP surface, so a scalar is the natural thing to send and
/// a client that sent one was refused during argument binding — before the tool body ran,
/// with the SDK's generic "An error occurred." reaching the caller. Reported from a live
/// instance as "search_index errors whenever a corpus argument is passed", reproduced four
/// times against two corpora; unscoped search worked throughout.
/// </summary>
public class CorpusArgumentTests
{
    static readonly MethodInfo SearchIndex =
        typeof(DexiconTools).GetMethod("SearchIndexAsync", BindingFlags.Public | BindingFlags.Static)!;

    static string[]? Bind(string json) =>
        JsonSerializer.Deserialize<string[]>(json, McpJson.Options);

    [Fact]
    public void OneCorpusMayBeNamedOnItsOwn()
    {
        Bind("\"docs\"").ShouldBe(["docs"]);
    }

    [Fact]
    public void AListIsStillAList()
    {
        Bind("[\"docs\",\"books\"]").ShouldBe(["docs", "books"]);
    }

    [Fact]
    public void AnEmptyListIsNotTheSameAsOmittingIt()
    {
        // Null means "everything visible to me"; an empty list is a scope naming nothing.
        // They reach SearchService differently and it is not this converter's job to
        // collapse one into the other.
        Bind("[]").ShouldBe([]);
        Bind("null").ShouldBeNull();
    }

    [Fact]
    public void SomethingThatIsNeitherSaysWhichArgumentItWas()
    {
        // The whole defect was a refusal a model could not act on. A message naming the
        // argument and what it takes is one the caller can fix on the next turn.
        // McpException rather than JsonException: measured against a live instance, the SDK
        // renders that one's message to the caller and turns everything else into
        // "An error occurred invoking 'search_index'."
        var thrown = Should.Throw<McpException>(() => Bind("7"));

        thrown.Message.ShouldContain("corpus");
        thrown.Message.ShouldContain("number");
    }

    [Fact]
    public void AListOfSomethingElseIsRefusedToo()
    {
        Should.Throw<McpException>(() => Bind("[{\"name\":\"docs\"}]"))
              .Message.ShouldContain("corpus");
    }

    [Fact]
    public void WhatTheToolAdvertisesIsUnchanged()
    {
        // Accepting a scalar is leniency in binding, not a wider contract: the schema a
        // client reads still says an array of strings, and a client that follows it is
        // right. Without this the converter could silently turn the advertised type into
        // `{}` and every caller would be guessing.
        var tool = McpServerTool.Create(SearchIndex, new McpServerToolCreateOptions { SerializerOptions = McpJson.Options });

        using var schema = JsonDocument.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        var corpus = schema.RootElement.GetProperty("properties").GetProperty("corpus");

        corpus.GetProperty("type").EnumerateArray().Select(t => t.GetString()).ShouldBe(["array", "null"]);
        corpus.GetProperty("items").GetProperty("type").EnumerateArray()
              .Select(t => t.GetString()).ShouldBe(["string", "null"]);
    }
}
