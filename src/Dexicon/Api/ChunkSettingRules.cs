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
    /// The settings a new set is made with: each from the request when it sent one and from the server's
    /// configuration when it did not.
    /// </summary>
    internal readonly record struct Resolved(int Size, int Overlap, string Mode);

    /// <summary>
    /// Judges the settings a new corpus's default set would be made with, and says whose they are when they
    /// are refused. A creation carries no custom pattern, so mode <c>custom</c> is refused whoever chose it.
    /// </summary>
    internal static ConfigRefusal? CheckNewCorpus(
        int? size, int? overlap, string? mode, IndexingOptions server, out Resolved resolved) =>
        CheckNew(size, overlap, mode, customPattern: null, carriesPattern: false, server, out resolved);

    /// <summary>
    /// Judges the settings a new set would be made with when it takes them from the server's configuration
    /// (a corpus with no set to inherit from). The request can carry a pattern.
    /// </summary>
    internal static ConfigRefusal? CheckNewSet(
        int? size, int? overlap, string? mode, string? customPattern, IndexingOptions server, out Resolved resolved) =>
        CheckNew(size, overlap, mode, customPattern, carriesPattern: true, server, out resolved);

    /// <summary>
    /// The refusal for settings made of a request's values and the server's, said in the order that finds
    /// whose fault it is:
    /// <list type="number">
    /// <item>A value the request sent that is wrong on its own: 400 in the chunk-set endpoints' words.</item>
    /// <item>A server setting the request did not override that is wrong on its own: 503 naming each such
    /// setting with its value, because the cause is the configuration.</item>
    /// <item>Values that are each fine and fail together (an overlap not below the size). Both from the
    /// request: 400. Both from the server: 503 naming both. One of each: 400 that quotes the values in
    /// force and names the server's setting.</item>
    /// </list>
    /// </summary>
    private static ConfigRefusal? CheckNew(
        int? size, int? overlap, string? mode, string? customPattern, bool carriesPattern,
        IndexingOptions server, out Resolved resolved)
    {
        resolved = new Resolved(size ?? server.ChunkSize, overlap ?? server.ChunkOverlap, mode ?? server.BoundaryMode);

        foreach (var field in new[] { ChunkSettingFields.Size, ChunkSettingFields.Overlap, ChunkSettingFields.Mode })
            if (IsSent(field, size, overlap, mode)
                && Own(field, size ?? 0, overlap ?? 0, mode ?? string.Empty, carriesPattern) is { } theirs)
                return theirs.ToRefusal();

        var broken = new List<(ChunkSettingFields Field, ChunkSettingProblem Problem)>();
        foreach (var field in new[] { ChunkSettingFields.Size, ChunkSettingFields.Overlap, ChunkSettingFields.Mode })
            if (!IsSent(field, size, overlap, mode)
                && Own(field, server.ChunkSize, server.ChunkOverlap, server.BoundaryMode, carriesPattern) is { } ours)
                broken.Add((field, ours));

        if (broken.Count > 0)
        {
            var fields = broken.Aggregate(ChunkSettingFields.None, (all, b) => all | b.Field);
            return ServerRefusal(broken[0].Problem, fields, server);
        }

        if (Check(resolved.Size, resolved.Overlap, resolved.Mode, customPattern) is not { } together) return null;

        var fromServer = together.Fields
            & ((size is null ? ChunkSettingFields.Size : 0)
               | (overlap is null ? ChunkSettingFields.Overlap : 0)
               | (mode is null ? ChunkSettingFields.Mode : 0));
        if (fromServer == ChunkSettingFields.None) return together.ToRefusal();
        if ((together.Fields & ~fromServer) == ChunkSettingFields.None) return ServerRefusal(together, fromServer, server);

        return new ConfigRefusal(
            together.Title,
            $"{Sentence(together.Detail ?? together.Title)} {Describe(fromServer, server)} is the server's setting, and "
            + "the request did not send its own; send that setting to change it.",
            400);
    }

    private static bool IsSent(ChunkSettingFields field, int? size, int? overlap, string? mode) => field switch
    {
        ChunkSettingFields.Size => size is not null,
        ChunkSettingFields.Overlap => overlap is not null,
        _ => mode is not null,
    };

    /// <summary>
    /// What is wrong with one setting by itself: a size outside the range, a negative overlap, an unknown
    /// mode, or <c>custom</c> where no pattern can be sent. The others are given values that pass.
    /// </summary>
    private static ChunkSettingProblem? Own(
        ChunkSettingFields field, int size, int overlap, string mode, bool carriesPattern) => field switch
        {
            ChunkSettingFields.Size => Check(size, 0, "none", null),
            ChunkSettingFields.Overlap => overlap < 0 ? Check(MaxChunkSize, overlap, "none", null) : null,
            _ when mode == "custom" => carriesPattern ? null : CustomAtCreation,
            _ => Check(MinChunkSize, 0, mode, null),
        };

    /// <summary>A creation carries no pattern, so <c>custom</c> there is refused with the route that can set one.</summary>
    private static readonly ChunkSettingProblem CustomAtCreation = new(
        "boundaryMode 'custom' cannot be set when a corpus is created",
        "A custom boundary needs a customBoundaryPattern, which creating a corpus does not take. Create the "
        + "corpus with another mode, then send boundaryMode 'custom' and customBoundaryPattern with "
        + "PATCH /api/corpora/{name}/chunk-sets/default.",
        400,
        ChunkSettingFields.Mode);

    /// <summary>
    /// The 503 for a server setting that cannot make a set. The detail is for the log and a REST caller, who
    /// can send the settings with the request. <see cref="ConfigRefusal.AgentDetail"/> is for an MCP tool,
    /// which sends none.
    /// </summary>
    private static ConfigRefusal ServerRefusal(ChunkSettingProblem problem, ChunkSettingFields fields, IndexingOptions server)
    {
        var settings = Describe(fields, server);
        return new ConfigRefusal(
            "Server chunk settings unusable",
            $"{Sentence(problem.Detail ?? problem.Title)} The value comes from the server setting {settings}, not from "
            + "the request. Nothing was created. Correct the setting in the server's environment and restart.",
            503,
            AgentDetail: $"The server's chunk settings ({settings}) cannot make a corpus, so it was not created. "
                         + "Ask whoever runs Dexicon to correct them.");
    }

    /// <summary>
    /// Why the server's configured settings cannot make a corpus, or null when they can. Written to the log
    /// at startup, so the setting is named before the first creation is refused.
    /// </summary>
    internal static ConfigRefusal? CheckServerDefaults(IndexingOptions server) =>
        CheckNewCorpus(null, null, null, server, out _);

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
