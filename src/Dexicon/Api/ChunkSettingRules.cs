using System.Text.RegularExpressions;

namespace Dexicon.Api;

/// <summary>
/// A chunk set's cutting settings, refused. <see cref="Detail"/> is null where the title says all there
/// is to say, which is how the chunk-set endpoints have always answered those.
/// </summary>
internal sealed record ChunkSettingProblem(string Title, string? Detail, int Status)
{
    /// <summary>The problem response, as the chunk-set endpoints send it.</summary>
    public IResult ToResult() => Results.Problem(title: Title, detail: Detail, statusCode: Status);

    /// <summary>
    /// The same refusal for <see cref="CorpusConfiguration"/>, whose callers (including the MCP tools) show
    /// only the detail, so a title with no detail of its own is repeated there.
    /// </summary>
    public ConfigRefusal ToRefusal() => new(Title, Detail ?? Title, Status);
}

/// <summary>
/// The rules for a chunk set's size, overlap and boundary mode, kept in one place because every path that
/// stores them needs all of them: <c>POST</c> and <c>PATCH</c> on a corpus's chunk sets, and the default
/// set that creating a corpus makes.
/// </summary>
internal static class ChunkSettingRules
{
    internal const int MinChunkSize = 64;
    internal const int MaxChunkSize = 8192;

    /// <summary>Null when the settings are acceptable.</summary>
    /// <param name="customPattern">Read only for boundary mode <c>custom</c>.</param>
    internal static ChunkSettingProblem? Check(int size, int overlap, string? boundaryMode, string? customPattern)
    {
        if (size is < MinChunkSize or > MaxChunkSize)
            return new ChunkSettingProblem(
                $"chunkSize must be between {MinChunkSize} and {MaxChunkSize} tokens", null, 400);

        if (overlap < 0)
            return new ChunkSettingProblem("chunkOverlap cannot be negative", null, 400);

        if (overlap >= size)
            return new ChunkSettingProblem(
                "chunkOverlap must be smaller than chunkSize",
                $"Asked for overlap {overlap} with size {size}.",
                400);

        if (boundaryMode is not ("none" or "blank-line" or "language-aware" or "custom"))
            return new ChunkSettingProblem(
                "Unknown boundary mode",
                $"'{boundaryMode}'. Expected none, blank-line, language-aware or custom.",
                400);

        if (boundaryMode == "custom")
        {
            if (string.IsNullOrWhiteSpace(customPattern))
                return new ChunkSettingProblem(
                    "customBoundaryPattern is required for boundary mode 'custom'", null, 400);

            try
            {
                // Compiled here so a bad pattern fails on the request that set it, rather
                // than part-way through an indexing job an hour later.
                _ = new Regex(customPattern, RegexOptions.Multiline, TimeSpan.FromMilliseconds(500));
            }
            catch (ArgumentException ex)
            {
                return new ChunkSettingProblem("Invalid custom boundary pattern", ex.Message, 400);
            }
        }

        return null;
    }
}
