using System.Text;
using Dexicon.Api;
using Dexicon.Core.Indexing;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// The live progress stream, over a connection that has been open a while.
///
/// Every page holds one of these for as long as it is open, and most of that time nothing
/// is indexing, so the stream spends most of its life sending pings. A report that
/// arrives after them has to reach the page like the first one did.
/// </summary>
public sealed class ProgressStreamTests
{
    /// <summary>
    /// What was written to the response, readable while the stream is still writing.
    /// <paramref name="perWrite"/> makes each write take that long, like a slow client.
    /// </summary>
    private sealed class RecordingStream(TimeSpan? perWrite = null) : Stream
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();

        public string Text { get { lock (_gate) return _text.ToString(); } }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate) _text.Append(Encoding.UTF8.GetString(buffer, offset, count));
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (perWrite is { } delay) await Task.Delay(delay, CancellationToken.None);
            Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (perWrite is { } delay) await Task.Delay(delay, CancellationToken.None);
            lock (_gate) _text.Append(Encoding.UTF8.GetString(buffer.Span));
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static IndexProgress Report(string jobId, string corpusId = "c1") =>
        new(jobId, corpusId, "extract", 16, 0, 16, 0, 0, null, null);

    /// <summary>What the caller can see, the same on every read.</summary>
    private static Func<CancellationToken, Task<HashSet<string>>> Fixed(params string[] ids) =>
        _ => Task.FromResult(ids.ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// What the caller can see as it stands at each read, counting the reads. Changed from
    /// the test while the stream runs, the way a corpus is created or a key's mapping edited.
    /// </summary>
    private sealed class Visibility(params string[] ids)
    {
        private readonly Lock _gate = new();
        private HashSet<string> _ids = ids.ToHashSet(StringComparer.Ordinal);
        private int _reads;

        public int Reads { get { lock (_gate) return _reads; } }

        public void Set(params string[] ids)
        {
            lock (_gate) _ids = ids.ToHashSet(StringComparer.Ordinal);
        }

        public Task<HashSet<string>> ReadAsync(CancellationToken _)
        {
            lock (_gate)
            {
                _reads++;
                return Task.FromResult(new HashSet<string>(_ids, StringComparer.Ordinal));
            }
        }
    }

    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a report may take to reach the page before the test calls it lost. These
    /// tests assert that it arrives, not how fast, and WaitFor returns the moment it does,
    /// so a long window costs a passing run nothing. Five seconds failed 2 of 60 CI runs,
    /// both on changes that did not touch the stream: on a two-core runner running test
    /// classes in parallel, some of them blocking pool threads on git's process I/O, a
    /// continuation can wait that long for a thread.
    /// </summary>
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(30);

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private static int Count(string text, string of)
    {
        var n = 0;
        for (var i = text.IndexOf(of, StringComparison.Ordinal); i >= 0; i = text.IndexOf(of, i + of.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>
    /// Each ping used to start a second read while the first was still waiting, and a
    /// channel hands an item to the oldest waiting read. So every ping before a report
    /// meant one report lost, on a connection that stayed open and kept pinging.
    /// </summary>
    [Fact]
    public async Task AReportAfterIdlePingsIsWritten()
    {
        var broadcaster = new IndexProgressBroadcaster();
        using var subscription = broadcaster.Subscribe(out var reader);
        var body = new RecordingStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cts = new CancellationTokenSource();

        var streaming = SystemEndpoints.StreamProgressAsync(
            response, reader, Fixed("c1"), TimeSpan.FromMilliseconds(20), Recheck, cts.Token);

        (await WaitFor(() => Count(body.Text, ": ping") >= 5, Arrival))
            .ShouldBeTrue("the premise: the stream sat idle through several pings");

        broadcaster.Publish(Report("j1"));
        broadcaster.Publish(Report("j2"));

        var arrived = await WaitFor(
            () => body.Text.Contains("\"jobId\":\"j1\"", StringComparison.Ordinal)
                  && body.Text.Contains("\"jobId\":\"j2\"", StringComparison.Ordinal),
            Arrival);

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        arrived.ShouldBeTrue($"both reports reach the page; the stream wrote:\n{body.Text}");
    }

    [Fact]
    public async Task AReportForACorpusTheCallerCannotReachIsNotWritten()
    {
        var broadcaster = new IndexProgressBroadcaster();
        using var subscription = broadcaster.Subscribe(out var reader);
        var body = new RecordingStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cts = new CancellationTokenSource();

        var streaming = SystemEndpoints.StreamProgressAsync(
            response, reader, Fixed("c1"), TimeSpan.FromMilliseconds(20), Recheck, cts.Token);

        broadcaster.Publish(Report("hidden", corpusId: "c2"));
        broadcaster.Publish(Report("shown"));

        var shown = await WaitFor(() => body.Text.Contains("\"jobId\":\"shown\"", StringComparison.Ordinal), Arrival);

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        shown.ShouldBeTrue();
        body.Text.ShouldNotContain("hidden");
    }

    /// <summary>
    /// A corpus created after the page connected. The set was read once, at connection, so
    /// its first index sent the page nothing and the page never re-read its counts.
    ///
    /// Inside the recheck window, which starts at the connection's own read: a corpus made
    /// a moment after the page connected, with a run that ends as quickly, has one report,
    /// and it has to be looked up rather than waited out.
    /// </summary>
    [Fact]
    public async Task ACorpusThatBecomesVisibleAfterTheStreamOpensIsWritten()
    {
        var broadcaster = new IndexProgressBroadcaster();
        using var subscription = broadcaster.Subscribe(out var reader);
        var body = new RecordingStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cts = new CancellationTokenSource();
        var visibility = new Visibility("c1");

        var streaming = SystemEndpoints.StreamProgressAsync(
            response, reader, visibility.ReadAsync, NoHeartbeat, TimeSpan.FromHours(1), cts.Token);

        (await WaitFor(() => visibility.Reads >= 1, Arrival)).ShouldBeTrue("the stream read what the caller can see");
        visibility.Set("c1", "c2");
        broadcaster.Publish(Report("new-corpus", corpusId: "c2"));

        var arrived = await WaitFor(() => body.Text.Contains("\"jobId\":\"new-corpus\"", StringComparison.Ordinal), Arrival);

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        arrived.ShouldBeTrue($"the new corpus's report reaches the page; the stream wrote:\n{body.Text}");
    }

    /// <summary>
    /// A key's mapping narrowed while its page is open. Read once, the corpus it lost kept
    /// reaching the page until it reconnected; a heartbeat re-reads it.
    /// </summary>
    [Fact]
    public async Task ACorpusThatStopsBeingVisibleIsNotWrittenAfterAHeartbeat()
    {
        var broadcaster = new IndexProgressBroadcaster();
        using var subscription = broadcaster.Subscribe(out var reader);
        var body = new RecordingStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cts = new CancellationTokenSource();
        var visibility = new Visibility("c1", "c2");

        var streaming = SystemEndpoints.StreamProgressAsync(
            response, reader, visibility.ReadAsync, TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1), cts.Token);

        (await WaitFor(() => visibility.Reads >= 1, Arrival)).ShouldBeTrue("the stream read what the caller can see");
        visibility.Set("c1");
        var pings = Count(body.Text, ": ping");
        (await WaitFor(() => Count(body.Text, ": ping") >= pings + 2, Arrival))
            .ShouldBeTrue("a heartbeat passed after the mapping changed");

        broadcaster.Publish(Report("revoked", corpusId: "c2"));
        broadcaster.Publish(Report("kept"));

        var kept = await WaitFor(() => body.Text.Contains("\"jobId\":\"kept\"", StringComparison.Ordinal), Arrival);

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        kept.ShouldBeTrue("the corpus still visible is written, so the stream was reading");
        body.Text.ShouldNotContain("revoked");
    }

    /// <summary>
    /// A corpus the caller cannot see reports as often as one they can. Re-reading on each
    /// of its reports would be a catalogue query per file indexed.
    /// </summary>
    [Fact]
    public async Task ReportsForAnUnseenCorpusDoNotEachReadTheCatalogue()
    {
        var broadcaster = new IndexProgressBroadcaster();
        using var subscription = broadcaster.Subscribe(out var reader);
        var body = new RecordingStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cts = new CancellationTokenSource();
        var visibility = new Visibility("c1");

        var streaming = SystemEndpoints.StreamProgressAsync(
            response, reader, visibility.ReadAsync, NoHeartbeat, TimeSpan.FromHours(1), cts.Token);

        (await WaitFor(() => visibility.Reads >= 1, Arrival)).ShouldBeTrue("the stream read what the caller can see");
        for (var i = 0; i < 40; i++) broadcaster.Publish(Report($"hidden-{i}", corpusId: "c9"));
        broadcaster.Publish(Report("last"));

        var done = await WaitFor(() => body.Text.Contains("\"jobId\":\"last\"", StringComparison.Ordinal), Arrival);

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        done.ShouldBeTrue("the stream got past the hidden reports");
        visibility.Reads.ShouldBe(2, "the read at connection and one for the corpus's first report, not one per report");
        body.Text.ShouldNotContain("hidden-");
    }

    /// <summary>
    /// A corpus that becomes visible after its reports were looked up and refused: a key's
    /// mapping widened while that corpus indexed. Its last report is the one the page acts on,
    /// and there may be no report after it to trigger another lookup.
    /// </summary>
    [Fact]
    public async Task TheNewestReportOfACorpusThatBecomesVisibleIsWrittenAtTheNextHeartbeat()
    {
        var broadcaster = new IndexProgressBroadcaster();
        using var subscription = broadcaster.Subscribe(out var reader);
        var body = new RecordingStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cts = new CancellationTokenSource();
        var visibility = new Visibility("c1");

        var streaming = SystemEndpoints.StreamProgressAsync(
            response, reader, visibility.ReadAsync, TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1), cts.Token);

        broadcaster.Publish(Report("running", corpusId: "c2"));
        broadcaster.Publish(Report("finished", corpusId: "c2"));
        (await WaitFor(() => visibility.Reads >= 2, Arrival)).ShouldBeTrue("the first report was looked up");
        visibility.Set("c1", "c2");

        var arrived = await WaitFor(() => body.Text.Contains("\"jobId\":\"finished\"", StringComparison.Ordinal), Arrival);

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        arrived.ShouldBeTrue($"the held report reaches the page; the stream wrote:\n{body.Text}");
        body.Text.ShouldNotContain("\"jobId\":\"running\"", customMessage: "only the newest, not a replay");
    }

    /// <summary>
    /// A backlog of reports. The read is then always complete, and WhenAny returns the first
    /// completed task in its list, so the heartbeat, and the re-read with it, waited until the
    /// backlog drained, while a revoked corpus's reports went on being written.
    /// </summary>
    [Fact]
    public async Task AHeartbeatIsNotStarvedByABacklogOfReports()
    {
        var broadcaster = new IndexProgressBroadcaster();
        using var subscription = broadcaster.Subscribe(out var reader);
        var body = new RecordingStream(perWrite: TimeSpan.FromMilliseconds(3));
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        using var cts = new CancellationTokenSource();
        var visibility = new Visibility("c1");

        for (var i = 0; i < 60; i++) broadcaster.Publish(Report($"queued-{i:00}"));

        var streaming = SystemEndpoints.StreamProgressAsync(
            response, reader, visibility.ReadAsync, TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1), cts.Token);

        var drained = await WaitFor(() => body.Text.Contains("\"jobId\":\"queued-59\"", StringComparison.Ordinal), Arrival);

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        drained.ShouldBeTrue("the premise: the whole backlog was written");
        var text = body.Text;
        var firstPing = text.IndexOf(": ping", StringComparison.Ordinal);
        firstPing.ShouldBeGreaterThanOrEqualTo(0, "a heartbeat was written");
        firstPing.ShouldBeLessThan(text.IndexOf("\"jobId\":\"queued-59\"", StringComparison.Ordinal),
            "a heartbeat came due while the backlog was being written, and was taken before it drained");
    }
}
