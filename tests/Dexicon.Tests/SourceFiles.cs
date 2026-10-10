namespace Dexicon.Tests;

/// <summary>The repository's source files, for the tests that read code to hold a rule no behaviour can reach.</summary>
internal static class SourceFiles
{
    /// <summary>
    /// The path of a file under the repository root, found by walking up from the test binary. It throws when
    /// the file is not there, so a change of layout fails the test that reads it and is not an empty scan.
    /// </summary>
    public static string Find(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"{Path.Combine(parts)} was not found above {AppContext.BaseDirectory}.");
    }
}
