using System.Diagnostics;

namespace Dexicon.Core.Extraction;

/// <summary>
/// A read-only view over another stream that stops answering once a deadline passes.
///
/// Extraction is synchronous and takes no cancellation token, because the libraries
/// underneath it do not: <c>PdfDocument.Open</c> and the OpenXML readers are ordinary
/// blocking calls. Cancelling an index job therefore could not interrupt one, and a
/// single file could hold the corpus indefinitely. One did: a truncated 68 MB PDF with
/// no cross-reference table sent PdfPig into a brute-force backward scan of the whole
/// file, one byte per 4 KB read, which over a 9p bind mount ran at about 6,000 reads a
/// second and would have taken 3.3 hours for that file alone, with the job's queue
/// stopped behind it.
///
/// Every one of those reads passes through here. Checking the clock on each one turns a
/// library with no cancellation support into one that unwinds within a read of the
/// deadline, so the thread is returned rather than left running until the process ends.
/// A wrapper that only reported the timeout to the caller would have left the thread
/// where it was, which is the case this exists to prevent.
///
/// The throw is permanent rather than one-shot: a library that catches broadly and
/// retries meets the same exception on its next read instead of resuming.
/// </summary>
public sealed class DeadlineStream(Stream inner, TimeSpan budget, string fileName) : Stream
{
    private readonly long _deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);

    /// <summary>Set once the deadline is hit, so the caller can tell a timeout from a parse error.</summary>
    public bool Expired { get; private set; }

    private void ThrowIfExpired()
    {
        if (!Expired && Stopwatch.GetTimestamp() < _deadline) return;

        Expired = true;
        throw new ExtractionTimeoutException(
            $"'{fileName}' was still being read after {budget.TotalSeconds:N0}s and was "
            + "abandoned. A document this slow is usually structurally broken: a PDF with "
            + "no cross-reference table is searched byte by byte.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfExpired();
        return inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfExpired();
        return inner.Read(buffer);
    }

    public override int ReadByte()
    {
        ThrowIfExpired();
        return inner.ReadByte();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfExpired();
        return inner.Seek(offset, origin);
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            ThrowIfExpired();
            inner.Position = value;
        }
    }

    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // The inner stream is owned by the caller: this wraps a `using` FileStream in the
    // indexer, so it is deliberately not disposed here. Only the base call is made.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

/// <summary>
/// Raised when a file took longer to read than the extraction budget. Separate from
/// <see cref="ExtractionFailedException"/> at the throw site so the message can say the
/// file ran out of time rather than that it could not be parsed, which is what the
/// generic PDF-open handler would otherwise report.
/// </summary>
public sealed class ExtractionTimeoutException(string message) : Exception(message);
