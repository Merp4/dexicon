using Dexicon.Core.Auth;
using Microsoft.Extensions.Caching.Memory;

namespace Dexicon.Infrastructure;

/// <summary>The authenticated caller and the tenant they resolved to, for this request.</summary>
public sealed class RequestContext
{
    public Principal? Principal { get; set; }
    public string? TenantId { get; set; }

    public Principal RequirePrincipal() => Principal
        ?? throw new InvalidOperationException("Request reached a handler with no principal.");

    public string RequireTenant() => TenantId
        ?? throw new InvalidOperationException("Request reached a handler with no tenant.");
}

public static class DexiconHeaders
{
    public const string Tenant = "X-Dexicon-Tenant";
}

/// <summary>
/// Bearer token in, principal and tenant out. Runs ahead of everything except the
/// health endpoints and the SPA's static files.
///
/// Tenant resolution is explicit and fails fast — there is no ambient or inferred
/// tenant. A token bound to one tenant resolves to it; the header may name that same
/// tenant; anything else is a 400 naming what was wrong.
/// </summary>
public sealed class DexiconAuthMiddleware(RequestDelegate next, IMemoryCache cache, ILogger<DexiconAuthMiddleware> log)
{
    private static readonly TimeSpan PrincipalTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Container and load-balancer probes only. NOT bare "/healthz": that one reports
    /// endpoints, model names and job state, so it authenticates like everything else.
    /// Prefix-matching the whole "/healthz" family would have made the detailed
    /// endpoint permanently unreachable — it would skip the middleware, arrive with no
    /// principal, and then fail its own scope check.
    /// </summary>
    private static readonly string[] AnonymousExact =
    [
        "/healthz/live", "/healthz/ready", "/favicon.ico", "/index.html", "/robots.txt",
    ];

    private static readonly string[] AnonymousPrefixes = ["/assets/"];

    public async Task InvokeAsync(HttpContext ctx, TokenService tokens, RequestContext request)
    {
        var path = ctx.Request.Path.Value ?? "/";

        if (path == "/"
            || AnonymousExact.Contains(path, StringComparer.OrdinalIgnoreCase)
            || AnonymousPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
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
                "Provide a token: Authorization: Bearer dex_…");
            return;
        }

        // PBKDF2 at 600k iterations on every MCP call would dominate the cost of a
        // search, so verified principals are cached briefly. Revocation punches
        // through by evicting the entry rather than waiting for the TTL.
        var cacheKey = $"principal::{presented.GetHashCode(StringComparison.Ordinal)}::{presented.Length}";
        if (!cache.TryGetValue(cacheKey, out Principal? principal) || principal is null)
        {
            principal = await tokens.VerifyAsync(presented, ctx.RequestAborted);
            if (principal is not null) cache.Set(cacheKey, principal, PrincipalTtl);
        }

        if (principal is null)
        {
            log.LogWarning("Rejected request to {Path}: token invalid, revoked, expired, or tenant disabled", path);
            await Problem(ctx, StatusCodes.Status401Unauthorized, "Invalid credentials",
                "The token was not recognised, or it has been revoked or has expired.");
            return;
        }

        var requestedTenant = ctx.Request.Headers[DexiconHeaders.Tenant].FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(requestedTenant) &&
            !string.Equals(requestedTenant, principal.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            await Problem(ctx, StatusCodes.Status400BadRequest, "Tenant mismatch",
                $"This token is bound to tenant '{principal.TenantId}', but the request asked for " +
                $"'{requestedTenant}'. Remove the {DexiconHeaders.Tenant} header or use a token for that tenant.");
            return;
        }

        request.Principal = principal;
        request.TenantId = principal.TenantId;

        ctx.Response.OnStarting(() =>
        {
            // Audit line. Token id, never the secret.
            log.LogInformation("{Method} {Path} -> {Status} (token {TokenId}, tenant {Tenant})",
                ctx.Request.Method, path, ctx.Response.StatusCode, principal.TokenId, principal.TenantId);
            return Task.CompletedTask;
        });

        await next(ctx);

        _ = tokens.TouchAsync(principal.TokenId, CancellationToken.None);
    }

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
                detail: $"This token has [{string.Join(", ", principal.Scopes)}] and needs '{scope}'.",
                statusCode: StatusCodes.Status403Forbidden);
    }
}
