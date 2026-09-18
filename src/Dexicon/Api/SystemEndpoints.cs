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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Dexicon.Api;

public static class SystemEndpoints
{
    /// <summary>
    /// How long a model probe may run before it is given up on.
    ///
    /// Long enough for two dozen embeds against an idle CPU backend, short enough that a
    /// busy one is reported rather than waited out. The probe has no partial answer, so a
    /// longer deadline buys nothing but a later failure.
    /// </summary>
    internal static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(90);

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
                Source = body.Source,
                Language = body.Language,
                Symbol = body.Symbol,
                MaxCharsPerHit = body.MaxCharsPerHit is { } n
                    ? Math.Clamp(n, 0, 100_000)
                    : SearchRequest.DefaultMaxCharsPerHit,
                DistinctTitles = body.DistinctTitles ?? true,
            }, ct);

            return Results.Ok(result);
        }).Produces<Dexicon.Core.Search.SearchResult>().WithTags("Search");
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
        }).Produces<IReadOnlyList<JobSummary>>();

        g.MapGet("/{id}", async (string id, RequestContext rc, ScopeResolver scopes, CatalogDbContext db,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return Results.NotFound();

            var visible = await scopes.VisibleAsync(rc.RequireTenant(), ct);
            if (!visible.Exists(c => c.Id == job.CorpusId)) return Results.NotFound();

            return Results.Ok(job.ToSummary());
        }).Produces<JobSummary>();
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
                            "Dexicon can only index paths bind-mounted into the container. See WORKSPACE_ROOT.",
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

            return Results.Ok(new WorkspaceListing(
                opts.Value.Indexing.WorkspaceRoot,
                Path.GetRelativePath(root, full).Replace('\\', '/'),
                entries));
        }).Produces<WorkspaceListing>().WithTags("Workspaces");
    }

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants").WithTags("Tenants");

        g.MapGet("/", async (RequestContext rc, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var tenants = await db.Tenants.OrderBy(t => t.Id).ToListAsync(ct);
            return Results.Ok(tenants.Select(t =>
                new TenantSummary(t.Id, t.DisplayName, t.CreatedUtc, t.Disabled)));
        }).Produces<IReadOnlyList<TenantSummary>>();

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
                    detail: $"Tenant '{id}' owns {owned} {(owned == 1 ? "corpus" : "corpora")}. Delete or move them first.",
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
        }).Produces<IReadOnlyList<TokenSummary>>();

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
        }).Produces<CreatedTokenResponse>();

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

        app.MapGet("/api/embedding-providers", (RequestContext rc, IEmbeddingGeneratorFactory factory,
            IModelCatalog catalog, IOptions<DexiconOptions> opts) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            var providers = factory.ProviderNames.Select(name =>
            {
                var options = factory.Options(name);
                var managed = catalog.IsManaged(name);

                // "Configured" means usable, not merely present. A provider whose API key
                // is missing looks identical in a list until someone picks it and the
                // failure surfaces at index time, an hour later and three steps away.
                var needsKey = options.Kind is EmbeddingProviderKind.OpenAI or EmbeddingProviderKind.AzureOpenAI;
                var hasKey = !needsKey
                             || !string.IsNullOrWhiteSpace(options.ApiKey)
                             || (options.ApiKeyEnvVar is { Length: > 0 } v
                                 && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)));

                var detail = hasKey
                    ? null
                    : $"No API key. Set {options.ApiKeyEnvVar ?? "an API key"} in the environment.";

                return new EmbeddingProviderInfo(
                    name, options.Kind.ToString().ToLowerInvariant(), managed, hasKey, detail);
            }).ToList();

            return Results.Ok(new EmbeddingProviderList(opts.Value.Embedding.Provider, providers));
        }).Produces<EmbeddingProviderList>().WithTags("System");

        app.MapGet("/api/embedding-models", async (string? provider, RequestContext rc,
            IModelCatalog catalog, IEmbeddingGeneratorFactory factory, CatalogDbContext db,
            IModelProfiles profiles, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            var name = string.IsNullOrWhiteSpace(provider) ? opts.Value.Embedding.Provider : provider;

            IReadOnlyList<AvailableModel> models;
            try
            {
                models = await catalog.ListAsync(name, ct);
            }
            catch (UnknownEmbeddingProviderException ex)
            {
                return Results.Problem(title: "Unknown embedding provider", detail: ex.Message, statusCode: 400);
            }
            catch (EmbeddingUnavailableException ex)
            {
                return Results.Problem(title: "Provider unavailable", detail: ex.Message, statusCode: 503);
            }

            // Which are in use, so the UI can warn before someone removes one that four
            // corpora depend on. Matched on provider AND model: the same model name under
            // two providers is two different vector spaces.
            var inUse = (await db.ChunkSets
                    .Select(s => new { s.EmbeddingProvider, s.EmbeddingModel })
                    .Distinct().ToListAsync(ct))
                .Where(s => string.Equals(s.EmbeddingProvider, name, StringComparison.OrdinalIgnoreCase))
                .Select(s => ModelNames.Normalise(s.EmbeddingModel))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var managed = catalog.IsManaged(name);

            // Ollama serves chat models from the same endpoint and they cannot embed, so
            // offering them would turn a bad pick into a 503 at index time. A hosted
            // provider's list is curated already, so nothing to filter.
            var candidates = models
                .Where(m => !managed || ModelNames.LooksLikeAnEmbeddingModel(m.Name, m.Family))
                .ToList();

            // The framing travels with the model, because "embedded raw" is the one state
            // nobody would think to ask about and the one that silently costs recall.
            var measured = (await db.ModelMeasurements
                    .Where(x => x.Provider == name)
                    .ToListAsync(ct))
                .ToDictionary(x => ModelNames.Normalise(x.Model), StringComparer.OrdinalIgnoreCase);

            var listed = new List<EmbeddingModelInfo>(candidates.Count);
            foreach (var m in candidates)
            {
                var templates = await profiles.ForAsync(new EmbeddingTarget(name, m.Name), ct);
                measured.TryGetValue(ModelNames.Normalise(m.Name), out var facts);

                listed.Add(new EmbeddingModelInfo(
                    m.Name, m.SizeBytes,
                    // A measured dimensionality beats "unknown until first use".
                    m.Dimensions ?? facts?.Dimensions,
                    inUse.Contains(ModelNames.Normalise(m.Name)),
                    templates.Document, templates.Query, templates.Origin.ToString().ToLowerInvariant(),
                    facts is null ? null : new ModelMeasurement(
                        facts.MaxInputChars, facts.TruncatesSilently,
                        facts.RecommendedChunkTokens, facts.CharsPerToken, facts.MeasuredUtc)));
            }

            return Results.Ok(new EmbeddingModelList(
                name, managed, opts.Value.Embedding.Model, listed,
                // Said plainly: an empty list otherwise reads as "the provider is broken".
                listed.Count == 0
                    ? managed
                        ? $"No embedding models are pulled. Pull one, or run: docker compose exec dexicon-ollama ollama pull {opts.Value.Embedding.Model}"
                        : $"Provider '{name}' has no models configured. Add them under Dexicon:Embedding:Providers:{name}:Models."
                    : null));
        }).Produces<EmbeddingModelList>().WithTags("System");

        app.MapPut("/api/embedding-models/profile", async (SaveModelProfileRequest body, RequestContext rc,
            CatalogDbContext db, IMemoryCache cache, IndexJobQueue queue,
            IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(body.Model))
                return Results.Problem(title: "A model name is required", statusCode: 400);

            // A template without the placeholder would silently drop every input and embed
            // a constant string, which returns the same vector for everything.
            foreach (var (label, template) in new[]
                     { ("documentTemplate", body.DocumentTemplate), ("queryTemplate", body.QueryTemplate) })
            {
                if (string.IsNullOrEmpty(template) || !template.Contains("{text}", StringComparison.Ordinal))
                    return Results.Problem(
                        title: $"{label} must contain {{text}}",
                        detail: "That is where the text being embedded goes. Use exactly \"{text}\" to embed it unchanged.",
                        statusCode: 400);
            }

            var provider = string.IsNullOrWhiteSpace(body.Provider) ? opts.Value.Embedding.Provider : body.Provider;
            var model = body.Model.Trim();

            var existing = await db.ModelProfiles.FirstOrDefaultAsync(
                p => p.Provider == provider && p.Model == model, ct);

            if (existing is null)
            {
                db.ModelProfiles.Add(new EmbeddingModelProfile
                {
                    Provider = provider,
                    Model = model,
                    DocumentTemplate = body.DocumentTemplate,
                    QueryTemplate = body.QueryTemplate,
                    Notes = body.Notes,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.DocumentTemplate = body.DocumentTemplate;
                existing.QueryTemplate = body.QueryTemplate;
                existing.Notes = body.Notes;
                existing.UpdatedUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            cache.Remove($"model-templates::{provider}::{model}");

            // Framing is part of the chunking fingerprint, so every set on this model is
            // now stale: its documents were embedded one way and its queries would arrive
            // framed another. Re-indexed rather than left to disagree quietly.
            var affected = await db.ChunkSets
                .Where(s => s.EmbeddingProvider == provider && s.EmbeddingModel == model)
                .Select(s => new { s.Id, s.CorpusId, s.Name, Corpus = s.Corpus!.Name })
                .ToListAsync(ct);

            var queued = new List<string>();
            foreach (var set in affected)
            {
                await queue.EnqueueAsync(set.CorpusId, JobKind.Rebuild, set.Id, ct);
                queued.Add($"{set.Corpus}:{set.Name}");
            }

            return Results.Ok(new ModelProfileSaved(
                provider, model, queued,
                queued.Count == 0
                    ? null
                    : $"{queued.Count} chunk set(s) are re-indexing: framing changes the vectors."));
        }).Produces<ModelProfileSaved>().WithTags("System");

        app.MapPost("/api/embedding-models/probe", async (ProbeModelRequest body, RequestContext rc,
            ModelProbe probe, CatalogDbContext db, IOptions<DexiconOptions> opts,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(body.Model))
                return Results.Problem(title: "A model name is required", statusCode: 400);

            var target = new EmbeddingTarget(
                string.IsNullOrWhiteSpace(body.Provider) ? opts.Value.Embedding.Provider : body.Provider.Trim(),
                body.Model.Trim());

            // The probe is two dozen sequential embed calls, each with the embedding
            // client's own 120 s timeout, so on a backend that is busy indexing it can run
            // for the better part of an hour. It has no partial answer to give, so grinding
            // is only a slower way to fail: bounded here, where the reason is known, rather
            // than left to whatever the caller does about a request that never returns.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ProbeDeadline);

            try
            {
                var caps = await probe.RunAsync(target, deadline.Token);

                // Remembered, because a measurement that has to be taken again is a
                // measurement nobody takes. Two dozen embed calls to learn a number that
                // then vanished on reload is why the chunk size field could never say what
                // the chosen model accepts.
                var row = await db.ModelMeasurements.FindAsync([target.Provider, target.Model], ct);
                if (row is null)
                {
                    row = new EmbeddingModelMeasurement { Provider = target.Provider, Model = target.Model };
                    db.ModelMeasurements.Add(row);
                }

                row.Dimensions = caps.Dimensions;
                row.MaxInputChars = caps.MaxInputChars;
                row.TruncatesSilently = caps.TruncatesSilently;
                row.RecommendedChunkChars = caps.RecommendedChunkChars;
                row.RecommendedChunkTokens = caps.RecommendedChunkTokens;
                row.CharsPerToken = caps.CharsPerToken;
                row.MeasuredUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                return Results.Ok(caps);
            }
            catch (UnknownEmbeddingProviderException ex)
            {
                return Results.Problem(title: "Unknown embedding provider", detail: ex.Message, statusCode: 400);
            }
            catch (EmbeddingUnavailableException ex)
            {
                return Results.Problem(title: "Provider unavailable", detail: ex.Message, statusCode: 503);
            }
            // Ours, not the caller's: a client that went away is not a timeout, and
            // reporting it as one would put an error on a screen nobody is looking at.
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return Results.Problem(
                    title: "The model probe timed out",
                    detail: $"No answer within {ProbeDeadline.TotalSeconds:F0}s. The probe embeds two dozen inputs, "
                          + "and the embedding service answers indexing first, so this usually means an index job is "
                          + "running. Check index_status or the Jobs view, and probe again when it has finished.",
                    statusCode: 504);
            }
        }).Produces<Dexicon.Core.Embedding.ModelCapabilities>().WithTags("System");

        app.MapPost("/api/embedding-models/pull", async (PullModelRequest body, HttpContext http,
            RequestContext rc, IModelCatalog catalog, IOptions<DexiconOptions> opts,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            // Admin, not ingest: this downloads gigabytes onto a shared volume.
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(body.Model))
                return Results.Problem(title: "A model name is required", statusCode: 400);

            var model = body.Model.Trim();
            var provider = string.IsNullOrWhiteSpace(body.Provider) ? opts.Value.Embedding.Provider : body.Provider.Trim();
            var log = logs.CreateLogger("Dexicon.ModelPull");

            // Server-sent events, because a pull takes minutes and a progress bar that
            // only moves when it finishes is not a progress bar. Same transport the
            // indexing UI already uses, so the client needs nothing new.
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            try
            {
                await foreach (var progress in catalog.PullAsync(provider, model, ct))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        model,
                        provider,
                        progress.Status,
                        progress.Completed,
                        progress.Total,
                        progress.Percent,
                        progress.Done,
                    }, JsonOptions.Web);

                    await http.Response.WriteAsync($"data: {json}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }

                log.LogInformation("Pulled {Model} into provider {Provider}", model, provider);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The client navigated away. Ollama keeps the partial download and
                // resumes next time, so there is nothing to clean up.
                log.LogInformation("Pull of {Model} was cancelled by the client", model);
            }
            catch (Exception ex) when (ex is EmbeddingUnavailableException
                                          or UnknownEmbeddingProviderException
                                          or InvalidOperationException)
            {
                log.LogWarning(ex, "Pull of {Model} failed", model);
                var json = JsonSerializer.Serialize(new { model, provider, error = ex.Message }, JsonOptions.Web);
                await http.Response.WriteAsync($"data: {json}\n\n", CancellationToken.None);
            }

            return Results.Empty;
        }).WithTags("System");

        app.MapDelete("/api/embedding-models/{model}", async (string model, string? provider, RequestContext rc,
            IModelCatalog catalog, CatalogDbContext db, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            var name = string.IsNullOrWhiteSpace(provider) ? opts.Value.Embedding.Provider : provider;

            // Refuse while anything depends on it. Deleting a model out from under a chunk
            // set does not fail immediately: the set keeps its vectors and its collection, and
            // breaks only at the next index or the next semantic query, by which point the
            // cause is several steps away.
            var usedBy = (await db.ChunkSets
                    .Select(s => new { s.Name, Corpus = s.Corpus!.Name, s.EmbeddingProvider, s.EmbeddingModel })
                    .ToListAsync(ct))
                .Where(s => string.Equals(s.EmbeddingProvider, name, StringComparison.OrdinalIgnoreCase)
                            && ModelNames.SameModel(s.EmbeddingModel, model))
                .ToList();

            if (usedBy.Count > 0)
                return Results.Problem(
                    title: "Model is in use",
                    detail: $"'{model}' is the embedding model for " +
                            string.Join(", ", usedBy.Select(u => $"{u.Corpus}:{u.Name}")) +
                            ". Migrate those chunk sets to another model first.",
                    statusCode: 409);

            if (ModelNames.SameModel(model, opts.Value.Embedding.Model)
                && string.Equals(name, opts.Value.Embedding.Provider, StringComparison.OrdinalIgnoreCase))
                return Results.Problem(
                    title: "Model is the configured default",
                    detail: $"'{model}' is DEXICON__EMBEDDING__MODEL, so new corpora would be created " +
                            "against a model that is no longer available. Change the configuration first.",
                    statusCode: 409);

            try
            {
                await catalog.DeleteAsync(name, model, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: "Provider does not manage models", detail: ex.Message, statusCode: 400);
            }
            catch (EmbeddingUnavailableException ex)
            {
                return Results.Problem(title: "Could not delete model", detail: ex.Message, statusCode: 503);
            }
        }).WithTags("System");

        app.MapGet("/healthz", async (RequestContext rc, IVectorStore vectors, IEmbeddingService embedder,
            IModelCatalog catalog, IEmbeddingGeneratorFactory factory, CatalogDbContext db,
            IMemoryCache cache, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;

            var qdrant = await vectors.PingAsync(ct);
            var target = new EmbeddingTarget(opts.Value.Embedding.Provider, opts.Value.Embedding.Model);

            // KnownDimensions never calls out. Only a cold cache pays for a round-trip,
            // which matters because this is polled every fifteen seconds per open tab.
            string? embeddingError = null;
            var dims = embedder.KnownDimensions(target);
            try { if (dims == 0) dims = await embedder.ProbeDimensionsAsync(target, ct); }
            catch (Exception ex) when (ex is EmbeddingUnavailableException or UnknownEmbeddingProviderException)
            { embeddingError = ex.Message; }

            var activeJob = await db.Jobs.Where(j => j.State == JobState.Running)
                .OrderByDescending(j => j.StartedUtc).FirstOrDefaultAsync(ct);

            return Results.Ok(new HealthResponse(
                qdrant ? "ok" : "degraded",
                new HealthDependency(qdrant, opts.Value.Qdrant.Endpoint),
                new EmbeddingHealth(
                    embeddingError is null, EndpointOf(factory, opts.Value, target.Provider),
                    target.Provider, target.Model, dims, embeddingError),
                await db.Corpora.CountAsync(ct),
                activeJob?.ToSummary(),
                await MissingModelsAsync(db, catalog, cache, rc.RequireTenant(), ct)));
        }).Produces<HealthResponse>().WithTags("Health");
    }

    /// <summary>
    /// Where the provider actually in use answers, not where Ollama does.
    ///
    /// This reported <c>Ollama.Endpoint</c> unconditionally, so a deployment whose default
    /// is OpenAI showed the address of a container it never talks to, beside a reachability
    /// badge for a backend on the other side of the internet. Azure has its own endpoint
    /// and OpenAI has exactly one, which is why the last arm is a constant rather than a
    /// setting nobody can change.
    /// </summary>
    internal static string EndpointOf(IEmbeddingGeneratorFactory factory, DexiconOptions opts, string provider)
    {
        EmbeddingProviderOptions configured;
        try { configured = factory.Options(provider); }
        // A default naming a provider that is not configured is a misconfiguration the
        // reachability probe already reports. Do not also invent an address for it.
        catch (UnknownEmbeddingProviderException) { return "(no such provider)"; }

        return configured.Kind switch
        {
            EmbeddingProviderKind.Ollama => configured.Endpoint ?? opts.Ollama.Endpoint,
            EmbeddingProviderKind.AzureOpenAI => configured.Endpoint ?? "(no endpoint configured)",
            _ => configured.Endpoint ?? "https://api.openai.com/v1",
        };
    }

    /// <summary>
    /// Chunk sets whose model the provider no longer has.
    ///
    /// Scoped to the caller's tenant: the corpora count above is a number and gives away
    /// nothing, but a set names its corpus, and another tenant's corpus names are not this
    /// caller's to see.
    ///
    /// Cached, because <c>/healthz</c> is polled every fifteen seconds per open tab and
    /// this costs a listing per provider. A minute is short enough that pulling a model
    /// back clears the warning while you are still looking at the screen.
    /// </summary>
    internal static async Task<IReadOnlyList<MissingModel>> MissingModelsAsync(
        CatalogDbContext db, IModelCatalog catalog, IMemoryCache cache, string tenantId, CancellationToken ct)
    {
        var key = $"health-missing-models::{tenantId}";
        if (cache.TryGetValue(key, out IReadOnlyList<MissingModel>? cached) && cached is not null) return cached;

        var sets = await db.ChunkSets
            .Where(s => s.Corpus!.TenantId == tenantId)
            .Select(s => new { s.EmbeddingProvider, s.EmbeddingModel, Corpus = s.Corpus!.Name, s.Name })
            .ToListAsync(ct);

        var missing = new List<MissingModel>();

        foreach (var group in sets.GroupBy(s => s.EmbeddingProvider, StringComparer.OrdinalIgnoreCase))
        {
            // Only a provider that can be listed can be checked. A hosted catalogue is the
            // configured list rather than what the backend holds, so a model absent from it
            // may be perfectly valid — and a warning that fires on a working deployment
            // costs more than the one it catches.
            if (!catalog.IsManaged(group.Key)) continue;

            IReadOnlyList<AvailableModel> available;
            try { available = await catalog.ListAsync(group.Key, ct); }
            // Unreachable is not missing. The embedding health above already says the
            // backend is down; claiming its models are gone as well is a second alarm for
            // one fault, and a wrong one.
            catch (Exception ex) when (ex is EmbeddingUnavailableException or UnknownEmbeddingProviderException)
            { continue; }

            var have = available.Select(m => ModelNames.Normalise(m.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            missing.AddRange(group
                .GroupBy(s => ModelNames.Normalise(s.EmbeddingModel), StringComparer.OrdinalIgnoreCase)
                .Where(m => !have.Contains(m.Key))
                .Select(m => new MissingModel(
                    group.Key, m.First().EmbeddingModel,
                    [.. m.Select(s => $"{s.Corpus}:{s.Name}").Order(StringComparer.Ordinal)])));
        }

        IReadOnlyList<MissingModel> result = missing;
        cache.Set(key, result, TimeSpan.FromMinutes(1));
        return result;
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
    /// into memory in turn just to render a dropdown.
    ///
    /// The name check alone is NOT enough, and it is worth being exact about why. Of the
    /// twelve models in Ollama's embedding category, four carry no "embed" in their name:
    /// bge-m3, bge-large, all-minilm and paraphrase-multilingual. All four are BERT
    /// derivatives, so the family check is what admits them and is load-bearing rather
    /// than a redundant extra check. (`all-minilm` reports family `bert`;
    /// `nomic-embed-text` reports `nomic-bert`.)
    ///
    /// The cost of being wrong is a model missing from a list, not a broken index,
    /// because the real check still runs when one is chosen.
    /// </summary>
    internal static bool LooksLikeAnEmbeddingModel(string name, string? family) =>
        name.Contains("embed", StringComparison.OrdinalIgnoreCase)
        || (family?.Contains("bert", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Ollama reports a tagged name such as "nomic-embed-text:latest", while a chunk set
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
