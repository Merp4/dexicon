using Dexicon.Core.Embedding;
using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The chunk budget is set in tokens and spent in characters, and the rate of exchange is
/// a property of the text. Measured over a 96-book library it runs from 2.93 characters a
/// token in the code-heavy chapters of a programming book to 5.94 in plain prose.
///
/// One number for the whole library cannot serve both ends. The model's own measured 3.80
/// is an average over prose, code and JSON, so it sits above the dense end: at a
/// 7,782-character budget the dense files overflowed the model's 2,048 tokens and were
/// embedded with their tails missing, 220 times in one run. Sizing everything for the
/// dense end means a 5,997-character budget, and 5,460 characters retrieved measurably
/// worse than 7,480, so that trades a loss across every chunk to protect about 1.7%.
/// </summary>
public sealed class TextDensityTests
{
    private static readonly EmbeddingTarget Target = new("ollama", "m");

    private static string Text(int length, char fill = 'a') => new(fill, length);

    /// <summary>
    /// A token counter that answers however the test says, and records what it was asked.
    /// Embedding throws: measuring density must never cost a real embed.
    /// </summary>
    private sealed class Counter(Func<string, int?> reply) : IEmbeddingService
    {
        public List<string> Seen { get; } = [];

        public Task<int?> CountTokensAsync(EmbeddingTarget t, string text, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Seen.Add(text);
            return Task.FromResult(reply(text));
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingTarget t, EmbedPurpose p,
            IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            throw new NotSupportedException("measuring density must not embed");

        public Task<int> ProbeDimensionsAsync(EmbeddingTarget t, CancellationToken ct = default) =>
            Task.FromResult(768);

        public int KnownDimensions(EmbeddingTarget t) => 768;
    }

    /// <summary>One token per <paramref name="charsPerToken"/> characters.</summary>
    private static Counter At(double charsPerToken) =>
        new(text => (int)Math.Round(text.Length / charsPerToken));

    [Fact]
    public async Task ItReportsTheMeasuredRatio()
    {
        var measured = await TextDensity.MeasureAsync(At(2.93), Target, Text(40_000));

        measured.ShouldNotBeNull();
        measured.Value.ShouldBe(2.93, 0.05);
    }

    [Theory]
    [InlineData(true)]      // dense chapters at the front
    [InlineData(false)]     // dense chapters at the back
    public async Task TheDensestWindowWinsWhereverItIs(bool denseFirst)
    {
        // A programming book opens with prose and turns dense later, or the reverse. The
        // budget has to hold for the worst chunk in the file, so averaging over the book
        // would still overflow exactly where the file is hardest.
        //
        // Both orders, because a test with the dense region last cannot tell "the densest
        // window" from "the last window", and a mutant that kept only the last one survived
        // the version that did.
        var text = denseFirst
            ? Text(30_000, 'z') + Text(30_000, 'a')
            : Text(30_000, 'a') + Text(30_000, 'z');
        var byRegion = new Counter(w =>
            (int)Math.Round(w.Length / (w.Count(c => c == 'z') > w.Length / 2 ? 2.9 : 5.5)));

        var measured = await TextDensity.MeasureAsync(byRegion, Target, text);

        measured!.Value.ShouldBe(2.9, 0.1);
    }

    [Fact]
    public async Task ItSamplesTheBodyRatherThanTheOpening()
    {
        // Front matter and a contents page tokenize nothing like a chapter does.
        var probe = At(4.0);

        await TextDensity.MeasureAsync(probe, Target, Text(60_000));

        probe.Seen.Count.ShouldBe(3);
        probe.Seen.ShouldAllBe(w => w.Length == 3_000);
    }

    [Fact]
    public async Task TheWindowsAreSpreadThroughTheFile()
    {
        // Three reads of the same opening would cost three calls and learn one thing.
        var text = string.Concat(Enumerable.Range(0, 60).Select(i => new string((char)('a' + i % 26), 1_000)));
        var probe = At(4.0);

        await TextDensity.MeasureAsync(probe, Target, text);

        probe.Seen.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task AShortFileIsNotMeasured()
    {
        // Too short to sample, and too short to produce an over-long chunk either, so the
        // calls would buy nothing.
        var probe = At(3.0);

        (await TextDensity.MeasureAsync(probe, Target, Text(1_000))).ShouldBeNull();
        probe.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task AProviderThatReportsNoTokensGivesNoOpinion()
    {
        // Null is "no opinion", which leaves the caller on the model's ratio. Inventing a
        // number here would size every chunk in the file from a guess.
        (await TextDensity.MeasureAsync(new Counter(_ => null), Target, Text(40_000))).ShouldBeNull();
    }

    [Fact]
    public async Task ADensityOfZeroIsNotAMeasurement()
    {
        // It reaches a division, and then a multiply that sizes every chunk in the file.
        (await TextDensity.MeasureAsync(new Counter(_ => 0), Target, Text(40_000))).ShouldBeNull();
    }

    [Fact]
    public async Task AProviderThatThrowsDoesNotFailTheFile()
    {
        // Indexing the file is the job; knowing its density exactly is an optimisation on
        // top, and an optimisation must not be able to fail the thing it optimises.
        var down = new Counter(_ => throw new InvalidOperationException("provider is down"));

        (await TextDensity.MeasureAsync(down, Target, Text(40_000))).ShouldBeNull();
    }

    [Fact]
    public async Task CancellationIsNotSwallowedAsAFailedMeasurement()
    {
        // A cancelled job has to stop, not quietly carry on having formed no opinion. The
        // catch that absorbs a provider failure is wide enough to take this one too.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            TextDensity.MeasureAsync(new Counter(_ => 1), Target, Text(40_000), null, cts.Token));
    }
}
