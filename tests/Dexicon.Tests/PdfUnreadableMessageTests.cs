using System.Text;
using Dexicon.Core.Extraction;
using Shouldly;

namespace Dexicon.Tests;

/// <summary>
/// What a PDF that cannot be read says about itself. The message is the status detail on the corpus
/// page and the Documents screen, so it names the cause the library found rather than a guess.
/// </summary>
public sealed class PdfUnreadableMessageTests
{
    /// <summary>
    /// A one-page PDF written by pypdf and encrypted with a user password (RC4, revision 3). Synthetic:
    /// its only text is "Locked report" and "secret body".
    /// </summary>
    private const string EncryptedPdf =
        "JVBERi0xLjQKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgPGM4OWFhNDdlMjU+Cj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9UeXBl" +
        "IC9QYWdlcwovQ291bnQgMQovS2lkcyBbIDQgMCBSIF0KPj4KZW5kb2JqCjMgMCBvYmoKPDwKL1R5cGUgL0NhdGFsb2cKL1BhZ2Vz" +
        "IDIgMCBSCi9PdXRsaW5lcyA3IDAgUgo+PgplbmRvYmoKNCAwIG9iago8PAovVHlwZSAvUGFnZQovTWVkaWFCb3ggWyAwIDAgNjEy" +
        "IDc5MiBdCi9SZXNvdXJjZXMgPDwKL0ZvbnQgPDwKL0YxIDUgMCBSCj4+Cj4+Ci9Db250ZW50cyA2IDAgUgovUGFyZW50IDIgMCBS" +
        "Cj4+CmVuZG9iago1IDAgb2JqCjw8Ci9UeXBlIC9Gb250Ci9TdWJ0eXBlIC9UeXBlMQovQmFzZUZvbnQgL0hlbHZldGljYQo+Pgpl" +
        "bmRvYmoKNiAwIG9iago8PAovTGVuZ3RoIDczCj4+CnN0cmVhbQqrFFO7VwWkzaZxWrV4Spa5ob3tFniGhvwVa7av7XSVMJmYqvzs" +
        "fEs2KZQyuRPGUSe+aL0uRht30mokmeZC5ZpAkp57/is+JsMvCmVuZHN0cmVhbQplbmRvYmoKNyAwIG9iago8PAo+PgplbmRvYmoK" +
        "OCAwIG9iago8PAovViAyCi9SIDMKL0xlbmd0aCAxMjgKL1AgNDI5NDk2NzI5MgovRmlsdGVyIC9TdGFuZGFyZAovTyA8NDE0ZWU5" +
        "OWYxNTFlYThlYTM3MWM4MjBjODRmY2IzZGUxZDdhMDM0ZDRiNDNkZDExYTRmMDBlOTgxYzUzNmJiMj4KL1UgPDRjNGE4ODgzMjUw" +
        "MGZiNmU4NDIyZDY0M2EzZWJjYWRmMjhiZjRlNWU0ZTc1OGE0MTY0MDA0ZTU2ZmZmYTAxMDg+Cj4+CmVuZG9iagp4cmVmCjAgOQow" +
        "MDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMTUgMDAwMDAgbiAKMDAwMDAwMDA1OSAwMDAwMCBuIAowMDAwMDAwMTE4IDAwMDAw" +
        "IG4gCjAwMDAwMDAxODMgMDAwMDAgbiAKMDAwMDAwMDMxMSAwMDAwMCBuIAowMDAwMDAwMzgxIDAwMDAwIG4gCjAwMDAwMDA1MDQg" +
        "MDAwMDAgbiAKMDAwMDAwMDUyNSAwMDAwMCBuIAp0cmFpbGVyCjw8Ci9TaXplIDkKL1Jvb3QgMyAwIFIKL0luZm8gMSAwIFIKL0lE" +
        "IFsgPDY1MzgzMzMxMzAzMDM4MzkzNTYxMzQ2NjMxMzIzODMzMzMzNjMzNjQ2MTMzMzMzNzM4MzkzNTM0MzY2MjM4MzY+IDw2NTM4" +
        "MzMzMTMwMzAzODM5MzU2MTM0NjYzMTMyMzgzMzMzMzYzMzY0NjEzMzMzMzczODM5MzUzNDM2NjIzODM2PiBdCi9FbmNyeXB0IDgg" +
        "MCBSCj4+CnN0YXJ0eHJlZgo3NDAKJSVFT0YK";

    [Fact]
    public void AnEncryptedPdfSaysItIsEncryptedRatherThanEncryptedOrCorrupt()
    {
        var ex = Should.Throw<UnreadableDocumentException>(() =>
            new PdfTextExtractor().Extract(new MemoryStream(Convert.FromBase64String(EncryptedPdf)), "locked.pdf"));

        ex.Message.ShouldBe("'locked.pdf' is encrypted with a password, so its text cannot be read. "
            + "To index it, use a copy saved without the password.");
    }

    [Fact]
    public void ATruncatedPdfSaysItIsIncompleteAndWasNotParsed()
    {
        var truncated = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R\n");

        var ex = Should.Throw<UnreadableDocumentException>(() =>
            new PdfTextExtractor().Extract(new MemoryStream(truncated), "corrupt.pdf"));

        ex.Message.ShouldBe(
            "'corrupt.pdf' is incomplete or damaged: the end of the PDF is missing, which usually means a download "
            + "or copy was cut short. Download or copy it again. "
            + $"(Not parsed: its {truncated.Length} bytes do not hold both startxref and %%EOF.)");
    }

    [Fact]
    public void AOneBytePdfSaysOneByte()
    {
        var ex = Should.Throw<UnreadableDocumentException>(() =>
            new PdfTextExtractor().Extract(new MemoryStream("%"u8.ToArray()), "one.pdf"));

        ex.Message.ShouldEndWith("(Not parsed: its one byte does not hold both startxref and %%EOF.)");
    }

    [Fact]
    public void ATruncatedPdfLongerThanTheTailSaysHowMuchOfItWasRead()
    {
        // Past 4,096 bytes only the tail is read, and the message says which part.
        var truncated = Encoding.ASCII.GetBytes("%PDF-1.4\n" + new string('x', 10_000));

        var ex = Should.Throw<UnreadableDocumentException>(() =>
            new PdfTextExtractor().Extract(new MemoryStream(truncated), "cut.pdf"));

        ex.Message.ShouldEndWith("(Not parsed: the last 4,096 of its 10,009 bytes do not hold both startxref and %%EOF.)");
    }
}
