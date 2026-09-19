using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Dexicon.Core.Auth;

/// <summary>
/// What the administrator's password is exchanged for: a bearer that lives in memory and
/// nowhere else.
///
/// In memory rather than in the catalogue, so that no durable row ever carries the
/// <c>admin</c> scope and a restart signs the operator out. The browser holds the value in
/// <c>sessionStorage</c> exactly as it held a pasted token before, so the SPA keeps the
/// bearer model it already had and gains no cookie, and with no cookie there is no CSRF
/// surface to reason about. See docs/decisions.md D-28.
/// </summary>
public sealed class AdminSessions(TimeProvider clock) : IDisposable
{
    /// <summary>
    /// Its own store, not the shared <see cref="IMemoryCache"/>.
    ///
    /// Revoking a key calls <c>IMemoryCacheEvictor.EvictPrincipals</c>, which clears the
    /// shared cache outright because MemoryCache has no prefix scan. That was harmless
    /// while the cache held nothing but 60-second principals. With sessions in it, revoking
    /// any key signed the operator out of the UI mid-action, and the browser reported
    /// "Invalid credentials" for something it had just done successfully.
    /// </summary>
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose() => _cache.Dispose();

    /// <summary>
    /// Distinct from <c>dex_</c> so the middleware can tell a session from a key by
    /// inspection, and so a session value pasted into an agent's configuration fails
    /// immediately rather than working until it expires.
    /// </summary>
    public const string Prefix = "dexs_";

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private const int SecretBytes = 32;

    public sealed record Session(string Presented, DateTime ExpiresUtc);

    public Session Issue()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var presented = Prefix + secret;
        var expires = clock.GetUtcNow().UtcDateTime + Lifetime;

        _cache.Set(KeyFor(presented), expires, Lifetime);
        return new Session(presented, expires);
    }

    /// <summary>
    /// The principal for a session bearer, or null when it is not one of ours.
    ///
    /// <see cref="Scopes.Admin"/> only. <see cref="Principal.Has"/> already treats admin as
    /// implying the rest, so a session reaches every guarded endpoint without the scope
    /// string having to enumerate them.
    /// </summary>
    public Principal? Verify(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        if (!presented.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        if (!_cache.TryGetValue(KeyFor(presented), out DateTime expires)) return null;
        if (expires <= clock.GetUtcNow().UtcDateTime) return null;

        return new Principal("admin-session", "admin", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin });
    }

    /// <summary>Sign out. Idempotent, because a browser may send it twice.</summary>
    public void Revoke(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return;
        _cache.Remove(KeyFor(presented));
    }

    /// <summary>
    /// SHA-256 of the presented value, never the value itself: cache keys turn up in dumps
    /// and diagnostics, and a live credential should not.
    /// </summary>
    private static string KeyFor(string presented) =>
        "admin-session::" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented)));
}

/// <summary>
/// The exponential back-off on password attempts.
///
/// Counted globally rather than per caller. There is one password, so there is one thing
/// to guess, and in a single container every caller arrives from the same gateway address,
/// which makes the source address useless as a discriminator rather than merely imperfect.
///
/// A delay and not a lockout. With one shared credential a lockout is a denial of service
/// that anyone able to reach the port can inflict on the owner, whereas a delay never locks
/// the legitimate operator out; it only makes them wait, and the cap bounds how long.
///
/// Held in memory, so a restart clears it. That is a real limit and an acceptable one: a
/// restart needs host access, and host access already defeats this model (docs/07).
/// </summary>
public sealed class LoginThrottle(TimeProvider clock) : IDisposable
{
    private const string CacheKey = "admin-login-failures";

    /// <summary>
    /// Its own store, for the same reason as <see cref="AdminSessions"/>, and one of its
    /// own: a failed-sign-in counter that an unrelated admin action resets is a counter an
    /// attacker can have cleared for them.
    /// </summary>
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose() => _cache.Dispose();

    /// <summary>Attempts allowed at full speed before the delay starts growing.</summary>
    public const int Free = 2;

    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    /// <summary>A counter idle for this long is forgotten, so a typo today costs nothing tomorrow.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public int Failures => _cache.TryGetValue(CacheKey, out int n) ? n : 0;

    /// <summary>
    /// How long to wait before answering the next attempt. Doubles per failure beyond
    /// <see cref="Free"/> and stops at <see cref="Cap"/>.
    /// </summary>
    public TimeSpan Delay()
    {
        var failures = Failures;
        if (failures < Free) return TimeSpan.Zero;

        var seconds = Math.Pow(2, Math.Min(failures - Free, 20));
        return seconds >= Cap.TotalSeconds ? Cap : TimeSpan.FromSeconds(seconds);
    }

    public void RecordFailure() => _cache.Set(CacheKey, Failures + 1, Window);

    /// <summary>A correct password clears the counter, so the operator is never left waiting.</summary>
    public void Reset() => _cache.Remove(CacheKey);

    /// <summary>
    /// Wait out the current delay. Cancellation is the caller's request aborting, which
    /// must not be reported as a failed attempt, so it propagates rather than being caught.
    /// </summary>
    public Task WaitAsync(CancellationToken ct = default)
    {
        var delay = Delay();
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, clock, ct);
    }
}
