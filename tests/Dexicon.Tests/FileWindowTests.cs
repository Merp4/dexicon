namespace Dexicon.Tests;

/// <summary>
/// Reading a file that is bigger than one response.
///
/// The viewer used to return the first 400,000 characters with "…(truncated)" glued on and
/// no way to ask for the rest. Fine for a source file; useless for a book. A 700-page
/// technical book runs to two or three million characters, so the reader got the first
/// chapter or two and the rest of the book was unreachable — which is what "some books
/// show as truncated" looked like from the outside.
///
/// The arithmetic below is the endpoint's, extracted so it can be checked without standing
/// up a server and a vector store. What matters is that the WINDOW's line numbers are the
/// window's own: reporting the whole file's range while showing a fraction of it is how
/// the header came to say "lines 1–40,521" over about five thousand lines of text.
/// </summary>
public class FileWindowTests
{
    private const int WindowChars = 400_000;

    /// <summary>Mirrors the endpoint: offset, window, and the window's line range.</summary>
    private static (string Text, int Start, int End, bool More, int? Next) Window(
        string text, int firstLine, int? start)
    {
        var total = text.Length;
        var offset = Math.Clamp(start ?? 0, 0, Math.Max(total - 1, 0));
        var window = text.Substring(offset, Math.Min(WindowChars, total - offset));
        var more = offset + window.Length < total;
        var windowStart = firstLine + text.AsSpan(0, offset).Count('\n');
        var windowEnd = windowStart + window.AsSpan().Count('\n');
        return (window, windowStart, windowEnd, more, more ? offset + window.Length : null);
    }

    private static string Lines(int n) =>
        string.Join('\n', Enumerable.Range(1, n).Select(i => $"line {i}"));

    [Fact]
    public void A_short_file_comes_back_whole_and_offers_nothing_more()
    {
        var (text, start, end, more, next) = Window(Lines(4), firstLine: 1, start: null);

        text.ShouldBe(Lines(4));
        start.ShouldBe(1);
        end.ShouldBe(4);
        more.ShouldBeFalse();
        next.ShouldBeNull();
    }

    [Fact]
    public void The_windows_line_numbers_are_the_windows_own()
    {
        // The bug this replaces: the header reported the whole file's range over a
        // fraction of its text, so every line number after the first screen was wrong.
        var file = Lines(100);
        var offsetOfLine51 = file.IndexOf("line 51", StringComparison.Ordinal);

        var (_, start, _, _, _) = Window(file, firstLine: 1, start: offsetOfLine51);

        start.ShouldBe(51);
    }

    [Fact]
    public void A_files_own_first_line_is_carried_into_the_window()
    {
        // Chunks do not have to start at line 1 — an indexed file can begin further in.
        var (_, start, end, _, _) = Window(Lines(3), firstLine: 900, start: null);

        start.ShouldBe(900);
        end.ShouldBe(902);
    }

    [Fact]
    public void Reading_on_continues_exactly_where_the_last_window_stopped()
    {
        // No gap and no repeat: the offset handed back must be the next character, or the
        // reader silently loses or re-reads a screenful.
        var file = new string('a', WindowChars) + "TAIL";

        var first = Window(file, firstLine: 1, start: null);
        first.More.ShouldBeTrue();
        first.Next.ShouldBe(WindowChars);

        var second = Window(file, firstLine: 1, start: first.Next);
        second.Text.ShouldBe("TAIL");
        second.More.ShouldBeFalse();
        (first.Text + second.Text).ShouldBe(file);
    }

    [Fact]
    public void An_offset_past_the_end_does_not_throw()
    {
        // A stale client, or a file that shrank between two requests.
        var (text, _, _, more, _) = Window(Lines(3), firstLine: 1, start: 10_000);

        text.ShouldNotBeNull();
        more.ShouldBeFalse();
    }

    [Fact]
    public void A_negative_offset_is_treated_as_the_beginning()
    {
        var (text, start, _, _, _) = Window(Lines(3), firstLine: 1, start: -5);

        start.ShouldBe(1);
        text.ShouldStartWith("line 1");
    }

    [Fact]
    public void An_empty_file_does_not_throw()
    {
        var (text, _, _, more, next) = Window(string.Empty, firstLine: 1, start: null);

        text.ShouldBe(string.Empty);
        more.ShouldBeFalse();
        next.ShouldBeNull();
    }

    [Fact]
    public void Every_window_of_a_long_file_joins_back_into_the_whole_file()
    {
        // The property that matters: paging must be lossless. A book read end to end
        // through this has to equal the book.
        var file = string.Join('\n', Enumerable.Range(1, 200_000).Select(i => $"line {i}"));
        file.Length.ShouldBeGreaterThan(WindowChars * 2);

        var rebuilt = new System.Text.StringBuilder();
        int? next = null;
        var windows = 0;

        do
        {
            var w = Window(file, firstLine: 1, start: next);
            rebuilt.Append(w.Text);
            next = w.Next;
            windows++;
        } while (next is not null && windows < 100);

        windows.ShouldBeGreaterThan(2);
        rebuilt.ToString().ShouldBe(file);
    }
}
