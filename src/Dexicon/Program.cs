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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
// DEXICON__SECTION__KEY from the environment, plus /run/secrets/* for orchestrated
// deployments. A secret never comes from appsettings.json — see docs/10.
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
    .WriteTo.Console(outputTemplate:
        // UTC, and labelled. Rendering the log in local time while the API returns UTC
        // makes the two impossible to line up — which cost real time once already,
        // reading a job that "started an hour ago" when it had started three minutes
        // before. Timestamps are UTC everywhere; only the UI localises, for its viewer.
        "[{UtcTime:HH:mm:ss}Z {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ScopeExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDbContext<CatalogDbContext>(o =>
    o.UseSqlite($"Data Source={options.Storage.CatalogPath};Cache=Shared"));

builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>();
builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();
builder.Services.AddSingleton<IndexProgressBroadcaster>();
builder.Services.AddSingleton<IMemoryCacheEvictor, MemoryCacheEvictor>();

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ScopeResolver>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<CorpusIndexer>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<IVectorStoreCleanup, VectorStoreCleanup>();
builder.Services.AddScoped<IndexJobQueue>();
builder.Services.AddScoped<RequestContext>();

builder.Services.AddHostedService<IndexingBackgroundService>();
builder.Services.AddHostedService<ScheduledRefreshService>();

// ── MCP ──────────────────────────────────────────────────────────────────────
// Stateless: the 2026-07-28 core removed the handshake and the session id, and
// Dexicon needs no server-to-client calls. Verified in the M0 spike.
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "dexicon", Version = ThisAssembly.Version })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<DexiconTools>()
    .WithResources<DexiconResources>();

builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────────
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseDexiconAuth();

app.MapHealthEndpoints();
app.MapSearchEndpoints();
app.MapCorpusEndpoints();
app.MapChunkSetEndpoints();
app.MapDocumentEndpoints();
app.MapJobEndpoints();
app.MapEventEndpoints();
app.MapWorkspaceEndpoints();
app.MapAdminEndpoints();
app.MapMcp("/mcp");

// SPA fallback. The built UI is not in source control (see .gitignore): the container
// image builds it, and `dev.ps1 ui` builds it locally. When it is absent — a bare
// `dotnet run` on a fresh clone — say so in words rather than 404ing, and say how to
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

await Bootstrapper.InitialiseAsync(app);

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
        // instrument is the correct one: revocation is rare and correctness beats a
        // few re-verifications.
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
    public static string Version =>
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}

/// <summary>Exposed so the integration tests can build the same host.</summary>
public partial class Program;
