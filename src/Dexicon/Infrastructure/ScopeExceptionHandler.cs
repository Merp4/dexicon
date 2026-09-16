using Dexicon.Core.Auth;
using Dexicon.Core.Embedding;
using Dexicon.Core.Search;
using Microsoft.AspNetCore.Diagnostics;

namespace Dexicon.Infrastructure;

/// <summary>
/// Turns the domain's own refusals into answers a caller can act on.
///
/// These exceptions all carry a written, specific message — "Unknown corpus 'api'.
/// Visible corpora: api-repo, rfc-library." — and the MCP surface has always surfaced
/// them verbatim. The REST surface did not: every one of them fell through to the default
/// handler as a bare 500 with a trace id, so the UI's own search box answered a mistyped
/// corpus name with "An error occurred while processing your request."
///
/// A 500 also says the wrong thing. Naming a corpus you cannot see is a bad request, not
/// a broken server, and an operator reading logs should not be hunting a fault that is
/// not there.
/// </summary>
public sealed class ScopeExceptionHandler(ILogger<ScopeExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ScopeResolutionException => (StatusCodes.Status400BadRequest, "Unknown or unreadable corpus"),
            EmbeddingDimensionMismatchException => (StatusCodes.Status409Conflict, "Embedding dimension mismatch"),
            EmbeddingUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Embedding service unavailable"),
            UnscopedQueryException => (StatusCodes.Status500InternalServerError, "Refused an unscoped query"),
            _ => (0, string.Empty),
        };

        if (status == 0) return false;   // not ours; let the default handler have it

        // An unscoped query reaching the store is a BUG, and the one guard standing
        // between a scoping mistake and a cross-tenant read. It is the only case here
        // worth an error-level log.
        if (exception is UnscopedQueryException)
            log.LogError(exception, "A query reached the vector store with no corpus scope");
        else
            log.LogDebug(exception, "Refused: {Title}", title);

        await Results.Problem(title: title, detail: exception.Message, statusCode: status)
            .ExecuteAsync(httpContext);

        return true;
    }
}
