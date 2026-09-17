using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// The test the specification says must ship in the same commit as the enforcement
/// code, not as a follow-up. See docs/07-tenancy-auth.md.
///
/// It asserts that every failure is an AUTHORIZATION ERROR naming the problem, not an
/// empty result, because an empty result is indistinguishable from "that content does
/// not exist", and a caller acts on it.
/// </summary>
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private ScopeResolver _scopes = null!;

    private const string Acme = "acme";
    private const string Globex = "globex";
    private const string SecretString = "hunter2-globex-launch-code";

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Tenants.AddRange(
            new Tenant { Id = Acme, DisplayName = "Acme", CreatedUtc = DateTime.UtcNow },
            new Tenant { Id = Globex, DisplayName = "Globex", CreatedUtc = DateTime.UtcNow });

        _db.Corpora.AddRange(
            Corpus("acme-private", Acme, CorpusVisibility.Private),
            Corpus("globex-private", Globex, CorpusVisibility.Private),
            Corpus("globex-shared-all", Globex, CorpusVisibility.Shared));

        await _db.SaveChangesAsync();
        _scopes = new ScopeResolver(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Corpus Corpus(string name, string tenant, CorpusVisibility visibility) => new()
    {
        Id = $"id-{name}",
        TenantId = tenant,
        Name = name,
        Visibility = visibility,
        State = CorpusState.Ready,
        CreatedUtc = DateTime.UtcNow,
        ChunkSets =
        {
            new ChunkSet
            {
                Id = $"set-{name}",
                CorpusId = $"id-{name}",
                Name = "default",
                EmbeddingModel = "nomic-embed-text",
                EmbeddingDimensions = 768,
                CollectionName = "dexicon__nomic-embed-text__768",
                ChunkSize = 768,
                ChunkOverlap = 100,
                BoundaryMode = "language-aware",
                IsDefault = true,
                State = CorpusState.Ready,
                CreatedUtc = DateTime.UtcNow,
            },
        },
    };

    // ── The headline assertion ───────────────────────────────────────────────

    [Fact]
    public async Task SecondTenantCannotRetrieveFirstTenantsContent()
    {
        var visible = await _scopes.VisibleAsync(Acme);
        var names = visible.Select(c => c.Name).ToList();

        names.ShouldContain("acme-private");
        names.ShouldContain("globex-shared-all");  // shared with no grants = readable by everyone
        names.ShouldNotContain("globex-private");  // private is never visible to another tenant

        // Naming the other tenant's private corpus is an ERROR, not a silent drop.
        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Acme, ["globex-private"]));
        ex.Message.ShouldContain("Unknown corpus");
        ex.Message.ShouldContain("globex-private");

        // Guessing the ID directly fares no better.
        await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync(Acme, ["id-globex-private"]));
    }

    [Fact]
    public async Task UnscopedSearch_NeverBecomesSearchEverything()
    {
        // A tenant with nothing visible gets an error. The alternative, an empty
        // corpus list reaching the vector store, is the bug this guards.
        //
        // The shared-with-everyone corpus has to go first: it is visible to EVERY
        // tenant by design (docs/07), so with it present there is no such thing as a
        // tenant that can see nothing. The first version of this test asserted against
        // a world that cannot exist.
        _db.Corpora.RemoveRange(_db.Corpora.Where(c => c.Visibility == CorpusVisibility.Shared));
        _db.Tenants.Add(new Tenant { Id = "empty", DisplayName = "Empty", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveReadableAsync("empty", null));
        ex.Message.ShouldContain("No corpora are visible");
    }

    [Fact]
    public async Task SharedCorpus_WithExplicitGrants_IsOnlyVisibleToThoseTenants()
    {
        _db.Tenants.Add(new Tenant { Id = "initech", DisplayName = "Initech", CreatedUtc = DateTime.UtcNow });
        var restricted = Corpus("globex-shared-some", Globex, CorpusVisibility.Shared);
        _db.Corpora.Add(restricted);
        _db.CorpusGrants.Add(new CorpusGrant { CorpusId = restricted.Id, TenantId = "initech" });
        await _db.SaveChangesAsync();

        (await _scopes.VisibleAsync("initech")).Select(c => c.Name).ShouldContain("globex-shared-some");
        (await _scopes.VisibleAsync(Acme)).Select(c => c.Name).ShouldNotContain("globex-shared-some");
    }

    [Fact]
    public async Task SharingIsReadOnly_ASharedCorpusCannotBeWrittenByTheGrantee()
    {
        // Writes are always owner-only, whatever the visibility. There is no setting
        // that changes this, so it gets a test rather than only a sentence in a doc.
        var ex = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.ResolveWritableAsync(Acme, "globex-shared-all"));
        ex.Message.ShouldContain("read access only");
    }

    [Fact]
    public async Task OwnerCanWriteTheirOwnCorpus()
    {
        var corpus = await _scopes.ResolveWritableAsync(Globex, "globex-shared-all");
        corpus.TenantId.ShouldBe(Globex);
    }

    // ── The repository-level guard ───────────────────────────────────────────

    [Fact]
    public async Task VectorStore_RefusesAQueryWithNoCorpusFilter()
    {
        // The second of three independent guards. Even if scope resolution were
        // bypassed entirely, the store will not issue an unfiltered query.
        var store = new QdrantVectorStore(
            Microsoft.Extensions.Options.Options.Create(new Core.Configuration.DexiconOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QdrantVectorStore>.Instance);

        var ex = await Should.ThrowAsync<UnscopedQueryException>(() => store.SearchAsync(
            new SearchQuery
            {
                Text = SecretString,
                CorpusIds = [],                       // the bug being guarded
                ChunkSetIds = [],
                CollectionName = "dexicon__nomic-embed-text__768",
            },
            denseVector: new float[768],
            sparse: SparseVector.Empty));

        ex.Message.ShouldContain("no corpus_id filter");
        store.Dispose();
    }
}
