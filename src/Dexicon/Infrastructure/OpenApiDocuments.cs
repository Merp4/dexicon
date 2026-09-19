using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Dexicon.Infrastructure;

/// <summary>
/// Two OpenAPI documents over one API.
///
/// <c>v1</c> describes everything and is what the web client is generated from, so it
/// cannot drift from the C# contracts: `api.ts` had already done so more than once, most
/// visibly when chunk sets landed and the Corpus interface still carried fields the server
/// had dropped.
///
/// <c>integration</c> describes the subset meant for other software. The difference is not
/// tidiness: the full document also describes the workspace browser, the model probe and
/// the sign-in endpoint, which are the admin screens' own plumbing, and publishing those as
/// an integration contract would commit the project to the shape of the UI. See
/// docs/decisions.md D-29.
/// </summary>
public static class OpenApiDocuments
{
    /// <summary>
    /// The integration document's name, and the group name its endpoints carry. They have
    /// to agree: the group name is how an endpoint is selected into the document.
    /// </summary>
    public const string Integration = "integration";

    public static IServiceCollection AddDexiconOpenApi(this IServiceCollection services, string version)
    {
        services.AddOpenApi(o =>
        {
            // Every endpoint, whether or not it carries a group name. The stock delegate
            // takes those with no group name plus those naming this document, which would
            // drop search, context, corpora and jobs out of the document the web client is
            // generated from the moment they were selected into the other one.
            o.ShouldInclude = _ => true;

            Describe(o, "Dexicon", version,
                "Semantic indexing and search. Every endpoint except sign-in requires a "
                + "bearer: an agent's API key, which reaches the corpora it is mapped to, or an "
                + "admin session obtained by posting the password to /api/session.");
        });

        services.AddOpenApi(Integration, o =>
        {
            o.ShouldInclude = d => d.GroupName == Integration;

            Describe(o, "Dexicon integration API", version,
                "The endpoints meant for other software: search, assembled context, the corpora "
                + "a key can reach, their indexing state, and reindexing. A key with the `search` "
                + "scope reaches all of these except the reindex endpoint, which needs `ingest`. "
                + "The rest of the REST surface serves the web UI and is described by the full "
                + "document rather than this one.");
        });

        return services;
    }

    private static void Describe(OpenApiOptions options, string title, string version, string description)
    {
        options.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new()
            {
                Title = title,
                Version = version,
                Description = description,
            };

            // The prose above said this; the document did not. A generated client reads the
            // document, not the description, and only attaches credentials to operations that
            // declare a security requirement, so with none declared the web UI's own client
            // sent every request anonymously and the server answered "Missing credentials",
            // which the UI reported to people as a bad token.
            doc.Components ??= new();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "A Dexicon API token: `Authorization: Bearer dex_…`.",
            };

            // Applied to the whole document rather than per operation: the few anonymous
            // endpoints are the health probes, and claiming they need a token is a far smaller
            // error than claiming the rest do not.
            doc.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("bearer", doc)] = [],
                },
            ];

            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            // The document-wide requirement above is right for almost everything, and wrong for
            // the container probes, which is what Docker's HEALTHCHECK calls, without a token.
            // An empty `security` on an operation means "this one needs none", and the list comes
            // from the middleware that actually enforces it rather than a copy that can drift.
            var path = "/" + (context.Description.RelativePath ?? string.Empty).TrimEnd('/');

            if (DexiconAuthMiddleware.IsAnonymous(path))
            {
                operation.Security = [];
            }

            return Task.CompletedTask;
        });
    }
}
