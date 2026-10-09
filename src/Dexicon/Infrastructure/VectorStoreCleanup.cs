using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Infrastructure;

/// <summary>
/// Detaching a document removes its chunks from THIS corpus only. The blob and its
/// cached extraction survive, because another corpus may still hold the same document
/// chunked its own way, which is the point of separating bytes from chunking.
/// </summary>
public sealed class VectorStoreCleanup(CatalogDbContext db, IVectorStore vectors, ILogger<VectorStoreCleanup> log)
    : IVectorStoreCleanup
{
    public async Task<bool> RemoveAttachmentAsync(Corpus corpus, string fileId, DocumentService documents,
        CancellationToken ct)
    {
        // Uploaded documents only. A file read from a folder or a commit is the source's, and goes
        // when the source does; detaching it here would take its vectors and its row from under a
        // source that still lists it.
        var file = await db.Files.Include(f => f.Source)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.Source!.CorpusId == corpus.Id
                                      && f.Source.Kind == SourceKind.Upload, ct);

        if (file is null) return false;

        // Vectors first. If the catalogue row went first and this threw, the corpus
        // would keep returning search hits for a document it no longer lists: a
        // result pointing at something the UI says is not there.
        // Once per set: a detached document must leave every chunking of it, not just
        // the one the caller happened to be looking at.
        var sets = await db.ChunkSets.Where(s => s.CorpusId == corpus.Id).ToListAsync(ct);
        foreach (var set in sets)
            await vectors.DeleteFileChunksAsync(set.CollectionName, set.Id, file.SourceId, file.RelativePath, ct);

        var detached = await documents.DetachFileAsync(corpus.Id, fileId, ct);
        if (detached is null) return false;

        // And again, for the file the detach deleted. An attachment can have renamed or replaced the
        // document since it was read above, and a pass can have written vectors for it since they were
        // deleted: nothing names those points once the row is gone, and search returns them until a later
        // pass removes points for a path no row names. Not cancellable and not fatal: the row is deleted,
        // and a failure here is left to that pass.
        foreach (var set in sets)
        {
            try
            {
                await vectors.DeleteFileChunksAsync(
                    set.CollectionName, set.Id, detached.SourceId, detached.RelativePath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex,
                    "Could not delete the points written for {File} in set {Set} while it was detached; "
                    + "a later pass removes them", detached.RelativePath, set.Name);
            }
        }

        return true;
    }
}
