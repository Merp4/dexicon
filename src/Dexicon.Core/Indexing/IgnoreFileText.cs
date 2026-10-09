using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dexicon.Core.Indexing;

/// <summary>
/// Reads the text of an ignore file. It opens only a regular file, reads at most <see cref="MaxBytes"/> of it, and
/// decodes it as <c>File.ReadAllLines</c> does: UTF-8 unless a byte order mark says UTF-16 or UTF-32. A NUL in the text
/// is refused, and for a file that must be exact (<c>.dexiconignore</c>) so are bytes that are not valid UTF-8.
/// A reason is an <see cref="InvalidDataException"/> whose message completes "the file ...".
/// </summary>
internal static class IgnoreFileText
{
    /// <summary>The largest ignore file read, in bytes. The rule limit (5,000 rules of 200 bytes) is a megabyte.</summary>
    internal const int MaxBytes = 1024 * 1024;

    /// <param name="strict">Bytes that are not valid UTF-8 are refused and not replaced.</param>
    /// <param name="byteCount">How many bytes the file held.</param>
    internal static string Read(string path, bool strict, out int byteCount)
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

            if (held.Length > MaxBytes) throw new InvalidDataException($"is larger than {MaxBytes / (1024 * 1024)} MiB");

            bytes = held.ToArray();
        }

        byteCount = bytes.Length;

        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, strict), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("is not valid UTF-8", ex);
        }

        if (text.Contains('\0', StringComparison.Ordinal)) throw new InvalidDataException("contains a NUL character");

        return text;
    }

    /// <summary>
    /// Opens <paramref name="path"/> for reading if it is a regular file. A named pipe opened the usual way blocks until
    /// something writes to it, so on Linux and macOS the open asks not to block, and a handle that cannot seek is
    /// refused. Windows has no such files in a directory listing.
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
