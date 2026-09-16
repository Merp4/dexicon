using Dexicon.Core.Catalog;
using Dexicon.Core.Search;

namespace Dexicon.Api;

// Request/response shapes for the REST surface. Deliberately separate from the
// catalogue entities: an entity is a storage concern and a contract is a promise.

public sealed record CreateCorpusRequest(
    string Name,
    string? Description,
    string? EmbeddingModel,
    int? ChunkSize,
    int? ChunkOverlap,
    string? BoundaryMode,
    string? Visibility,
    string? WorkspacePath);

public sealed record UpdateCorpusRequest(
    string? Description,
    string? Visibility,
    IReadOnlyList<string>? GrantTenantIds);

/// <summary>
/// A new way of cutting and embedding a corpus's existing content. Omitted fields are
/// inherited from the corpus's default set, so "same as now but on another model" is a
/// two-field request.
/// </summary>
public sealed record CreateChunkSetRequest(
    string Name,
    string? Description,
    string? EmbeddingModel,
    int? ChunkSize,
    int? ChunkOverlap,
    string? BoundaryMode,
    string? CustomBoundaryPattern,
    bool? UnitAware,
    bool? SentenceAware,
    bool? HeadingContext,
    /// <summary>Promote immediately instead of backfilling first. Rarely what you want.</summary>
    bool? MakeDefault);

/// <summary>
/// Everything here re-chunks the set. The embedding model is absent by design: it is a
/// different vector space, so it is a NEW set and a promotion, not an edit.
/// </summary>
public sealed record UpdateChunkSetRequest(
    string? Description,
    int? ChunkSize,
    int? ChunkOverlap,
    string? BoundaryMode,
    string? CustomBoundaryPattern,
    bool? UnitAware,
    bool? SentenceAware,
    bool? HeadingContext);

public sealed record ChunkSetSummary(
    string Id,
    string Name,
    string? Description,
    string EmbeddingModel,
    int EmbeddingDimensions,
    string CollectionName,
    int ChunkSize,
    int ChunkOverlap,
    string BoundaryMode,
    string? CustomBoundaryPattern,
    bool UnitAware,
    bool SentenceAware,
    bool HeadingContext,
    bool IsDefault,
    string State,
    int FileCount,
    int ChunkCount,
    int PendingCount,
    int FailedCount,
    DateTime CreatedUtc,
    DateTime? LastIndexedUtc);

public sealed record PullModelRequest(string Model);

/// <summary>One embedding model Ollama has pulled and can serve.</summary>
public sealed record EmbeddingModelInfo(string Name, long SizeBytes, int? Dimensions, bool InUse);

public sealed record AddSourceRequest(string WorkspacePath, bool? UseGitignore, int? MaxFileBytes,
    IReadOnlyList<string>? IncludeGlobs, IReadOnlyList<string>? ExcludeGlobs);

public sealed record CorpusSummary(
    string Id,
    string Name,
    string? Description,
    string TenantId,
    bool Owned,
    string Visibility,
    string State,
    DateTime CreatedUtc,
    DateTime? LastIndexedUtc,
    int SourceCount,
    int FileCount,
    int ChunkCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<SourceSummary> Sources,
    /// <summary>Every way this corpus is cut. The default one is what search uses.</summary>
    IReadOnlyList<ChunkSetSummary> ChunkSets);

public sealed record SourceSummary(string Id, string Kind, string? RootPath, bool UseGitignore, int MaxFileBytes);

public sealed record FileSummary(
    string Id, string RelativePath, string Status, string? StatusDetail,
    string? Language, long SizeBytes, int ChunkCount, DateTime? IndexedUtc);

public sealed record JobSummary(
    string Id, string CorpusId, string? ChunkSetId, string Kind, string State, string? Phase,
    int FilesTotal, int FilesDone, int FilesSkipped, int FilesFailed, int ChunksWritten,
    string? Error, DateTime QueuedUtc, DateTime? StartedUtc, DateTime? FinishedUtc);

public sealed record SearchApiRequest(
    string Query,
    IReadOnlyList<string>? Corpus,
    string? Mode,
    int? Limit,
    string? PathPrefix,
    string? Language,
    string? Symbol);

public sealed record CreateTenantRequest(string Id, string? DisplayName);

public sealed record CreateTokenRequest(string Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

public sealed record TokenSummary(
    string Id, string Name, string TenantId, string Scopes,
    DateTime CreatedUtc, DateTime? LastUsedUtc, DateTime? ExpiresUtc, DateTime? RevokedUtc);

public sealed record CreatedTokenResponse(TokenSummary Token, string Secret, string McpAddCommand);

public sealed record WorkspaceEntry(string Name, string RelativePath, bool IsDirectory, int? ChildCount);

public static class Mapping
{
    public static SourceSummary ToSummary(this Source s) =>
        new(s.Id, s.Kind.ToString().ToLowerInvariant(), s.RootPath, s.UseGitignore, s.MaxFileBytes);

    /// <summary>
    /// A file as one chunk set sees it. The state argument is separate because the same
    /// attachment has a different answer per set — indexed in one, pending in another.
    /// </summary>
    public static FileSummary ToSummary(this IndexedFile f, FileChunkState? state) =>
        new(f.Id, f.RelativePath,
            (state?.Status ?? FileStatus.Pending).ToString().ToLowerInvariant(),
            state?.StatusDetail, f.Language, f.SizeBytes, state?.ChunkCount ?? 0, state?.IndexedUtc);

    public static ChunkSetSummary ToSummary(this ChunkSet s,
        int fileCount, int chunkCount, int pendingCount, int failedCount) =>
        new(s.Id, s.Name, s.Description, s.EmbeddingModel, s.EmbeddingDimensions, s.CollectionName,
            s.ChunkSize, s.ChunkOverlap, s.BoundaryMode, s.CustomBoundaryPattern,
            s.UnitAware, s.SentenceAware, s.HeadingContext, s.IsDefault,
            s.State.ToString().ToLowerInvariant(),
            fileCount, chunkCount, pendingCount, failedCount, s.CreatedUtc, s.LastIndexedUtc);

    public static JobSummary ToSummary(this IndexJob j) =>
        new(j.Id, j.CorpusId, j.ChunkSetId, j.Kind.ToString().ToLowerInvariant(), j.State.ToString().ToLowerInvariant(),
            j.Phase, j.FilesTotal, j.FilesDone, j.FilesSkipped, j.FilesFailed, j.ChunksWritten,
            j.Error, j.QueuedUtc, j.StartedUtc, j.FinishedUtc);

    public static TokenSummary ToSummary(this ApiToken t) =>
        new(t.Id, t.Name, t.TenantId, t.Scopes, t.CreatedUtc, t.LastUsedUtc, t.ExpiresUtc, t.RevokedUtc);

    public static SearchMode ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "semantic" => SearchMode.Semantic,
        "keyword" => SearchMode.Keyword,
        null or "" or "hybrid" => SearchMode.Hybrid,
        _ => throw new ArgumentException($"Unknown search mode '{mode}'. Expected hybrid, semantic or keyword."),
    };
}
