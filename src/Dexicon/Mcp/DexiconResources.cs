using System.Text;
using System.Text.Json;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dexicon.Mcp;

/// <summary>
/// The MCP resource surface, as specified in docs/06-mcp-surface.md.
///
/// Resources are for BROWSING; tools are for asking questions. A client with a resource
/// picker can let someone attach "this corpus" or "that file" to a conversation without
/// the model having to guess a search query first. Everything here is read-only.
///
/// Scope resolution is the same as search's, deliberately: a corpus a tenant cannot
/// search must not become readable merely because it was reached by URI instead. The
/// tenant boundary is the whole security model, and a second way in is a second way to
/// get it wrong.
/// </summary>
[McpServerResourceType]
public sealed class DexiconResources
{
    [McpServerResource(
        UriTemplate = "dexicon://corpus/{name}",
        Name = "corpus",
        Title = "Corpus summary",
        MimeType = "application/json")]
    [System.ComponentModel.Description(
        "A corpus's configuration and current state as JSON: sources, file and chunk counts, embedding model, chunking settings.")]
    public static async Task<string> CorpusAsync(
        RequestContext rc,
        ScopeResolver scopes,
        CatalogDbContext db,
        string name,
        CancellationToken ct = default)
    {
        var (tenant, corpus) = await ResolveAsync(rc, scopes, name, ct);
        var summary = await CorpusEndpoints.Summarise(db, corpus, tenant, ct);
        return JsonSerializer.Serialize(summary, JsonOptions.Web);
    }

    [McpServerResource(
        UriTemplate = "dexicon://corpus/{name}/file/{+path}",
        Name = "corpus-file",
        Title = "Indexed file text",
        MimeType = "text/plain")]
    [System.ComponentModel.Description(
        "The text of one indexed file, reconstructed from its chunks. Use the file path exactly as search_index reports it.")]
    public static async Task<string> FileAsync(
        RequestContext rc,
        ScopeResolver scopes,
        IVectorStore vectors,
        string name,
        string path,
        CancellationToken ct = default)
    {
        var (_, corpus) = await ResolveAsync(rc, scopes, name, ct);

        // Rebuilt from the INDEX, not from disk: an uploaded PDF has no file to read, and
        // the original would in any case differ from what was indexed.
        //
        // A FILTER, not a search. Reconstructing a file is a lookup, and an early version
        // of this used keyword search — which let RELEVANCE decide which parts of the file
        // came back. A reader asking for a file got a plausible-looking one with holes in
        // it, disclosed only by the gap markers.
        var chunks = await vectors.GetFileChunksAsync(
            corpus.CollectionName, corpus.Id, path, ct);

        var pieces = chunks
            .Select(h => (h.StartLine, h.EndLine, h.Content))
            .ToList();

        if (pieces.Count == 0)
            throw new McpException(
                $"No indexed file '{path}' in corpus '{corpus.Name}'. " +
                "Paths are exactly as search_index reports them; browse dexicon://corpus/" +
                $"{corpus.Name} for what the corpus contains.");

        var sb = new StringBuilder();
        sb.Append(path).Append(" (corpus: ").Append(corpus.Name).Append(")\n\n");
        sb.Append(DexiconTools.Stitch(pieces));
        return sb.ToString();
    }

    /// <summary>
    /// Authenticate, authorise, and resolve the corpus — in that order, and once, so
    /// there is a single place where a resource read can be allowed.
    /// </summary>
    private static async Task<(string Tenant, Corpus Corpus)> ResolveAsync(
        RequestContext rc, ScopeResolver scopes, string name, CancellationToken ct)
    {
        var principal = rc.Principal
            ?? throw new McpException(
                "Not authenticated. Add an Authorization: Bearer dex_… header to the MCP server configuration.");

        if (!principal.Has(Scopes.Search))
            throw new McpException(
                $"This token has scopes [{string.Join(", ", principal.Scopes)}] and needs '{Scopes.Search}'.");

        var tenant = rc.RequireTenant();

        try
        {
            var scope = await scopes.ResolveReadableAsync(tenant, [name], ct);
            return (tenant, scope.Corpora[0]);
        }
        catch (ScopeResolutionException ex)
        {
            // Carries the list of visible corpora, which is what makes it recoverable.
            throw new McpException(ex.Message);
        }
    }
}
