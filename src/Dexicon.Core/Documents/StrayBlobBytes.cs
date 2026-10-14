using Dexicon.Core.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dexicon.Core.Documents;

/// <summary>What <see cref="StrayBlobBytes.SweepAsync"/> did under the blob root.</summary>
/// <param name="Deleted">Files deleted.</param>
/// <param name="Bytes">The total size of the files deleted.</param>
/// <param name="Failed">Files that could not be deleted.</param>
/// <param name="KeptForEmptyCatalogue">Hash-named files left because the catalogue holds no blob row at all.</param>
public readonly record struct StraySweep(int Deleted, long Bytes, int Failed, int KeptForEmptyCatalogue);

/// <summary>
/// Deletes the files under the blob root that no document names. Three things leave them: a crash between an
/// upload's first byte and its move into place (<c>.incoming-</c> and 32 hex digits, directly under the root),
/// a delete of the bytes that failed after the blob row went (<see cref="DocumentService.ReleaseAsync"/>), and
/// the refusal of its check for a blob stored again. Nothing else removes them.
///
/// Only two shapes are touched: a file directly under the root named as an upload's temporary file, and a file
/// named by 64 lowercase hex digits in the directory named by its first two. Anything else under the root is
/// left, as is a link, and nothing deeper is read.
///
/// Meant for startup, when nothing is being uploaded: a file that an upload has stored and not yet saved a row
/// for would look like one nothing names. That holds while one process owns the data folder (D-01). A second
/// process writing to it, such as an old container still serving, is not covered.
///
/// Every file with no row is taken for a leak, so the catalogue and the blob root have to be restored together.
/// A catalogue restored alone, older than the files beside it, makes their newer files look stray. The one check
/// against that is a catalogue with no blob row at all next to hash-named files: it is a restored or replaced
/// catalogue, or one pointed at another data folder, and the files may be the only copy of what it held. Those
/// files are left and reported, and temporary files are deleted either way. A catalogue emptied by detaching
/// every document keeps any leaked bytes the same way, until a document is stored.
/// </summary>
public static class StrayBlobBytes
{
    private const string TemporaryPrefix = ".incoming-";

    public static async Task<StraySweep> SweepAsync(CatalogDbContext db, string blobRoot, ILogger? log = null)
    {
        if (!Directory.Exists(blobRoot)) return default;

        var temporary = new List<string>();
        var named = new List<(string Sha, string Path)>();

        foreach (var path in Directory.EnumerateFiles(blobRoot))
        {
            var name = Path.GetFileName(path);
            if (name.Length == TemporaryPrefix.Length + 32
                && name.StartsWith(TemporaryPrefix, StringComparison.Ordinal)
                && IsLowerHex(name.AsSpan(TemporaryPrefix.Length))
                && !IsLink(path))
                temporary.Add(path);
        }

        var unreadable = 0;
        foreach (var directory in Directory.EnumerateDirectories(blobRoot))
        {
            var fanOut = Path.GetFileName(directory);
            if (fanOut.Length != 2 || !IsLowerHex(fanOut) || IsLink(directory)) continue;

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(path);
                    if (name.Length == 64 && IsLowerHex(name) && name.StartsWith(fanOut, StringComparison.Ordinal)
                        && !IsLink(path))
                        named.Add((name, path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The files of the folders that can be read are still swept. Its own are found by a later start.
                if (unreadable++ == 0)
                    log?.LogWarning(ex, "Could not list the stored files in folder {Folder} of the blob root", fanOut);
            }
        }

        var strays = new List<string>(temporary);
        var kept = 0;
        if (named.Count > 0)
        {
            var known = (await db.Blobs.AsNoTracking().Select(b => b.Sha256).ToListAsync(CancellationToken.None))
                .ToHashSet(StringComparer.Ordinal);
            var unnamed = named.Where(f => !known.Contains(f.Sha)).Select(f => f.Path).ToList();

            if (known.Count == 0)
            {
                kept = unnamed.Count;
                if (kept > 0)
                    log?.LogWarning("Left {Count} file(s) under the blob root that no document names, because the "
                                    + "catalogue holds no documents at all. They may belong to another catalogue.", kept);
            }
            else
            {
                strays.AddRange(unnamed);
            }
        }

        int deleted = 0, failed = 0;
        long bytes = 0;
        foreach (var path in strays)
        {
            try
            {
                var size = new FileInfo(path).Length;
                File.Delete(path);
                deleted++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One line per file would repeat for as long as the cause lasts. The first says why.
                if (failed++ == 0)
                    log?.LogWarning(ex, "Could not delete a stray file under the blob root, {Name}; the next start tries again",
                        Path.GetFileName(path));
            }
        }

        return new StraySweep(deleted, bytes, failed, kept);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;

        return true;
    }

    /// <summary>A symbolic link, or a junction or other reparse point, which the sweep neither follows nor deletes.</summary>
    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }
}
