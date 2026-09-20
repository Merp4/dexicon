using System.Text;
using System.Text.Json;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Configuration;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.Extensions.Options;
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
/// Scope resolution is the same as search's, deliberately: a corpus a key cannot search
/// must not become readable merely because it was reached by URI instead. What a key can
/// reach is the whole of the model, and a second way in is a second way to get it wrong.
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
        IOptions<DexiconOptions> opts,
        string name,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(rc, scopes, name, ct);
        var summary = await CorpusEndpoints.Summarise(db, target.Corpus, opts.Value.Indexing, ct);
        return JsonSerializer.Serialize(summary, JsonOptions.Web);
    }

    [McpServerResource(
        UriTemplate = "dexicon://corpus/{name}/file/{+path}",
        Name = "corpus-file",
        Title = "Indexed file text",
        MimeType = "text/plain")]
    [System.ComponentModel.Description(
        "The extracted text of one indexed file, whole. Use the file path exactly as search_index reports it.")]
    public static async Task<string> FileAsync(
        RequestContext rc,
        ScopeResolver scopes,
        IVectorStore vectors,
        DocumentReader documents,
        string name,
        string path,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(rc, scopes, name, ct);
        var corpus = target.Corpus;

        // Read from what was EXTRACTED, not from disk: an uploaded PDF has no file to
        // read, and the original would in any case differ from what was indexed.
        //
        // A FILTER, not a search. Reconstructing a file is a lookup, and an early version
        // of this used keyword search, which let relevance decide which parts of the file
        // came back. A reader asking for a file got a plausible-looking one with holes in
        // it, disclosed only by the gap markers.
        var chunks = await vectors.GetFileChunksAsync(
            target.Set.CollectionName, target.Set.Id, path, ct);

        if (chunks.Count == 0)
        {
            // Indexed and empty is not the same as absent, and a scanned PDF is the
            // first. Saying the path is wrong sends the reader to check a right one.
            var state = await documents.StatusAtAsync(corpus.Id, target.Set.Id, path, ct);

            throw new McpException(state is { Status: FileStatus.Empty } empty
                ? $"'{path}' is indexed in corpus '{corpus.Name}' and has no text: " +
                  $"{empty.Detail ?? "no extractable text content"}."
                : $"No indexed file '{path}' in corpus '{corpus.Name}'. " +
                  "Paths are exactly as search_index reports them; browse dexicon://corpus/" +
                  $"{corpus.Name} for what the corpus contains.");
        }

        // The stored document where there is one. Stitching the chunks back together is
        // the fallback, and it is a reconstruction: it can only return the lines the
        // index happens to hold, and marks the ones it cannot account for.
        var sources = chunks.Select(c => c.SourceId).Distinct(StringComparer.Ordinal).ToList();
        var document = sources.Count == 1
            ? await documents.ForAsync(corpus.Id, target.Set.Id, path, sources[0], ct)
            : null;

        var sb = new StringBuilder();
        sb.Append(path).Append(" (corpus: ").Append(corpus.Name).Append(")\n\n");
        sb.Append(document?.Text
            ?? Passage.Stitch(chunks.Select(h => (h.StartLine, h.EndLine, h.Content))));
        return sb.ToString();
    }

    /// <summary>
    /// Authenticate, authorise, and resolve the corpus, in that order and once, so
    /// there is a single place where a resource read can be allowed.
    /// </summary>
    private static async Task<ScopedCorpus> ResolveAsync(
        RequestContext rc, ScopeResolver scopes, string name, CancellationToken ct)
    {
        var principal = rc.Principal
            ?? throw new McpException(
                "Not authenticated. Add an Authorization: Bearer dex_… header to the MCP server configuration.");

        if (!principal.Has(Scopes.Search))
            throw new McpException(
                $"This key has scopes [{string.Join(", ", principal.Scopes)}] and needs '{Scopes.Search}'.");

        try
        {
            // `name` may be `corpus` or `corpus:set`: the same addressing search uses.
            var scope = await scopes.ResolveReadableAsync(principal, [name], ct);
            return scope.Targets[0];
        }
        catch (ScopeResolutionException ex)
        {
            // Carries the list of visible corpora, which is what makes it recoverable.
            throw new McpException(ex.Message);
        }
    }
}
