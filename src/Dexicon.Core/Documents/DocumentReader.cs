using Dexicon.Core.Catalog;
using Dexicon.Core.Extraction;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Core.Documents;

/// <param name="Text">The document's extracted text, whole.</param>
/// <param name="Units">Page, slide or chapter offsets, where the format has them.</param>
/// <param name="Store">Which table it came from: <c>blob_texts</c> or <c>file_texts</c>.</param>
public sealed record DocumentBody(
    string Text, IReadOnlyList<ExtractedUnit> Units, string? Title, string Store);

/// <summary>
/// The whole text of an indexed file, as one chunk set last read it.
///
/// Until <see cref="FileText"/> existed, the only copy of a workspace file's text was its
/// chunk payloads, so "give me this file" meant stitching them back together and marking
/// where the index had holes. That is what forces a chunk to be the unit a caller reads
/// rather than the unit a search finds, which is the half of D-31 the passage benchmark
/// could not measure: there was no document to read a passage out of.
///
/// Uploads have always had one, in <see cref="BlobText"/>. This reads either, by whichever
/// hash the file carries, and reports which.
///
/// PER CHUNK SET, because a job can target one set while the others keep serving. The
/// hash comes from that set's own <see cref="FileChunkState"/>, so the text returned is
/// the text its chunks were cut from. A file-wide hash would point a reader for a
/// still-old set at a document reindexed for a newer one, whose line numbers its own
/// hits do not address.
/// </summary>
public sealed class DocumentReader(CatalogDbContext db)
{
    /// <summary>
    /// Null when this set has no document for that path. That is the ordinary answer for
    /// a plain-text or code file on a mount, whose text is not cached because reading it
    /// is the extraction; for a file indexed before the hash was recorded; and for a path
    /// two of the corpus's sources both hold, where picking either would serve one book's
    /// text under the other's name. The caller falls back rather than treating any of
    /// them as an empty document.
    /// </summary>
    /// <param name="sourceId">
    /// The source the caller already knows, where it knows it. Without one, a path that
    /// two sources share resolves to nothing rather than to a guess.
    /// </param>
    public async Task<DocumentBody?> ForAsync(
        string corpusId, string chunkSetId, string relativePath, string? sourceId,
        CancellationToken ct = default)
    {
        var file = await FileAtAsync(corpusId, relativePath, sourceId, ct);
        if (file is null) return null;

        if (file.BlobSha256 is { Length: > 0 } blob)
        {
            // An upload's bytes are fixed for the life of the attachment, so this needs
            // no per-set hash: every set reading this file read the same blob.
            var uploaded = await db.BlobTexts.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Sha256 == blob, ct);
            return uploaded is null
                ? null
                : new DocumentBody(
                    uploaded.Text, DocumentService.UnitsFrom(uploaded), uploaded.Title, "blob_texts");
        }

        var sha = await db.FileChunkStates.AsNoTracking()
            .Where(s => s.FileId == file.Id && s.ChunkSetId == chunkSetId)
            .Select(s => s.SourceSha256)
            .FirstOrDefaultAsync(ct);

        if (sha is not { Length: > 0 }) return null;

        // The extractor is part of the key, so it has to be part of the lookup. Derived
        // from the path the same way indexing derived it, and null for a format with no
        // extractor, which has no row to find.
        var extractor = ExtractorRegistry.For(relativePath)?.GetType().Name;
        if (extractor is null) return null;

        var text = await db.FileTexts.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Sha256 == sha && t.Extractor == extractor, ct);

        return text is null
            ? null
            : new DocumentBody(text.Text, UnitsFrom(text.UnitsJson), text.Title, "file_texts");
    }

    /// <summary>
    /// What this set made of the file at that path, or null if it has no such file.
    ///
    /// For the callers that find no chunks and have to say why. "Not indexed" and
    /// "indexed, and the format yielded nothing" are different answers, and a scanned
    /// PDF gets the second one; telling the reader their path was wrong sends them to
    /// check a path that is right.
    /// </summary>
    public async Task<(FileStatus Status, string? Detail)?> StatusAtAsync(
        string corpusId, string chunkSetId, string relativePath, CancellationToken ct = default)
    {
        var file = await FileAtAsync(corpusId, relativePath, sourceId: null, ct);
        if (file is null) return null;

        var state = await db.FileChunkStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.FileId == file.Id && s.ChunkSetId == chunkSetId, ct);

        return state is null ? null : (state.Status, state.StatusDetail);
    }

    /// <summary>The one file a corpus has at this path, or null where that is not one file.</summary>
    private async Task<IndexedFile?> FileAtAsync(
        string corpusId, string relativePath, string? sourceId, CancellationToken ct)
    {
        var q = db.Files.AsNoTracking()
            .Where(f => f.Source!.CorpusId == corpusId && f.RelativePath == relativePath);

        if (sourceId is { Length: > 0 }) q = q.Where(f => f.SourceId == sourceId);

        // Two, so "more than one" can be told from "one" without counting the whole set.
        var matches = await q.Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<ExtractedUnit> UnitsFrom(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<ExtractedUnit>>(json) ?? [];
}
