using Dexicon.Core.Extraction;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// Whitespace inside a <c>pre</c> is content, and the walker was collapsing it.
///
/// Everywhere else in an HTML document, source whitespace is layout: a paragraph broken
/// across three source lines is one paragraph, and collapsing it is what makes the text
/// read like the page. A <c>pre</c> is the one place the author chose the whitespace, and
/// it was getting the same treatment, so a code listing arrived as a single line with its
/// indentation gone.
///
/// The listing stayed findable, which is why this survived: the tokens are all still
/// there in order. What it cost was legibility in a search result, and the ability to
/// split a long listing at all, since the chunker splits on lines.
/// </summary>
public sealed class PreformattedTextTests
{
    private static List<string> Lines(string body)
    {
        using var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            $"<html><body>{body}</body></html>"));
        return new HtmlTextExtractor().Extract(s, "page.html").Text
            .Split('\n').Select(l => l.TrimEnd())
            .Where(l => l.Trim().Length > 0).ToList();
    }

    [Fact]
    public void AListingKeepsItsLines()
    {
        var lines = Lines("<p>Before.</p><pre><code>if (a) {\n    b();\n}</code></pre><p>After.</p>");

        lines.ShouldBe(["Before.", "if (a) {", "    b();", "}", "After."]);
    }

    [Fact]
    public void AListingKeepsItsIndentation()
    {
        // Indentation is the structure of the code. Without it a nested block is
        // indistinguishable from a flat one.
        var lines = Lines("<pre>def f():\n    if x:\n        return 1\n    return 0</pre>");

        lines.ShouldBe(["def f():", "    if x:", "        return 1", "    return 0"]);
    }

    [Fact]
    public void PreformattingIsInheritedByTheCodeElementInside()
    {
        // `pre > code` is how a listing is marked up nearly everywhere. Checking only the
        // element that holds the text would miss every one of them.
        Lines("<pre><code>one\ntwo</code></pre>").ShouldBe(["one", "two"]);
    }

    [Fact]
    public void ProseAroundItIsStillCollapsed()
    {
        // The change is confined to `pre`. A paragraph broken across source lines is one
        // line, as it has always been.
        Lines("<p>A sentence\n   split across\n   source lines.</p>")
            .ShouldBe(["A sentence split across source lines."]);
    }

    [Fact]
    public void ProseAfterAListingIsCollapsedAgain()
    {
        // The flag has to come back off on the way out, or everything after the first
        // listing in a chapter keeps its source line breaks.
        Lines("<pre>code\nhere</pre><p>Prose\nwrapped\nin source.</p>")
            .ShouldBe(["code", "here", "Prose wrapped in source."]);
    }

    [Fact]
    public void CarriageReturnsDoNotDoubleTheLines()
    {
        // A CRLF file is one line ending per line. Treating the CR as its own break puts
        // a blank line between every line of the listing.
        //
        // Asserted on the RAW split, blank lines included. Filtering them out first is
        // what the first version of this test did, and it could not see the doubling.
        using var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "<html><body><pre>one\r\ntwo\r\nthree</pre></body></html>"));
        var text = new HtmlTextExtractor().Extract(s, "page.html").Text;

        text.Trim().Split('\n').ShouldBe(["one", "two", "three"]);
    }

    [Fact]
    public void ALongListingIsNowSomethingTheChunkerCanSplit()
    {
        // The reason this matters beyond legibility. One line is one indivisible unit to a
        // line-accumulating chunker, whatever the budget says.
        var listing = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"    line{i}();"));

        Lines($"<pre>{listing}</pre>").Count.ShouldBe(400);
    }
}
