using System.Security.Cryptography;
using System.Text;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Auth;

public static class Scopes
{
    public const string Search = "search";
    public const string Ingest = "ingest";
    public const string Admin = "admin";

    public static readonly string[] All = [Search, Ingest, Admin];
}

/// <summary>An authenticated caller. Carries no secret.</summary>
public sealed record Principal(string TokenId, string TokenName, string TenantId, IReadOnlySet<string> Scopes)
{
    public bool Has(string scope) => Scopes.Contains(scope) || Scopes.Contains(Auth.Scopes.Admin);
}

public sealed record IssuedToken(string Id, string Secret)
{
    /// <summary>The full value the caller presents. Shown once and never stored.</summary>
    public string Presented => $"{TokenService.Prefix}{Id}_{Secret}";
}

/// <summary>
/// The only credential. Format <c>dex_&lt;id&gt;_&lt;secret&gt;</c>:
/// the prefix makes it greppable by secret scanners, the id lets the UI list and
/// revoke a token without storing its secret, and the secret is verified against a
/// PBKDF2 hash in constant time.
/// </summary>
public sealed class TokenService(CatalogDbContext db, TimeProvider clock)
{
    public const string Prefix = "dex_";
    private const int Iterations = 600_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    private const int SecretBytes = 32;

    public async Task<(ApiToken Row, IssuedToken Issued)> CreateAsync(
        string tenantId, string name, IEnumerable<string> scopes, DateTime? expiresUtc, CancellationToken ct = default)
    {
        var id = Ulid.NewUlid().ToString();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);

        var row = new ApiToken
        {
            Id = id,
            Name = name,
            TokenHash = Hash(secret, salt),
            TokenSalt = salt,
            TenantId = tenantId,
            Scopes = string.Join(',', scopes.Select(s => s.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)),
            CreatedUtc = clock.GetUtcNow().UtcDateTime,
            ExpiresUtc = expiresUtc,
        };

        db.Tokens.Add(row);
        await db.SaveChangesAsync(ct);
        return (row, new IssuedToken(id, secret));
    }

    /// <summary>
    /// Verify a presented token. Returns null for every failure mode — unknown id,
    /// wrong secret, revoked, expired, disabled tenant — because telling a caller
    /// which of those it was is free reconnaissance.
    /// </summary>
    public async Task<Principal?> VerifyAsync(string? presented, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;
        if (!presented.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var rest = presented[Prefix.Length..];
        var sep = rest.IndexOf('_', StringComparison.Ordinal);
        if (sep <= 0 || sep == rest.Length - 1) return null;

        var id = rest[..sep];
        var secret = rest[(sep + 1)..];

        // AsNoTracking is load-bearing, not an optimisation. RevokeAsync and TouchAsync
        // use ExecuteUpdateAsync, which writes straight to the database and does NOT
        // update the change tracker. A tracked read therefore returns the stale entity,
        // with RevokedUtc still null — so a revoked token kept authenticating for the
        // lifetime of the DbContext. Caught by Token_RevokedAndExpired_StopVerifying.
        var row = await db.Tokens.AsNoTracking().Include(t => t.Tenant)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null) return null;
        if (!row.IsActive(clock.GetUtcNow().UtcDateTime)) return null;
        if (row.Tenant is null || row.Tenant.Disabled) return null;

        var candidate = Hash(secret, row.TokenSalt);
        if (!CryptographicOperations.FixedTimeEquals(candidate, row.TokenHash)) return null;

        return new Principal(row.Id, row.Name, row.TenantId,
            row.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Store a token whose secret the operator chose, rather than one we generated.
    /// Used only for <c>DEXICON__BOOTSTRAP__TOKEN</c> — scripted setup, and recovery
    /// when the one-time printed value is lost.
    ///
    /// The value must still be a well-formed <c>dex_&lt;id&gt;_&lt;secret&gt;</c>, so the
    /// same parser serves both paths and an operator cannot accidentally create a token
    /// the verifier will never recognise.
    /// </summary>
    public async Task<ApiToken> AdoptAsync(string tenantId, string name, IEnumerable<string> scopes,
        string presented, CancellationToken ct = default)
    {
        if (!presented.StartsWith(Prefix, StringComparison.Ordinal))
            throw new ArgumentException($"A bootstrap token must start with '{Prefix}'.", nameof(presented));

        var rest = presented[Prefix.Length..];
        var sep = rest.IndexOf('_', StringComparison.Ordinal);
        if (sep <= 0 || sep == rest.Length - 1)
            throw new ArgumentException($"Expected the form {Prefix}<id>_<secret>.", nameof(presented));

        var id = rest[..sep];
        var secret = rest[(sep + 1)..];
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);

        var existing = await db.Tokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (existing is not null)
        {
            // Re-adopting the same id rotates its hash and un-revokes it, which is
            // exactly what "I lost access, put this value back" should do.
            existing.TokenHash = Hash(secret, salt);
            existing.TokenSalt = salt;
            existing.TenantId = tenantId;
            existing.RevokedUtc = null;
            existing.ExpiresUtc = null;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var row = new ApiToken
        {
            Id = id,
            Name = name,
            TokenHash = Hash(secret, salt),
            TokenSalt = salt,
            TenantId = tenantId,
            Scopes = string.Join(',', scopes.Select(s => s.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)),
            CreatedUtc = clock.GetUtcNow().UtcDateTime,
        };

        db.Tokens.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task TouchAsync(string tokenId, CancellationToken ct = default)
    {
        await db.Tokens.Where(t => t.Id == tokenId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedUtc, clock.GetUtcNow().UtcDateTime), ct);
    }

    public async Task<bool> RevokeAsync(string tokenId, string tenantId, CancellationToken ct = default)
    {
        var n = await db.Tokens.Where(t => t.Id == tokenId && t.TenantId == tenantId && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, clock.GetUtcNow().UtcDateTime), ct);
        return n > 0;
    }

    private static byte[] Hash(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
