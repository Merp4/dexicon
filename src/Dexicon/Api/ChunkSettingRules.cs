using System.Text.RegularExpressions;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;

namespace Dexicon.Api;

/// <summary>The chunk settings a <see cref="ChunkSettingProblem"/> is about.</summary>
[Flags]
internal enum ChunkSettingFields
{
    None = 0,
    Size = 1,
    Overlap = 2,
    Mode = 4,
    Pattern = 8,
}

/// <summary>
/// A chunk set's cutting settings, refused. <see cref="Detail"/> is null where the title says all there
/// is to say, which is how the chunk-set endpoints have always answered those.
/// </summary>
/// <param name="Fields">The settings the refusal is about, so a caller can tell where each came from.</param>
internal sealed record ChunkSettingProblem(string Title, string? Detail, int Status, ChunkSettingFields Fields)
{
    /// <summary>The problem response, as the chunk-set endpoints send it.</summary>
    public IResult ToResult() => Results.Problem(title: Title, detail: Detail, statusCode: Status);

    /// <summary>
    /// The same refusal for <see cref="CorpusConfiguration"/>, whose callers (including the MCP tools) show
    /// only the detail, so a title with no detail of its own is repeated there. <see cref="ConfigRefusal.ToResult"/>
    /// sends no detail for it again, so a REST caller does not read the sentence twice.
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
    internal const int MinChunkSize = CodeChunker.MinChunkTokens;
    internal const int MaxChunkSize = CodeChunker.MaxChunkTokens;

    /// <summary>
    /// Null when the settings are acceptable. The rules are checked in this order, and the first to fail is
    /// the one reported: the size range, a negative overlap, an overlap not below the size, the boundary mode,
    /// then the custom pattern.
    /// </summary>
    /// <param name="customPattern">Read only for boundary mode <c>custom</c>.</param>
    internal static ChunkSettingProblem? Check(int size, int overlap, string? boundaryMode, string? customPattern)
    {
        if (size is < MinChunkSize or > MaxChunkSize)
            return new ChunkSettingProblem(
                $"chunkSize must be between {MinChunkSize} and {MaxChunkSize} tokens", null, 400, ChunkSettingFields.Size);

        if (overlap < 0)
            return new ChunkSettingProblem("chunkOverlap cannot be negative", null, 400, ChunkSettingFields.Overlap);

        if (overlap >= size)
            return new ChunkSettingProblem(
                "chunkOverlap must be smaller than chunkSize",
                $"Asked for overlap {overlap} with size {size}.",
                400,
                ChunkSettingFields.Overlap | ChunkSettingFields.Size);

        if (boundaryMode is not ("none" or "blank-line" or "language-aware" or "custom"))
            return new ChunkSettingProblem(
                "Unknown boundary mode",
                $"'{boundaryMode}'. Expected none, blank-line, language-aware or custom.",
                400,
                ChunkSettingFields.Mode);

        if (boundaryMode == "custom")
        {
            if (string.IsNullOrWhiteSpace(customPattern))
                return new ChunkSettingProblem(
                    "customBoundaryPattern is required for boundary mode 'custom'", null, 400,
                    ChunkSettingFields.Mode | ChunkSettingFields.Pattern);

            try
            {
                // Compiled here so a bad pattern fails on the request that set it, rather
                // than part-way through an indexing job an hour later.
                _ = new Regex(customPattern, RegexOptions.Multiline, TimeSpan.FromMilliseconds(500));
            }
            catch (ArgumentException ex)
            {
                return new ChunkSettingProblem(
                    "Invalid custom boundary pattern", ex.Message, 400, ChunkSettingFields.Mode | ChunkSettingFields.Pattern);
            }
        }

        return null;
    }

    /// <summary>
    /// The settings a new corpus's default set is made with: each from the request when it sent one and
    /// from the server's configuration when it did not.
    /// </summary>
    internal readonly record struct Resolved(int Size, int Overlap, string Mode);

    /// <summary>
    /// Judges the settings a new corpus would be made with, and says whose they are when they are refused.
    ///
    /// A creation carries no custom pattern, so mode <c>custom</c> is refused here whoever chose it.
    /// A request value that fails is answered 400 in the chunk-set endpoints' words. When the request's own
    /// values are fine with usable defaults in place of the server's, the server's setting is in the
    /// failure: 503 when the server's settings are unusable by themselves, so the cause is the
    /// configuration, and 400 naming the setting when only the request's value beside it is too much.
    /// </summary>
    internal static ConfigRefusal? CheckNewCorpus(
        int? size, int? overlap, string? mode, IndexingOptions server, out Resolved resolved)
    {
        resolved = new Resolved(size ?? server.ChunkSize, overlap ?? server.ChunkOverlap, mode ?? server.BoundaryMode);
        if (Judge(resolved) is not { } problem) return null;

        var usable = new IndexingOptions();
        var requestOnly = new Resolved(size ?? usable.ChunkSize, overlap ?? usable.ChunkOverlap, mode ?? usable.BoundaryMode);
        if (Judge(requestOnly) is { } theirs) return theirs.ToRefusal();

        var fromServer = problem.Fields
            & ((size is null ? ChunkSettingFields.Size : 0)
               | (overlap is null ? ChunkSettingFields.Overlap : 0)
               | (mode is null ? ChunkSettingFields.Mode : 0));
        var settings = Describe(fromServer, server);
        var serverAlone = Judge(new Resolved(server.ChunkSize, server.ChunkOverlap, server.BoundaryMode));

        if (serverAlone is not null)
            return new ConfigRefusal(
                "Server chunk settings unusable",
                $"{Sentence(problem.Detail ?? problem.Title)} The value comes from the server setting {settings}, not from "
                + "the request. Nothing was created. Correct the setting in the server's environment and restart, or "
                + "send chunkSize, chunkOverlap and boundaryMode with the request.",
                503);

        return new ConfigRefusal(
            problem.Title,
            $"{Sentence(problem.Detail ?? problem.Title)} Part of it is the server setting {settings}; send that "
            + "value with the request to override it.",
            400);
    }

    /// <summary>
    /// Why the server's configured settings cannot make a corpus, or null when they can. Written to the log
    /// at startup, so the setting is named before the first creation is refused.
    /// </summary>
    internal static ConfigRefusal? CheckServerDefaults(IndexingOptions server) =>
        CheckNewCorpus(null, null, null, server, out _);

    /// <summary>
    /// A creation carries no pattern, so <c>custom</c> there is refused with the route that can set one.
    /// The chunk-set endpoints keep <see cref="Check"/>'s wording, which names a field they do take.
    /// </summary>
    private static ChunkSettingProblem? Judge(Resolved settings)
    {
        var problem = Check(settings.Size, settings.Overlap, settings.Mode, customPattern: null);
        if (problem is not null && settings.Mode == "custom" && problem.Fields.HasFlag(ChunkSettingFields.Pattern))
            return new ChunkSettingProblem(
                "boundaryMode 'custom' cannot be set when a corpus is created",
                "A custom boundary needs a customBoundaryPattern, which creating a corpus does not take. Create the "
                + "corpus with another mode, then send boundaryMode 'custom' and customBoundaryPattern with "
                + "PATCH /api/corpora/{name}/chunk-sets/default.",
                400,
                ChunkSettingFields.Mode);

        return problem;
    }

    private static string Sentence(string text) => text.EndsWith('.') ? text : text + ".";

    private static string Describe(ChunkSettingFields fields, IndexingOptions server)
    {
        var named = new List<string>();
        if (fields.HasFlag(ChunkSettingFields.Size)) named.Add($"DEXICON__INDEXING__CHUNKSIZE={server.ChunkSize}");
        if (fields.HasFlag(ChunkSettingFields.Overlap)) named.Add($"DEXICON__INDEXING__CHUNKOVERLAP={server.ChunkOverlap}");
        if (fields.HasFlag(ChunkSettingFields.Mode)) named.Add($"DEXICON__INDEXING__BOUNDARYMODE={server.BoundaryMode}");
        return string.Join(", ", named);
    }
}
