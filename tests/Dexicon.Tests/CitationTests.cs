using Dexicon.Core.Search;

namespace Dexicon.Tests;

/// <summary>
/// A hit's <c>Location</c> is the part of a search result that gets repeated verbatim —
/// pasted into an editor, quoted back by a model, cited in an answer. A wrong one
/// propagates further than a wrong score ever will.
/// </summary>
public sealed class CitationTests
{
    private static SearchHit Hit(string path, int? page = null, int start = 1, int end = 1) =>
        new() { CorpusId = "c", FilePath = path, Content = "x", Page = page, StartLine = start, EndLine = end };

    [Fact]
    public void ACodeHitCitesFileAndLineRange() =>
        Hit("src/Auth/TokenService.cs", start: 40, end: 72).Location
            .ShouldBe("src/Auth/TokenService.cs:40-72");

    [Fact]
    public void ASingleLineHitOmitsTheRange() =>
        Hit("src/Program.cs", start: 12, end: 12).Location.ShouldBe("src/Program.cs:12");

    [Fact]
    public void APdfCitesAPage() =>
        // #page= is a real convention: PDF viewers honour it.
        Hit("handbook.pdf", page: 34).Location.ShouldBe("handbook.pdf#page=34");

    [Fact]
    public void AnEpubCitesAChapterRatherThanAPage() =>
        // Regression: this said "#page=14" of a book that has no page 14.
        Hit("testing.epub", page: 14).Location.ShouldBe("testing.epub#chapter=14");

    [Fact]
    public void APowerPointCitesASlide() =>
        Hit("kickoff.pptx", page: 3).Location.ShouldBe("kickoff.pptx#slide=3");

    [Fact]
    public void ExtensionCasingDoesNotChangeTheUnit() =>
        Hit("Testing.EPUB", page: 2).Location.ShouldBe("Testing.EPUB#chapter=2");

    [Fact]
    public void ADocumentWithoutAUnitFallsBackToLines() =>
        // DOCX has no page count available from the file, so it has no unit number.
        Hit("notes.docx", start: 5, end: 9).Location.ShouldBe("notes.docx:5-9");
}
