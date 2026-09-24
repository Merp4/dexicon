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
    /// <summary>What was written to the response, readable while the stream is still writing.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();

        public string Text { get { lock (_gate) return _text.ToString(); } }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate) _text.Append(Encoding.UTF8.GetString(buffer, offset, count));
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_gate) _text.Append(Encoding.UTF8.GetString(buffer.Span));
            return ValueTask.CompletedTask;
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
            response, reader, new HashSet<string>(StringComparer.Ordinal) { "c1" },
            TimeSpan.FromMilliseconds(20), cts.Token);

        (await WaitFor(() => Count(body.Text, ": ping") >= 5, TimeSpan.FromSeconds(10)))
            .ShouldBeTrue("the premise: the stream sat idle through several pings");

        broadcaster.Publish(Report("j1"));
        broadcaster.Publish(Report("j2"));

        var arrived = await WaitFor(
            () => body.Text.Contains("\"jobId\":\"j1\"", StringComparison.Ordinal)
                  && body.Text.Contains("\"jobId\":\"j2\"", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

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
            response, reader, new HashSet<string>(StringComparer.Ordinal) { "c1" },
            TimeSpan.FromMilliseconds(20), cts.Token);

        broadcaster.Publish(Report("hidden", corpusId: "c2"));
        broadcaster.Publish(Report("shown"));

        var shown = await WaitFor(() => body.Text.Contains("\"jobId\":\"shown\"", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        try { await streaming; } catch (OperationCanceledException) { }

        shown.ShouldBeTrue();
        body.Text.ShouldNotContain("hidden");
    }
}
