using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dexicon.Core.Configuration;
using Dexicon.Core.Documents;
using Dexicon.Core.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Indexing;

/// <summary>
/// One file as the reader left it: its bytes hashed and its text extracted, or the
/// exception that stopped it.
/// </summary>
/// <param name="Error">
/// Carried rather than thrown. Reading happens on several threads and every failure mode
/// here is already turned into a catalogue row by the indexer, which owns the catalogue;
/// throwing across the boundary would need that handling written twice.
/// </param>
public sealed record ReadFile(
    WorkspaceWalker.Candidate Candidate, ReadText? Read, Exception? Error);

/// <summary>
/// Reads a source's files concurrently, into a stream the caller consumes one at a time.
///
/// Reading is the slow half of indexing and needs nothing shared: a hash, and a parse
/// whose only state is the extracted-text cache. Recording is the half that touches a
/// pass's <c>DbContext</c>, its dictionaries and its counters, none of which is safe from
/// two threads and none of which is slow. Keeping them apart is what lets one corpus
/// indexing alone use the whole extraction budget, which a limit shared between jobs
/// cannot give it while each job reads one file at a time.
///
/// Order is not preserved. Nothing downstream depends on it: every file's outcome is its
/// own row, and the only state across files is a count.
/// </summary>
public sealed class WorkspaceFileReader(IServiceScopeFactory scopes, IOptions<DexiconOptions> options)
{
    private readonly IndexingOptions _indexing = options.Value.Indexing;

    /// <summary>
    /// How many files are read at once, and how many may wait to be consumed.
    ///
    /// Matched to the extraction budget, because that is what the reading waits for. It
    /// also decides peak memory: at most this many documents being extracted and this
    /// many extracted and queued, and a technical book's text runs to two or three
    /// million characters.
    /// </summary>
    private int ReadAhead => Math.Max(1, _indexing.MaxConcurrentExtractions);

    public async IAsyncEnumerable<ReadFile> ReadAsync(
        IReadOnlyList<WorkspaceWalker.Candidate> files,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Linked, so abandoning the enumeration stops the readers. A bounded writer with
        // nobody reading blocks for ever, and a consumer that gives up has to say so.
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var channel = Channel.CreateBounded<ReadFile>(
            new BoundedChannelOptions(ReadAhead) { SingleReader = true, SingleWriter = false });

        var readers = FillAsync(files, channel.Writer, reading.Token);

        try
        {
            await foreach (var read in channel.Reader.ReadAllAsync(ct)) yield return read;
        }
        finally
        {
            // Reached on an ordinary finish, on an exception, and on a `break`, because
            // the compiler runs it when the enumerator is disposed. On the ordinary path
            // the readers have already completed and this costs nothing.
            await reading.CancelAsync();
            try { await readers; }
            catch (OperationCanceledException) { /* asked for, just above */ }
        }
    }

    private async Task FillAsync(
        IReadOnlyList<WorkspaceWalker.Candidate> files,
        ChannelWriter<ReadFile> writer, CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = ReadAhead, CancellationToken = ct },
                async (candidate, token) => await writer.WriteAsync(await ReadOneAsync(candidate, token), token));
        }
        catch (OperationCanceledException) { /* the consumer stopped; nothing to record */ }
        finally
        {
            // Always, or the consumer waits on a channel nobody will finish.
            writer.Complete();
        }
    }

    private async Task<ReadFile> ReadOneAsync(WorkspaceWalker.Candidate candidate, CancellationToken ct)
    {
        try
        {
            // Its own scope, and so its own catalogue connection: this runs on several
            // threads at once and a DbContext belongs to one. The connections are SQLite
            // handles on the same WAL file, which is what the busy timeout is for.
            using var scope = scopes.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ExtractedTextCache>();

            var read = await cache.ReadAsync(
                candidate.FullPath, candidate.RelativePath,
                ExtractorRegistry.For(candidate.RelativePath),
                _indexing.ExtractionTimeoutSeconds, ct);
            return new ReadFile(candidate, read, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ReadFile(candidate, null, ex);
        }
    }
}
