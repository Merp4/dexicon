using Dexicon.Api;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Two measurements of the same model, stored under two names.
///
/// The measurement table is keyed on (provider, model), and a model has more than one name:
/// `embeddinggemma` and `embeddinggemma:latest` are the same weights, and which one is
/// written depends on what the caller asked to probe. The UI sends the tagged name, the API
/// takes whatever it is given.
///
/// Listing the models normalised those names to one key and then keyed a dictionary on it,
/// which threw ArgumentException and took the whole models list to a 500 — a listing that
/// could not survive its own stored data, and reachable by doing nothing stranger than
/// probing the same model twice by its two names.
/// </summary>
public sealed class DuplicateModelMeasurementTests
{
    private sealed record Row(string Model, DateTime MeasuredUtc, int Tokens);

    /// <summary>The grouping the endpoint does, over the names it actually has to survive.</summary>
    private static Dictionary<string, Row> Collapse(IEnumerable<Row> rows) =>
        rows.GroupBy(x => ModelNames.Normalise(x.Model), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.MeasuredUtc).First(),
                StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TwoNamesForOneModelDoNotThrow()
    {
        var rows = new[]
        {
            new Row("embeddinggemma", DateTime.UtcNow.AddHours(-1), 2065),
            new Row("embeddinggemma:latest", DateTime.UtcNow.AddHours(-2), 2065),
        };

        Should.NotThrow(() => Collapse(rows)).Count.ShouldBe(1);
    }

    [Fact]
    public void TheMostRecentMeasurementWins()
    {
        // A re-probe is a correction, and the older row is what it corrects. Taking the
        // first row instead would show a number the model no longer gives.
        var rows = new[]
        {
            new Row("embeddinggemma:latest", DateTime.UtcNow.AddDays(-1), 2065),
            new Row("embeddinggemma", DateTime.UtcNow, 1365),
        };

        Collapse(rows)[ModelNames.Normalise("embeddinggemma")].Tokens.ShouldBe(1365);
    }

    [Fact]
    public void DifferentModelsStayDifferent()
    {
        // The collapse must not go so far that two models become one.
        var rows = new[]
        {
            new Row("embeddinggemma:latest", DateTime.UtcNow, 2065),
            new Row("nomic-embed-text:latest", DateTime.UtcNow, 768),
            new Row("mxbai-embed-large", DateTime.UtcNow, 665),
        };

        Collapse(rows).Count.ShouldBe(3);
    }

    [Fact]
    public void ANameThatDiffersOnlyInCaseIsTheSameModel()
    {
        var rows = new[]
        {
            new Row("EmbeddingGemma", DateTime.UtcNow.AddHours(-1), 1),
            new Row("embeddinggemma", DateTime.UtcNow, 2),
        };

        var collapsed = Collapse(rows);
        collapsed.Count.ShouldBe(1);
        collapsed.Values.Single().Tokens.ShouldBe(2);
    }

    [Fact]
    public void TheTagIsWhatNormalisationRemoves()
    {
        // The property the whole collapse rests on, asserted directly so a change to
        // Normalise cannot quietly reintroduce the crash.
        ModelNames.Normalise("embeddinggemma:latest")
            .ShouldBe(ModelNames.Normalise("embeddinggemma"), StringCompareShould.IgnoreCase);
    }
}
