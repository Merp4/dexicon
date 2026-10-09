using Dexicon.Api;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Dexicon.Tests;

/// <summary>
/// The rules a chunk set's settings are judged by, called directly. The custom pattern cannot be sent
/// when a corpus is created, so its cases are here and on the chunk-set endpoints.
/// </summary>
public sealed class ChunkSettingRulesTests
{
    [Theory]
    [InlineData(64, 0, "none")]
    [InlineData(256, 255, "blank-line")]
    [InlineData(8192, 32, "language-aware")]
    public void SettingsOnTheEdgeOfTheRangesAreAccepted(int size, int overlap, string mode) =>
        ChunkSettingRules.Check(size, overlap, mode, customPattern: null).ShouldBeNull();

    [Theory]
    [InlineData("^#{1,3} ")]
    [InlineData(@"\n\n+")]
    public void ACustomModeWithACompilablePatternIsAccepted(string pattern) =>
        ChunkSettingRules.Check(256, 32, "custom", pattern).ShouldBeNull();

    [Theory]
    [InlineData("[z-a]")]
    [InlineData("(unclosed")]
    [InlineData("*leading")]
    public void ACustomModeWithAnUncompilablePatternIsRefusedWithTheParserMessage(string pattern)
    {
        var problem = ChunkSettingRules.Check(256, 32, "custom", pattern).ShouldNotBeNull();

        (problem.Status, problem.Title).ShouldBe((400, "Invalid custom boundary pattern"));
        problem.Detail.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ACustomModeWithNoPatternIsRefused(string? pattern)
    {
        var problem = ChunkSettingRules.Check(256, 32, "custom", pattern).ShouldNotBeNull();

        (problem.Status, problem.Title, problem.Detail)
            .ShouldBe((400, "customBoundaryPattern is required for boundary mode 'custom'", null));
    }

    [Fact]
    public void ThePatternIsNotReadUnlessTheModeIsCustom() =>
        ChunkSettingRules.Check(256, 32, "blank-line", "[z-a]").ShouldBeNull();

    [Fact]
    public void ANullBoundaryModeIsUnknown() =>
        ChunkSettingRules.Check(256, 32, null, null).ShouldNotBeNull().Title.ShouldBe("Unknown boundary mode");

    [Fact]
    public void TheRangeIsCheckedBeforeTheOverlap()
    {
        // A size of 3 with an overlap of 5 is wrong twice. The size is what the chunk-set endpoints named first.
        ChunkSettingRules.Check(3, 5, "none", null).ShouldNotBeNull().Title
            .ShouldBe("chunkSize must be between 64 and 8192 tokens");
    }

    [Fact]
    public void ARefusalWithNoDetailRepeatsItsTitleForTheConfigureTools()
    {
        var problem = ChunkSettingRules.Check(10, 0, "none", null).ShouldNotBeNull();

        problem.Detail.ShouldBeNull();
        problem.ToRefusal().Detail.ShouldBe(problem.Title);
        problem.ToRefusal().Status.ShouldBe(400);
    }

    [Fact]
    public void AResultCarriesTheTitleAndStatusAndNoInventedDetail()
    {
        var result = ChunkSettingRules.Check(10, 0, "none", null).ShouldNotBeNull().ToResult();

        var details = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;
        (details.Status, details.Title, details.Detail).ShouldBe((400, "chunkSize must be between 64 and 8192 tokens", null));
    }
}
