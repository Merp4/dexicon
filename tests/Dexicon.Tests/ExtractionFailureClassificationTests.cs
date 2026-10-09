using Dexicon.Core.Extraction;

namespace Dexicon.Tests;

/// <summary>
/// Whether an exception an extractor met is a verdict on the file or a fact about the host
/// (<see cref="ExtractionFailures.Of"/>). A verdict is stored with the document; anything else leaves it
/// to be tried again.
/// </summary>
public sealed class ExtractionFailureClassificationTests
{
    private static readonly InvalidDataException Corrupt = new("bad block");

    private static readonly IOException Disk = new("disk fault");

    private static ExtractionFailedException Classify(Exception cause) => ExtractionFailures.Of("could not be read", cause);

    private static void ShouldBeAVerdict(Exception cause) => Classify(cause).ShouldBeOfType<UnreadableDocumentException>();

    private static void ShouldBeEnvironmental(Exception cause) =>
        Classify(cause).ShouldBeOfType<ExtractionFailedException>();

    [Fact]
    public void AnIoErrorIsEnvironmental() => ShouldBeEnvironmental(Disk);

    [Fact]
    public void EndOfStreamIsAVerdictOnTheBytes() => ShouldBeAVerdict(new EndOfStreamException());

    [Fact]
    public void EndOfStreamUnderAParserExceptionIsAVerdictOnTheBytes() =>
        ShouldBeAVerdict(new InvalidDataException("truncated", new EndOfStreamException()));

    [Fact]
    public void AnIoErrorUnderAParserExceptionIsEnvironmental() =>
        ShouldBeEnvironmental(new InvalidDataException("failed to parse", Disk));

    [Fact]
    public void AnAggregateWithAnIoErrorSecondIsEnvironmental() => ShouldBeEnvironmental(new AggregateException(Corrupt, Disk));

    [Fact]
    public void AnAggregateWithAnIoErrorFirstIsEnvironmental() => ShouldBeEnvironmental(new AggregateException(Disk, Corrupt));

    [Fact]
    public void AnAggregateOfParserExceptionsIsAVerdict() =>
        ShouldBeAVerdict(new AggregateException(Corrupt, new FormatException("bad")));

    [Fact]
    public void AnAggregateWithAnIoErrorBesideEndOfStreamIsEnvironmental() =>
        ShouldBeEnvironmental(new AggregateException(new EndOfStreamException(), Disk));

    [Fact]
    public void AnExplicitVerdictOverAnIoErrorIsAVerdict()
    {
        // Code that read the file said so, and the I/O error under it is only how the parser noticed.
        ShouldBeAVerdict(new UnreadableDocumentException("not a readable .docx", Disk));
    }

    [Fact]
    public void AnExtractionFailureThatIsNotAVerdictStaysEnvironmental() =>
        ShouldBeEnvironmental(new ExtractionFailedException("could not be read", Disk));

    [Fact]
    public void ATimeoutUnderAnExplicitVerdictIsEnvironmental() =>
        ShouldBeEnvironmental(new UnreadableDocumentException("not a readable .docx", new ExtractionTimeoutException("too slow")));

    [Fact]
    public void ATimeoutBesideAnExplicitVerdictInAnAggregateIsEnvironmental() =>
        ShouldBeEnvironmental(new AggregateException(
            new UnreadableDocumentException("not a readable .docx"), new TimeoutException("too slow")));

    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(KeyNotFoundException))]
    public void AVerdictMadeFromAFaultInTheReaderIsMarkedUnexpected(Type fault)
    {
        var cause = fault == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("stream")
            : (Exception)Activator.CreateInstance(fault)!;

        Classify(cause).ShouldBeOfType<UnreadableDocumentException>().Unexpected.ShouldBeTrue();
    }

    [Fact]
    public void AVerdictMadeFromAMalformedFileIsNotMarkedUnexpected()
    {
        Classify(Corrupt).ShouldBeOfType<UnreadableDocumentException>().Unexpected.ShouldBeFalse();
        Classify(new FormatException("bad")).ShouldBeOfType<UnreadableDocumentException>().Unexpected.ShouldBeFalse();
    }

    [Fact]
    public void AnExplicitVerdictAroundAFaultIsNotMarkedUnexpected() =>
        Classify(new UnreadableDocumentException("not a readable .docx", Activator.CreateInstance<NullReferenceException>()))
            .ShouldBeOfType<UnreadableDocumentException>().Unexpected.ShouldBeFalse();
}
