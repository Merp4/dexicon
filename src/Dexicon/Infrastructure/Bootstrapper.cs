using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Infrastructure;

/// <summary>
/// First-run setup and startup verification. Deliberately noisy: an endpoint that
/// resolves is not the same as an endpoint that resolves to the right thing, and the
/// first question when a client misbehaves is what the server actually talked to.
/// </summary>
public static class Bootstrapper
{
    public static async Task InitialiseAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILogger<Program>>();
        var options = sp.GetRequiredService<IOptions<DexiconOptions>>().Value;
        var db = sp.GetRequiredService<CatalogDbContext>();

        Directory.CreateDirectory(options.Storage.DataPath);
        Directory.CreateDirectory(options.Storage.BlobRoot);

        await db.Database.MigrateAsync();
        log.LogInformation("Catalogue ready at {Path}", options.Storage.CatalogPath);

        await ReconcileOrphanedJobsAsync(db, log);
        await VerifyDependenciesAsync(sp, log, options);
        await EnsureTenantAsync(db, log, options);
        await EnsureBootstrapTokenAsync(sp, db, log, options);
    }

    /// <summary>
    /// A job left Queued or Running belongs to a process that no longer exists: the
    /// queue is in-memory, so nothing will ever pick it up again. Without this, killing
    /// Dexicon mid-index leaves the corpus reading "indexing" forever, the UI shows a
    /// job that is not running, and `index_refresh` refuses to queue a replacement
    /// because one is apparently already pending. Observed exactly that after a restart
    /// during a large PDF.
    /// </summary>
    private static async Task ReconcileOrphanedJobsAsync(CatalogDbContext db, ILogger log)
    {
        var orphaned = await db.Jobs
            .Where(j => j.State == JobState.Queued || j.State == JobState.Running)
            .ToListAsync();

        if (orphaned.Count == 0) return;

        foreach (var job in orphaned)
        {
            job.State = JobState.Failed;
            job.Phase = null;
            job.FinishedUtc = DateTime.UtcNow;
            job.Error = "Interrupted — Dexicon restarted while this job was running. Re-run the index.";
        }

        // Any corpus mid-index is now simply not being indexed. Say so rather than
        // leaving a state that nothing will ever move on.
        var corpusIds = orphaned.Select(j => j.CorpusId).Distinct().ToList();
        await db.Corpora.Where(c => corpusIds.Contains(c.Id) && c.State == CorpusState.Indexing)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, CorpusState.Degraded));

        await db.SaveChangesAsync();
        log.LogWarning("Reconciled {Count} job(s) orphaned by a previous shutdown; affected corpora marked degraded",
            orphaned.Count);
    }

    /// <summary>
    /// Call both dependencies and log what answered. A wrong-but-reachable endpoint —
    /// someone else's Ollama on a shared network — is otherwise indistinguishable from
    /// the right one until the embeddings turn out to mean nothing.
    /// </summary>
    private static async Task VerifyDependenciesAsync(IServiceProvider sp, ILogger log, DexiconOptions options)
    {
        var vectors = sp.GetRequiredService<IVectorStore>();
        if (await vectors.PingAsync())
            log.LogInformation("Qdrant reachable at {Endpoint}", options.Qdrant.Endpoint);
        else
            log.LogError("Qdrant NOT reachable at {Endpoint}. Search will fail until it is.", options.Qdrant.Endpoint);

        var embedder = sp.GetRequiredService<IEmbeddingProvider>();
        try
        {
            var dims = await embedder.ProbeDimensionsAsync(options.Embedding.Model);
            log.LogInformation("Ollama reachable at {Endpoint}, model {Model} ({Dims}d)",
                options.Ollama.Endpoint, options.Embedding.Model, dims);
        }
        catch (Exception ex)
        {
            // Not fatal. Keyword search still works without embeddings, and taking the
            // service down because one dependency is cold would be a worse outage than
            // the one being reported.
            log.LogError(ex,
                "Ollama model '{Model}' not usable at {Endpoint}. Search degrades to keyword-only and " +
                "indexing will back off until it recovers.", options.Embedding.Model, options.Ollama.Endpoint);
        }
    }

    private static async Task EnsureTenantAsync(CatalogDbContext db, ILogger log, DexiconOptions options)
    {
        var id = options.Bootstrap.Tenant.Trim().ToLowerInvariant();
        if (await db.Tenants.AnyAsync(t => t.Id == id)) return;

        db.Tenants.Add(new Tenant
        {
            Id = id,
            DisplayName = id,
            CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        log.LogInformation("Created bootstrap tenant '{Tenant}'", id);
    }

    private static async Task EnsureBootstrapTokenAsync(
        IServiceProvider sp, CatalogDbContext db, ILogger log, DexiconOptions options)
    {
        var tenantId = options.Bootstrap.Tenant.Trim().ToLowerInvariant();
        var tokens = sp.GetRequiredService<TokenService>();

        // A pinned bootstrap token, for scripted setup and CI — and the escape hatch
        // when someone loses the one-time printed value. Without this the only recovery
        // from a lost token is deleting the catalogue, which also deletes every corpus.
        //
        // .env.example has documented this since the first commit; nothing implemented
        // it, so setting DEXICON__BOOTSTRAP__TOKEN did precisely nothing.
        if (options.Bootstrap.Token is { Length: > 0 } pinned)
        {
            if (await tokens.VerifyAsync(pinned) is not null) return;

            await tokens.AdoptAsync(tenantId, "bootstrap (pinned)", Scopes.All, pinned);
            log.LogWarning(
                "Adopted the bootstrap token from DEXICON__BOOTSTRAP__TOKEN. It is a SECRET: " +
                "it lives in your .env, which is gitignored and must never be committed.");
            return;
        }

        if (await db.Tokens.AnyAsync(t => t.TenantId == tenantId && t.RevokedUtc == null)) return;

        var (_, issued) = await tokens.CreateAsync(tenantId, "bootstrap", Scopes.All, expiresUtc: null);

        // A log line is an acceptable delivery channel for a value that is about to be
        // rotated; a config file is not. Printed exactly once, on first run only.
        log.LogWarning(
            "\n" +
            "  ┌───────────────────────────────────────────────────────────────────────┐\n" +
            "  │  Dexicon bootstrap token — shown ONCE, copy it now                    │\n" +
            "  └───────────────────────────────────────────────────────────────────────┘\n" +
            "  {Token}\n\n" +
            "  claude mcp add --transport http dexicon http://localhost:8477/mcp \\\n" +
            "    --header \"Authorization: Bearer {Token}\"\n",
            issued.Presented, issued.Presented);
    }
}
