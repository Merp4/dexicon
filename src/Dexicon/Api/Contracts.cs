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
    string EmbeddingModel,
    int EmbeddingDimensions,
    int ChunkSize,
    int ChunkOverlap,
    string BoundaryMode,
    DateTime CreatedUtc,
    DateTime? LastIndexedUtc,
    int SourceCount,
    int FileCount,
    int ChunkCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<SourceSummary> Sources);

public sealed record SourceSummary(string Id, string Kind, string? RootPath, bool UseGitignore, int MaxFileBytes);

public sealed record FileSummary(
    string Id, string RelativePath, string Status, string? StatusDetail,
    string? Language, long SizeBytes, int ChunkCount, DateTime? IndexedUtc);

public sealed record JobSummary(
    string Id, string CorpusId, string Kind, string State, string? Phase,
    int FilesTotal, int FilesDone, int FilesSkipped, int FilesFailed, int ChunksWritten,
    string? Error, DateTime? StartedUtc, DateTime? FinishedUtc);

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

    public static FileSummary ToSummary(this IndexedFile f) =>
        new(f.Id, f.RelativePath, f.Status.ToString().ToLowerInvariant(), f.StatusDetail,
            f.Language, f.SizeBytes, f.ChunkCount, f.IndexedUtc);

    public static JobSummary ToSummary(this IndexJob j) =>
        new(j.Id, j.CorpusId, j.Kind.ToString().ToLowerInvariant(), j.State.ToString().ToLowerInvariant(),
            j.Phase, j.FilesTotal, j.FilesDone, j.FilesSkipped, j.FilesFailed, j.ChunksWritten,
            j.Error, j.StartedUtc, j.FinishedUtc);

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
