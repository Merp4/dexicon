using System.Reflection;
using System.Text.Json.Serialization;
using Dexicon.Api;
using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
// DEXICON__SECTION__KEY from the environment, plus /run/secrets/* for orchestrated
// deployments. A secret never comes from appsettings.json; see docs/10.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true, reloadOnChange: false);
builder.Services.Configure<DexiconOptions>(builder.Configuration.GetSection(DexiconOptions.SectionName));

var options = builder.Configuration.GetSection(DexiconOptions.SectionName).Get<DexiconOptions>() ?? new DexiconOptions();

// ── Logging ──────────────────────────────────────────────────────────────────
var logLevel = options.Log.Level switch
{
    "Trace" or "Verbose" => LogEventLevel.Verbose,
    "Debug" => LogEventLevel.Debug,
    "Warning" => LogEventLevel.Warning,
    "Error" => LogEventLevel.Error,
    _ => LogEventLevel.Information,
};

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.With<UtcTimestampEnricher>()
    .WriteTo.Console(outputTemplate: LogOutput.ConsoleTemplate)
    .CreateLogger();

builder.Host.UseSerilog();

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddProblemDetails();

// Describes the REST surface so the web client's types can be generated from it rather
// than hand-maintained. api.ts had already drifted from the C# contracts more than once:
// chunk sets landed and the Corpus interface still carried fields the server had dropped.
//
// Two documents: the full surface, and the subset other software is invited to depend on.
// See Infrastructure/OpenApiDocuments.
builder.Services.AddDexiconOpenApi(ThisAssembly.ApiVersion);
builder.Services.AddExceptionHandler<ScopeExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // A number is a number. The web defaults also accept one written as a string, which
    // is leniency nobody asked for and which the OpenAPI document has to describe
    // truthfully, so every int32 came out as `["integer", "string"]` and the generated
    // TypeScript typed every count and every chunk size as `number | string`. Arithmetic
    // on that is a cast at every call site, to support input no client sends.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});

// No Cache=Shared. It arrived with the walking skeleton and nothing depended on it, and
// it changes what contention looks like: measured, a second writer blocked out by a lock
// held longer than the command timeout fails with SQLITE_LOCKED under shared cache and
// SQLITE_BUSY without it. Only the second is a wait that `busy_timeout` can serve; LOCKED
// is refused outright however long the caller was willing to wait.
//
// The journal mode and busy timeout are applied per connection by SqlitePragmas, because
// neither can be set in a connection string.
builder.Services.AddDbContext<CatalogDbContext>((sp, o) =>
    o.UseSqlite($"Data Source={options.Storage.CatalogPath}")
     .AddInterceptors(new SqlitePragmas(
         TimeSpan.FromSeconds(options.Storage.BusyTimeoutSeconds),
         sp.GetRequiredService<ILogger<SqlitePragmas>>())));

// Singleton: a generator holds a connection and a credential, and the model is a
// per-call argument, so there is nothing per-request about it.
builder.Services.AddSingleton<IEmbeddingGeneratorFactory, EmbeddingGeneratorFactory>();
builder.Services.AddScoped<IModelProfiles, ModelProfiles>();
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<IModelCatalog, ModelCatalog>();
builder.Services.AddScoped<ModelProbe>();
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();
builder.Services.AddSingleton<IndexProgressBroadcaster>();
builder.Services.AddSingleton<IMemoryCacheEvictor, MemoryCacheEvictor>();

builder.Services.AddSingleton<AdminSessions>();
builder.Services.AddSingleton<LoginThrottle>();

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ScopeResolver>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<ContextService>();
builder.Services.AddScoped<CorpusIndexer>();
// A singleton, because what it counts belongs to the machine and the endpoints rather
// than to a caller. Scoped, it would be one limit per request and describe nothing.
builder.Services.AddSingleton<IndexingLimits>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<DocumentReader>();
builder.Services.AddScoped<IVectorStoreCleanup, VectorStoreCleanup>();
builder.Services.AddScoped<IndexJobQueue>();
builder.Services.AddScoped<RequestContext>();

builder.Services.AddHostedService<IndexingBackgroundService>();
builder.Services.AddHostedService<ScheduledRefreshService>();

// The discovery lane, deliberately its own worker: a sweep that queued behind indexing
// would wait for exactly the work it exists to get in front of. See D-32.
builder.Services.AddScoped<CorpusSweeper>();
builder.Services.AddSingleton<SweepQueue>();
builder.Services.AddHostedService<SweepWorker>();

// Singleton: the lease is a row, and the service only takes scopes to reach it.
builder.Services.AddSingleton<CorpusLeases>();

// ── MCP ──────────────────────────────────────────────────────────────────────
// Stateless: the 2026-07-28 core removed the handshake and the session id, and
// Dexicon needs no server-to-client calls. Verified in the M0 spike.
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "dexicon", Version = ThisAssembly.Version })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<DexiconTools>()
    .WithResources<DexiconResources>();

// A key without `ingest` is not shown `index_refresh` at all, rather than being refused
// when it calls it: an agent that can see a tool will call it, spend a turn on the error,
// and sometimes retry. `tools/list` carries the bearer like every other request, so the
// principal is available here. See docs/decisions.md D-28.
//
// The list is not live. The transport is stateless, so there is no
// notifications/tools/list_changed to send, and a client lists on connect and caches:
// granting `ingest` reaches an agent when it reconnects, whereas mapping a corpus reaches
// it on the next call.
builder.Services.Configure<McpServerOptions>(o =>
    o.Filters.Request.ListToolsFilters.Add(next => async (ctx, ct) =>
    {
        var result = await next(ctx, ct);

        // Fully qualified: ModelContextProtocol.Server.RequestContext<T> is the filter's own
        // context type and shadows ours at this call site.
        var principal = ctx.Services?.GetService<Dexicon.Infrastructure.RequestContext>()?.Principal;
        if (principal is null || principal.Has(Scopes.Ingest)) return result;

        result.Tools = [.. result.Tools.Where(t => t.Name != "index_refresh")];
        return result;
    }));

builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

// Kestrel's stock 30 MB request cap stays on for every endpoint EXCEPT the document
// upload, which lifts it per request (DocumentEndpoints). That cap is the right guard for
// a JSON body and the wrong one for a book: DEXICON__UPLOAD__MAXFILEBYTES advertises
// 200 MB per file and nothing could ever reach it: a 40 MB PDF died on a bare 413 with
// no message, which reads as the upload being broken rather than as a limit.
//
// The multipart limit has to move with it. It governs the whole form, and the UI posts
// every dropped file in ONE request, so a fixed number here would cap a batch rather than
// a file. The real per-file limit is enforced while streaming, in DocumentService.
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = long.MaxValue;
});

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────────
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseDexiconAuth();

app.MapHealthEndpoints();
app.MapSearchEndpoints();
app.MapContextEndpoints();
app.MapCorpusEndpoints();
app.MapChunkSetEndpoints();
app.MapDocumentEndpoints();
app.MapJobEndpoints();
app.MapEventEndpoints();
app.MapWorkspaceEndpoints();
app.MapAdminEndpoints();
app.MapMcp("/mcp");

// The integration document, served by the instance that implements it.
//
// Only that one. The framework's own template maps this in Development alone, to avoid
// exposing the surface in production, and that reasoning holds for the full document:
// it describes the workspace browser, the model endpoints and sign-in, and its consumer
// is a build-time code generator that reads the committed file. The integration document
// has a consumer here that the file cannot serve — an integrator generating a client
// against the instance they are actually talking to, at whatever version it is running,
// rather than against a checkout that may be ahead of it.
//
// Authenticated like everything else, which is the remedy the OpenAPI documentation
// gives for the exposure the dev-only default is avoiding: DexiconAuthMiddleware denies
// by default, and this path is not on its anonymous list. Authentication only, with no
// scope of its own: a contract is not data, and a key holding any scope at all can
// already see the endpoints it describes.
//
// The document is regenerated per request. At seven paths, for a caller that fetches it
// once per code generation, that is cheaper than a cache to invalidate.
app.MapOpenApi($"/openapi/{{documentName:regex(^{OpenApiDocuments.Integration}$)}}.json");

// Anything else under /openapi/ is a name that is not published. Without this the SPA
// fallback answers /openapi/v1.json with the index page and a 200, which reads to an
// integrator as a document they failed to parse rather than one that is not served.
app.Map("/openapi/{**rest}", () => Results.NotFound()).ExcludeFromDescription();

// SPA fallback. The built UI is not in source control (see .gitignore): the container
// image builds it, and `dev.ps1 ui` builds it locally. When it is absent, as after a
// bare `dotnet run` on a fresh clone, say so in words rather than 404ing, and say how to
// get it.
var spaIndex = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");

if (File.Exists(spaIndex))
{
    app.MapFallbackToFile("index.html");
}
else
{
    app.MapFallback(() => Results.Content(
        """
        <!doctype html>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Dexicon</title>
        <style>
          body{font-family:system-ui,sans-serif;max-width:34rem;margin:15vh auto;padding:0 1rem;
               color:#1c1c1e;background:#fbfbfd;line-height:1.55}
          @media (prefers-color-scheme:dark){body{color:#e9e9ec;background:#17181c}}
          code{background:color-mix(in oklab,currentColor 12%,transparent);padding:.12rem .35rem;border-radius:4px}
        </style>
        <h1>Dexicon</h1>
        <p>The API and MCP endpoint are running. The web UI has not been built into this instance.</p>
        <p>Build it with <code>./scripts/dev.ps1 ui</code>, or use the container image, which builds
           it at image build time.</p>
        <p>MCP endpoint <code>/mcp</code> &middot; health <code>/healthz/live</code></p>
        """,
        "text/html"));
}

// Skipped when `dotnet build` is only reading the OpenAPI document out of this app;
// see Bootstrapper.IsBuildTimeToolRun. A build must not migrate a database or mint a
// token.
if (!Bootstrapper.IsBuildTimeToolRun)
{
    await Bootstrapper.InitialiseAsync(app);
}

// Report the addresses the server ACTUALLY bound, after it has bound them. Logging
// a guess beforehand is how "listening on 8477" ends up in the log of a process that
// failed to bind 8477.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var bound = app.Services.GetService<IServer>()?.Features
        .Get<IServerAddressesFeature>()?.Addresses;
    Log.Information("Dexicon listening on {Urls}",
        bound is { Count: > 0 } ? string.Join(", ", bound) : "(no bound address reported)");
});

try
{
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Dexicon terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Evicts cached principals so a revoked token stops working immediately.</summary>
internal sealed class MemoryCacheEvictor(IMemoryCache cache) : IMemoryCacheEvictor
{
    public void EvictPrincipals()
    {
        // MemoryCache has no prefix-scan, and the principal TTL is 60s, so the blunt
        // instrument is the correct one: revocation is rare and correctness beats a few
        // re-verifications.
        //
        // It is only correct while this cache holds nothing but principals. Admin sessions
        // and the sign-in throttle each keep their own store for exactly that reason;
        // putting them here signed the operator out whenever they revoked a key.
        if (cache is MemoryCache mc) mc.Clear();
    }
}

/// <summary>
/// Optional periodic refresh. Off by default (RefreshMinutes = 0) because a tool that
/// silently re-embeds a large repository on a timer is a surprise, not a feature.
/// </summary>
internal sealed class ScheduledRefreshService(
    IServiceScopeFactory scopes,
    IOptions<DexiconOptions> options,
    ILogger<ScheduledRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.Indexing.RefreshMinutes;
        if (minutes <= 0)
        {
            log.LogInformation("Scheduled refresh disabled (DEXICON__INDEXING__REFRESHMINUTES=0)");
            return;
        }

        var interval = TimeSpan.FromMinutes(minutes);
        log.LogInformation("Scheduled refresh every {Minutes} minutes", minutes);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var queue = scope.ServiceProvider.GetRequiredService<IndexJobQueue>();

                var due = await db.Corpora
                    .Where(c => c.State != CorpusState.Indexing)
                    .Select(c => c.Id)
                    .ToListAsync(stoppingToken);

                foreach (var id in due)
                    await queue.EnqueueAsync(id, JobKind.Refresh, ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduled refresh tick failed; will try again next interval");
            }
        }
    }
}

internal static class ThisAssembly
{
    /// <summary>
    /// What this build is: <c>0.1.1</c> on a release, <c>0.1.2-alpha.0.7</c> seven commits
    /// after one. Derived from the nearest git tag at build time; see Directory.Build.props.
    /// </summary>
    /// <remarks>
    /// The INFORMATIONAL version, not <c>Assembly.GetName().Version</c>. MinVer pins that
    /// one to <c>major.0.0.0</c> on purpose, so a patch release cannot break assembly
    /// binding, which means that for a 0.x project it is <c>0.0.0.0</c>, and reading it
    /// reported <c>0.0.0</c> for every build. The <c>+sha</c> a deterministic build appends
    /// is dropped: it is provenance, and the image already carries it as a `sha-` tag.
    /// </remarks>
    public static string Version { get; } = Read();

    /// <summary>
    /// The version of the API SURFACE, which is what an OpenAPI document versions.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse, and deliberately not <see cref="Version"/>. The document is
    /// generated at build time and committed, because the image builds the web client from
    /// it, so a version carrying the commit height would make that file differ on every
    /// commit, and the check that it matches the code would become noise everyone learns
    /// to ignore. A patch does not change the contract; a minor does.
    /// </remarks>
    public static string ApiVersion { get; } = MajorMinor(Version);

    private static string Read()
    {
        var informational = typeof(ThisAssembly).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational)) return "0.0.0";

        var build = informational.IndexOf('+', StringComparison.Ordinal);
        return build < 0 ? informational : informational[..build];
    }

    private static string MajorMinor(string version)
    {
        var parts = version.Split('.');
        return parts.Length < 2 ? version : $"{parts[0]}.{parts[1]}";
    }
}

/// <summary>Exposed so the integration tests can build the same host.</summary>
public partial class Program;
