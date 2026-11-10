using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The comparison a model canary makes, apart from any catalogue. The numbers that matter are the
/// measured ones: the same model twice agrees to 3e-16, an engine change moved vectors by at most
/// 3e-6, and another quantisation of one model agreed at a median 0.988.
/// </summary>
public sealed class ModelCanaryMeasurementTests
{
    [Fact]
    public void TheCanaryTextIsPinned() =>
        // Every recorded vector answers this text. Changing it makes every set read as changed.
        ModelCanary.Text.ShouldBe("The quick brown fox jumps over the lazy dog.");

    [Fact]
    public void TheThresholdSitsBetweenAnEngineChangeAndAnotherQuantisation()
    {
        ModelCanary.SameModel.ShouldBeGreaterThan(0.9886, "the highest median seen between two builds of one model");
        ModelCanary.SameModel.ShouldBeLessThan(0.999997, "the lowest similarity seen across an engine change");
    }

    [Fact]
    public void IdenticalVectorsAreFullySimilar() =>
        ModelCanary.Similarity([0.3f, -0.4f, 0.5f], [0.3f, -0.4f, 0.5f]).ShouldBe(1.0, 1e-9);

    [Fact]
    public void ScalingAVectorDoesNotChangeItsSimilarity() =>
        ModelCanary.Similarity([1f, 2f, 3f], [10f, 20f, 30f]).ShouldBe(1.0, 1e-9);

    [Fact]
    public void OrthogonalVectorsHaveNoSimilarity() =>
        ModelCanary.Similarity([1f, 0f], [0f, 1f]).ShouldBe(0.0, 1e-9);

    [Fact]
    public void VectorsOfDifferentLengthAreNotComparable() =>
        // A different dimensionality is a different space, however the shared leading values compare.
        ModelCanary.Similarity([1f, 0f, 0f], [1f, 0f, 0f, 0f]).ShouldBe(0.0);

    [Fact]
    public void AVectorOfZerosIsSimilarToNothing()
    {
        ModelCanary.Similarity([0f, 0f], [1f, 0f]).ShouldBe(0.0);
        ModelCanary.Similarity([0f, 0f], [0f, 0f]).ShouldBe(0.0);
    }

    [Fact]
    public void AnEmptyVectorIsSimilarToNothing() =>
        ModelCanary.Similarity([], []).ShouldBe(0.0);

    [Fact]
    public void AComponentThatIsNotAFiniteNumberIsSimilarToNothing()
    {
        // NaN would be stored as NULL, and a NULL similarity is neither above nor below the threshold.
        ModelCanary.Similarity([1f, 0f], [float.NaN, 0f]).ShouldBe(0.0);
        ModelCanary.Similarity([1f, 0f], [float.PositiveInfinity, 0f]).ShouldBe(0.0);
        ModelCanary.Similarity([float.NaN, 0f], [1f, 0f]).ShouldBe(0.0);
    }

    [Fact]
    public void APackedVectorUnpacksToTheSameValues()
    {
        float[] vector = [0.25f, -1.5f, 3e-7f, 1e9f, 0f];

        ModelCanary.Unpack(ModelCanary.Pack(vector)).ShouldBe(vector);
    }

    [Fact]
    public void APackedVectorIsLittleEndianFloat32()
    {
        // 1.0f is 0x3F800000.
        ModelCanary.Pack([1f]).ShouldBe(new byte[] { 0x00, 0x00, 0x80, 0x3F });
    }

    [Fact]
    public void BytesThatAreNotWholeFloatsUnpackToNothing() =>
        ModelCanary.Unpack(new byte[] { 1, 2, 3, 4, 5 }).ShouldBeEmpty();

    [Fact]
    public void ARecordThatDoesNotUnpackReadsAsAChange() =>
        // Fails visibly: a record nothing can read should not look like a model that matches.
        ModelCanary.Similarity(ModelCanary.Unpack(new byte[] { 1, 2, 3 }), [1f, 0f]).ShouldBeLessThan(ModelCanary.SameModel);
}

/// <summary>
/// Recording and comparing a set's canary against a catalogue, with the model's answer scripted.
/// </summary>
public sealed class ModelCanaryChecks : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CatalogDbContext _db = null!;
    private readonly ScriptedEmbedder _embedder = new();

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Corpora.Add(new Corpus { Id = "c", Name = "books", CreatedUtc = DateTime.UtcNow });
        _db.Corpora.Add(new Corpus { Id = "c2", Name = "theirs", CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        await AddSet("s1", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task AddSet(string id, string name, string model = "embeddinggemma", string corpusId = "c")
    {
        _db.ChunkSets.Add(new ChunkSet
        {
            Id = id,
            CorpusId = corpusId,
            Name = name,
            EmbeddingModel = model,
            EmbeddingProvider = "ollama",
            CollectionName = $"dexicon_{id}",
            BoundaryMode = "blank-line",
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>The set as the catalogue holds it now. Untracked, so each read sees what the last check wrote.</summary>
    private Task<ChunkSet> Stored(string id = "s1") =>
        _db.ChunkSets.AsNoTracking().SingleAsync(s => s.Id == id, TestContext.Current.CancellationToken);

    private async Task<CanaryOutcome> Check(string id = "s1") =>
        await ModelCanary.CheckAsync(
            _db, _embedder, await Stored(id), NullLogger.Instance, TestContext.Current.CancellationToken);

    /// <summary>A unit vector whose cosine with <c>[1, 0, 0, 0]</c> is <paramref name="similarity"/>.</summary>
    private static float[] At(double similarity) =>
        [(float)similarity, (float)Math.Sqrt(1 - similarity * similarity), 0f, 0f];

    private static readonly float[] Original = [1f, 0f, 0f, 0f];

    private static Task<IReadOnlyList<ChangedModel>> Changed(CatalogDbContext db, params string[] corpusIds) =>
        SystemEndpoints.ChangedModelsAsync(
            db, corpusIds.Length == 0 ? ["c"] : corpusIds, TestContext.Current.CancellationToken);

    // ── Recording ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASetWithNoRecordTakesTheCurrentAnswerAsItsBaseline()
    {
        _embedder.Answer = Original;

        (await Check()).ShouldBe(CanaryOutcome.Recorded);

        var set = await Stored();
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Original));
        set.CanarySimilarity.ShouldBe(1.0);
        set.CanaryCheckedUtc.ShouldNotBeNull();
        (await Changed(_db)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCanaryIsEmbeddedRawWithItsOwnText()
    {
        _embedder.Answer = Original;

        await Check();
        await Check();

        // Not framed: a framing edit already rebuilds the set, and the canary measures the model.
        _embedder.Calls.Count.ShouldBe(2);
        _embedder.Calls.ShouldAllBe(c =>
            c.Purpose == EmbedPurpose.Raw && c.Inputs.SequenceEqual(new[] { ModelCanary.Text }));
        _embedder.Calls.ShouldAllBe(c => c.Target == new EmbeddingTarget("ollama", "embeddinggemma"));
    }

    // ── Comparing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AModelThatAnswersAsBeforeIsUnchangedAndKeepsItsBaseline()
    {
        _embedder.Answer = Original;
        await Check();
        var recorded = await Stored();

        (await Check()).ShouldBe(CanaryOutcome.Unchanged);

        var set = await Stored();
        set.CanaryVector.ShouldBe(recorded.CanaryVector);
        set.CanarySimilarity!.Value.ShouldBe(1.0, 1e-9);
        set.CanaryCheckedUtc!.Value.ShouldBeGreaterThanOrEqualTo(recorded.CanaryCheckedUtc!.Value);
    }

    [Fact]
    public async Task AModelThatAnswersDifferentlyIsChangedAndTheBaselineIsKept()
    {
        _embedder.Answer = Original;
        await Check();

        _embedder.Answer = At(0.9);
        (await Check()).ShouldBe(CanaryOutcome.Changed);

        var set = await Stored();
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Original), "the record is what the set holds, not what the model says now");
        set.CanarySimilarity!.Value.ShouldBe(0.9, 1e-6);

        var changed = (await Changed(_db)).ShouldHaveSingleItem();
        changed.Set.ShouldBe("books:default");
        changed.Provider.ShouldBe("ollama");
        changed.Model.ShouldBe("embeddinggemma");
        changed.Similarity.ShouldBe(0.9, 1e-6);
    }

    [Fact]
    public async Task ADifferentDimensionalityIsChangedWithNoSimilarity()
    {
        _embedder.Answer = Original;
        await Check();

        _embedder.Answer = [1f, 0f, 0f, 0f, 0f];
        (await Check()).ShouldBe(CanaryOutcome.Changed);

        (await Stored()).CanarySimilarity.ShouldBe(0.0);
        (await Changed(_db)).ShouldHaveSingleItem().Similarity.ShouldBe(0.0);
    }

    [Fact]
    public async Task AModelThatAnswersAsBeforeAgainClearsTheChange()
    {
        _embedder.Answer = Original;
        await Check();
        _embedder.Answer = At(0.5);
        await Check();
        (await Changed(_db)).ShouldHaveSingleItem();

        // The tag was pulled back to what it was.
        _embedder.Answer = Original;
        (await Check()).ShouldBe(CanaryOutcome.Unchanged);

        (await Changed(_db)).ShouldBeEmpty();
    }

    // ── A provider that does not answer ─────────────────────────────────────

    public static TheoryData<Exception> Outages => new()
    {
        new EmbeddingUnavailableException("connection refused"),
        new EmbeddingInputTooLongException("context"),
        new UnknownEmbeddingProviderException("ollama", ["other"]),
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task AnOutageLeavesTheRecordAsItWas(Exception outage)
    {
        _embedder.Answer = Original;
        await Check();
        _embedder.Answer = At(0.5);
        await Check();
        var before = await Stored();

        _embedder.Fault = outage;
        (await Check()).ShouldBe(CanaryOutcome.Unavailable);

        // A set that had changed still has, and one that had not still has not: an outage is neither.
        var after = await Stored();
        after.CanaryVector.ShouldBe(before.CanaryVector);
        after.CanarySimilarity.ShouldBe(before.CanarySimilarity);
        after.CanaryCheckedUtc.ShouldBe(before.CanaryCheckedUtc);
        (await Changed(_db)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnOutageBeforeAnyRecordRecordsNothing()
    {
        _embedder.Fault = new EmbeddingUnavailableException("connection refused");

        (await Check()).ShouldBe(CanaryOutcome.Unavailable);

        var set = await Stored();
        set.CanaryVector.ShouldBeNull();
        set.CanarySimilarity.ShouldBeNull();
    }

    [Fact]
    public async Task CancellationIsNotReadAsAnOutage()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _embedder.Fault = new OperationCanceledException(cancelled.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await ModelCanary.CheckAsync(_db, _embedder, await Stored(), NullLogger.Instance, cancelled.Token));
    }

    // ── Two writers ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ACheckThatReadTheSetBeforeAnotherRecordedItDoesNotOverwriteTheOther()
    {
        var staleRead = await Stored();   // no record yet

        _embedder.Answer = Original;
        await Check();                    // another check records the baseline first

        _embedder.Answer = At(0.5);
        var outcome = await ModelCanary.CheckAsync(
            _db, _embedder, staleRead, NullLogger.Instance, TestContext.Current.CancellationToken);

        outcome.ShouldBe(CanaryOutcome.Superseded);
        var set = await Stored();
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Original));
        set.CanarySimilarity.ShouldBe(1.0);
    }

    [Fact]
    public async Task ACheckAgainstARecordThatAFullPassReplacedWhileItRanReportsNoChange()
    {
        _embedder.Answer = Original;
        await Check();
        var staleRead = await Stored();   // holds the record of the old model

        // A rebuild finishes and records the current model while the check is still asking.
        _embedder.Answer = At(0.5);
        (await ModelCanary.RecordAsync(
            _db, _embedder, staleRead, NullLogger.Instance, TestContext.Current.CancellationToken)).ShouldBeTrue();

        var outcome = await ModelCanary.CheckAsync(
            _db, _embedder, staleRead, NullLogger.Instance, TestContext.Current.CancellationToken);

        // Compared with the old record it would read as 0.5. The set holds the new model's vectors now.
        outcome.ShouldBe(CanaryOutcome.Superseded);
        (await Stored()).CanarySimilarity!.Value.ShouldBe(1.0, 1e-9);
        (await Changed(_db)).ShouldBeEmpty();
    }

    // ── One set's write does not reach another's ─────────────────────────────

    [Fact]
    public async Task ACheckOrARecordOfOneSetLeavesAnotherSetOnTheSameModelAlone()
    {
        await AddSet("s2", "fine");
        _embedder.Answer = Original;
        await Check("s1");
        await Check("s2");
        var other = await Stored("s2");

        // The model changes, and only s1 is checked: the two rows hold the same bytes, so a write that matched
        // on the record and not on the set would reach both.
        _embedder.Answer = At(0.5);
        (await Check("s1")).ShouldBe(CanaryOutcome.Changed);
        var afterCheck = await Stored("s2");
        afterCheck.CanarySimilarity.ShouldBe(other.CanarySimilarity);
        afterCheck.CanaryCheckedUtc.ShouldBe(other.CanaryCheckedUtc);

        (await ModelCanary.RecordAsync(
            _db, _embedder, await Stored("s1"), NullLogger.Instance, TestContext.Current.CancellationToken)).ShouldBeTrue();
        var afterRecord = await Stored("s2");
        afterRecord.CanaryVector.ShouldBe(other.CanaryVector, "recording s1 does not record s2");
        afterRecord.CanarySimilarity.ShouldBe(other.CanarySimilarity);
        (await Changed(_db)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ACheckOnAChangedSetUpdatesTheTimeItWasChecked()
    {
        _embedder.Answer = Original;
        await Check();
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.ChunkSets.ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanaryCheckedUtc, past), TestContext.Current.CancellationToken);

        _embedder.Answer = At(0.5);
        (await Check()).ShouldBe(CanaryOutcome.Changed);

        (await Stored()).CanaryCheckedUtc!.Value.ShouldBeGreaterThan(past);
    }

    [Fact]
    public async Task AnEmptyAnswerIsNotRecordedAsABaseline()
    {
        _embedder.Answer = [];

        (await Check()).ShouldBe(CanaryOutcome.Unavailable);

        (await Stored()).CanaryVector.ShouldBeNull();
    }

    public static TheoryData<float[]> UnusableAnswers => new()
    {
        new float[] { 0f, 0f, 0f, 0f },
        new float[] { float.NaN, 0f, 0f, 0f },
        new float[] { 1f, float.PositiveInfinity, 0f, 0f },
    };

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task AnAnswerThatCannotBeComparedWithItselfIsNotRecordedAsABaseline(float[] answer)
    {
        _embedder.Answer = answer;

        (await Check()).ShouldBe(CanaryOutcome.Unavailable);

        var set = await Stored();
        set.CanaryVector.ShouldBeNull();
        set.CanarySimilarity.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task AnAnswerThatCannotBeComparedWithItselfDoesNotReplaceAnEarlierRecord(float[] answer)
    {
        _embedder.Answer = Original;
        await Check();
        _embedder.Answer = At(0.5);
        await Check();

        _embedder.Answer = answer;
        (await ModelCanary.RecordAsync(
            _db, _embedder, await Stored(), NullLogger.Instance, TestContext.Current.CancellationToken)).ShouldBeFalse();

        // Still the first model's record, and still reported.
        (await Stored()).CanaryVector.ShouldBe(ModelCanary.Pack(Original));
        (await Changed(_db)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnUnusableAnswerToACheckAgainstARecordReadsAsAChange()
    {
        _embedder.Answer = Original;
        await Check();

        _embedder.Answer = [0f, 0f, 0f, 0f];
        (await Check()).ShouldBe(CanaryOutcome.Changed);

        (await Stored()).CanarySimilarity!.Value.ShouldBe(0.0);
        (await Changed(_db)).ShouldHaveSingleItem().Similarity.ShouldBe(0.0);
    }

    [Fact]
    public async Task ChangedSetsAreListedInCorpusAndSetOrderWhateverOrderTheyWereAdded()
    {
        await AddSet("s2", "zeta");
        await AddSet("s3", "alpha");
        await _db.ChunkSets.ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanarySimilarity, 0.5), TestContext.Current.CancellationToken);

        (await Changed(_db)).Select(c => c.Set).ShouldBe(["books:alpha", "books:default", "books:zeta"]);
    }

    // ── Recording after a full pass ─────────────────────────────────────────

    [Fact]
    public async Task RecordingReplacesTheBaselineAndClearsAChange()
    {
        _embedder.Answer = Original;
        await Check();
        _embedder.Answer = At(0.5);
        await Check();

        (await ModelCanary.RecordAsync(
            _db, _embedder, await Stored(), NullLogger.Instance, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        var set = await Stored();
        set.CanaryVector.ShouldBe(ModelCanary.Pack(At(0.5)));
        set.CanarySimilarity.ShouldBe(1.0);
        (await Changed(_db)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordingWhileTheProviderIsDownKeepsTheEarlierRecord()
    {
        _embedder.Answer = Original;
        await Check();
        _embedder.Answer = At(0.5);
        await Check();
        var before = await Stored();

        _embedder.Fault = new EmbeddingUnavailableException("connection refused");
        (await ModelCanary.RecordAsync(
            _db, _embedder, before, NullLogger.Instance, TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        var after = await Stored();
        after.CanaryVector.ShouldBe(before.CanaryVector);
        after.CanarySimilarity.ShouldBe(before.CanarySimilarity);
    }

    // ── What the health report lists ────────────────────────────────────────

    [Fact]
    public async Task OnlyASetBelowTheThresholdIsListed()
    {
        await AddSet("s2", "exact");
        await AddSet("s3", "just-under");
        await AddSet("s4", "unrecorded");
        await _db.ChunkSets.Where(s => s.Id == "s1").ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanarySimilarity, 1.0), TestContext.Current.CancellationToken);
        await _db.ChunkSets.Where(s => s.Id == "s2").ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanarySimilarity, ModelCanary.SameModel), TestContext.Current.CancellationToken);
        await _db.ChunkSets.Where(s => s.Id == "s3").ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanarySimilarity, ModelCanary.SameModel - 0.0001)
                  .SetProperty(s => s.CanaryCheckedUtc, DateTime.UtcNow), TestContext.Current.CancellationToken);

        (await Changed(_db)).Select(c => c.Set).ShouldBe(["books:just-under"]);
    }

    [Fact]
    public async Task ASetInACorpusTheCallerCannotReachIsNotListed()
    {
        await AddSet("s2", "theirs-set", corpusId: "c2");
        await _db.ChunkSets.ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanarySimilarity, 0.5), TestContext.Current.CancellationToken);

        (await Changed(_db, "c")).Select(c => c.Set).ShouldBe(["books:default"]);
        (await Changed(_db, "c2")).Select(c => c.Set).ShouldBe(["theirs:theirs-set"]);
        (await Changed(_db, "c", "c2")).Select(c => c.Set).ShouldBe(["books:default", "theirs:theirs-set"]);
        (await Changed(_db, "elsewhere")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoSetsOnOneModelAreListedSeparately()
    {
        // One built before the change and one after can disagree about the same model.
        await AddSet("s2", "fine");
        await _db.ChunkSets.Where(s => s.Id == "s1").ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanarySimilarity, 0.4), TestContext.Current.CancellationToken);
        await _db.ChunkSets.Where(s => s.Id == "s2").ExecuteUpdateAsync(
            u => u.SetProperty(s => s.CanarySimilarity, 1.0), TestContext.Current.CancellationToken);

        (await Changed(_db)).Select(c => c.Set).ShouldBe(["books:default"]);
    }

    /// <summary>Answers the canary with a scripted vector, or fails, and records what it was asked.</summary>
    private sealed class ScriptedEmbedder : IEmbeddingService
    {
        public float[] Answer { get; set; } = [1f, 0f, 0f, 0f];
        public Exception? Fault { get; set; }
        public List<(EmbeddingTarget Target, EmbedPurpose Purpose, string[] Inputs)> Calls { get; } = [];

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            Calls.Add((target, purpose, [.. inputs]));
            if (Fault is not null) throw Fault;
            return Task.FromResult<IReadOnlyList<float[]>>([Answer]);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            Task.FromResult(Answer.Length);
        public int KnownDimensions(EmbeddingTarget t) => Answer.Length;
    }
}

/// <summary>
/// The timer's check of every set, as <c>ModelCanaryService.RunTickAsync</c> runs it.
/// </summary>
public sealed class ModelCanaryServiceTicks : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private readonly PerModelEmbedder _embedder = new();

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEmbeddingService>(_embedder);
        services.AddDbContext<CatalogDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        db.Corpora.Add(new Corpus { Id = "c", Name = "books", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private CatalogDbContext NewContext() => new(_services.GetRequiredService<DbContextOptions<CatalogDbContext>>());

    private async Task AddSet(string id, string model, string provider = "ollama")
    {
        await using var db = NewContext();
        db.ChunkSets.Add(new ChunkSet
        {
            Id = id,
            CorpusId = "c",
            Name = id,
            EmbeddingModel = model,
            EmbeddingProvider = provider,
            CollectionName = $"dexicon_{id}",
            BoundaryMode = "blank-line",
            CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private ModelCanaryService Service() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ModelCanaryService>.Instance);

    private async Task<ChunkSet> Stored(string id)
    {
        await using var db = NewContext();
        return await db.ChunkSets.AsNoTracking().SingleAsync(s => s.Id == id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ATickRecordsABaselineForEverySet()
    {
        await AddSet("a", "embeddinggemma");
        await AddSet("b", "nomic-embed-text");

        await Service().RunTickAsync(TestContext.Current.CancellationToken);

        (await Stored("a")).CanaryVector.ShouldNotBeNull();
        (await Stored("b")).CanaryVector.ShouldNotBeNull();
    }

    [Fact]
    public async Task ATickReportsAModelThatChangedSinceTheLastOne()
    {
        await AddSet("a", "embeddinggemma");
        await Service().RunTickAsync(TestContext.Current.CancellationToken);

        _embedder.Answers["ollama/embeddinggemma"] = [0.6f, 0.8f, 0f, 0f];
        await Service().RunTickAsync(TestContext.Current.CancellationToken);

        await using var db = NewContext();
        var changed = (await SystemEndpoints.ChangedModelsAsync(db, ["c"], TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem();
        changed.Set.ShouldBe("books:a");
        changed.Similarity.ShouldBe(0.6, 1e-6);
    }

    [Fact]
    public async Task AModelThatDoesNotAnswerIsAskedOncePerTickWhateverTheNumberOfSetsOnIt()
    {
        await AddSet("a", "gone");
        await AddSet("b", "gone");
        await AddSet("c", "gone:latest");
        await AddSet("d", "embeddinggemma");
        _embedder.Down.Add("ollama/gone");

        await Service().RunTickAsync(TestContext.Current.CancellationToken);

        // The model that is down is asked once; `gone:latest` is the same model. The others are not held up.
        _embedder.Asked.Count(m => m == "gone" || m == "gone:latest").ShouldBe(1);
        (await Stored("d")).CanaryVector.ShouldNotBeNull();
        (await Stored("a")).CanaryVector.ShouldBeNull();
    }

    [Fact]
    public async Task ATickWithNoSetsDoesNothing()
    {
        await Service().RunTickAsync(TestContext.Current.CancellationToken);

        _embedder.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoProvidersServingOneModelNameAreAskedSeparately()
    {
        await AddSet("a", "gone", "ollama");
        await AddSet("b", "gone", "other");
        _embedder.Down.Add("ollama/gone");

        await Service().RunTickAsync(TestContext.Current.CancellationToken);

        (await Stored("a")).CanaryVector.ShouldBeNull();
        (await Stored("b")).CanaryVector.ShouldNotBeNull("the other provider's model of that name answered");
    }

    [Fact]
    public async Task AnUnexpectedFaultInOneSetDoesNotStopTheTick()
    {
        await AddSet("a", "broken");
        await AddSet("b", "embeddinggemma");
        _embedder.Throws.Add("ollama/broken");

        await Service().RunTickAsync(TestContext.Current.CancellationToken);

        (await Stored("a")).CanaryVector.ShouldBeNull();
        (await Stored("b")).CanaryVector.ShouldNotBeNull("the set after the one that failed is still checked");
    }

    [Fact]
    public void TheDefaultTimingIsOneMinuteThenSixHours()
    {
        ModelCanaryService.DefaultFirstCheckAfter.ShouldBe(TimeSpan.FromMinutes(1));
        ModelCanaryService.DefaultEvery.ShouldBe(TimeSpan.FromHours(6));
    }

    private static async Task<bool> WaitFor(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return await condition();
    }

    [Fact]
    public async Task TheServiceChecksOnItsTimerAndStopsWhenAskedTo()
    {
        await AddSet("a", "embeddinggemma");
        var service = new ModelCanaryService(
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ModelCanaryService>.Instance,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50));

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            (await WaitFor(async () => (await Stored("a")).CanaryVector is not null))
                .ShouldBeTrue("the first check recorded a baseline");

            _embedder.Answers["ollama/embeddinggemma"] = [0.6f, 0.8f, 0f, 0f];
            (await WaitFor(async () => (await Stored("a")).CanarySimilarity < ModelCanary.SameModel))
                .ShouldBeTrue("a later tick found the change");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var asked = _embedder.Asked.Count;
        await Task.Delay(250, TestContext.Current.CancellationToken);
        _embedder.Asked.Count.ShouldBe(asked, "a stopped service asks no more");
    }

    [Fact]
    public async Task AServiceStoppedBeforeItsFirstCheckChecksNothing()
    {
        await AddSet("a", "embeddinggemma");
        var service = new ModelCanaryService(
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ModelCanaryService>.Instance,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        _embedder.Asked.ShouldBeEmpty();
        (await Stored("a")).CanaryVector.ShouldBeNull();
    }

    /// <summary>
    /// One answer per model, and the models that fail, each keyed <c>provider/model</c>: <c>Down</c> as a model
    /// the provider does not have, <c>Throws</c> as a fault no embedding service reports.
    /// </summary>
    private sealed class PerModelEmbedder : IEmbeddingService
    {
        public Dictionary<string, float[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            var key = $"{target.Provider}/{target.CanonicalModel}";
            Asked.Add(target.Model);
            if (Throws.Contains(key)) throw new InvalidOperationException("the provider cannot be built");
            if (Down.Contains(key)) throw new EmbeddingUnavailableException("model not found");
            var answer = Answers.TryGetValue(key, out var a) ? a : [1f, 0f, 0f, 0f];
            return Task.FromResult<IReadOnlyList<float[]>>([answer]);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) => Task.FromResult(4);
        public int KnownDimensions(EmbeddingTarget t) => 4;
    }
}

/// <summary>
/// What a pass does with the canary: it checks before the set's first write, reports a change without
/// stopping, and takes the new model as the baseline only when a full pass finished cleanly.
/// </summary>
public sealed class ModelCanaryIndexing
{
    private static readonly float[] Original = [1f, 0f, 0f, 0f];
    private static readonly float[] Replaced = [0f, 1f, 0f, 0f];

    private static async Task<(IndexingHarness Harness, CanaryAwareEmbedder Embedder)> StartAsync()
    {
        var harness = await IndexingHarness.StartAsync("notes");
        await harness.WriteFileAsync("one.txt", IndexingHarness.Prose("first"));
        await harness.SeedCorpusAsync(SourceKind.Workspace);
        var embedder = new CanaryAwareEmbedder();
        harness.Embedder = embedder;
        return (harness, embedder);
    }

    private static async Task<ChunkSet> StoredSet(IndexingHarness harness)
    {
        await using var db = harness.NewContext();
        return await db.ChunkSets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFirstPassRecordsTheModelBeforeItEmbedsAnything()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Succeeded);
        embedder.Order.First().ShouldBe("canary");
        embedder.Order.ShouldContain("chunk");
        var set = await StoredSet(harness);
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Original));
        set.CanarySimilarity.ShouldBe(1.0);
    }

    [Fact]
    public async Task ARefreshAfterTheModelChangedStillIndexesAndReportsTheChange()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        embedder.Answer = Replaced;
        await harness.WriteFileAsync("two.txt", IndexingHarness.Prose("second"));
        var job = await harness.RunIndexAsync(JobKind.Refresh, cancel: TestContext.Current.CancellationToken);

        // Advisory: the pass is not failed or degraded by the change.
        job.State.ShouldBe(JobState.Succeeded);
        var set = await StoredSet(harness);
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Original), "a refresh leaves the files it did not touch as they were");
        set.CanarySimilarity!.Value.ShouldBe(0.0, 1e-9);

        await using var db = harness.NewContext();
        (await SystemEndpoints.ChangedModelsAsync(
            db, [IndexingHarness.CorpusId], TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem().Set.ShouldBe("notes:default");
    }

    [Theory]
    [InlineData(JobKind.Full)]
    [InlineData(JobKind.Rebuild)]
    public async Task AFullPassAfterTheModelChangedTakesTheNewModelAsTheBaseline(JobKind kind)
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        embedder.Answer = Replaced;

        var job = await harness.RunIndexAsync(kind, cancel: TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Succeeded);
        var set = await StoredSet(harness);
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Replaced));
        set.CanarySimilarity.ShouldBe(1.0);
        await using var db = harness.NewContext();
        (await SystemEndpoints.ChangedModelsAsync(
            db, [IndexingHarness.CorpusId], TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFullPassThatLeftAFileUnembeddedDoesNotTakeTheNewModelAsTheBaseline()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        embedder.Answer = Replaced;
        embedder.ChunksFail = true;

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        // The set still holds the old model's vectors for the file that did not re-embed.
        job.State.ShouldBe(JobState.Degraded);
        var set = await StoredSet(harness);
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Original));
        set.CanarySimilarity!.Value.ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public async Task ACancelledPassIsNotLoggedAsAFailedModelCheck()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        using var cts = new CancellationTokenSource();
        embedder.OnCanary = () => cts.Cancel();
        var logs = new RecordingLoggerFactory();

        var job = await harness.RunIndexAsync(JobKind.Full, log: logs.CreateLogger<CorpusIndexer>(), cancel: cts.Token);

        job.State.ShouldBe(JobState.Cancelled);
        logs.Lines.ShouldNotContain(l => l.Contains("the model check failed", StringComparison.Ordinal));
        embedder.Order.ShouldNotContain("chunk");
    }

    [Fact]
    public async Task AFullPassOnAnUnreachableSourceKeepsTheEarlierModel()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        embedder.Answer = Replaced;
        Directory.Delete(harness.SourceDirectory, recursive: true);

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        // The files of the set were not visited, so none of them was embedded by the current model.
        job.State.ShouldBe(JobState.Degraded);
        (await StoredSet(harness)).CanaryVector.ShouldBe(ModelCanary.Pack(Original));
    }

    [Fact]
    public async Task AFullPassThatLeftAFileOnItsEarlierVectorsDoesNotTakeTheNewModel()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        embedder.Answer = Replaced;
        // Not an embedding service failure, so the pass-wide embedding flag stays clear.
        embedder.ChunksFault = new InvalidOperationException("a fault in the reader");

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        job.FilesFailed.ShouldBe(1);
        var set = await StoredSet(harness);
        set.CanaryVector.ShouldBe(ModelCanary.Pack(Original));
        set.CanarySimilarity!.Value.ShouldBe(0.0, 1e-9);
        await using var db = harness.NewContext();
        (await CorpusIndexer.EarlierVectorsKeptAsync(db, set.Id, job.StartedUtc!.Value, TestContext.Current.CancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task AFullPassThatCouldNotRemoveAVanishedFilesVectorsDoesNotTakeTheNewModel()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        await harness.WriteFileAsync("two.txt", IndexingHarness.Prose("second"));
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        embedder.Answer = Replaced;
        File.Delete(Path.Combine(harness.SourceDirectory, "two.txt"));
        harness.Vectors.DeleteFailsForPath = "two.txt";

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        // The file is gone from disk and its state is still Indexed, with the old model's vectors in the store.
        job.State.ShouldBe(JobState.Succeeded);
        (await StoredSet(harness)).CanaryVector.ShouldBe(ModelCanary.Pack(Original));
        await using var db = harness.NewContext();
        (await CorpusIndexer.EarlierVectorsKeptAsync(
            db, (await StoredSet(harness)).Id, job.StartedUtc!.Value, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task AFileThatCanNeverBeReadDoesNotHoldBackTheRecord()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        // No trailer, which is what a truncated download is: the same on every read, and it never had chunks.
        var pdf = new byte[40_000];
        "%PDF-1.7\n"u8.CopyTo(pdf);
        Array.Fill(pdf, (byte)'x', 9, pdf.Length - 9);
        await File.WriteAllBytesAsync(Path.Combine(harness.SourceDirectory, "broken.pdf"), pdf, TestContext.Current.CancellationToken);
        await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);
        embedder.Answer = Replaced;

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        job.FilesFailed.ShouldBe(1, "the unreadable file fails again on every full pass");
        (await harness.StateOfAsync("broken.pdf")).Status.ShouldBe(FileStatus.Failed);
        (await StoredSet(harness)).CanaryVector.ShouldBe(ModelCanary.Pack(Replaced),
            "it holds no vectors of an earlier model, so it does not keep the notice up for good");
    }

    [Fact]
    public async Task APassIsNotFailedByACanaryThatCannotBeEmbedded()
    {
        var (harness, embedder) = await StartAsync();
        await using var _ = harness;
        embedder.CanaryFails = true;

        var job = await harness.RunIndexAsync(JobKind.Full, cancel: TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Succeeded);
        (await StoredSet(harness)).CanaryVector.ShouldBeNull();
        embedder.Order.ShouldContain("chunk");
    }

    /// <summary>Answers the canary text with a scripted vector and any other input with zeros.</summary>
    private sealed class CanaryAwareEmbedder : IEmbeddingService
    {
        public float[] Answer { get; set; } = Original;
        public bool CanaryFails { get; set; }
        public bool ChunksFail { get; set; }

        /// <summary>A fault no embedding service reports, thrown for a chunk: the pass records the file as failed.</summary>
        public Exception? ChunksFault { get; set; }

        /// <summary>Runs when the canary is asked, before it answers.</summary>
        public Action? OnCanary { get; set; }

        /// <summary>"canary" or "chunk" for each call, in the order made.</summary>
        public List<string> Order { get; } = [];

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget target, EmbedPurpose purpose,
            IReadOnlyList<string> inputs, string? source = null, CancellationToken ct = default)
        {
            if (IndexingHarness.IsCanary(inputs))
            {
                Order.Add("canary");
                OnCanary?.Invoke();
                ct.ThrowIfCancellationRequested();
                if (CanaryFails) throw new EmbeddingUnavailableException("canary refused");
                return Task.FromResult<IReadOnlyList<float[]>>([Answer]);
            }

            Order.Add("chunk");
            if (ChunksFault is not null) throw ChunksFault;
            if (ChunksFail) throw new EmbeddingUnavailableException("chunk refused");
            return Task.FromResult<IReadOnlyList<float[]>>([.. inputs.Select(_ => new float[768])]);
        }

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) => Task.FromResult(768);
        public int KnownDimensions(EmbeddingTarget t) => 768;
    }
}
