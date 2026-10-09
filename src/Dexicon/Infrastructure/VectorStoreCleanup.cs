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
    /// <summary>How long the deletes that follow the row delete have between them. Replaced in tests.</summary>
    internal TimeSpan SecondDeleteTimeout { get; init; } = TimeSpan.FromSeconds(30);

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
        // pass of the set removes points for a path no row names. The row is deleted, so the caller's token
        // is not used and a failure is logged and left to that pass. The deletes share SecondDeleteTimeout,
        // so a vector store that does not answer cannot hold the request.
        using var timeout = new CancellationTokenSource(SecondDeleteTimeout);
        foreach (var set in sets)
        {
            try
            {
                await vectors.DeleteFileChunksAsync(
                    set.CollectionName, set.Id, detached.SourceId, detached.RelativePath, timeout.Token);
            }
            // Any exception while the token is cancelled: a gRPC client that does not throw
            // OperationCanceledException reports a cancelled call as an RpcException with StatusCode.Cancelled.
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                log.LogWarning(
                    "Deleting the points written for {File} while it was detached timed out after {Seconds:N0} s; "
                    + "a later pass removes them",
                    DexiconAuthMiddleware.OneLine(detached.RelativePath), SecondDeleteTimeout.TotalSeconds);
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex,
                    "Could not delete the points written for {File} in set {Set} while it was detached; "
                    + "a later pass removes them",
                    DexiconAuthMiddleware.OneLine(detached.RelativePath), DexiconAuthMiddleware.OneLine(set.Name));
            }
        }
        return true;
    }
}
