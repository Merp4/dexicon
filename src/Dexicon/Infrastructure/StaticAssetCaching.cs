using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Dexicon.Infrastructure;

/// <summary>
/// The Cache-Control the built web UI is served with.
///
/// Static files carry an ETag and a Last-Modified and no Cache-Control by default. A browser may then
/// treat the response as fresh for a share of the time since Last-Modified (Chrome uses a tenth of it),
/// and Last-Modified is when the UI was last built into the image, which can be days before the deploy.
/// After a deploy that changed the UI, a person who loaded <c>index.html</c> within that window keeps
/// running the old bundle without asking the server. Measured on 2026-11-11 with the Browser pane: with
/// 0.6.13 loaded (Last-Modified nine days earlier) and the container replaced by 0.6.14, a normal
/// navigation to <c>/</c> answered from the cache (transfer size 0) and ran 0.6.13's bundle while the
/// server's <c>index.html</c> named 0.6.14's.
///
/// <c>index.html</c> and everything that is not a hashed bundle file is served with <c>no-cache</c>: the
/// browser keeps a copy and asks with its ETag, and the server answers 304 until the file changes. A file
/// under <c>/assets</c> whose name ends in an 8-character content hash (what Vite writes) can never change
/// under that name, so it is served as immutable for a year.
/// </summary>
internal static class StaticAssetCaching
{
    /// <summary>Keep a copy, and ask the server before using it.</summary>
    internal const string Revalidate = "no-cache";

    /// <summary>Never changes under this name: the name carries a hash of the content.</summary>
    internal const string Immutable = "public, max-age=31536000, immutable";

    // A direct child of /assets named <name>-<8 characters>.<extension>. Anything else under /assets is not
    // taken for content-addressed, since a file cached as immutable under a name that is reused is wrong until
    // the cache is cleared by hand.
    private static readonly Regex HashedAsset =
        new(@"^/assets/[^/]+-[A-Za-z0-9_-]{8}\.[A-Za-z0-9]+\z", RegexOptions.CultureInvariant);

    /// <summary>The policy for a request path.</summary>
    internal static string For(PathString path) =>
        HashedAsset.IsMatch(path.Value ?? string.Empty) ? Immutable : Revalidate;

    /// <summary>
    /// For <c>UseStaticFiles</c>. A new instance for each host, so a test that starts several does not share one.
    /// </summary>
    internal static StaticFileOptions Options() => new()
    {
        OnPrepareResponse = context => Apply(context, For(context.Context.Request.Path)),
    };

    /// <summary>
    /// Sets the policy on a response that carries the file: 200, 206 and 304, which is where an unchanged
    /// <c>index.html</c> is answered. A 412 or a 416 is an answer about the request and says nothing about the file,
    /// and a year of caching on it would be wrong; the middleware calls this for those too.
    /// </summary>
    private static void Apply(StaticFileResponseContext context, string policy)
    {
        var response = context.Context.Response;
        if (response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent or StatusCodes.Status304NotModified)
            response.Headers.CacheControl = policy;
    }
}
