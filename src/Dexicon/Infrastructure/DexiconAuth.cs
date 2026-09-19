using System.Security.Cryptography;
using System.Text;
using Dexicon.Core.Auth;
using Microsoft.Extensions.Caching.Memory;

namespace Dexicon.Infrastructure;

/// <summary>The authenticated caller for this request.</summary>
public sealed class RequestContext
{
    public Principal? Principal { get; set; }

    public Principal RequirePrincipal() => Principal
        ?? throw new InvalidOperationException("Request reached a handler with no principal.");
}

/// <summary>
/// Bearer in, principal out. Runs ahead of everything except the health probes, the SPA's
/// static files and the login endpoint.
///
/// Two kinds of bearer reach here. A <c>dexs_</c> value is an admin session, verified in
/// memory and carrying the <c>admin</c> scope. A <c>dex_</c> value is an agent's key,
/// verified against the catalogue and carrying <c>search</c> and perhaps <c>ingest</c>.
/// Which corpora a key may reach is not decided here: it is read per request by
/// <see cref="Dexicon.Core.Auth.ScopeResolver"/>, so that a change in the UI is not held
/// behind this cache's TTL. See docs/decisions.md D-28.
/// </summary>
public sealed class DexiconAuthMiddleware(RequestDelegate next, IMemoryCache cache, ILogger<DexiconAuthMiddleware> log)
{
    private static readonly TimeSpan PrincipalTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Container and load-balancer probes only. NOT bare "/healthz": that one reports
    /// endpoints, model names and job state, so it authenticates like everything else.
    /// Prefix-matching the whole "/healthz" family would have made the detailed
    /// endpoint permanently unreachable, because it would skip the middleware, arrive with no
    /// principal, and then fail its own scope check.
    /// </summary>
    private static readonly string[] AnonymousExact =
    [
        "/healthz/live", "/healthz/ready", "/favicon.ico", "/index.html", "/robots.txt",
        // Signing in cannot require being signed in. The handler is throttled instead, and
        // signing out only ever revokes a session whose value the caller already holds.
        "/api/session",
    ];

    private static readonly string[] AnonymousPrefixes = ["/assets/"];

    /// <summary>
    /// Whether a path is served without a token. Public because the OpenAPI document has
    /// to describe the same rule: keeping a second list over there is how a document ends
    /// up documenting a version of the rule that no longer exists.
    /// </summary>
    public static bool IsAnonymous(string path) =>
        path == "/"
        || AnonymousExact.Contains(path, StringComparer.OrdinalIgnoreCase)
        || AnonymousPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public async Task InvokeAsync(
        HttpContext ctx, TokenService tokens, AdminSessions sessions, RequestContext request)
    {
        var path = ctx.Request.Path.Value ?? "/";

        if (IsAnonymous(path))
        {
            await next(ctx);
            return;
        }

        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        var presented = header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

        if (string.IsNullOrEmpty(presented))
        {
            await Problem(ctx, StatusCodes.Status401Unauthorized, "Missing credentials",
                "Provide a key: Authorization: Bearer dex_…, or sign in at / for the UI.");
            return;
        }

        // An admin session is held in memory, so it costs a dictionary lookup and is not
        // worth caching. Checked first because the prefixes are disjoint and a session
        // value must never reach the key verifier, where it would be a database round trip
        // that can only fail.
        var principal = sessions.Verify(presented);

        if (principal is null)
        {
            // PBKDF2 at 600k iterations on every MCP call would dominate the cost of a
            // search, so verified principals are cached briefly. Revocation punches
            // through by evicting the entry rather than waiting for the TTL.
            var cacheKey = PrincipalCacheKey(presented);
            if (!cache.TryGetValue(cacheKey, out principal) || principal is null)
            {
                principal = await tokens.VerifyAsync(presented, ctx.RequestAborted);
                if (principal is not null) cache.Set(cacheKey, principal, PrincipalTtl);
            }
        }

        if (principal is null)
        {
            log.LogWarning("Rejected request to {Path}: credential invalid, revoked or expired", path);
            await Problem(ctx, StatusCodes.Status401Unauthorized, "Invalid credentials",
                "The credential was not recognised, or it has been revoked or has expired.");
            return;
        }

        request.Principal = principal;

        ctx.Response.OnStarting(() =>
        {
            // Audit line. The credential's id and name, never its value.
            log.LogInformation("{Method} {Path} -> {Status} (caller {TokenId} '{TokenName}')",
                ctx.Request.Method, path, ctx.Response.StatusCode, principal.TokenId, principal.TokenName);
            return Task.CompletedTask;
        });

        await next(ctx);

        // Sessions have no row to touch, and writing one per admin request would be a
        // database write on every page of the UI.
        if (!presented.StartsWith(AdminSessions.Prefix, StringComparison.Ordinal))
            _ = tokens.TouchAsync(principal.TokenId, CancellationToken.None);
    }

    /// <summary>
    /// The key a verified principal is cached under, for the TTL above.
    ///
    /// SHA-256 over the whole presented token, NOT <c>string.GetHashCode</c>. The key used
    /// to be a 32-bit non-cryptographic hash plus the token's length, and a collision in
    /// that space does not return a stale value; it returns another caller's
    /// authenticated principal, with verification skipped. .NET randomises string hashing
    /// per process, so the collisions could not be found offline; that is a reason it was
    /// hard to exploit and not a reason it was sound.
    ///
    /// A digest of a sixty-character string costs nothing against the 600k-iteration
    /// PBKDF2 this cache exists to avoid, and the token itself never becomes the key,
    /// because cache keys turn up in dumps and diagnostics and a credential should not.
    /// </summary>
    internal static string PrincipalCacheKey(string presented) =>
        "principal::" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    private static Task Problem(HttpContext ctx, int status, string title, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        return ctx.Response.WriteAsJsonAsync(new { type = "about:blank", title, status, detail });
    }
}

public static class AuthExtensions
{
    public static IApplicationBuilder UseDexiconAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<DexiconAuthMiddleware>();

    /// <summary>Guard an endpoint on a scope. Returns null when allowed.</summary>
    public static IResult? RequireScope(this RequestContext ctx, string scope)
    {
        var principal = ctx.Principal;
        if (principal is null)
            return Results.Problem(title: "Not authenticated", statusCode: StatusCodes.Status401Unauthorized);

        return principal.Has(scope)
            ? null
            : Results.Problem(
                title: "Insufficient scope",
                detail: $"This key has [{string.Join(", ", principal.Scopes)}] and needs '{scope}'. " +
                        "Administration is the password's; sign in to the UI for it.",
                statusCode: StatusCodes.Status403Forbidden);
    }
}
