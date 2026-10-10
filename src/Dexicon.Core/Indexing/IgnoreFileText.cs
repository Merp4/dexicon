using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Reads the text of an ignore file. It opens only a file that can seek, reads at most <see cref="MaxBytes"/> + 1 bytes of it,
/// and decodes it as <c>File.ReadAllLines</c> does: UTF-8 unless a byte order mark says UTF-16 or UTF-32. A NUL in
/// the text is refused, and for a file that must be exact (<c>.dexiconignore</c>) so are bytes that are not valid in
/// the encoding, in every encoding. A reason is an <see cref="InvalidDataException"/> whose message completes "the
/// file ...".
/// </summary>
internal static class IgnoreFileText
{
    /// <summary>The largest ignore file read, in bytes.</summary>
    internal const int MaxBytes = 1024 * 1024;

    /// <param name="strict">Bytes that are not valid in the file's encoding are refused and not replaced.</param>
    /// <param name="bytesRead">
    /// Increased by the bytes read, including those of a file that is then refused, so a walk can bound what it reads.
    /// </param>
    /// <exception cref="IgnoreFileTooLargeException">The file is larger than <see cref="MaxBytes"/>.</exception>
    internal static string Read(string path, bool strict, ref long bytesRead)
    {
        byte[] bytes;
        using (var stream = OpenRegularFile(path))
        {
            // Sized from the length so a small file costs a small buffer, and read until the end or one byte past
            // the limit, whatever the file grows to.
            var held = new MemoryStream((int)Math.Min(stream.Length + 1, MaxBytes + 1));
            var chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxBytes + 1 - held.Length))) > 0)
                held.Write(chunk, 0, read);

            bytesRead += held.Length;
            if (held.Length > MaxBytes)
                throw new IgnoreFileTooLargeException($"is larger than {MaxBytes / (1024 * 1024)} MiB");

            bytes = held.ToArray();
        }

        var (encoding, label, skip) = EncodingOf(bytes, strict);

        string text;
        try
        {
            text = encoding.GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"is not valid {label}", ex);
        }

        if (text.Contains('\0', StringComparison.Ordinal)) throw new InvalidDataException("contains a NUL character");

        return text;
    }

    /// <summary>The encoding a byte order mark names, the way <see cref="StreamReader"/> detects it, and the mark's length.</summary>
    private static (Encoding Encoding, string Label, int Skip) EncodingOf(byte[] bytes, bool strict) =>
        bytes switch
        {
            [0xFF, 0xFE, 0, 0, ..] => (new UTF32Encoding(false, false, strict), "UTF-32", 4),
            [0xFF, 0xFE, ..] => (new UnicodeEncoding(false, false, strict), "UTF-16", 2),
            [0xFE, 0xFF, ..] => (new UnicodeEncoding(true, false, strict), "UTF-16", 2),
            [0, 0, 0xFE, 0xFF, ..] => (new UTF32Encoding(true, false, strict), "UTF-32", 4),
            [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(false, strict), "UTF-8", 3),
            _ => (new UTF8Encoding(false, strict), "UTF-8", 0),
        };

    /// <summary>
    /// Opens <paramref name="path"/> for reading if the handle can seek. On Linux and macOS the open is made
    /// non-blocking (<c>O_NONBLOCK</c>), because a named pipe opened the usual way blocks until something writes to
    /// it, and a handle that cannot seek is refused. Windows has no such files in a directory listing.
    /// </summary>
    private static FileStream OpenRegularFile(string path)
    {
        FileStream? stream = null;

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var flags = OperatingSystem.IsLinux() ? LinuxNonBlocking | LinuxCloseOnExec : MacNonBlocking | MacCloseOnExec;
            var descriptor = Open(path, flags);
            if (descriptor >= 0) stream = new FileStream(new SafeFileHandle(descriptor, ownsHandle: true), FileAccess.Read, 1, false);
        }

        stream ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);

        if (stream.CanSeek) return stream;

        stream.Dispose();
        throw new InvalidDataException("is not a regular file");
    }

    private const int LinuxNonBlocking = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private const int MacNonBlocking = 0x4;
    private const int MacCloseOnExec = 0x1000000;

    // DllImport and not LibraryImport, which would need unsafe code enabled for the project.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}

/// <summary>An ignore file past <see cref="IgnoreFileText.MaxBytes"/>, which fails a walk for every kind of file.</summary>
internal sealed class IgnoreFileTooLargeException(string message) : IOException(message);
