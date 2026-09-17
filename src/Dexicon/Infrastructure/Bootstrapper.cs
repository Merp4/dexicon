using System.Reflection;
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
    /// <summary>
    /// True when a build-time tool has loaded this entry point to inspect the app rather
    /// than to serve it.
    /// </summary>
    /// <remarks>
    /// `dotnet build` generates the OpenAPI document by loading this assembly and calling
    /// Main, and it runs it all the way into <c>app.RunAsync()</c> before intercepting,
    /// so there is no host lifecycle hook early enough to use, and the check has to be
    /// explicit. Without it a build performs first-run setup: creates directories,
    /// migrates a database, contacts Qdrant and Ollama, and mints a bootstrap token. On CI
    /// that failed outright ("Access to the path '/data' is denied") and turned every
    /// build red; where it had not failed it was doing all of that silently.
    ///
    /// The entry assembly is the tool's, because Main is invoked by reflection. Both names
    /// are matched: the dll ships as dotnet-getdocument.dll but its assembly name is
    /// GetDocument.Insider.
    /// </remarks>
    public static bool IsBuildTimeToolRun =>
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider" or "dotnet-getdocument";

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
        await PurgeLegacyChunksAsync(sp, db, log);
    }

    /// <summary>
    /// One-time cleanup after the chunk-sets migration: delete points written before sets
    /// existed. They carry no chunk_set_id, so no query can reach them, and because point
    /// ids are now derived from the set rather than the corpus, re-indexing writes fresh
    /// points beside them instead of overwriting them. Left alone they are permanent
    /// garbage in the index.
    ///
    /// Guarded on there being something to do: once every chunk set has indexed at least
    /// once, there is nothing from before, and this costs one cheap filtered delete.
    /// </summary>
    private static async Task PurgeLegacyChunksAsync(IServiceProvider sp, CatalogDbContext db, ILogger log)
    {
        // Guarded on work actually being outstanding. An earlier version asked whether any
        // set had never been indexed, which the migration had already answered "no" to by
        // copying the corpus's timestamp, so the sweep never ran and nothing said so.
        var anyUnindexed = await db.FileChunkStates.AnyAsync(s => s.ContentHash == null);
        if (!anyUnindexed) return;

        try
        {
            var vectors = sp.GetRequiredService<IVectorStore>();
            var touched = await vectors.PurgeUnsetChunksAsync();
            if (touched > 0)
                log.LogInformation(
                    "Swept {Count} collection(s) for chunks written before chunk sets existed", touched);
        }
        catch (Exception ex)
        {
            // Not fatal. Qdrant may simply not be up yet, and unreachable points are a
            // waste of space rather than a correctness problem: they match no query.
            log.LogWarning(ex, "Could not sweep pre-chunk-set vectors; will retry on next start");
        }
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
            job.Error = "Interrupted: Dexicon restarted while this job was running. Re-run the index.";
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
    /// Call both dependencies and log what answered. A wrong but reachable endpoint, such
    /// as someone else's Ollama on a shared network, is otherwise indistinguishable from
    /// the right one until the embeddings turn out to mean nothing.
    /// </summary>
    private static async Task VerifyDependenciesAsync(IServiceProvider sp, ILogger log, DexiconOptions options)
    {
        var vectors = sp.GetRequiredService<IVectorStore>();
        if (await vectors.PingAsync())
            log.LogInformation("Qdrant reachable at {Endpoint}", options.Qdrant.Endpoint);
        else
            log.LogError("Qdrant NOT reachable at {Endpoint}. Search will fail until it is.", options.Qdrant.Endpoint);

        var embedder = sp.GetRequiredService<IEmbeddingService>();
        var target = new EmbeddingTarget(options.Embedding.Provider, options.Embedding.Model);
        try
        {
            var dims = await embedder.ProbeDimensionsAsync(target);
            log.LogInformation("Embedding provider '{Provider}' reachable, model {Model} ({Dims}d)",
                target.Provider, target.Model, dims);
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

        // A pinned bootstrap token, for scripted setup and CI, and the recovery path
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
            "  │  Dexicon bootstrap token: shown once, copy it now                     │\n" +
            "  └───────────────────────────────────────────────────────────────────────┘\n" +
            "  {Token}\n\n" +
            "  claude mcp add --transport http dexicon http://localhost:8477/mcp \\\n" +
            "    --header \"Authorization: Bearer {Token}\"\n",
            issued.Presented, issued.Presented);
    }
}
