using System.Reflection;
using System.Security.Cryptography;
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
    /// migrates a database, contacts Qdrant and Ollama, and sets an admin password. On CI
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
        await EnsureAdminPasswordAsync(sp, log, options);
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

    /// <summary>
    /// Ensure there is an admin password, because the UI cannot be reached without one.
    ///
    /// Configured, or generated and printed once, which is what the bootstrap token has
    /// always done. Stored hashed in the catalogue rather than read from the environment on
    /// each request, so it can be changed in the UI without a restart.
    /// </summary>
    private static async Task EnsureAdminPasswordAsync(
        IServiceProvider sp, ILogger log, DexiconOptions options)
    {
        var tokens = sp.GetRequiredService<TokenService>();

        if (options.Admin.Password is { Length: > 0 } configured)
        {
            // Rewritten on every start, so changing the variable changes the password and
            // an operator locked out by a forgotten one has a way back in.
            await tokens.SetPasswordAsync(configured);
            log.LogInformation("Admin password set from DEXICON__ADMIN__PASSWORD.");
            return;
        }

        if (await tokens.HasPasswordAsync()) return;

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await tokens.SetPasswordAsync(generated);

        log.LogWarning(
            "\n" +
            "  ┌───────────────────────────────────────────────────────────────────────┐\n" +
            "  │  Dexicon admin password: shown once, copy it now                      │\n" +
            "  └───────────────────────────────────────────────────────────────────────┘\n" +
            "  {Password}\n\n" +
            "  Sign in at the web UI with this. Set DEXICON_ADMIN_PASSWORD in your .env to\n" +
            "  own, or change it in the UI once you are in.\n",
            generated);
    }

    private static async Task EnsureBootstrapTokenAsync(
        IServiceProvider sp, CatalogDbContext db, ILogger log, DexiconOptions options)
    {
        var tokens = sp.GetRequiredService<TokenService>();

        // A pinned key, for scripted setup and CI: somewhere that has to reach the MCP
        // surface with a known credential and no browser to create one in.
        //
        // .env.example has documented this since the first commit; nothing implemented
        // it, so setting DEXICON__BOOTSTRAP__TOKEN did precisely nothing.
        if (options.Bootstrap.Token is { Length: > 0 } pinned)
        {
            if (await tokens.VerifyAsync(pinned) is not null) return;

            await tokens.AdoptAsync("bootstrap (pinned)", Scopes.Issuable, pinned);
            log.LogWarning(
                "Adopted the bootstrap key from DEXICON__BOOTSTRAP__TOKEN. It is a SECRET: " +
                "it lives in your .env, which is gitignored and must never be committed.");
            return;
        }

        if (await db.Tokens.AnyAsync(t => t.RevokedUtc == null)) return;

        // Nothing is minted on a blank value any more, and the reason is D-28 rather than
        // tidiness. The generated key existed because a token was the only credential: it
        // was how you reached the UI, so one had to exist before you could do anything.
        // The password is that now, and printing a second secret beside it on first run
        // mostly invites pasting the wrong one into the sign-in form.
        //
        // Creating the key in the UI is also the only way to choose what it reaches. At
        // first run there is no corpus to choose, so a minted key can only ever reach
        // everything, which is the opposite of what D-28 is for.
        log.LogInformation(
            "No API key exists yet. Sign in at the web UI and create one under Access, " +
            "which gives you the corpora it may reach and a ready-to-paste " +
            "`claude mcp add` command. For scripted setup with no browser, set " +
            "DEXICON__BOOTSTRAP__TOKEN to a value of your own instead.");
    }
}
