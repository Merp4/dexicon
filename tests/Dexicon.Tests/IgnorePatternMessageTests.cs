using Dexicon.Core.Indexing;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The message of an <see cref="IgnorePatternException"/> reaches logs, a job's error text and <c>index_status</c>,
/// and all of it is text from the indexed tree: the line, and the directory names in the path of the file. The
/// whole message is cleaned of anything a log reader or a terminal acts on.
/// </summary>
public sealed class IgnorePatternMessageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"message-{Guid.NewGuid():N}");

    public IgnorePatternMessageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static TheoryData<string, char> Characters() => new()
    {
        { "line feed", '\n' },
        { "carriage return", '\r' },
        { "tab", '\t' },
        { "bell", '\u0007' },
        { "escape", '\u001b' },
        { "C1 next line", '\u0085' },
        { "C1 control sequence introducer", '\u009b' },
        { "line separator", '\u2028' },
        { "paragraph separator", '\u2029' },
        { "right-to-left override", '\u202E' },
        { "right-to-left isolate", '\u2067' },
        { "left-to-right mark", '\u200E' },
        { "arabic letter mark", '\u061C' },
        { "zero-width space", '\u200B' },
    };

    [Theory]
    [MemberData(nameof(Characters))]
    public void ACharacterInThePatternTextIsReplaced(string name, char c)
    {
        var thrown = Should.Throw<IgnorePatternException>(
            () => new IgnoreRuleSet().AddPatterns([$"[z-a]{c}x"], ".gitignore"), name);

        thrown.Message.ShouldBe(".gitignore line 1 ('[z-a]?x') cannot be compiled (reversed character range)");
    }

    [Theory]
    [MemberData(nameof(Characters))]
    public void ACharacterInTheFilesPathIsReplaced(string name, char c)
    {
        var thrown = Should.Throw<IgnorePatternException>(
            () => new IgnoreRuleSet().AddPatterns(["[z-a]"], $"dir{c}name/.dexiconignore"), name);

        thrown.Message.ShouldBe("dir?name/.dexiconignore line 1 ('[z-a]') cannot be compiled (reversed character range)");
    }

    [Theory]
    [MemberData(nameof(Characters))]
    public void ACharacterInAWholeFileFailureIsReplaced(string name, char c)
    {
        IgnorePatternException.ForFile($"dir{c}name/.dexiconignore", "cannot be read")
            .Message.ShouldBe("dir?name/.dexiconignore cannot be read", name);
    }

    [Fact]
    public void ADirectoryNamedWithABidiControlIsReplacedInTheMessageOfARealWalk()
    {
        var directory = Path.Combine(_root, "evil\u202Egnp.sub");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, WorkspaceWalker.IgnoreFileName), "[z-a]\n");

        Should.Throw<IgnorePatternException>(() => WorkspaceWalker.Walk(_root, true, null, null, 1_000_000))
            .Message.ShouldBe("evil?gnp.sub/.dexiconignore line 1 ('[z-a]') cannot be compiled (reversed character range)");
    }

    [Fact]
    public void ASkippedGitignoreLineIsCleanedInTheWarningOfARealWalk()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "[z-a]\u202E\u2028x\n");

        WorkspaceWalker.Walk(_root, true, null, null, 1_000_000).Warnings.ShouldBe(
            [".gitignore line 1 ('[z-a]??x') cannot be compiled (reversed character range); the line was skipped"]);
    }

    [Fact]
    public void TextWithNothingToReplaceIsReturnedAsItIs()
    {
        var text = "sub/.gitignore line 3 ('*.md') is fine, unicode kept: caf\u00E9 \u65E5\u672C";

        IgnorePatternException.Clean(text).ShouldBeSameAs(text);
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(text[i])) return true;
        }

        return false;
    }

    [Fact]
    public void TheCutOfALongPatternNeverSplitsASurrogatePair()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);

        // 99 characters and then a pair: the hundredth unit would be the pair's first half.
        var split = new string('a', 99) + emoji + "b[z-a]";
        var thrown = Should.Throw<IgnorePatternException>(() => new IgnoreRuleSet().AddPatterns([split], "test"));

        HasLoneSurrogate(thrown.Message).ShouldBeFalse();
        thrown.Message.ShouldStartWith($"test line 1 ('{new string('a', 99)}...')");

        // 98 and then a pair: the cut falls after the pair, which is kept whole.
        var fits = new string('a', 98) + emoji + "b[z-a]";
        Should.Throw<IgnorePatternException>(() => new IgnoreRuleSet().AddPatterns([fits], "test"))
            .Message.ShouldStartWith($"test line 1 ('{new string('a', 98)}{emoji}...')");
    }

    [Fact]
    public void ASurrogateWithNoPartnerIsReplaced()
    {
        var high = ((char)0xD83D).ToString();
        var low = ((char)0xDE00).ToString();

        IgnorePatternException.Clean($"a{high}b").ShouldBe("a?b");
        IgnorePatternException.Clean($"a{low}b").ShouldBe("a?b");
        IgnorePatternException.Clean($"{low}{high}").ShouldBe("??");
        IgnorePatternException.Clean($"a{high}{low}b").ShouldBe($"a{high}{low}b");
    }
}
