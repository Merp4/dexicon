using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dexicon.Core.Catalog;
using Dexicon.Core.Extraction;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Documents;

/// <param name="Sha256">Of the file's BYTES, which is the key <see cref="FileText"/> is under.</param>
/// <param name="Extractor">Null for plain text and code, where reading is the extraction.</param>
public sealed record ReadText(string Sha256, ExtractedText Text, ITextExtractor? Extractor);

/// <summary>
/// A workspace file's text, from <c>file_texts</c> when those bytes have been read before
/// and by running an extractor when they have not.
///
/// The write side of the table <see cref="DocumentReader"/> reads. They are separate types
/// because they answer different questions from different places: this one is asked during
/// indexing, by path on disk, and may do minutes of work; that one is asked during
/// retrieval, by catalogue row, and must not.
///
/// The key is a hash of the bytes rather than of the text, because it has to be computable
/// without doing the work it exists to avoid. The extractor is part of it because which
/// extractor runs is decided by extension, and DOCX, PPTX and EPUB are all zip containers
/// that a rename moves between.
/// </summary>
public sealed class ExtractedTextCache(
    CatalogDbContext db,
    IndexingLimits limits,
    ILogger<ExtractedTextCache> log)
{
    /// <summary>
    /// Why a file that was readable produced nothing. Said plainly rather than left as an
    /// absence: "why isn't my PDF searchable" is answered by this string, in the UI.
    /// </summary>
    public static string EmptyReason(ITextExtractor? extractor) =>
        extractor is PdfTextExtractor
            ? "no text layer: this is a scanned PDF, and OCR is not supported"
            : "no extractable text content";

    /// <summary>Extraction units as stored, or none where the row predates them.</summary>
    public static List<ExtractedUnit> UnitsFrom(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<ExtractedUnit>>(json) ?? [];

    /// <param name="extractor">
    /// Null for plain text and code. Chosen by the caller rather than looked up here:
    /// which extractor a path gets is a fact about the file, and this type's concern is
    /// caching whatever that extractor produces.
    /// </param>
    /// <param name="timeoutSeconds">
    /// How long a file may go on reading itself before it is abandoned. 0 disables it.
    /// Passed in rather than read here, because it belongs to the indexing pass that
    /// decided to open this file.
    /// </param>
    public async Task<ReadText> ReadAsync(
        string fullPath, string relativePath, ITextExtractor? extractor, int timeoutSeconds,
        CancellationToken ct)
    {
        if (extractor is null)
        {
            // Plain text and code. Not cached, because the read IS the extraction and a
            // cache would hold a second copy of the tree to save a file read. The hash is
            // still taken: it goes on the file's per-set row, so a reader can tell whether
            // the mount still holds what was indexed.
            var (sha, text) = await HashAndReadAsync(fullPath, ct);
            return new ReadText(sha, new ExtractedText(text, []), null);
        }

        var name = extractor.GetType().Name;

        // ONE open for both the hash and the parse. Two would leave a window where the
        // file changes in between, and the text of one revision would be stored under the
        // hash of another: the cache would then hand that text to every later pass over
        // the new bytes, which is worse than not caching at all. An open handle keeps the
        // bytes it was opened on. It also halves the round trips, which is what a bind
        // mount charges for.
        await using var stream = File.OpenRead(fullPath);
        var fileSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

        // Untracked, and detached again after the write below. A row carries a whole
        // document's text and the caller's context can live as long as a job, so tracking
        // one per file would hold an entire library in memory at once.
        var cached = await db.FileTexts.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Sha256 == fileSha && t.Extractor == name, ct);

        // Not `==`. A row stamped NEWER than this build was written by a later one, and
        // overwriting it would make a rollback and the version it rolled back from take
        // turns re-extracting the same library.
        if (cached is not null && cached.ExtractorVersion >= ExtractorVersions.Current)
            return new ReadText(
                fileSha, new ExtractedText(cached.Text, UnitsFrom(cached.UnitsJson), cached.Title), extractor);

        stream.Position = 0;
        var extracted = Parse(stream, extractor, relativePath, timeoutSeconds);

        if (cached is not null)
            // Same bytes, same extractor, older version: the row is overwritten rather
            // than added beside. This is what lets an extractor fix reach files indexed
            // before it.
            log.LogInformation(
                "Re-extracted {File} with extractor v{Version}: {Before:N0} -> {After:N0} chars",
                relativePath, ExtractorVersions.Current, cached.ExtractedChars, extracted.Text.Length);

        await StoreAsync(fileSha, name, extractor, extracted, replacing: cached is not null, relativePath, ct);
        return new ReadText(fileSha, extracted, extractor);
    }

    /// <summary>
    /// Runs the extractor under the machine's parsing budget.
    ///
    /// The permit is taken here rather than around the whole method, so a cache hit costs
    /// a hash and a row read instead of queueing behind other corpora's parsing. The
    /// deadline starts inside the permit for the same reason it starts after the hash: it
    /// budgets the parse, and time spent waiting for a busy machine is not the file being
    /// slow.
    /// </summary>
    private ExtractedText Parse(
        Stream stream, ITextExtractor extractor, string relativePath, int timeoutSeconds)
    {
        limits.Extractions.Wait();
        try
        {
            // Every read the extractor makes passes through the deadline, which is the
            // only way to interrupt one: Extract is synchronous and the libraries under it
            // take no cancellation token.
            return timeoutSeconds > 0
                ? extractor.Extract(
                    new DeadlineStream(stream, TimeSpan.FromSeconds(timeoutSeconds), relativePath),
                    relativePath)
                : extractor.Extract(stream, relativePath);
        }
        finally { limits.Extractions.Release(); }
    }

    private async Task StoreAsync(
        string sha, string extractorName, ITextExtractor extractor, ExtractedText extracted,
        bool replacing, string relativePath, CancellationToken ct)
    {
        var row = new FileText
        {
            Sha256 = sha,
            Extractor = extractorName,
            Text = extracted.Text,
            UnitsJson = extracted.Units.Count > 0 ? JsonSerializer.Serialize(extracted.Units) : null,
            Title = extracted.Title,
            ExtractedChars = extracted.Text.Length,
            ExtractorVersion = ExtractorVersions.Current,
            ExtractedUtc = DateTime.UtcNow,

            // "Produced no text" is recorded as a property of these bytes rather than
            // rediscovered on every pass. A scanned PDF costs the same to re-read as a
            // readable one and yields nothing either time.
            EmptyReason = extracted.Text.Trim().Length > 0 ? null : EmptyReason(extractor),
        };

        if (replacing)
        {
            // ExecuteUpdate rather than the change tracker, which would have to attach the
            // row to write it. Nothing else needs this entity, and a second instance of a
            // key already tracked throws rather than replacing.
            await db.FileTexts.Where(t => t.Sha256 == sha && t.Extractor == extractorName)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Text, row.Text)
                    .SetProperty(t => t.UnitsJson, row.UnitsJson)
                    .SetProperty(t => t.Title, row.Title)
                    .SetProperty(t => t.ExtractedChars, row.ExtractedChars)
                    .SetProperty(t => t.ExtractorVersion, row.ExtractorVersion)
                    .SetProperty(t => t.ExtractedUtc, row.ExtractedUtc)
                    .SetProperty(t => t.EmptyReason, row.EmptyReason), ct);
            return;
        }

        try
        {
            db.FileTexts.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.Entries.Any(e => e.Entity is FileText))
        {
            // Another source or corpus extracted the same bytes first. The text is already
            // in hand, so the collision costs a duplicated extraction and nothing else;
            // letting it escape would record a readable file as failed. Filtered on the
            // entry, so an unrelated write failing here still throws.
            log.LogDebug(ex, "file_texts row for {File} was written concurrently", relativePath);
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Text and hash from one read. A workspace tree is often on a bind mount where a
    /// round trip is the cost that matters, and hashing separately would read every file
    /// in a 27,000-file repository twice.
    /// </summary>
    private static async Task<(string Sha256, string Text)> HashAndReadAsync(
        string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), Decode(bytes));
    }

    /// <summary>BOM, then UTF-8, then Latin-1. Never throws on a file with unusual bytes.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? bytes.AsSpan(3)
                    : bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
