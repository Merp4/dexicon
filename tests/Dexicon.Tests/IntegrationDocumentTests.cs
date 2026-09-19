using System.Text.Json;

namespace Dexicon.Tests;

/// <summary>
/// The two OpenAPI documents the build writes, checked as contracts rather than as files.
///
/// Both failure modes are silent. Selecting an endpoint into the integration document
/// with a group name also removes it from the full one under the stock rule, and what
/// notices is the web client, which is generated from the full document and simply loses
/// the method. And an endpoint that gains a group name by being copied from its neighbour
/// joins a published contract without anyone deciding that it should.
///
/// See docs/decisions.md D-29.
/// </summary>
public sealed class IntegrationDocumentTests
{
    /// <summary>
    /// What the integration document describes. Adding a line here is a decision to
    /// support that endpoint for other software; a change that adds one without it fails.
    /// </summary>
    private static readonly string[] Published =
    [
        "/api/context",
        "/api/corpora",
        "/api/corpora/{nameOrId}",
        "/api/corpora/{nameOrId}/reindex",
        "/api/jobs",
        "/api/jobs/{id}",
        "/api/search",
    ];

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

    private static string[] Paths(string file) =>
        [.. Document(file).GetProperty("paths").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void TheIntegrationDocumentDescribesExactlyTheSubset() =>
        Paths("Dexicon_integration.json").ShouldBe(Published);

    [Fact]
    public void TheAdminSurfaceIsNotPublished()
    {
        // The screens' own plumbing. Publishing these as an integration contract would
        // commit the project to the shape of the UI.
        string[] withheld =
        [
            "/api/session", "/api/tokens", "/api/workspaces", "/api/events",
            "/api/embedding-models", "/api/embedding-providers", "/healthz",
        ];

        var published = Paths("Dexicon_integration.json");
        foreach (var path in withheld)
            published.ShouldNotContain(path);
    }

    [Fact]
    public void CreatingAndDeletingACorpusIsNotPublished()
    {
        // GET /api/corpora is in the contract and POST is not, so the check is per method
        // rather than per path.
        var methods = Document("Dexicon_integration.json")
            .GetProperty("paths").GetProperty("/api/corpora")
            .EnumerateObject().Select(m => m.Name).ToList();

        methods.ShouldBe(["get"]);
    }

    [Fact]
    public void TheFullDocumentStillDescribesTheIntegrationEndpoints()
    {
        // The regression this exists for: a group name removes an endpoint from every
        // other document under the stock rule, and the web client is generated from the
        // full one.
        var full = Paths("Dexicon.json");
        foreach (var path in Published)
            full.ShouldContain(path);
    }

    [Fact]
    public void TheFullDocumentIsStillTheWholeSurface()
    {
        var full = Paths("Dexicon.json");

        full.Length.ShouldBeGreaterThan(Published.Length);
        full.ShouldContain("/api/workspaces");
        full.ShouldContain("/api/tokens");
    }

    [Fact]
    public void BothDocumentsRequireABearer()
    {
        foreach (var file in new[] { "Dexicon.json", "Dexicon_integration.json" })
        {
            var doc = Document(file);

            doc.GetProperty("components").GetProperty("securitySchemes")
                .TryGetProperty("bearer", out _).ShouldBeTrue($"{file} declares no bearer scheme");

            doc.TryGetProperty("security", out var security).ShouldBeTrue($"{file} declares no security");
            security.GetArrayLength().ShouldBe(1);
        }
    }

    [Fact]
    public void BothDocumentsCarryTheSameApiVersion()
    {
        var full = Document("Dexicon.json").GetProperty("info").GetProperty("version").GetString();
        var integration = Document("Dexicon_integration.json")
            .GetProperty("info").GetProperty("version").GetString();

        integration.ShouldBe(full);
    }
}
