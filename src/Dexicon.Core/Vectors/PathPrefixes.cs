namespace Dexicon.Core.Vectors;

/// <summary>
/// The directories of a file path, kept on every chunk so that "files under this path" is an exact keyword
/// match. Qdrant has no prefix match on a keyword field, and the text match that <c>pathPrefix</c> used to
/// send is a match on whole words anywhere in the path, which finds <c>lib/src/Auth/b.cs</c> for
/// <c>src/Auth</c>.
///
/// A chunk of <c>src/Auth/a.cs</c> carries <c>src</c>, <c>src/auth</c> and <c>src/auth/a.cs</c>, so a value
/// that names a folder or a file matches by equality. Both sides are lower-cased, which keeps the
/// case-insensitive match the word search had. A value that stops inside a name (<c>src/Au</c>) matches
/// nothing: a prefix is a whole path segment.
/// </summary>
internal static class PathPrefixes
{
    /// <summary>The payload field, a keyword index on an array of strings.</summary>
    public const string Field = "path_prefixes";

    /// <summary>
    /// The segments of a path as both sides compare them: backslashes as slashes, each segment trimmed, no empty or
    /// <c>.</c> segment. <see cref="Of"/> and <see cref="Normalise"/> both read a path through this, so a stored path
    /// and an argument that name the same file give the same prefixes.
    /// </summary>
    private static string[] Segments(string path) =>
        path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s != ".")
            .ToArray();

    /// <summary>The folders above a path and the path itself, lower-cased, slash-separated; empty for a blank path.</summary>
    public static string[] Of(string filePath)
    {
        var segments = Segments(filePath);
        var prefixes = new string[segments.Length];
        var path = "";
        for (var i = 0; i < segments.Length; i++)
        {
            path = i == 0 ? segments[i] : $"{path}/{segments[i]}";
            prefixes[i] = path.ToLowerInvariant();
        }

        return prefixes;
    }

    /// <summary>
    /// A <c>pathPrefix</c> argument as it is compared: backslashes as slashes, no empty or <c>.</c> segment, lower-cased.
    /// Null when it names no path (blank, <c>/</c>, <c>./</c>), which adds no condition.
    /// </summary>
    public static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var segments = Segments(value);
        return segments.Length == 0 ? null : string.Join('/', segments).ToLowerInvariant();
    }
}
