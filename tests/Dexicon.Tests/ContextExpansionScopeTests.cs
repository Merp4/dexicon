using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// Which scope context expansion re-resolves against.
///
/// Expansion needs the collection and chunk set a file's chunks live in, and neither is
/// part of a search result, so it resolves the scope a second time. It resolved the hits'
/// corpus IDs, which drops the `corpus:set` qualification the caller asked with, and a
/// bare corpus resolves to its DEFAULT set. Asking for `books:fine` therefore read the
/// neighbours out of `books`.
///
/// Nothing failed, which is why it survived: a chunk index means different things in two
/// chunkings, so the window either pulled unrelated text or missed the hit's own index and
/// fell back to the hit alone — expansion appearing to do nothing.
/// </summary>
public sealed class ContextExpansionScopeTests
{
    private static SearchHit Hit(string corpusId, int index = 0) => new()
    {
        CorpusId = corpusId,
        FilePath = "docs/x.md",
        ChunkIndex = index,
        Content = "text",
    };

    [Fact]
    public void TheCallersQualifiedNamesAreWhatGetResolved()
    {
        // The whole defect in one assertion: `books:fine` must survive into the second
        // resolution. Resolving the hit's corpus ID instead loses the `:fine`.
        var scope = ContextService.ScopeForExpansion(["books:fine"], [Hit("01CORPUS")]);

        scope.ShouldBe(["books:fine"]);
    }

    [Fact]
    public void SeveralQualifiedNamesAllSurvive()
    {
        var scope = ContextService.ScopeForExpansion(
            ["books:fine", "docs"], [Hit("01BOOKS"), Hit("01DOCS")]);

        scope.ShouldBe(["books:fine", "docs"]);
    }

    [Fact]
    public void NamingNothingFallsBackToTheCorporaThatAnswered()
    {
        // A caller who named nothing was searched against the default sets, so resolving
        // the hits' corpora agrees with what produced them. It also keeps the second
        // resolution narrow rather than re-resolving everything the key can reach.
        var scope = ContextService.ScopeForExpansion(
            null, [Hit("01BOOKS"), Hit("01DOCS"), Hit("01BOOKS", 4)]);

        scope.ShouldBe(["01BOOKS", "01DOCS"]);
    }

    [Fact]
    public void AnEmptyRequestIsTreatedAsNamingNothing()
    {
        ContextService.ScopeForExpansion([], [Hit("01BOOKS")]).ShouldBe(["01BOOKS"]);
    }

    /// <summary>
    /// The property that made the old code wrong, asserted against the resolver itself
    /// rather than assumed: a bare corpus name resolves to the default set, so it cannot
    /// stand in for a qualified one.
    /// </summary>
    [Fact]
    public async Task ABareCorpusNameResolvesToTheDefaultSet()
    {
        await using var db = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>()
                .UseSqlite("DataSource=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var corpus = new Corpus { Id = "01BOOKS", Name = "books", State = CorpusState.Ready };
        corpus.ChunkSets.Add(new ChunkSet
        {
            Id = "01SET-DEFAULT", CorpusId = corpus.Id, Name = "default", IsDefault = true,
            EmbeddingProvider = "ollama", EmbeddingModel = "embeddinggemma",
            EmbeddingDimensions = 768, CollectionName = "coll__default",
            ChunkSize = 768, ChunkOverlap = 100, BoundaryMode = "language-aware",
            State = CorpusState.Ready,
        });
        corpus.ChunkSets.Add(new ChunkSet
        {
            Id = "01SET-FINE", CorpusId = corpus.Id, Name = "fine", IsDefault = false,
            EmbeddingProvider = "ollama", EmbeddingModel = "embeddinggemma",
            EmbeddingDimensions = 768, CollectionName = "coll__fine",
            ChunkSize = 256, ChunkOverlap = 32, BoundaryMode = "language-aware",
            State = CorpusState.Ready,
        });
        db.Corpora.Add(corpus);
        await db.SaveChangesAsync();

        var scopes = new ScopeResolver(db);
        var principal = new Principal("admin-session", "admin",
            new HashSet<string>(StringComparer.Ordinal) { Scopes.Admin });

        var bare = await scopes.ResolveReadableAsync(principal, ["books"]);
        var qualified = await scopes.ResolveReadableAsync(principal, ["books:fine"]);

        bare.Targets[0].Set.Name.ShouldBe("default");
        qualified.Targets[0].Set.Name.ShouldBe("fine");
        bare.Targets[0].Set.CollectionName.ShouldNotBe(qualified.Targets[0].Set.CollectionName);
    }
}
