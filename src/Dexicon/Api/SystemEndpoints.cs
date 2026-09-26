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
    /// The runs worth listing: anything that did work, failed, or has not finished yet.
    ///
    /// A field rather than a literal inside the query, so the test that pins this is
    /// pinning what the endpoint runs. Written out twice, it is two rules that agree
    /// until one of them is edited, and the one nobody would think to edit is the test.
    ///
    /// Not-succeeded comes first on purpose: a failed job and a degraded one have the
    /// same zero counters as a quiet refresh, and they are the rows a person opens this
    /// screen to find.
    /// </summary>
    internal static readonly System.Linq.Expressions.Expression<Func<IndexJob, bool>> DidSomething =
        j => j.State != JobState.Succeeded || j.FilesDone > 0 || j.ChunksWritten > 0;

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

            var result = await search.SearchAsync(rc.RequirePrincipal(), new SearchRequest
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
        }).Produces<Dexicon.Core.Search.SearchResult>().WithTags("Search")
          .WithGroupName(OpenApiDocuments.Integration);
    }

    /// <summary>
    /// One assembled passage for a query, rather than a page of hits to fetch separately.
    ///
    /// The MCP surface deliberately does not have this: an agent already has search_index
    /// and get_context, and a sixth tool definition is context every agent pays for on
    /// every turn. What has no agent loop is a hook, a CI step or a shell script, and for
    /// those the two-call shape is a round trip per hit. See docs/decisions.md D-29.
    /// </summary>
    public static void MapContextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/context", async (ContextApiRequest body, RequestContext rc, ContextService context,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(body.Query))
                return Results.Problem(title: "Query is required", statusCode: 400);

            var result = await context.BuildAsync(rc.RequirePrincipal(), new ContextRequest
            {
                Query = body.Query,
                Corpus = body.Corpus,
                Mode = Mapping.ParseMode(body.Mode),
                Limit = Math.Clamp(body.Limit ?? 10, 1, 50),
                PathPrefix = body.PathPrefix,
                Source = body.Source,
                Language = body.Language,
                Symbol = body.Symbol,

                // No lower bound worth enforcing: a budget too small for one chunk returns
                // an empty passage and a note saying what the smallest one costs, which is
                // more use than a number the caller did not choose.
                MaxChars = Math.Clamp(body.MaxChars ?? ContextRequest.DefaultMaxChars, 1, 200_000),

                // Each neighbour is a whole chunk, and every one of them competes with a
                // further hit for the same budget. Five either side of ten hits is already
                // a hundred chunks asking to fit.
                Neighbours = Math.Clamp(body.Neighbours ?? 0, 0, 5),
                LineNumbers = body.LineNumbers ?? false,
                DistinctTitles = body.DistinctTitles ?? true,
            }, ct);

            return Results.Ok(result);
        }).Produces<ContextResult>().WithTags("Search")
          .WithGroupName(OpenApiDocuments.Integration);
    }

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/jobs").WithTags("Jobs");

        g.MapGet("/", async (string? corpusId, int? limit, bool? activity, RequestContext rc,
            ScopeResolver scopes, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var visible = await scopes.VisibleAsync(rc.RequirePrincipal(), ct);
            var ids = visible.Select(c => c.Id).ToList();

            var q = db.Jobs.Where(j => ids.Contains(j.CorpusId));
            if (!string.IsNullOrWhiteSpace(corpusId)) q = q.Where(j => j.CorpusId == corpusId);

            // `activity=true` drops the runs that found nothing to do, IN THE QUERY.
            //
            // A scheduled refresh enqueues one job per corpus per interval, and on an
            // unchanged tree every one of them indexes nothing. The list used to collapse
            // them in the client, which can only collapse what it fetched: at five
            // corpora every ten minutes, a page of thirty jobs is an hour, and the
            // reindex somebody actually ran scrolls off the end of the window before it
            // scrolls off the screen. Measured on this instance: 200 jobs spanned 449
            // minutes, 48 of them did real work, and none of those 48 were in the most
            // recent thirty.
            //
            // The same rule as the client's, so the two cannot disagree about what counts
            // as quiet: succeeded, nothing indexed, nothing written.
            if (activity == true) q = q.Where(DidSomething);

            // Newest first, by when it was QUEUED. Ordering on StartedUtc floated every
            // never-started job to the top, so two long-dead failures sat above the job
            // that was running -- and anything reading jobs[0] to find "the current job"
            // got a stale answer.
            var jobs = await q.OrderByDescending(j => j.QueuedUtc).ThenByDescending(j => j.Id)
                .Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);

            return Results.Ok(jobs.Select(j => j.ToSummary()));
        }).Produces<IReadOnlyList<JobSummary>>().WithGroupName(OpenApiDocuments.Integration);

        g.MapGet("/{id}", async (string id, RequestContext rc, ScopeResolver scopes, CatalogDbContext db,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return Results.NotFound();

            var visible = await scopes.VisibleAsync(rc.RequirePrincipal(), ct);
            if (!visible.Exists(c => c.Id == job.CorpusId)) return Results.NotFound();

            return Results.Ok(job.ToSummary());
        }).Produces<JobSummary>().WithGroupName(OpenApiDocuments.Integration);
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

            var visible = await scopes.VisibleAsync(rc.RequirePrincipal(), ct);
            var visibleIds = visible.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            using var _ = broadcaster.Subscribe(out var reader);
            await ctx.Response.WriteAsync(": connected\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);

            try
            {
                await StreamProgressAsync(ctx.Response, reader, visibleIds, TimeSpan.FromSeconds(20), ct);
            }
            catch (OperationCanceledException) { /* client went away — normal */ }
        }).WithTags("Events");
    }

    /// <summary>
    /// Writes every report the caller can see, and a ping whenever <paramref name="interval"/>
    /// passes without one.
    ///
    /// One read stays pending across pings. Each ping used to start a fresh read while the
    /// previous one was still waiting, and a channel gives an item to the oldest waiting
    /// read, so every ping before a report cost one report: a page left open for twenty
    /// minutes had about sixty abandoned reads ahead of the live one, and the next sixty
    /// reports went to reads nothing awaited. The connection stayed open and kept pinging,
    /// so the client never had a reason to reconnect, and live progress on that page stopped.
    /// </summary>
    internal static async Task StreamProgressAsync(
        HttpResponse response, System.Threading.Channels.ChannelReader<IndexProgress> reader,
        IReadOnlySet<string> visibleIds, TimeSpan interval, CancellationToken ct)
    {
        var heartbeat = Task.Delay(interval, ct);
        Task<IndexProgress>? next = null;

        while (!ct.IsCancellationRequested)
        {
            next ??= reader.ReadAsync(ct).AsTask();
            var winner = await Task.WhenAny(next, heartbeat);

            if (winner == heartbeat)
            {
                await response.WriteAsync(": ping\n\n", ct);
                await response.Body.FlushAsync(ct);
                heartbeat = Task.Delay(interval, ct);
                continue;
            }

            var progress = await next;
            next = null;
            // Scoped to what this caller can reach: a corpus their key is not
            // mapped to is not their business, and its id is not either.
            if (!visibleIds.Contains(progress.CorpusId)) continue;

            var json = JsonSerializer.Serialize(progress, JsonOptions.Web);
            await response.WriteAsync($"event: progress\ndata: {json}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
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

        // What a folder's repository could be followed at, so a history source is pointed
        // at a branch picked from the repository rather than at a name typed from memory.
        app.MapGet("/api/workspaces/git", async (string? path, string? @ref, RequestContext rc,
            IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            return await RepositoryRefsAsync(opts.Value.Indexing.WorkspaceRoot, path, ct, @ref);
        }).Produces<GitRefsResponse>().WithTags("Workspaces");
    }

    /// <summary>
    /// The command the Access dialog hands over with a new key. User scope, because Claude
    /// Code keys its project and local scopes by the literal working-directory string, so a
    /// server added from one shell can be missing from a session started in another.
    /// </summary>
    internal static string McpAddCommand(string key) =>
        "claude mcp add --transport http dexicon http://localhost:8477/mcp \\\n" +
        $"  --header \"Authorization: Bearer {key}\" --scope user";

    /// <summary>
    /// The refs of the repository at <paramref name="path"/>, or that there is none.
    ///
    /// The path is resolved the way a history source's is, so the picker lists exactly
    /// what a source added there would walk: 400 outside the workspace or through a link,
    /// 404 when nothing is mounted there. A folder that is not a repository's root is an
    /// answer, not an error, since the picker offers the folder anyway. git failing to
    /// answer is 503 with its reason, and the picker falls back to a typed ref.
    ///
    /// <paramref name="ref"/>, the ref a source follows, is resolved in the listing. A ref
    /// the ref rule refuses resolves to nothing rather than failing the listing, so a branch
    /// can still be picked in its place.
    /// </summary>
    internal static async Task<IResult> RepositoryRefsAsync(
        string workspaceRoot, string? path, CancellationToken ct, string? @ref = null)
    {
        GitRepository? repo;
        try { repo = GitHistory.RepositoryIn(workspaceRoot, path); }
        catch (UnauthorizedAccessException ex)
        { return Results.Problem(title: "Path refused", detail: ex.Message, statusCode: 400); }

        if (repo is null)
            return Results.Problem(
                title: "Not mounted",
                detail: $"'{path}' does not exist under {workspaceRoot}. " +
                        "Dexicon can only index paths bind-mounted into the container. See WORKSPACE_ROOT.",
                statusCode: 404);

        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), repo.FullPath).Replace('\\', '/');
        if (relative == ".") relative = string.Empty;

        try
        {
            return await GitHistory.IsRepositoryAsync(repo, ct)
                ? Results.Ok(new GitRefsResponse(relative, true, await GitHistory.RefsAsync(repo, ct, @ref)))
                : Results.Ok(new GitRefsResponse(relative, false, null));
        }
        catch (GitHistoryException ex)
        {
            return Results.Problem(title: "git could not be asked", detail: ex.Message, statusCode: 503);
        }
    }

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/session").WithTags("Session");

        // Anonymous by necessity: signing in cannot require being signed in. The throttle
        // below is what stands between this and a guessing loop, together with the 600k
        // PBKDF2 iterations each attempt costs. See docs/decisions.md D-28.
        g.MapPost("/", async (SignInRequest body, TokenService tokens, AdminSessions sessions,
            LoginThrottle throttle, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Dexicon.SignIn");

            // Waited before the attempt is judged, so a correct password presented in the
            // middle of an attack waits too. Charging only failures would let an attacker
            // probe at full speed by never being right.
            await throttle.WaitAsync(ct);

            if (!await tokens.VerifyPasswordAsync(body.Password, ct))
            {
                throttle.RecordFailure();
                log.LogWarning("Failed admin sign-in. {Failures} consecutive, next attempt delayed {Delay}",
                    throttle.Failures, throttle.Delay());
                return Results.Problem(
                    title: "Incorrect password", statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Reset();
            var session = sessions.Issue();
            log.LogInformation("Admin signed in; session expires {ExpiresUtc:O}", session.ExpiresUtc);
            return Results.Ok(new SignInResponse(session.Presented, session.ExpiresUtc));
        }).Produces<SignInResponse>();

        // Revokes only a session whose value the caller already holds, so it needs no
        // scope of its own.
        g.MapDelete("/", (HttpContext ctx, AdminSessions sessions) =>
        {
            var header = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                sessions.Revoke(header["Bearer ".Length..].Trim());
            return Results.NoContent();
        });

        var t = app.MapGroup("/api/tokens").WithTags("Tokens");

        t.MapGet("/", async (RequestContext rc, CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var rows = await db.Tokens.Include(x => x.Corpora)
                .OrderByDescending(x => x.CreatedUtc).ToListAsync(ct);
            return Results.Ok(rows.Select(x => x.ToSummary()));
        }).Produces<IReadOnlyList<TokenSummary>>();

        t.MapPost("/", async (CreateTokenRequest body, RequestContext rc, TokenService tokens,
            CatalogDbContext db, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.Problem(title: "Name is required", statusCode: 400);

            var requested = body.Scopes is { Count: > 0 } ? body.Scopes : [Scopes.Search];
            var unknown = requested
                .Where(s => !Scopes.Issuable.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                return Results.Problem(
                    title: "Unknown scope",
                    detail: $"{string.Join(", ", unknown)}. A key may hold " +
                            $"{string.Join(" or ", Scopes.Issuable)}. Administration is the password's, " +
                            "so that no credential in an agent's configuration can delete a corpus.",
                    statusCode: 400);

            var expires = body.ExpiresInDays is { } d and > 0 ? DateTime.UtcNow.AddDays(d) : (DateTime?)null;
            var (row, issued) = await tokens.CreateAsync(body.Name.Trim(), requested, expires, ct);

            if (body.CorpusIds is { Count: > 0 })
            {
                var denied2 = await MapCorporaAsync(db, row.Id, body.CorpusIds, ct);
                if (denied2 is not null) return denied2;
                await db.Entry(row).Collection(x => x.Corpora).LoadAsync(ct);
            }

            // The highest-value thing on the page: the step between installed and working.
            return Results.Ok(new CreatedTokenResponse(row.ToSummary(), issued.Presented, McpAddCommand(issued.Presented)));
        }).Produces<CreatedTokenResponse>();

        t.MapDelete("/{id}", async (string id, RequestContext rc, TokenService tokens, IMemoryCacheEvictor evictor,
            CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;
            var revoked = await tokens.RevokeAsync(id, ct);
            if (!revoked) return Results.NotFound();

            // Revocation must be effective immediately, not after the principal cache TTL.
            evictor.EvictPrincipals();
            return Results.NoContent();
        });

        // Replaces the mapping outright rather than patching it, because the UI edits the
        // whole set of ticks at once and a partial update would need a way to say "leave
        // that one alone" that is indistinguishable from "untick it".
        //
        // No cache to evict: the mapping is read per request, never cached on the
        // principal, so the agent's next call already sees this.
        t.MapPut("/{id}/corpora", async (string id, UpdateTokenCorporaRequest body, RequestContext rc,
            CatalogDbContext db, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Admin) is { } denied) return denied;

            var token = await db.Tokens.Include(x => x.Corpora).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (token is null) return Results.NotFound();

            var refused = await MapCorporaAsync(db, id, body.CorpusIds, ct);
            if (refused is not null) return refused;

            await db.Entry(token).Collection(x => x.Corpora).LoadAsync(ct);
            return Results.Ok(token.ToSummary());
        }).Produces<TokenSummary>();
    }

    /// <summary>
    /// Replace a key's corpus mapping. Returns a problem result when an id does not exist,
    /// and null on success.
    ///
    /// An unknown id is refused rather than dropped. Silently ignoring one would leave the
    /// key mapped to fewer corpora than the operator ticked, and an empty mapping means
    /// every corpus, so the failure mode of dropping the last one is the opposite of what
    /// was asked for.
    /// </summary>
    private static async Task<IResult?> MapCorporaAsync(
        CatalogDbContext db, string tokenId, IReadOnlyList<string> corpusIds, CancellationToken ct)
    {
        var wanted = corpusIds.Distinct(StringComparer.Ordinal).ToList();

        var known = await db.Corpora.Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        var unknown = wanted.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            return Results.Problem(
                title: "Unknown corpus",
                detail: $"No corpus with id {string.Join(", ", unknown)}. The mapping was not changed.",
                statusCode: 400);

        var existing = await db.TokenCorpora.Where(tc => tc.TokenId == tokenId).ToListAsync(ct);
        db.TokenCorpora.RemoveRange(existing);
        foreach (var corpusId in wanted)
            db.TokenCorpora.Add(new TokenCorpus { TokenId = tokenId, CorpusId = corpusId });

        await db.SaveChangesAsync(ct);
        return null;
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
            // Grouped, not keyed directly. A measurement is stored under the model name it
            // was requested with, and `embeddinggemma` and `embeddinggemma:latest` are the
            // same model under two names: both are legal rows, since the table's key is
            // (provider, model), and both normalise to one key here. Keying threw
            // ArgumentException and took the whole models list to a 500 — a listing that
            // cannot survive its own stored data.
            //
            // The most recent wins, because a re-probe is a correction.
            var measured = (await db.ModelMeasurements
                    .Where(x => x.Provider == name)
                    .ToListAsync(ct))
                .GroupBy(x => ModelNames.Normalise(x.Model), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.MeasuredUtc).First(),
                    StringComparer.OrdinalIgnoreCase);

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
                row.ContextTokens = caps.ContextTokens;
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
            ScopeResolver scopes, IMemoryCache cache, IOptions<DexiconOptions> opts, CancellationToken ct) =>
        {
            if (rc.RequireScope(Scopes.Search) is { } denied) return denied;
            var visible = await scopes.VisibleAsync(rc.RequirePrincipal(), ct);

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
                await MissingModelsAsync(db, catalog, cache, visible.Select(c => c.Id).ToList(),
                    rc.RequirePrincipal().TokenId, ct)));
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
    /// Scoped to what the caller can reach: the corpora count above is a number and gives
    /// away nothing, but a set names its corpus, and a corpus a key is not mapped to is not
    /// that key's to see.
    ///
    /// Cached, because <c>/healthz</c> is polled every fifteen seconds per open tab and
    /// this costs a listing per provider. A minute is short enough that pulling a model
    /// back clears the warning while you are still looking at the screen.
    /// </summary>
    internal static async Task<IReadOnlyList<MissingModel>> MissingModelsAsync(
        CatalogDbContext db, IModelCatalog catalog, IMemoryCache cache,
        IReadOnlyList<string> corpusIds, string cacheScope, CancellationToken ct)
    {
        var key = $"health-missing-models::{cacheScope}";
        if (cache.TryGetValue(key, out IReadOnlyList<MissingModel>? cached) && cached is not null) return cached;

        var sets = await db.ChunkSets
            .Where(s => corpusIds.Contains(s.CorpusId))
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
