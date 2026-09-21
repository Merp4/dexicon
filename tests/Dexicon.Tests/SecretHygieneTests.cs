using System.Text.Json;
using System.Text.RegularExpressions;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The guards from docs/10-security-secrets.md, as tests rather than as prose.
/// Prose does not hold a line.
/// </summary>
public sealed class SecretHygieneTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx"))
                               && !File.Exists(Path.Combine(dir.FullName, "LICENSE")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    [Fact]
    public void NoSecretValuesInTrackedConfiguration()
    {
        // A secret that appears in appsettings.json is a bug. This scans for keys that
        // look like credentials carrying a non-empty value.
        var root = RepoRoot();
        var suspicious = new Regex("password|secret|apikey|api_key|token|credential",
            RegexOptions.IgnoreCase);

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "appsettings*.json", SearchOption.AllDirectories))
        {
            if (file.Contains(".local.", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Walk(doc.RootElement, "", Path.GetRelativePath(root, file));
        }

        offenders.ShouldBeEmpty(
            $"secrets must never live in tracked configuration:\n  {string.Join("\n  ", offenders)}");

        void Walk(JsonElement el, string path, string file)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                    {
                        var next = path.Length == 0 ? prop.Name : $"{path}:{prop.Name}";
                        if (suspicious.IsMatch(prop.Name)
                            && prop.Value.ValueKind == JsonValueKind.String
                            && !string.IsNullOrEmpty(prop.Value.GetString()))
                            offenders.Add($"{file} -> {next}");
                        Walk(prop.Value, next, file);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray()) Walk(item, path, file);
                    break;
            }
        }
    }

    [Fact]
    public void EnvExampleDocumentsEveryVariableComposeUses()
    {
        // A new setting that is undocumented fails here rather than surprising someone
        // on their first run.
        var root = RepoRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        var example = File.ReadAllText(Path.Combine(root, ".env.example"));

        var referenced = Regex.Matches(compose, @"\$\{([A-Z0-9_]+)(?::-[^}]*)?\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        referenced.ShouldNotBeEmpty();

        var missing = referenced
            .Where(v => !Regex.IsMatch(example, $@"^#?\s*{Regex.Escape(v)}=", RegexOptions.Multiline))
            .ToList();

        missing.ShouldBeEmpty($".env.example is missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void QdrantApiKeyIsNeverBlankInCompose()
    {
        // Qdrant enables auth on the PRESENCE of its key variable, so an empty string
        // turns auth ON with an unmatchable key and 401s everything. Found in M0.
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));

        // An empty default would enable Qdrant auth with a key nothing can present.
        compose.ShouldNotContain("QDRANT_API_KEY:-}");

        var anchor = Regex.Match(compose, @"x-qdrant-api-key:\s*&qdrant-api-key\s*\$\{QDRANT_API_KEY:-([^}]+)\}");
        anchor.Success.ShouldBeTrue("the API key should be defined once as an anchor with a non-empty default");
        anchor.Groups[1].Value.Trim().ShouldNotBeEmpty();
    }

    [Fact]
    public void GitignoreCoversTheSecretsModel()
    {
        var gitignore = File.ReadAllText(Path.Combine(RepoRoot(), ".gitignore"));
        foreach (var pattern in new[] { ".env", "secrets/", "*.local.json", "*.pem", "*.key", "data/" })
            gitignore.ShouldContain(pattern);
        gitignore.ShouldContain("!.env.example");  // the template itself must stay tracked
    }

    // ── Token handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task Token_IsVerifiableOnceAndNeverRecoverable()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        await db.SaveChangesAsync();

        var service = new TokenService(db, TimeProvider.System);
        var (row, issued) = await service.CreateAsync("test", [Scopes.Search], null);

        issued.Presented.ShouldStartWith("dex_");

        // The stored hash must not contain the secret in any recoverable form.
        var hashText = Convert.ToBase64String(row.TokenHash);
        hashText.ShouldNotContain(issued.Secret);
        row.TokenHash.Length.ShouldBe(32);
        row.TokenSalt.Length.ShouldBe(32);

        (await service.VerifyAsync(issued.Presented)).ShouldNotBeNull();
        (await service.VerifyAsync(issued.Presented + "x")).ShouldBeNull();
        (await service.VerifyAsync("dex_wrong_secret")).ShouldBeNull();
        (await service.VerifyAsync(null)).ShouldBeNull();
        (await service.VerifyAsync("not-a-dexicon-token")).ShouldBeNull();
    }

    [Fact]
    public async Task Token_RevokedAndExpired_StopVerifying()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        await db.SaveChangesAsync();

        var service = new TokenService(db, TimeProvider.System);

        var (_, live) = await service.CreateAsync("live", [Scopes.Search], null);
        (await service.RevokeAsync((await service.VerifyAsync(live.Presented))!.TokenId)).ShouldBeTrue();
        (await service.VerifyAsync(live.Presented)).ShouldBeNull("a revoked token must stop working");

        var (_, expired) = await service.CreateAsync("expired", [Scopes.Search],
            DateTime.UtcNow.AddSeconds(-1));
        (await service.VerifyAsync(expired.Presented)).ShouldBeNull("an expired token must stop working");
    }

    [Fact]
    public async Task Token_Revoked_StopsVerifying()
    {
        // Was Token_DisabledTenant_StopsVerifying. A tenant is no longer a thing that can
        // be disabled, and revocation is now the only way a live key stops working, so it
        // is the path worth holding down.
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        await db.SaveChangesAsync();

        var service = new TokenService(db, TimeProvider.System);
        var (row, issued) = await service.CreateAsync("x", [Scopes.Search], null);
        (await service.VerifyAsync(issued.Presented)).ShouldNotBeNull();

        (await service.RevokeAsync(row.Id)).ShouldBeTrue();
        db.ChangeTracker.Clear();

        (await service.VerifyAsync(issued.Presented)).ShouldBeNull();
    }

    [Fact]
    public void Principal_AdminImpliesEveryScope()
    {
        var admin = new Principal("id", "n", new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin });
        admin.Has(Scopes.Search).ShouldBeTrue();
        admin.Has(Scopes.Ingest).ShouldBeTrue();

        var reader = new Principal("id", "n", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search });
        reader.Has(Scopes.Search).ShouldBeTrue();
        reader.Has(Scopes.Ingest).ShouldBeFalse();
        reader.Has(Scopes.Admin).ShouldBeFalse();
    }

    [Fact]
    public void PrincipalCacheKey_IsACryptographicDigest_NotA32BitHash()
    {
        // The key was `GetHashCode() + length`. A collision in 32 bits hands the second
        // caller the FIRST caller's authenticated principal without verifying anything,
        // so the width of this key is a security property and belongs in a test.
        var key = DexiconAuthMiddleware.PrincipalCacheKey("dex_01JBXQZ9K7MNPRSTVWXYZ01234_secret-value");

        var digest = key["principal::".Length..];
        digest.Length.ShouldBe(64, "SHA-256 as lower-case hex");
        digest.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void PrincipalCacheKey_NeverContainsTheToken()
    {
        // Cache keys surface in memory dumps and diagnostics. The credential must not.
        const string Token = "dex_01JBXQZ9K7MNPRSTVWXYZ01234_a-secret-nobody-should-read";

        DexiconAuthMiddleware.PrincipalCacheKey(Token).ShouldNotContain("a-secret-nobody-should-read");
    }

    /// <summary>
    /// A rejected request names the caller, so a stale browser tab and a credential being
    /// guessed can be told apart. The credential itself must still never be written.
    /// </summary>
    [Fact]
    public void CallerDigest_NeverContainsTheCredential()
    {
        const string Token = "dex_01JBXQZ9K7MNPRSTVWXYZ01234_a-secret-nobody-should-read";

        var digest = DexiconAuthMiddleware.CallerDigest(Token);

        digest.ShouldNotContain("a-secret-nobody-should-read");
        digest.ShouldNotContain("01JBXQZ9K7MNPRSTVWXYZ01234");
        digest.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void CallerDigest_IsNarrowEnoughToBeWorthless_AndWideEnoughToCount()
    {
        // 32 bits. It answers "one caller retrying, or several" and nothing else: at this
        // width a reader who wanted to confirm a guessed credential against the log finds
        // collisions instead of an answer. The full digest would confirm it.
        DexiconAuthMiddleware.CallerDigest("dex_anything").Length.ShouldBe(8);

        // Stable, or two lines from one caller cannot be recognised as one caller.
        DexiconAuthMiddleware.CallerDigest("dex_one")
            .ShouldBe(DexiconAuthMiddleware.CallerDigest("dex_one"));

        DexiconAuthMiddleware.CallerDigest("dex_one")
            .ShouldNotBe(DexiconAuthMiddleware.CallerDigest("dex_two"));
    }

    [Fact]
    public void PrincipalCacheKey_IsStableAndSeparatesTokensThatLookAlike()
    {
        const string Token = "dex_01JBXQZ9K7MNPRSTVWXYZ01234_secret-value";

        // Stable, or the cache never hits and PBKDF2 runs on every MCP call.
        DexiconAuthMiddleware.PrincipalCacheKey(Token)
            .ShouldBe(DexiconAuthMiddleware.PrincipalCacheKey(Token));

        // Same length, one character apart: the case the old key was weakest on.
        DexiconAuthMiddleware.PrincipalCacheKey(Token)
            .ShouldNotBe(DexiconAuthMiddleware.PrincipalCacheKey("dex_01JBXQZ9K7MNPRSTVWXYZ01234_secret-valuf"));
    }
}
