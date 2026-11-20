using System.Threading.Channels;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Dexicon.Core.Search;

/// <summary>
/// Where a finished search is recorded. Takes the row and returns at once: a search never waits on its own
/// record, and a record that cannot be kept is counted and dropped rather than raised.
/// </summary>
public interface ISearchLog
{
    void Record(SearchLogRow row);
}

/// <summary>Records nothing. What a <see cref="SearchService"/> built without a log uses.</summary>
public sealed class NullSearchLog : ISearchLog
{
    public static readonly NullSearchLog Instance = new();

    public void Record(SearchLogRow row)
    {
    }
}

/// <summary>
/// The queue between searches and <see cref="SearchLogWriter"/>. Bounded, because the writer shares the
/// catalogue's single writer with indexing and a long wait there must not become memory: when the queue is
/// full the new row is dropped, and <see cref="Dropped"/> counts it for the Searches screen.
///
/// The text policy lives here, in one place, so no caller decides what is kept: the query is cut at
/// <see cref="MaxQueryChars"/> characters, and removed when <c>StoreQueryText</c> is off.
/// </summary>
public sealed class SearchLog(IOptions<DexiconOptions> options) : ISearchLog
{
    /// <summary>
    /// Characters of a query kept. A prompt sent by the context hook can be many thousands, and the first part says
    /// what it was about.
    /// </summary>
    public const int MaxQueryChars = 500;

    /// <summary>Characters of the list of corpora kept. Names are at most 200 each, so a scope of several is cut.</summary>
    public const int MaxCorporaChars = 500;

    /// <summary>Rows waiting for the writer. A search every few seconds never nears it.</summary>
    internal const int Capacity = 1_000;

    private readonly Channel<SearchLogRow> _queue = Channel.CreateBounded<SearchLogRow>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private long _dropped;

    internal ChannelReader<SearchLogRow> Reader => _queue.Reader;

    /// <summary>Rows dropped since the host started: the queue was full, or the catalogue refused the write.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    internal void CountDropped(int rows) => Interlocked.Add(ref _dropped, rows);

    public void Record(SearchLogRow row)
    {
        var settings = options.Value.SearchLog;
        if (!settings.Enabled) return;

        row.Query = settings.StoreQueryText ? Cut(row.Query, MaxQueryChars) : null;
        row.Corpora = Cut(row.Corpora, MaxCorporaChars) ?? string.Empty;

        // Wait mode with TryWrite refuses when full instead of blocking, which is the drop.
        if (!_queue.Writer.TryWrite(row)) CountDropped(1);
    }

    /// <summary>The text cut at <paramref name="max"/> characters, never between the halves of a surrogate pair.</summary>
    internal static string? Cut(string? text, int max)
    {
        if (text is null || text.Length <= max) return text;

        var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end];
    }
}
