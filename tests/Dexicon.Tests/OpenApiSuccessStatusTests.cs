using System.Text.Json;

namespace Dexicon.Tests;

/// <summary>
/// The success status each operation declares in the two OpenAPI documents the build writes,
/// against the status its handler returns.
///
/// An endpoint that returns <c>Results.Accepted</c> or <c>Results.NoContent</c> but declares
/// <c>Produces&lt;T&gt;()</c> is published as a 200. A client generated from the document, or
/// written from it, then checks for a status the server never sends. The handlers cannot be
/// inspected for the status they return, so this lists the operations that answer anything but
/// 200 and fails when the document disagrees; a new such endpoint is added here with the
/// declaration, in the same change.
/// </summary>
public sealed class OpenApiSuccessStatusTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx"))
                               && !File.Exists(Path.Combine(dir.FullName, "LICENSE")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static JsonElement Document(string file) =>
        JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "clients", "web-ui", file))).RootElement;

    private static string[] DeclaredStatuses(JsonElement document, string path, string method) =>
        [.. document.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("responses").EnumerateObject().Select(r => r.Name)];

    public static TheoryData<string, string, string> OperationsThatAnswerOtherThan200 => new()
    {
        { "post", "/api/corpora", "201" },
        { "delete", "/api/corpora/{nameOrId}", "204" },
        { "delete", "/api/corpora/{nameOrId}/sources/{sourceId}", "204" },
        { "post", "/api/corpora/{nameOrId}/reindex", "202" },
        { "post", "/api/corpora/{nameOrId}/sweep", "202" },
        { "post", "/api/corpora/{nameOrId}/chunk-sets", "202" },
        { "delete", "/api/corpora/{nameOrId}/chunk-sets/{setName}", "204" },
        { "post", "/api/corpora/{nameOrId}/documents", "202" },
        { "post", "/api/corpora/{nameOrId}/documents/attach", "202" },
        { "delete", "/api/corpora/{nameOrId}/documents/{fileId}", "204" },
        { "delete", "/api/embedding-models/{model}", "204" },
        { "delete", "/api/session", "204" },
        { "delete", "/api/tokens/{id}", "204" },
    };

    /// <summary>
    /// The success status is declared and 200 is not. Other entries are allowed: documenting a
    /// refusal (a 404 for an unknown token, say) must not fail a test about the success status.
    /// </summary>
    private static void ShouldDeclareOnlyThisSuccess(string[] declared, string status)
    {
        declared.ShouldContain(status);
        declared.ShouldNotContain("200");
    }

    [Theory]
    [MemberData(nameof(OperationsThatAnswerOtherThan200))]
    public void TheFullDocumentDeclaresTheStatusTheHandlerReturns(string method, string path, string status) =>
        ShouldDeclareOnlyThisSuccess(DeclaredStatuses(Document("Dexicon.json"), path, method), status);

    [Fact]
    public void ReindexIsPublishedAsAcceptedBecauseThatIsWhatItReturns() =>
        ShouldDeclareOnlyThisSuccess(
            DeclaredStatuses(Document("Dexicon_integration.json"), "/api/corpora/{nameOrId}/reindex", "post"), "202");
}
