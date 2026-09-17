using System.Text.Json;
using Dexicon.Core.Catalog;
using Dexicon.Core.Search;

namespace Dexicon.Api;

// Request/response shapes for the REST surface. Deliberately separate from the
// catalogue entities: an entity is a storage concern and a contract is a promise.

public sealed record CreateCorpusRequest(
    string Name,
    string? Description = null,
    string? EmbeddingProvider = null,
    string? EmbeddingModel = null,
    int? ChunkSize = null,
    int? ChunkOverlap = null,
    string? BoundaryMode = null,
    string? Visibility = null,
    string? WorkspacePath = null);

public sealed record UpdateCorpusRequest(
    string? Description = null,
    string? Visibility = null,
    IReadOnlyList<string>? GrantTenantIds = null);

/// <summary>
/// A new way of cutting and embedding a corpus's existing content. Omitted fields are
/// inherited from the corpus's default set, so "same as now but on another model" is a
/// two-field request.
/// </summary>
public sealed record CreateChunkSetRequest(
    string Name,
    string? Description = null,
    string? EmbeddingProvider = null,
    string? EmbeddingModel = null,
    int? ChunkSize = null,
    int? ChunkOverlap = null,
    string? BoundaryMode = null,
    string? CustomBoundaryPattern = null,
    bool? UnitAware = null,
    bool? SentenceAware = null,
    bool? HeadingContext = null,
    /// <summary>Promote immediately instead of backfilling first. Rarely what you want.</summary>
    bool? MakeDefault = null);

/// <summary>
/// Everything here re-chunks the set. The embedding model is absent by design: it is a
/// different vector space, so it is a NEW set and a promotion, not an edit.
/// </summary>
public sealed record UpdateChunkSetRequest(
    string? Description = null,
    int? ChunkSize = null,
    int? ChunkOverlap = null,
    string? BoundaryMode = null,
    string? CustomBoundaryPattern = null,
    bool? UnitAware = null,
    bool? SentenceAware = null,
    bool? HeadingContext = null);

public sealed record ChunkSetSummary(
    string Id,
    string Name,
    string? Description,
    string EmbeddingProvider,
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

public sealed record PullModelRequest(string Model, string? Provider = null);

// ── Response shapes ──────────────────────────────────────────────────────────
//
// These were anonymous objects. A minimal API returning `Results.Ok(new { … })`
// describes nothing in the OpenAPI document, so the generated TypeScript client typed
// every one of them as `unknown`, which defeats most of the point of generating it.
// Named records make the contract explicit on both sides of the wire.

public sealed record ChunkSetCreated(ChunkSetSummary ChunkSet, JobSummary BackfillJob);

public sealed record ChunkSetUpdated(ChunkSetSummary ChunkSet, JobSummary? RechunkJob);

public sealed record ChunkSetPromoted(string Promoted, string Corpus);

public sealed record CorpusUpdated(CorpusSummary Corpus);

public sealed record FileListResponse(int Total, string ChunkSet, IReadOnlyList<FileSummary> Files);

/// <summary>What a document was chunked as, in one corpus, by one set.</summary>
public sealed record AttachedChunking(
    string Set, int ChunkSize, int ChunkOverlap, string BoundaryMode, string EmbeddingModel);

public sealed record DocumentAttached(
    string Corpus, string FileId, string FileName,
    IReadOnlyList<AttachedChunking> Chunking, JobSummary Job);

/// <param name="Preview">
/// The extracted text, truncated at 20,000 characters. Named for what it is: a caller
/// that assumed it was the whole document would be wrong for exactly the documents where
/// it matters.
/// </param>
public sealed record ExtractedTextResponse(
    string Sha256, string? Title, string Extractor, int ExtractedChars,
    DateTime ExtractedUtc, string? EmptyReason, string Preview);

public sealed record EmbeddingModelList(
    string Provider, bool Managed, string Configured,
    IReadOnlyList<EmbeddingModelInfo> Models, string? Note);

public sealed record EmbeddingProviderList(string Default, IReadOnlyList<EmbeddingProviderInfo> Providers);

public sealed record TenantSummary(string Id, string DisplayName, DateTime CreatedUtc, bool Disabled);

public sealed record HealthDependency(bool Reachable, string Endpoint);

/// <summary>
/// Still called "ollama" on the wire. Renaming a health field to "embedding" would break
/// every dashboard reading it, for a word.
/// </summary>
public sealed record EmbeddingHealth(
    bool Reachable, string Endpoint, string Provider, string Model, int Dimensions, string? Error);

public sealed record HealthResponse(
    string Status, HealthDependency Qdrant, EmbeddingHealth Ollama, int Corpora, JobSummary? ActiveJob);

public sealed record ReadinessResponse(string Status, bool Qdrant, bool Catalogue);

public sealed record WorkspaceListing(string Root, string Path, IReadOnlyList<WorkspaceEntry> Entries);

public sealed record UploadFailure(string File, string Error);

public sealed record UploadResponse(
    string Corpus, IReadOnlyList<UploadedDocumentResponse> Stored,
    IReadOnlyList<UploadFailure> Failed, JobSummary Job);

public sealed record ModelProfileSaved(
    string Provider, string Model, IReadOnlyList<string> Reindexing, string? Note);

/// <summary>Measure a model's real input limit without indexing anything.</summary>
public sealed record ProbeModelRequest(string Model, string? Provider = null);

/// <summary>One embedding model a provider can serve.</summary>
public sealed record EmbeddingModelInfo(
    string Name, long SizeBytes, int? Dimensions, bool InUse,
    /// <summary>How text is framed for this model, and whether that is saved or assumed.</summary>
    string DocumentTemplate, string QueryTemplate, string TemplateOrigin,
    /// <summary>
    /// What a probe measured about this model, or null if it has never been probed.
    /// </summary>
    /// <remarks>
    /// Here rather than only on the probe response because the moment it matters is when
    /// somebody is choosing a chunk size, and that screen was showing "64-8192" with no
    /// reference to what the selected model can actually take. The probe measured it once
    /// and then forgot, so the answer existed and was unreachable.
    /// </remarks>
    ModelMeasurement? Measured);

/// <summary>A probe's findings, as a caller sees them.</summary>
public sealed record ModelMeasurement(
    int? MaxInputChars,
    bool TruncatesSilently,
    int RecommendedChunkTokens,
    double? CharsPerToken,
    DateTime MeasuredUtc);

/// <summary>
/// Saves the task framing for one model. Templates contain <c>{text}</c>; sending
/// <c>{text}</c> alone means "embed it raw", which is correct for some models.
/// </summary>
public sealed record SaveModelProfileRequest(
    string Model,
    string DocumentTemplate,
    string QueryTemplate,
    // Required first, optional after: C# demands that order, and the two that are genuinely
    // optional are the provider (defaults to the configured one) and a note about where
    // the templates came from.
    string? Provider = null,
    string? Notes = null);

/// <summary>A configured backend, and whether its models can be pulled and deleted.</summary>
public sealed record EmbeddingProviderInfo(string Name, string Kind, bool Managed, bool Configured, string? Detail);

public sealed record AddSourceRequest(string WorkspacePath, bool? UseGitignore = null, int? MaxFileBytes = null,
    IReadOnlyList<string>? IncludeGlobs = null, IReadOnlyList<string>? ExcludeGlobs = null);

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

/// <summary>
/// One place a corpus takes content from, and the filters applied to it.
/// </summary>
/// <remarks>
/// The globs are here because they were accepted, stored and then never returned, so
/// anything setting them had no way to read them back and no way to show what a source is
/// actually doing. They are persisted as a JSON array in one column; the contract is a
/// list, because a caller should not be parsing our storage format.
/// </remarks>
public sealed record SourceSummary(
    string Id,
    string Kind,
    string? RootPath,
    bool UseGitignore,
    int MaxFileBytes,
    IReadOnlyList<string> IncludeGlobs,
    IReadOnlyList<string> ExcludeGlobs,
    /// <summary>
    /// Files this source contributed. A corpus with one source does not need it, since
    /// the corpus total is the source total. A corpus with ten does: without it there is no
    /// way to see that one folder brought in nothing, which is what a mistyped path, an
    /// over-eager exclude glob, or an index that stopped early all look like.
    /// </summary>
    int FileCount = 0);

/// <summary>
/// One indexed file, reconstructed from the chunks of one chunk set.
/// </summary>
/// <remarks>
/// Rebuilt from the INDEX rather than read from disk, for the same reason the MCP resource
/// is: an uploaded PDF has no file to read, and the original would in any case differ from
/// what was indexed. What comes back is what search is actually searching, which is the
/// thing worth looking at when a result is surprising.
///
/// <paramref name="Gaps"/> is not decoration. Chunks from one pass tile the file, so a gap
/// means the index really is missing those lines; the text says so inline, and this says
/// how many times, so a caller can show it without parsing prose.
/// </remarks>
public sealed record IndexedFileText(
    string Corpus,
    string ChunkSet,
    string Path,
    /// <summary>First line of the RETURNED WINDOW, not of the file.</summary>
    int StartLine,
    /// <summary>Last line of the returned window.</summary>
    int EndLine,
    int Gaps,
    /// <summary>More text follows this window. Fetch it with <see cref="NextOffset"/>.</summary>
    bool Truncated,
    string Text,
    /// <summary>Character offset this window starts at.</summary>
    int Offset = 0,
    /// <summary>Length of the whole file's indexed text, so a caller can show progress.</summary>
    int TotalChars = 0,
    /// <summary>Offset to pass for the next window, or null at the end.</summary>
    int? NextOffset = null);

/// <summary>
/// A source, and the job now reading it. The job is returned rather than left implicit so
/// a caller can follow the work it just caused instead of polling and hoping.
/// </summary>
public sealed record SourceAdded(SourceSummary Source, JobSummary IndexJob);

public sealed record FileSummary(
    string Id, string RelativePath, string Status, string? StatusDetail,
    string? Language, long SizeBytes, int ChunkCount, DateTime? IndexedUtc);

public sealed record JobSummary(
    string Id, string CorpusId, string? ChunkSetId, string Kind, string State, string? Phase,
    int FilesTotal, int FilesDone, int FilesSkipped, int FilesFailed, int ChunksWritten,
    string? Error, DateTime QueuedUtc, DateTime? StartedUtc, DateTime? FinishedUtc);

public sealed record SearchApiRequest(
    string Query,
    IReadOnlyList<string>? Corpus = null,
    string? Mode = null,
    int? Limit = null,
    string? PathPrefix = null,
    string? Source = null,
    string? Language = null,
    string? Symbol = null);

public sealed record CreateTenantRequest(string Id, string? DisplayName = null);

public sealed record CreateTokenRequest(string Name, IReadOnlyList<string>? Scopes = null, int? ExpiresInDays = null);

public sealed record TokenSummary(
    string Id, string Name, string TenantId, string Scopes,
    DateTime CreatedUtc, DateTime? LastUsedUtc, DateTime? ExpiresUtc, DateTime? RevokedUtc);

public sealed record CreatedTokenResponse(TokenSummary Token, string Secret, string McpAddCommand);

public sealed record WorkspaceEntry(string Name, string RelativePath, bool IsDirectory, int? ChildCount);

public static class Mapping
{
    public static SourceSummary ToSummary(this Source s, int fileCount = 0) =>
        new(s.Id,
            s.Kind.ToString().ToLowerInvariant(),
            s.RootPath,
            s.UseGitignore,
            s.MaxFileBytes,
            Globs(s.IncludeGlobs),
            Globs(s.ExcludeGlobs),
            fileCount);

    /// <summary>
    /// A stored glob column as a list. Empty rather than null when unset or unreadable:
    /// "no filter" and "a filter we could not read" look the same to a caller, and the
    /// honest one of those two is the one that does not crash a screen.
    /// </summary>
    private static List<string> Globs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A file as one chunk set sees it. The state argument is separate because the same
    /// attachment has a different answer per set: indexed in one, pending in another.
    /// </summary>
    public static FileSummary ToSummary(this IndexedFile f, FileChunkState? state) =>
        new(f.Id, f.RelativePath,
            (state?.Status ?? FileStatus.Pending).ToString().ToLowerInvariant(),
            state?.StatusDetail, f.Language, f.SizeBytes, state?.ChunkCount ?? 0, state?.IndexedUtc);

    public static ChunkSetSummary ToSummary(this ChunkSet s,
        int fileCount, int chunkCount, int pendingCount, int failedCount) =>
        new(s.Id, s.Name, s.Description, s.EmbeddingProvider, s.EmbeddingModel, s.EmbeddingDimensions, s.CollectionName,
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
