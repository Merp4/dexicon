namespace Dexicon.Tests;

/// <summary>Characters and splits for the tests that read log output, written as code points so the source holds none of them.</summary>
internal static class TestText
{
    /// <summary>The C1 control sequence introducer, U+009B.</summary>
    public static readonly string Csi = ((char)0x9B).ToString();

    /// <summary>The ways a reader ends a line: \n, \r, \r\n, NEL, and the Unicode separators.</summary>
    public static string[] Lines(string text) =>
        text.Split(["\r\n", "\n", "\r", ((char)0x85).ToString(), ((char)0x2028).ToString(), ((char)0x2029).ToString()],
            StringSplitOptions.None);
}
