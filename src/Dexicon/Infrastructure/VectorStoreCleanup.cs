using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Documents;
using Dexicon.Core.Vectors;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Infrastructure;

/// <summary>
/// Detaching a document removes its chunks from THIS corpus only. The blob and its
/// cached extraction survive, because another corpus may still hold the same document
/// chunked its own way — that is the point of separating bytes from chunking.
/// </summary>
public sealed class VectorStoreCleanup(CatalogDbContext db, IVectorStore vectors) : IVectorStoreCleanup
{
    public async Task<bool> RemoveAttachmentAsync(Corpus corpus, string fileId, DocumentService documents,
        CancellationToken ct)
    {
        var file = await db.Files.Include(f => f.Source)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.Source!.CorpusId == corpus.Id, ct);

        if (file is null) return false;

        // Vectors first. If the catalogue row went first and this threw, the corpus
        // would keep returning search hits for a document it no longer lists — a
        // result pointing at something the UI says is not there.
        await vectors.DeleteFileChunksAsync(corpus.CollectionName, corpus.Id, file.RelativePath, ct);

        return await documents.DetachAsync(corpus.Id, fileId, ct);
    }
}
