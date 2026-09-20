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
/// The whole text of an indexed file, from wherever that file's text is kept.
///
/// Until <see cref="FileText"/> existed, the only copy of a workspace file's text was its
/// chunk payloads, so "give me this file" meant stitching them back together and marking
/// where the index had holes. That is what forces a chunk to be the unit a caller reads
/// rather than the unit a search finds, which is the half of D-31 the passage benchmark
/// could not measure: there was no document to read a passage out of.
///
/// Uploads have always had one, in <see cref="BlobText"/>. This reads either, by whichever
/// hash the file row carries, and reports which.
/// </summary>
public sealed class DocumentReader(CatalogDbContext db)
{
    /// <summary>
    /// Null when no text is stored for this file: a plain-text or code file on a mount,
    /// which is read from disk rather than cached, or a file indexed before this existed.
    /// The caller falls back rather than treating it as an empty document.
    /// </summary>
    public async Task<DocumentBody?> ForAsync(IndexedFile file, CancellationToken ct = default)
    {
        if (file.BlobSha256 is { Length: > 0 } blob)
        {
            var text = await db.BlobTexts.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Sha256 == blob, ct);
            return text is null
                ? null
                : new DocumentBody(text.Text, DocumentService.UnitsFrom(text), text.Title, "blob_texts");
        }

        if (file.Sha256 is { Length: > 0 } bytes)
        {
            var text = await db.FileTexts.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Sha256 == bytes, ct);
            return text is null
                ? null
                : new DocumentBody(text.Text, UnitsFrom(text.UnitsJson), text.Title, "file_texts");
        }

        return null;
    }

    /// <summary>
    /// The one file a corpus has at this path, or null where that is not one file.
    ///
    /// A corpus can carry several sources and two of them can hold the same relative
    /// path, which is why a path alone does not identify a document. The caller passes
    /// the source the chunks came from where it knows it.
    /// </summary>
    public async Task<IndexedFile?> FileAtAsync(
        string corpusId, string relativePath, string? sourceId, CancellationToken ct = default)
    {
        var q = db.Files.AsNoTracking()
            .Where(f => f.Source!.CorpusId == corpusId && f.RelativePath == relativePath);

        if (sourceId is { Length: > 0 }) q = q.Where(f => f.SourceId == sourceId);

        // Two, so "more than one" can be told from "one" without counting the whole set.
        // Not First: two sources holding this path means the answer is "which one", and
        // picking either would serve one book's text under the other's name. The caller
        // falls back to what it can say for certain.
        var matches = await q.Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<ExtractedUnit> UnitsFrom(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<ExtractedUnit>>(json) ?? [];
}
