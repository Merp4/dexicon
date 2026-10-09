using Dexicon.Core.Auth;
using Dexicon.Core.Catalog;
using Dexicon.Infrastructure;
using Dexicon.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void AnEchoedValueIsCutBeforeItIsSanitisedSoALineBreakPairThatShrinksDoesNotShiftTheCut()
    {
        // 150 CRLF pairs are 300 characters, and one space each once sanitised. Cut at 200 first, the value
        // is 100 pairs. Sanitised first it would be 150 spaces and 50 of the x.
        var value = string.Concat(Enumerable.Repeat("\r\n", 150)) + new string('x', 300);

        DexiconTools.Echo(value).ShouldBe(new string(' ', 100) + "...");
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

    private static string RaggedName(int i, int length) => $"{i:D3}-".PadRight(length, 'r');

    [Fact]
    public async Task AWrongSourceListsTheRootsUpToTheLimitAndSaysHowManyAreLeft()
    {
        await using (var seed = _harness.NewContext())
        {
            for (var i = 0; i < 40; i++)
                seed.Sources.Add(new Source
                {
                    Id = $"extra-source-{i:D3}",
                    CorpusId = IndexingHarness.CorpusId,
                    Kind = SourceKind.Workspace,
                    RootPath = RaggedName(i, 100),
                    CreatedUtc = DateTime.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        await using var db = _harness.NewContext();
        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).SourceIdsAsync([IndexingHarness.CorpusId], "nowhere"));

        thrown.Message.ShouldStartWith("No source at 'nowhere' in the corpora searched. Sources: ");
        thrown.Message.ShouldEndWith(" more). A parent matches everything beneath it.");
        thrown.Message.Length.ShouldBeLessThan(ScopeResolver.ListedMax + 200);
    }

    [Fact]
    public async Task AWrongChunkSetListsTheSetsUpToTheLimitAndSaysHowManyAreLeft()
    {
        await using (var seed = _harness.NewContext())
        {
            for (var i = 0; i < 40; i++)
                seed.ChunkSets.Add(new ChunkSet
                {
                    Id = $"extra-set-{i:D3}",
                    CorpusId = IndexingHarness.CorpusId,
                    Name = RaggedName(i, 100),
                    EmbeddingModel = "m",
                    EmbeddingDimensions = 768,
                    CollectionName = "c",
                    ChunkSize = 256,
                    ChunkOverlap = 0,
                    BoundaryMode = "none",
                    State = CorpusState.Ready,
                    CreatedUtc = DateTime.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        await using var db = _harness.NewContext();
        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveReadableAsync(Reader, ["notes:nope"]));

        thrown.Message.ShouldStartWith("Corpus 'notes' has no chunk set named 'nope'. Its sets: ");
        thrown.Message.ShouldEndWith(" more).");
        thrown.Message.Length.ShouldBeLessThan(ScopeResolver.ListedMax + 200);
    }

    [Fact]
    public async Task AWritableLookupOfAMissingCorpusListsTheCorporaUpToTheLimit()
    {
        await AddCorporaAsync(count: 40, nameLength: 100);
        await using var db = _harness.NewContext();

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveWritableAsync(Reader, "nowhere"));

        thrown.Message.ShouldStartWith("No corpus named 'nowhere' is reachable by key 'reader'. Corpora this key can reach: ");
        thrown.Message.ShouldEndWith(" more).");
        thrown.Message.Length.ShouldBeLessThan(ScopeResolver.ListedMax + 200);
    }

    [Fact]
    public void AListWhoseNamesJoinToExactlyTheLimitIsWrittenWholeAndOneCharacterMoreLosesTheLastName()
    {
        // Fourteen names of 98 characters and a last of 100: 1,372 + 100 + 14 separators of 2 = 1,500.
        var names = Enumerable.Range(0, 14).Select(i => RaggedName(i, 98)).Append(RaggedName(14, 100)).ToList();
        var oneOver = names.Take(14).Append(RaggedName(14, 101)).ToList();

        var whole = ScopeResolver.Listed(names);
        var over = ScopeResolver.Listed(oneOver);

        whole.Length.ShouldBe(ScopeResolver.ListedMax);
        whole.ShouldNotContain("(and");
        over.ShouldEndWith(" (and 1 more)");
        over.ShouldNotContain(RaggedName(14, 101));
    }

    [Fact]
    public void AListWithOneVeryLongNameIsBoundedByTheCutOfThatName()
    {
        var listed = ScopeResolver.Listed([new string('n', 3_000)]);

        listed.ShouldBe(new string('n', ScopeResolver.ShownMax) + "...");
    }

    [Fact]
    public async Task ANameOfExactlyTwoHundredCharactersIsQuotedWholeAndTheCutDoesNotSplitAPair()
    {
        await using var db = _harness.NewContext();
        var resolver = new ScopeResolver(db);
        var pair = char.ConvertFromUtf32(0x1F600);

        var exact = await Should.ThrowAsync<ScopeResolutionException>(() => resolver.ResolveReadableAsync(Reader, [new string('a', 200)]));
        var plusOne = await Should.ThrowAsync<ScopeResolutionException>(() => resolver.ResolveReadableAsync(Reader, [new string('a', 201)]));
        var straddling = await Should.ThrowAsync<ScopeResolutionException>(() =>
            resolver.ResolveReadableAsync(Reader, [new string('a', 199) + pair + "tail"]));

        exact.Message.ShouldStartWith($"Unknown corpus '{new string('a', 200)}'. ");
        plusOne.Message.ShouldStartWith($"Unknown corpus '{new string('a', 200)}...'. ");
        straddling.Message.ShouldStartWith($"Unknown corpus '{new string('a', 199)}...'. ");
    }

    [Fact]
    public async Task OnlyTheFirstFewUnknownNamesAreQuotedWhateverHowManyAreSent()
    {
        await AddCorporaAsync(count: 3, nameLength: 20);
        await using var db = _harness.NewContext();
        var asked = Enumerable.Range(0, 25).Select(i => $"{i:D2}" + new string('u', 198)).ToList();

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() =>
            new ScopeResolver(db).ResolveReadableAsync(Reader, asked));

        thrown.Message.ShouldContain("(and 22 more). Corpora this key can reach: ");
        thrown.Message.ShouldEndWith(".");
        thrown.Message.ShouldContain("'00" + new string('u', 198) + "'");
        thrown.Message.ShouldContain("'02" + new string('u', 198) + "'");
        thrown.Message.ShouldNotContain("'03");
        thrown.Message.Length.ShouldBeLessThan(1_500);
    }

    [Fact]
    public async Task AMillionUnknownNamesAreAnswerredByAShortMessageToAToolAndToTheRestHandler()
    {
        await using var db = _harness.NewContext();
        var asked = Enumerable.Range(0, 1_000_000).Select(i => "n" + i).ToList();
        var resolver = new ScopeResolver(db);

        var thrown = await Should.ThrowAsync<ScopeResolutionException>(() => resolver.ResolveReadableAsync(Reader, asked));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging().AddOptions().AddProblemDetails().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        await new ScopeExceptionHandler(NullLogger<ScopeExceptionHandler>.Instance).TryHandleAsync(context, thrown, default);

        thrown.Message.ShouldStartWith("Unknown corpus 'n0', 'n1', 'n2' (and 999997 more). ");
        thrown.Message.Length.ShouldBeLessThan(1_000);
        context.Response.Body.Length.ShouldBeLessThan(2_000);
        context.Response.Body.Position = 0;
        new StreamReader(context.Response.Body).ReadToEnd().ShouldContain("(and 999997 more)");
    }
}
