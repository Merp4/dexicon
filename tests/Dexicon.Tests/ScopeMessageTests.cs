using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using ModelContextProtocol;

namespace Dexicon.Tests;

/// <summary>
/// What the resolver's errors say about the value a caller sent and about the list of corpora a key can
/// reach: the caller's value is cut where it is quoted, the list is shortened at a whole name, and the
/// tool layer's own cut then has nothing to take off.
/// </summary>
public sealed class ScopeMessageTests : IAsyncLifetime
{
    private IndexingHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _harness = await IndexingHarness.StartAsync("notes");
        await _harness.SeedCorpusAsync(SourceKind.Workspace);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private static Principal Reader => new("k", "reader", new HashSet<string>(StringComparer.Ordinal) { Scopes.Search });

    private async Task AddCorporaAsync(int count, int nameLength)
    {
        await using var db = _harness.NewContext();
        for (var i = 0; i < count; i++)
        {
            var name = $"{i:D3}-".PadRight(nameLength, 'n');
            db.Corpora.Add(new Corpus { Id = $"extra-{i:D3}", Name = name, State = CorpusState.Ready, CreatedUtc = DateTime.UtcNow });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ACallersNameIsQuotedUpToTwoHundredCharactersAndNoFurther()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveReadableAsync(Reader, [new string('a', 250)]));

        thrown.Message.ShouldStartWith($"Unknown corpus '{new string('a', 200)}...'. ");
    }

    [Fact]
    public async Task AWritableLookupQuotesTheNameTheSameWay()
    {
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveWritableAsync(Reader, new string('a', 250)));

        thrown.Message.ShouldStartWith($"No corpus named '{new string('a', 200)}...' is reachable");
    }

    [Fact]
    public async Task ALongListOfCorporaEndsAtAWholeNameWithTheNumberLeftOut()
    {
        await AddCorporaAsync(count: 40, nameLength: 100);
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveReadableAsync(Reader, ["nowhere"]));

        var listed = thrown.Message[(thrown.Message.IndexOf("reach: ", StringComparison.Ordinal) + "reach: ".Length)..];
        listed.ShouldEndWith(" more).");
        var names = listed[..listed.IndexOf(" (and ", StringComparison.Ordinal)].Split(", ");
        names.ShouldAllBe(n => n.Length == 100 || n == "notes", "every name listed is whole");
        var more = int.Parse(listed[(listed.IndexOf("(and ", StringComparison.Ordinal) + 5)..listed.IndexOf(" more)", StringComparison.Ordinal)]);
        (names.Length + more).ShouldBe(41);
        string.Join(", ", names).Length.ShouldBeLessThanOrEqualTo(ScopeResolver.ListedMax);
    }

    [Fact]
    public void AListThatFitsIsWrittenWholeAndOneThatDoesNotLosesNothingButTheEnd()
    {
        ScopeResolver.Listed(["a", "b", "c"]).ShouldBe("a, b, c");
        ScopeResolver.Listed([]).ShouldBe(string.Empty);

        var names = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 100)).ToList();
        var listed = ScopeResolver.Listed(names);

        listed.ShouldStartWith(string.Join(", ", names.Take(14)));
        listed.ShouldEndWith(" (and 6 more)");
    }

    [Fact]
    public async Task TheToolLayerKeepsTheWholeListWhenTheCallerSentAHugeName()
    {
        await AddCorporaAsync(count: 40, nameLength: 100);
        await using var db = _harness.NewContext();
        var rc = new RequestContext { Principal = Reader };

        var thrown = await Should.ThrowAsync<McpException>(() => DexiconTools.GetContextAsync(
            rc, new ScopeResolver(db), _harness.Vectors, new Dexicon.Core.Documents.DocumentReader(db),
            new string('b', 5_000), "a.md", aroundLine: 1));

        thrown.Message.ShouldEndWith(" more).");
        thrown.Message.ShouldContain($"'{new string('b', 200)}...'");
        thrown.Message.Length.ShouldBeLessThan(DexiconTools.MessageMax);
    }

    [Fact]
    public async Task AKeyThatReachesNothingIsToldSoWithoutAList()
    {
        await using var empty = await IndexingHarness.StartAsync("notes");
        await using var db = empty.NewContext();

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveReadableAsync(Reader, ["nowhere"]));

        thrown.Message.ShouldBe("Unknown corpus 'nowhere'. Key 'reader' can reach no corpora at all.");
    }

    [Fact]
    public void AnEchoedValueIsCutAtTheLengthOfACorpusNameAndNoLonger()
    {
        DexiconTools.EchoMax.ShouldBe(200);
        DexiconTools.Echo(new string('a', 250)).ShouldBe(new string('a', 200) + "...");
        DexiconTools.Echo(new string('a', 200)).ShouldBe(new string('a', 200));
        DexiconTools.Echo(new string('a', 201)).ShouldBe(new string('a', 200) + "...");
    }

    [Fact]
    public void ARefusalIsCutAtFourThousandCharacters()
    {
        DexiconTools.MessageMax.ShouldBe(4_000);
        DexiconTools.Refusal(new string('r', 5_000)).Message.ShouldBe(new string('r', 4_000) + "...");
        DexiconTools.Refusal(new string('r', 4_000)).Message.ShouldBe(new string('r', 4_000));
    }

    [Fact]
    public void ARefusalIsHeldToOneLine()
    {
        DexiconTools.Refusal("a\r\nb" + (char)0x1B + "c").Message.ShouldBe("a b�c");
    }
}
