using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dexicon.Tests;

/// <summary>
/// What Dexicon indexes does not belong in what Dexicon publishes.
///
/// This repository is public; the libraries it is pointed at are not. Illustrative
/// examples had been taken from a real private shelf, so comments, docs and fixtures
/// named a publisher, a third-party repository and real book titles — none of which
/// carried any of the points they were making, and any of which reads as an association
/// the project has not made.
///
/// The rule is in CONTRIBUTING.md. This is the part that holds it, because a rule in a
/// document is a rule nobody runs.
///
/// It is names, not numbers. "a 1,834-document corpus" and "an intact 84 MB PDF at
/// 13.4s" are measurements worth keeping and say nothing about whose shelf they came
/// from.
///
/// The names are held as hashes. Written out, even split across string joins, the list
/// would be the thing the rule forbids, in the file every reader of the rule opens. Each
/// is the SHA-256 of the name's words: lowercased, and every run of characters that are
/// not a letter or a digit reduced to one space. To add one, hash the name as written with
/// the same normalisation, or call <see cref="HashOfName"/>:
///
///   printf '%s' 'The Name, As Written' | tr 'A-Z' 'a-z' | tr -cs 'a-z0-9' ' ' \
///     | sed 's/^ //; s/ $//' | tr -d '\n' | sha256sum
///
/// Hashing the name exactly as typed gives a hash the scan never produces, so the name
/// would stay unguarded with nothing failing to say so.
///
/// Holding no names, this file is scanned like any other.
/// </summary>
public sealed class IndexedContentIsNotPublishedTests
{
    /// <summary>
    /// A forbidden name. One that matches by words is found as <paramref name="Length"/>
    /// consecutive words of the text, so it is not found inside a longer word, and a name
    /// split by a path separator, a hyphen, an apostrophe, a change of case or a line break
    /// still matches. One that matches within a word is found as <paramref name="Length"/>
    /// characters anywhere in a word, for an identifier that turns up joined to others, as
    /// in a camel-cased path.
    /// </summary>
    internal sealed record Name(string What, int Length, bool WithinWord, string Sha256);

    private static Name Words(string what, int words, string sha256) => new(what, words, false, sha256);

    private static Name InWord(string what, int chars, string sha256) => new(what, chars, true, sha256);

    /// <summary>
    /// What must not reappear. Deliberately specific: a general "looks like a book title"
    /// rule would fire on prose and be turned off within a week, which is also why a title
    /// that is an ordinary two-word phrase is not here.
    /// </summary>
    internal static readonly Name[] Forbidden =
    [
        Words("a publisher's shorthand", 1, "4e79f3a89a553a9af66bec6b8b223f14f56a19f2e24270502517b466c3699370"),
        Words("a publisher", 2, "47ba0e7b8d53a2103ccc1d53514954e1c4e879c5320d1f35ac6fbda22504a915"),
        Words("a real book title", 3, "c2e0fca0b66bd9623afcc4f2f649726b1e126df0068732b300d7799878696363"),
        Words("a real book title", 5, "676e57905882b3ede4f152088a723f21734e64698860c47f0b9e69af31d0fea0"),
        Words("a real book title", 4, "59cd5e9716986f5eb671c149ede4fa7e07c42a365bfb0a627d67c59c24c4170b"),
        Words("a real book title", 4, "505531287cf4bef5d5d87cb149a02072c49acdb52a930810c6fe7154473c3426"),
        Words("a real book title", 6, "65728f8be524ebb8b1702939a0fbdb571227a542710bedc74320a8a4cfe73135"),
        Words("a real book title", 3, "2f8df0199964e2079a2a4284f9250b2414adb6b1cbc3b2f0b7ded5d551d289ec"),
        Words("a real book title", 4, "f5d1b33f427b6d977d1611c88d903869304425823dc864bf7045802744d75d28"),
        Words("a real book title", 7, "d58bdde431a3b6eb00b34705a3339589a92ecaeb7bf5a97e404ae0dcbb647e5b"),
        Words("a real paper title", 4, "20b359201172297cc4d5a8717a876614b0faf056aadd9339fcdc18c2bd72ad1f"),
        Words("a third-party repository", 1, "1b2ffa27d8046b022abb6d26312a80064c1ef2774e3d49bcb5dc05bba0176466"),
        InWord("another project of the author's", 10, "007fb394c348b4f260ed810e2164d830cac6310bd753dccd02d6c157ee8b857c"),
        InWord("a path from a real mount", 7, "07d7d21b603b5d0d8b7499e2d33ec308d96422f33b59f4c418269299fd25a799"),
    ];

    private static readonly string[] Extensions =
        [".cs", ".md", ".ts", ".tsx", ".mjs", ".css", ".py", ".json", ".yml", ".yaml", ".props",
         ".csproj", ".xml", ".toml", ".html", ".txt", ".sh", ".ps1", ".example"];

    private static readonly string[] SkipDirectories =
        ["obj", "bin", "node_modules", ".git", "dist", "generated", ".claude", "TestResults"];

    private static readonly Regex NotALetterOrDigit = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// A name's hash as the list holds it, from the name as written: capitals, punctuation
    /// and spacing are normalised first, exactly as the scan normalises the text it reads.
    /// </summary>
    internal static string HashOfName(string name) => HashOfWords(Normalise(name));

    /// <summary>
    /// The hash of words already normalised, which is what the scan builds. Private, because
    /// given a name as written it returns a hash nothing will ever match.
    /// </summary>
    private static string HashOfWords(string normalised) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));

    /// <summary>A name's words as the hash is taken of them.</summary>
    private static string Normalise(string text) =>
        string.Join(' ', NotALetterOrDigit.Split(text.ToLowerInvariant()).Where(w => w.Length > 0));

    /// <summary>
    /// Every place in <paramref name="lines"/> one of <paramref name="names"/> occurs, with
    /// the line it starts on. The text is read as one run of words, not line by line, so a
    /// title wrapped across two comment lines is still one title.
    /// </summary>
    internal static List<(Name Name, int Line)> Find(string[] lines, IReadOnlyList<Name> names)
    {
        var words = new List<(string Word, int Line)>();
        for (var i = 0; i < lines.Length; i++)
            foreach (var w in NotALetterOrDigit.Split(lines[i].ToLowerInvariant()))
                if (w.Length > 0) words.Add((w, i + 1));

        var found = new List<(Name, int)>();
        var window = new StringBuilder();

        foreach (var byLength in names.Where(n => !n.WithinWord).GroupBy(n => n.Length))
        {
            var wanted = byLength.ToDictionary(n => n.Sha256, StringComparer.Ordinal);
            for (var i = 0; i + byLength.Key <= words.Count; i++)
            {
                window.Clear();
                for (var j = 0; j < byLength.Key; j++)
                    window.Append(j == 0 ? "" : " ").Append(words[i + j].Word);

                if (wanted.TryGetValue(HashOfWords(window.ToString()), out var name))
                    found.Add((name, words[i].Line));
            }
        }

        foreach (var byLength in names.Where(n => n.WithinWord).GroupBy(n => n.Length))
        {
            var wanted = byLength.ToDictionary(n => n.Sha256, StringComparer.Ordinal);
            foreach (var (word, line) in words)
                for (var k = 0; k + byLength.Key <= word.Length; k++)
                    if (wanted.TryGetValue(HashOfWords(word.Substring(k, byLength.Key)), out var name))
                        found.Add((name, line));
        }

        return found;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dexicon.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static List<string> TrackedTextFiles(string root) =>
    [
        .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => SkipDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)))
    ];

    [Fact]
    public void NoIndexedContentNamesAnywhereInTheRepository()
    {
        var root = RepoRoot();
        var files = TrackedTextFiles(root);

        // The scan asserts its own reach before it reports a clean result. The first
        // version of this sweep ran from a worktree living under a `.claude` directory
        // and tested every part of the ABSOLUTE path against the skip list, so it read
        // nothing and reported zero occurrences — which is what real success looks like.
        files.Count.ShouldBeGreaterThan(100,
            "the filter is wrong, not the tree: a scan that reads nothing always passes");

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var (name, line) in Find(File.ReadAllLines(file), Forbidden))
                offenders.Add($"{relative}:{line} — {name.What}");
        }

        offenders.ShouldBeEmpty(
            "this repository is public and the content it indexes is not. Use an invented "
            + "name; keep the measurement, drop what identifies the library. See "
            + $"CONTRIBUTING.md.\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// The matching, shown on invented names so that this file names nothing real.
    ///
    /// A name matched by words is left alone inside a longer word: that is what keeps an
    /// ordinary English word containing a shorthand's letters from failing the build. It is
    /// still found across a path separator, a hyphen, a change of case and a line break. A
    /// name matched within a word is found inside a longer identifier.
    /// </summary>
    [Fact]
    public void NamesMatchAsWholeWordsUnlessTheyMatchWithinAWord()
    {
        Name[] names =
        [
            Words("an invented shorthand", 1, HashOfName("zorbl")),
            Words("an invented title", 3, HashOfName("Tidewater Clerk Almanac")),
            InWord("an invented project", "quillwort".Length, HashOfName("quillwort")),
        ];

        string[] Found(params string[] lines) => [.. Find(lines, names).Select(f => f.Name.What)];

        Found("unzorbly written").ShouldBeEmpty();
        Found("books/ZORBL/AI").ShouldBe(["an invented shorthand"]);
        Found("zorbl-pdfs").ShouldBe(["an invented shorthand"]);

        Found("Tidewater-Clerk Almanac, 2nd Edition.pdf").ShouldBe(["an invented title"]);
        Found("/// the Tidewater Clerk", "/// Almanac, wrapped").ShouldBe(["an invented title"]);
        Find(["/// the Tidewater Clerk", "/// Almanac, wrapped"], names).Single().Line.ShouldBe(1);
        Found("a tidewater clerk and an almanac").ShouldBeEmpty();

        Found("src/QuillwortServer.cs").ShouldBe(["an invented project"]);
        Found("quillwor").ShouldBeEmpty();
    }

    /// <summary>
    /// A name is hashed as the scan reads text, whatever form it was copied in. Hashing it
    /// as typed gave a hash the scan never produces, which leaves the name unguarded while
    /// the test stays green.
    /// </summary>
    [Fact]
    public void ANameAsWrittenHashesToItsWords()
    {
        HashOfName("  Tidewater-Clerk, ALMANAC. ").ShouldBe(HashOfName("tidewater clerk almanac"));
        HashOfName("Tidewater Clerk Almanac").ShouldNotBe(HashOfName("Tidewater Clerk Almanacs"));
    }

    [Fact]
    public void EveryNameIsAWellFormedHash()
    {
        Forbidden.ShouldAllBe(n => n.Sha256.Length == 64 && n.Sha256.All(c => char.IsAsciiHexDigitLower(c)));
        Forbidden.ShouldAllBe(n => n.Length > 0 && n.What.Length > 0);
        Forbidden.Select(n => n.Sha256).Distinct().Count().ShouldBe(Forbidden.Length);
    }
}
