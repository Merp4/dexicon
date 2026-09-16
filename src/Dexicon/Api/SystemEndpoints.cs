using System.Text.Json;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

public static class SystemEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/search", async (SearchApiRequest body, RequestContext rc, SearchService search,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(body.Query))
                return Results.Problem(title: "Query is required", statusCode: 400);

            var result = await search.SearchAsync(rc.RequireTenant(), new SearchRequest
            {
                Query = body.Query,
                Corpus = body.Corpus,
                Mode = Mapping.ParseMode(body.Mode),
                Limit = Math.Clamp(body.Limit ?? 10, 1, 50),
                PathPrefix = body.PathPrefix,
                Language = body.Language,
                Symbol = body.Symbol,
            }, ct);

            return Results.Ok(result);
        }).WithTags("Search");
    }

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/jobs").WithTags("Jobs");

        g.MapGet("/", async (string? corpusId, int? limit, RequestContext rc, ScopeResolver scopes,
            CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var visible = await scopes.VisibleAsync(rc.RequireTenant(), ct);
            var ids = visible.Select(c => c.Id).ToList();

            var q = db.Jobs.Where(j => ids.Contains(j.CorpusId));
            if (!string.IsNullOrWhiteSpace(corpusId)) q = q.Where(j => j.CorpusId == corpusId);

            // Newest first, by when it was QUEUED. Ordering on StartedUtc floated every
            // never-started job to the top, so two long-dead failures sat above the job
            // that was running -- and anything reading jobs[0] to find "the current job"
            // got a stale answer.
            var jobs = await q.OrderByDescending(j => j.QueuedUtc).ThenByDescending(j => j.Id)
                .Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);

            return Results.Ok(jobs.Select(j => j.ToSummary()));
        });

        g.MapGet("/{id}", async (string id, RequestContext rc, ScopeResolver scopes, CatalogDbContext db,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return Results.NotFound();

            var visible = await scopes.VisibleAsync(rc.RequireTenant(), ct);
            if (!visible.Exists(c => c.Id == job.CorpusId)) return Results.NotFound();

            return Results.Ok(job.ToSummary());
        });
    }

    /// <summary>
    /// Server-sent events for live indexing progress. Chosen over SignalR because a
    /// one-way stream of small JSON objects does not need a negotiation handshake, a
    /// client library, or a fallback transport.
    /// </summary>
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/events", async (HttpContext ctx, RequestContext rc, ScopeResolver scopes,
            IndexProgressBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is not null)
            {
                ctx.Response.StatusCode = 403;
                return;
            }

            var visible = await scopes.VisibleAsync(rc.RequireTenant(), ct);
            var visibleIds = visible.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            using var _ = broadcaster.Subscribe(out var reader);
            await ctx.Response.WriteAsync(": connected\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            var heartbeat = Task.Delay(TimeSpan.FromSeconds(20), ct);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var next = reader.ReadAsync(ct).AsTask();
                    var winner = await Task.WhenAny(next, heartbeat);

                    if (winner == heartbeat)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                        heartbeat = Task.Delay(TimeSpan.FromSeconds(20), ct);
                        continue;
                    }

                    var progress = await next;
                    // Events are tenant-scoped: another tenant's indexing progress is
                    // not this caller's business, and its corpus ids are not either.
                    if (!visibleIds.Contains(progress.CorpusId)) continue;

                    var json = JsonSerializer.Serialize(progress, JsonOptions.Web);
                    await ctx.Response.WriteAsync($"event: progress\ndata: {json}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client went away — normal */ }
        }).WithTags("Events");
    }

    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        // Browsing the mounted tree, so the UI can offer a picker rather than a text box
        // the user can type a non-existent path into.
        app.MapGet("/api/workspaces", (string? path, RequestContext rc, CorpusIndexer indexer,
            IOptions<DexiconOptions> opts) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            string full;
            try { full = indexer.ResolveWorkspacePath(path); }
            catch (UnauthorizedAccessException ex)
            { return Results.Problem(title: "Path refused", detail: ex.Message, statusCode: 400); }

            if (!Directory.Exists(full))
                return Results.Problem(
                    title: "Not mounted",
                    detail: $"'{path}' does not exist under {opts.Value.Indexing.WorkspaceRoot}. " +
                            "Dexicon can only index paths bind-mounted into the container — see WORKSPACE_ROOT.",
                    statusCode: 404);

            var root = Path.GetFullPath(opts.Value.Indexing.WorkspaceRoot);
            var entries = new List<WorkspaceEntry>();

            foreach (var dir in Directory.EnumerateDirectories(full).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') && name is not ".github") continue;
                int? children = null;
                try { children = Directory.EnumerateFileSystemEntries(dir).Take(500).Count(); } catch { /* unreadable */ }
                entries.Add(new WorkspaceEntry(name, Path.GetRelativePath(root, dir).Replace('\\', '/'), true, children));
            }

            return Results.Ok(new
            {
                root = opts.Value.Indexing.WorkspaceRoot,
                path = Path.GetRelativePath(root, full).Replace('\\', '/'),
                entries,
            });
        }).WithTags("Workspaces");
    }

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants").WithTags("Tenants");

        g.MapGet("/", async (RequestContext rc, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenants = await db.Tenants.OrderBy(t => t.Id).ToListAsync(ct);
            return Results.Ok(tenants.Select(t => new { t.Id, t.DisplayName, t.CreatedUtc, t.Disabled }));
        });

        g.MapPost("/", async (CreateTenantRequest body, RequestContext rc, CatalogDbContext db,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            var id = body.Id?.Trim().ToLowerInvariant() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$"))
                return Results.Problem(
                    title: "Invalid tenant id",
                    detail: "Use 3-40 characters: lowercase letters, digits and hyphens, not starting or ending with a hyphen.",
                    statusCode: 400);

            if (await db.Tenants.AnyAsync(t => t.Id == id, ct))
                return Results.Problem(title: "Tenant already exists", statusCode: 409);

            var tenant = new Tenant { Id = id, DisplayName = body.DisplayName ?? id, CreatedUtc = DateTime.UtcNow };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/tenants/{id}", new { tenant.Id, tenant.DisplayName, tenant.CreatedUtc });
        });

        g.MapDelete("/{id}", async (string id, RequestContext rc, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null) return Results.NotFound();

            // Refused while it owns corpora. An implicit cascade over someone's whole
            // index is not a thing a button should do.
            var owned = await db.Corpora.CountAsync(c => c.TenantId == id, ct);
            if (owned > 0)
                return Results.Problem(
                    title: "Tenant still owns corpora",
                    detail: $"Tenant '{id}' owns {owned} corpus/corpora. Delete or move them first.",
                    statusCode: 409);

            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        var t = app.MapGroup("/api/tokens").WithTags("Tokens");

        t.MapGet("/", async (RequestContext rc, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();
            var rows = await db.Tokens.Where(x => x.TenantId == tenant)
                .OrderByDescending(x => x.CreatedUtc).ToListAsync(ct);
            return Results.Ok(rows.Select(x => x.ToSummary()));
        });

        t.MapPost("/", async (CreateTokenRequest body, RequestContext rc, TokenService tokens,
            IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenant = rc.RequireTenant();

            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.Problem(title: "Name is required", statusCode: 400);

            var requested = body.Scopes is { Count: > 0 } ? body.Scopes : [Scopes.Search];
            var unknown = requested.Where(s => !Scopes.All.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                return Results.Problem(
                    title: "Unknown scope",
                    detail: $"{string.Join(", ", unknown)}. Valid scopes: {string.Join(", ", Scopes.All)}.",
                    statusCode: 400);

            var expires = body.ExpiresInDays is { } d and > 0 ? DateTime.UtcNow.AddDays(d) : (DateTime?)null;
            var (row, issued) = await tokens.CreateAsync(tenant, body.Name.Trim(), requested, expires, ct);

            // The highest-value thing on the page: the step between installed and working.
            var command =
                $"claude mcp add --transport http dexicon http://localhost:8477/mcp \\\n" +
                $"  --header \"Authorization: Bearer {issued.Presented}\"";

            return Results.Ok(new CreatedTokenResponse(row.ToSummary(), issued.Presented, command));
        });

        t.MapDelete("/{id}", async (string id, RequestContext rc, TokenService tokens, IMemoryCacheEvictor evictor,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var revoked = await tokens.RevokeAsync(id, rc.RequireTenant(), ct);
            if (!revoked) return Results.NotFound();

            // Revocation must be effective immediately, not after the principal cache TTL.
            evictor.EvictPrincipals();
            return Results.NoContent();
        });
    }

    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz/live", () => Results.Ok(new { status = "ok" })).WithTags("Health");

        app.MapGet("/healthz/ready", async (IVectorStore vectors, CatalogDbContext db, CancellationToken ct) =>
        {
            // Ollama being down does NOT make the service unready: keyword search still
            // works, and taking the whole service down because embeddings are cold is a
            // worse outage than the one being reported.
            var qdrant = await vectors.PingAsync(ct);
            var catalogue = await db.Database.CanConnectAsync(ct);
            var ready = qdrant && catalogue;

            return ready
                ? Results.Ok(new { status = "ready", qdrant, catalogue })
                : Results.Json(new { status = "not-ready", qdrant, catalogue }, statusCode: 503);
        }).WithTags("Health");

        app.MapGet("/api/embedding-models", async (RequestContext rc, IEmbeddingProvider embedder,
            CatalogDbContext db, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            IReadOnlyList<AvailableModel> models;
            try
            {
                models = await embedder.ListModelsAsync(ct);
            }
            catch (EmbeddingUnavailableException ex)
            {
                return Results.Problem(
                    title: "Embedding service unavailable",
                    detail: ex.Message,
                    statusCode: 503);
            }

            // Which models are already in use, so the UI can warn before someone deletes
            // the last set on one -- and so a model that is pulled but unused is visibly
            // available rather than looking the same as one that is load-bearing.
            var inUse = (await db.ChunkSets.Select(s => s.EmbeddingModel).Distinct().ToListAsync(ct))
                .Select(ModelNames.Normalise).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Ollama serves chat models from the same endpoint and they cannot embed.
            // Offering them would turn a bad pick into a 503 much later, at index time.
            var embedding = models
                .Where(m => ModelNames.LooksLikeAnEmbeddingModel(m.Name, m.Family))
                .Select(m => new EmbeddingModelInfo(
                    m.Name, m.SizeBytes, m.Dimensions,
                    inUse.Contains(ModelNames.Normalise(m.Name))))
                .ToList();

            return Results.Ok(new
            {
                configured = opts.Value.Embedding.Model,
                models = embedding,
                // Said plainly: an empty list otherwise reads as "Ollama is broken".
                note = embedding.Count == 0
                    ? "No embedding models are pulled. Run: docker compose exec dexicon-ollama ollama pull nomic-embed-text"
                    : null,
            });
        }).WithTags("System");

        app.MapPost("/api/embedding-models/pull", async (PullModelRequest body, HttpContext http,
            RequestContext rc, IEmbeddingProvider embedder, ILoggerFactory logs, CancellationToken ct) =>
        {
            // Admin, not ingest: this downloads gigabytes onto a shared volume.
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(body.Model))
                return Results.Problem(title: "A model name is required", statusCode: 400);

            var model = body.Model.Trim();
            var log = logs.CreateLogger("Dexicon.ModelPull");
            log.LogInformation("Pulling embedding model {Model}", model);

            // Server-sent events, because a pull takes minutes and a progress bar that
            // only moves when it finishes is not a progress bar. Same transport the
            // indexing UI already uses, so the client needs nothing new.
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            try
            {
                await foreach (var progress in embedder.PullModelAsync(model, ct))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        model,
                        progress.Status,
                        progress.Completed,
                        progress.Total,
                        progress.Percent,
                        progress.Done,
                    }, JsonOptions.Web);

                    await http.Response.WriteAsync($"data: {json}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }

                log.LogInformation("Pulled embedding model {Model}", model);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The client navigated away. Ollama keeps the partial download and
                // resumes next time, so there is nothing to clean up.
                log.LogInformation("Pull of {Model} was cancelled by the client", model);
            }
            catch (EmbeddingUnavailableException ex)
            {
                log.LogWarning(ex, "Pull of {Model} failed", model);
                var json = JsonSerializer.Serialize(new { model, error = ex.Message }, JsonOptions.Web);
                await http.Response.WriteAsync($"data: {json}\n\n", CancellationToken.None);
            }

            return Results.Empty;
        }).WithTags("System");

        app.MapDelete("/api/embedding-models/{model}", async (string model, RequestContext rc,
            IEmbeddingProvider embedder, CatalogDbContext db, IOptions<DexiconOptions> opts,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            // Refuse while anything depends on it. Deleting a model out from under a chunk
            // set does not fail loudly -- the set keeps its vectors and its collection, and
            // breaks only at the next index or the next semantic query, by which point the
            // cause is several steps away.
            // Compared on the normalised name, so deleting "nomic-embed-text:latest"
            // still sees the sets that recorded it as "nomic-embed-text".
            var usedBy = (await db.ChunkSets
                    .Select(s => new { s.Name, Corpus = s.Corpus!.Name, s.EmbeddingModel })
                    .ToListAsync(ct))
                .Where(s => ModelNames.SameModel(s.EmbeddingModel, model))
                .ToList();

            if (usedBy.Count > 0)
                return Results.Problem(
                    title: "Model is in use",
                    detail: $"'{model}' is the embedding model for " +
                            string.Join(", ", usedBy.Select(u => $"{u.Corpus}:{u.Name}")) +
                            ". Migrate those chunk sets to another model first.",
                    statusCode: 409);

            if (ModelNames.SameModel(model, opts.Value.Embedding.Model))
                return Results.Problem(
                    title: "Model is the configured default",
                    detail: $"'{model}' is DEXICON__EMBEDDING__MODEL, so new corpora would be created " +
                            "against a model that is no longer pulled. Change the configuration first.",
                    statusCode: 409);

            try
            {
                await embedder.DeleteModelAsync(model, ct);
                return Results.NoContent();
            }
            catch (EmbeddingUnavailableException ex)
            {
                return Results.Problem(title: "Could not delete model", detail: ex.Message, statusCode: 503);
            }
        }).WithTags("System");

        app.MapGet("/healthz", async (RequestContext rc, IVectorStore vectors, IEmbeddingProvider embedder,
            CatalogDbContext db, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            var qdrant = await vectors.PingAsync(ct);
            string? embeddingError = null;
            var dims = embedder.Dimensions;
            try { if (dims == 0) dims = await embedder.ProbeDimensionsAsync(opts.Value.Embedding.Model, ct); }
            catch (EmbeddingUnavailableException ex) { embeddingError = ex.Message; }

            var activeJob = await db.Jobs.Where(j => j.State == JobState.Running)
                .OrderByDescending(j => j.StartedUtc).FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                status = qdrant ? "ok" : "degraded",
                qdrant = new { reachable = qdrant, endpoint = opts.Value.Qdrant.Endpoint },
                ollama = new
                {
                    reachable = embeddingError is null,
                    endpoint = opts.Value.Ollama.Endpoint,
                    model = opts.Value.Embedding.Model,
                    dimensions = dims,
                    error = embeddingError,
                },
                corpora = await db.Corpora.CountAsync(ct),
                activeJob = activeJob?.ToSummary(),
            });
        }).WithTags("Health");
    }
}

/// <summary>Lets token revocation punch through the principal cache immediately.</summary>
public interface IMemoryCacheEvictor
{
    void EvictPrincipals();
}

public static class ModelNames
{
    /// <summary>
    /// Whether a pulled model can embed.
    ///
    /// A heuristic, and labelled as one: Ollama's /api/tags does not say what a model is
    /// FOR, and asking every model to embed a probe string would mean loading each one
    /// into memory in turn just to render a dropdown. Every embedding model in Ollama's
    /// library carries "embed" or "embedding" in its name; the cost of being wrong is a
    /// model missing from a list, not a broken index, because the real check still runs
    /// when one is chosen.
    /// </summary>
    internal static bool LooksLikeAnEmbeddingModel(string name, string? family) =>
        name.Contains("embed", StringComparison.OrdinalIgnoreCase)
        || (family?.Contains("bert", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Ollama reports a tagged name — "nomic-embed-text:latest" — while a chunk set
    /// stores whatever was typed, usually "nomic-embed-text". They refer to the same
    /// model, and comparing them raw made a model in active use look unused: the listing
    /// said so, and the delete guard would have let it be removed out from under four
    /// corpora. Only ":latest" is stripped; ":v1.5" is a genuinely different model.
    /// </summary>
    internal static string Normalise(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? model[..^7] : model;

    internal static bool SameModel(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
