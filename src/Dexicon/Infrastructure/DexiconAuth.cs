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
            // Which caller, not which credential. A rejection said only the path, so a
            // browser tab left open on an expired session and a credential being guessed
            // wrote the identical line, and a log full of them answered neither
            // question: not how many callers, not whether it was always the same one.
            log.LogWarning(
                "Rejected request to {Path} from {Remote} ({Agent}), credential {Digest}: "
                + "invalid, revoked or expired",
                OneLine(path),
                ctx.Connection.RemoteIpAddress?.ToString() ?? "an unknown address",
                Agent(ctx.Request.Headers.UserAgent.FirstOrDefault()),
                CallerDigest(presented));
            await Problem(ctx, StatusCodes.Status401Unauthorized, "Invalid credentials",
                "The credential was not recognised, or it has been revoked or has expired.");
            return;
        }

        request.Principal = principal;

        ctx.Response.OnStarting(() =>
        {
            // Audit line. The credential's id and name, never its value.
            log.LogInformation("{Method} {Path} -> {Status} (caller {TokenId} '{TokenName}')",
                ctx.Request.Method, OneLine(path), ctx.Response.StatusCode,
                principal.TokenId, OneLine(principal.TokenName));
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

    /// <summary>
    /// A short one-way mark for a credential that was rejected, so repeats can be
    /// counted without the credential being written down.
    ///
    /// Eight hex characters, which is 32 bits and deliberately narrow. The question it
    /// answers is "one caller retrying, or many", and at that width collisions are
    /// common enough that the mark is worth nothing to anyone reading the logs who
    /// wanted to confirm a guess. The full digest would answer the same question and be
    /// a confirmation oracle for any credential someone could think of.
    /// </summary>
    internal static string CallerDigest(string presented) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(presented)))[..8];

    /// <summary>
    /// The caller's own description of itself, which is the field that separates a stale
    /// browser tab from a script, and is worth more than either of the other two.
    ///
    /// Capped, because it is whatever the caller sent and a rejected request is the one
    /// path an unauthenticated caller can reach: uncapped, a kilobyte of user agent per
    /// poll is theirs to write into the audit trail. Cut before <see cref="OneLine"/>,
    /// so the cost of the pass is bounded by the cap and not by what was sent.
    /// </summary>
    internal static string Agent(string? header) =>
        string.IsNullOrWhiteSpace(header) ? "no user agent"
        : header.Length <= AgentMax ? OneLine(header)
        : OneLine(header[..AgentMax]) + "…";

    private const int AgentMax = 120;

    /// <summary>
    /// One log line, whatever the caller sent.
    ///
    /// A request path arrives URL-decoded, so <c>%0A</c> in it is a real newline by the
    /// time it reaches here, and a newline inside an entry lets the caller append a line
    /// that reads as the server's own. The same goes for the user-agent header, and for
    /// a key's name, which an admin chose but the log then quotes.
    ///
    /// The console sink renders message properties with <c>{Message:j}</c>, which quotes
    /// and escapes a string, so that output is already safe — see
    /// <c>LogOutput.ConsoleTemplate</c>. That is the sink's formatting and not this
    /// code's decision, and it covers one sink: a second one configured later, or that
    /// format specifier dropped, silently restores the hole. The value is held here
    /// instead, where the caller's text is known to be the caller's.
    ///
    /// Replaced rather than removed, and with U+FFFD, so a path that was odd still reads
    /// as odd rather than as a path somebody sent. Every control character goes and not
    /// just the two line breaks, because an escape sequence in a terminal is the same
    /// trick by another route.
    /// </summary>
    internal static string OneLine(string value)
    {
        var at = 0;
        while (at < value.Length && !char.IsControl(value[at])) at++;
        if (at == value.Length) return value;

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? Replacement : source[i];
        });
    }

    private const char Replacement = '�';

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
