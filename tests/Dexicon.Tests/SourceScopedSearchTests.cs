using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Narrowing a search to one source.
///
/// A corpus over a shelf of books has one source per topic folder, and until now there was
/// no way to say "only the AI ones". `pathPrefix` cannot do it: file_path is relative to a
/// SOURCE root, so a source at `orly/AI` stores its books as bare filenames and no prefix
/// matches the folder they came from. The information was in the index the whole time,
/// source_id is written to every point and indexed as a keyword, and unreachable.
/// </summary>
public sealed class SourceScopedSearchTests : IDisposable
{
    private readonly CatalogDbContext _db;
    private readonly ScopeResolver _scopes;

    public SourceScopedSearchTests()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        _db = new CatalogDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        // Sources have a foreign key to their corpus, and a corpus to its tenant.
        _db.Corpora.AddRange(
            new Corpus { Id = "books", Name = "books", CreatedUtc = DateTime.UtcNow },
            new Corpus { Id = "code", Name = "code", CreatedUtc = DateTime.UtcNow });
        _db.SaveChanges();

        _db.Sources.AddRange(
            Source("src-ai", "books", "books/orly/AI"),
            Source("src-phil", "books", "books/orly/Philosophy"),
            Source("src-arch", "books", "books/orly/Architecture"),
            Source("src-other", "code", "src"),
            // An upload source has no root path at all.
            new Source { Id = "src-upload", CorpusId = "books", Kind = SourceKind.Upload, RootPath = null, CreatedUtc = DateTime.UtcNow });
        _db.SaveChanges();

        _scopes = new ScopeResolver(_db);
    }

    private static Source Source(string id, string corpusId, string root) => new()
    {
        Id = id,
        CorpusId = corpusId,
        Kind = SourceKind.Workspace,
        RootPath = root,
        CreatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task One_folder_resolves_to_one_source()
    {
        var ids = await _scopes.SourceIdsAsync(["books"], "books/orly/AI");

        ids.ShouldBe(["src-ai"]);
    }

    [Fact]
    public async Task A_parent_folder_resolves_to_everything_beneath_it()
    {
        // `orly` should mean the whole shelf, not nothing. Without this a caller has to
        // know every topic folder to search more than one.
        var ids = await _scopes.SourceIdsAsync(["books"], "books/orly");

        ids.ShouldBe(["src-ai", "src-phil", "src-arch"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_prefix_that_is_not_a_folder_boundary_does_not_match()
    {
        // "books/orly/A" must not drag in "books/orly/AI" and "books/orly/Architecture" by
        // string prefix. Folder boundaries, not characters.
        await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.SourceIdsAsync(["books"], "books/orly/A"));
    }

    [Fact]
    public async Task Leading_and_trailing_slashes_are_forgiven()
    {
        // list_corpora prints the stored form; a person types whatever looks like a path.
        (await _scopes.SourceIdsAsync(["books"], "/books/orly/AI/")).ShouldBe(["src-ai"]);
        (await _scopes.SourceIdsAsync(["books"], "books\\orly\\AI")).ShouldBe(["src-ai"]);
    }

    [Fact]
    public async Task It_can_only_narrow_a_scope_never_widen_one()
    {
        // The security property. Corpus ids come from resolution that already authorised
        // them, so naming another corpus's source must not reach it.
        await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.SourceIdsAsync(["books"], "src"));
    }

    [Fact]
    public async Task An_unknown_folder_says_what_there_is()
    {
        // A silent empty result set is indistinguishable from "nothing matched your query".
        var error = await Should.ThrowAsync<ScopeResolutionException>(
            () => _scopes.SourceIdsAsync(["books"], "books/orly/Rust"));

        error.Message.ShouldContain("books/orly/Rust");
    }

    [Fact]
    public async Task An_upload_source_never_matches_a_folder()
    {
        // It has no root path. Matching it against one would be matching against null.
        var ids = await _scopes.SourceIdsAsync(["books"], "books/orly");

        ids.ShouldNotContain("src-upload");
    }

    public void Dispose() => _db.Dispose();
}
