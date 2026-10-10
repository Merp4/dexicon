using System.Text;
using Dexicon.Infrastructure;

namespace Dexicon.Tests;

/// <summary>
/// The rules the log sink, <c>DexiconAuthMiddleware.OneLine</c> and the tools' echo share. Characters are
/// written as code points, so the source holds none of them.
/// </summary>
public sealed class LogTextTests
{
    private static readonly string Marker = LogText.Marker.ToString();
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);
    private static readonly string Zwsp = char.ConvertFromUtf32(0x200B);
    private static readonly string Tag = char.ConvertFromUtf32(0xE0041);
    private static readonly string LoneHigh = ((char)0xD800).ToString();
    private static readonly string LoneLow = ((char)0xDC00).ToString();

    [Fact]
    public void ATextWithNothingToReplaceIsTheSameInstance()
    {
        var text = "plain " + Emoji + " text\t";

        LogText.Neutralise(text, includeC0: false).ShouldBeSameAs(text);
    }

    [Fact]
    public void AValidPairAfterAReplacedCharacterIsKeptWhole()
    {
        LogText.Neutralise(Zwsp + Emoji + "x", includeC0: false).ShouldBe(Marker + Emoji + "x");
    }

    [Fact]
    public void ASupplementaryCharacterThatIsHostileIsOneMarker()
    {
        LogText.Neutralise("a" + Tag + "b", includeC0: false).ShouldBe("a" + Marker + "b");
        LogText.Neutralise(Tag + Emoji, includeC0: false).ShouldBe(Marker + Emoji);
    }

    [Fact]
    public void ALoneSurrogateIsOneMarkerAndTheCharacterAfterItIsRead()
    {
        LogText.Neutralise(LoneHigh + Emoji, includeC0: false).ShouldBe(Marker + Emoji);
        LogText.Neutralise(LoneLow + "b", includeC0: false).ShouldBe(Marker + "b");
        LogText.Neutralise("a" + LoneHigh, includeC0: false).ShouldBe("a" + Marker);
        LogText.Neutralise(Zwsp + LoneHigh + LoneHigh + "c", includeC0: false).ShouldBe(Marker + Marker + Marker + "c");
    }

    [Fact]
    public void TheC0ControlsAreReplacedOnlyWhenAskedFor()
    {
        LogText.Neutralise("a\tb\nc", includeC0: false).ShouldBe("a\tb\nc");
        LogText.Neutralise("a\tb\nc", includeC0: true).ShouldBe("a" + Marker + "b" + Marker + "c");
    }

    [Theory]
    [InlineData(0x7F, true)]
    [InlineData(0x80, true)]
    [InlineData(0x85, true)]
    [InlineData(0x9B, true)]
    [InlineData(0x9F, true)]
    [InlineData(0xA0, false)]
    [InlineData(0xAD, true)]
    [InlineData(0x200B, true)]
    [InlineData(0x200D, true)]
    [InlineData(0x2028, true)]
    [InlineData(0x2029, true)]
    [InlineData(0x202E, true)]
    [InlineData(0x2066, true)]
    [InlineData(0xFEFF, true)]
    [InlineData(0xE0041, true)]
    [InlineData(0x41, false)]
    [InlineData(0x20, false)]
    [InlineData(0xE9, false)]
    [InlineData(0x3000, false)]
    [InlineData(0x1F600, false)]
    public void EachKindOfCharacterIsClassifiedByItsCategory(int codePoint, bool hostile)
    {
        LogText.IsHostile(new Rune(codePoint), includeC0: false).ShouldBe(hostile);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x1B)]
    [InlineData(0x1F)]
    public void AC0ControlIsHostileOnlyWhenAskedFor(int codePoint)
    {
        LogText.IsHostile(new Rune(codePoint), includeC0: true).ShouldBeTrue();
        LogText.IsHostile(new Rune(codePoint), includeC0: false).ShouldBeFalse();
        LogText.IsHostile(new Rune(0x20), includeC0: true).ShouldBeFalse();
    }

    [Fact]
    public void ACutDoesNotSplitASurrogatePair()
    {
        var text = "aaa" + Emoji + "bbb";

        LogText.Cut(text, 4).ShouldBe("aaa...");
        LogText.Cut(text, 5).ShouldBe("aaa" + Emoji + "...");
        LogText.Cut(text, 3).ShouldBe("aaa...");
    }

    [Fact]
    public void ATextAtTheLimitIsTheSameInstanceAndOneOverIsCut()
    {
        var text = new string('a', 10);

        LogText.Cut(text, 10).ShouldBeSameAs(text);
        LogText.Cut(text, 9).ShouldBe(new string('a', 9) + "...");
    }

    [Fact]
    public void AnEchoCutsBeforeItHoldsTheTextToOneLineAndDoesNotSplitAPair()
    {
        var text = new string('a', 3) + Emoji + new string('b', 10);

        LogText.Echo(text, 4).ShouldBe("aaa...");
        LogText.Echo(text, 5).ShouldBe("aaa" + Emoji + "...");
        LogText.Echo("a\r\nb", 200).ShouldBe("a b");
        LogText.Echo(null, 200).ShouldBe(string.Empty);
        LogText.Echo(new string('z', 10_000_000), 200).Length.ShouldBe(203);
    }

    [Fact]
    public void ALineBreakInAValueBecomesASpaceAndACrlfIsOne()
    {
        LogText.OneLine("a\r\nb\nc\rd").ShouldBe("a b c d");
        LogText.OneLine("a" + char.ConvertFromUtf32(0x85) + "b").ShouldBe("a b");
        LogText.OneLine("a" + char.ConvertFromUtf32(0x2028) + "b").ShouldBe("a b");
    }
}
