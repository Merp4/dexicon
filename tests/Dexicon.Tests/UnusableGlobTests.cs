using Dexicon.Api;
using Dexicon.Core.Catalog;
using Dexicon.Core.Configuration;
using Dexicon.Core.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Dexicon.Tests;

/// <summary>
/// A glob list the walk cannot read is refused where it is sent. A null element, or a character class
/// such as <c>[z-a]</c>, was stored as sent and threw from <see cref="IgnoreRuleSet.AddPatterns"/> on
/// every pass of every source that read the list, so the corpus stopped indexing with the reason in a
/// job and the request answered 200.
/// </summary>
public sealed class UnusableGlobTests
{
    private static readonly string[] NullAmongPatterns = ["**/*.md", null!];

    [Theory]
    [InlineData("[z-a]")]
    [InlineData("docs/[]x")]
    public void TheWalkItselfRefusesAPatternItCannotCompile(string pattern)
    {
        // The reason for the check: this is what each pass of the walk did with the stored value.
        Should.Throw<ArgumentException>(() => new IgnoreRuleSet().AddPatterns([pattern], "source.include"));
    }

    [Fact]
    public void ThePositionOfTheFirstPatternNothingCanReadIsReported()
    {
        SourceFilters.FirstUnusable(["**/*.md", "[z-a]", "[y-b]"]).ShouldBe(1);
        SourceFilters.FirstUnusable(NullAmongPatterns).ShouldBe(1);
    }

    [Fact]
    public void PatternsThatReadAreAcceptedAsAreNoListAndAnEmptyList()
    {
        // The control: patterns the walk takes, including a class, a negation, an anchor and a comment.
        SourceFilters.FirstUnusable(["**/*.md", "[a-c]*.txt", "!drafts/", "/build", "# note", ""]).ShouldBeNull();
        SourceFilters.FirstUnusable([]).ShouldBeNull();
        SourceFilters.FirstUnusable(null).ShouldBeNull();
    }

    [Fact]
    public void AHistorySourcesIncludeListIsOnlyHeldToNotBeingNullOrEmpty()
    {
        // Git reads these as pathspecs, which the walk's parser never sees: a class it cannot compile is left to git,
        // an empty pathspec is one git rejects, and a pathspec of spaces is one git accepts.
        SourceFilters.FirstUnusable(["docs/", "[z-a]"], SourceFilters.GlobReader.Git).ShouldBeNull();
        SourceFilters.FirstUnusable(["docs/", "  "], SourceFilters.GlobReader.Git).ShouldBeNull();
        SourceFilters.FirstUnusable(["docs/", ""], SourceFilters.GlobReader.Git).ShouldBe(1);
        SourceFilters.FirstUnusable(["docs/", "a\0b"], SourceFilters.GlobReader.Git).ShouldBe(1);
        SourceFilters.FirstUnusable(NullAmongPatterns, SourceFilters.GlobReader.Git).ShouldBe(1);
    }

    [Fact]
    public void ACorpusDefaultIncludeListIsHeldToBothReaders()
    {
        // Sources of both kinds inherit it: a file source compiles it, a history source hands it to git.
        SourceFilters.FirstUnusable(["docs/", "**/*.md"], SourceFilters.GlobReader.WalkAndGit).ShouldBeNull();
        SourceFilters.FirstUnusable(["docs/", ""], SourceFilters.GlobReader.WalkAndGit).ShouldBe(1);
        SourceFilters.FirstUnusable(["docs/", "[z-a]"], SourceFilters.GlobReader.WalkAndGit).ShouldBe(1);
        SourceFilters.FirstUnusable(NullAmongPatterns, SourceFilters.GlobReader.WalkAndGit).ShouldBe(1);
    }

    public static TheoryData<SourceFilters.GlobReader> EveryReader => new()
    {
        SourceFilters.GlobReader.Walk,
        SourceFilters.GlobReader.Git,
        SourceFilters.GlobReader.WalkAndGit,
    };

    [Theory]
    [MemberData(nameof(EveryReader))]
    public void AListOfExactlyTheCapIsAcceptedAndOneMoreIsRefusedAtTheFirstElementPastIt(SourceFilters.GlobReader reader)
    {
        var atCap = Enumerable.Range(0, SourceFilters.MaxGlobsPerList).Select(i => $"d{i}/").ToList();
        var over = atCap.Append("one-more/").ToList();

        SourceFilters.Check(atCap, reader).ShouldBeNull();
        SourceFilters.Check(over, reader).ShouldBe(
            new SourceFilters.GlobProblem(SourceFilters.MaxGlobsPerList, SourceFilters.GlobProblemKind.TooMany));
        SourceFilters.FirstUnusable(over, reader).ShouldBe(SourceFilters.MaxGlobsPerList);
    }

    [Theory]
    [MemberData(nameof(EveryReader))]
    public void TheCountIsJudgedBeforeAnyElementIsCompiled(SourceFilters.GlobReader reader)
    {
        // Every element is one the walk's parser refuses. A list judged element by element would stop at index 0
        // with an unusable pattern; the count cap reports first, so none of the million was looked at.
        var million = Enumerable.Repeat("[z-a]", 1_000_000).ToList();

        var problem = SourceFilters.Check(million, reader).ShouldNotBeNull();

        problem.Kind.ShouldBe(SourceFilters.GlobProblemKind.TooMany);
        problem.Index.ShouldBe(SourceFilters.MaxGlobsPerList);
    }

    [Theory]
    [MemberData(nameof(EveryReader))]
    public void AnElementOfExactlyTheLengthCapIsAcceptedAndOneMoreCharacterIsRefused(SourceFilters.GlobReader reader)
    {
        var atCap = new string('a', SourceFilters.MaxGlobLength);

        SourceFilters.Check(["docs/", atCap], reader).ShouldBeNull();
        SourceFilters.Check(["docs/", atCap + "a"], reader).ShouldBe(
            new SourceFilters.GlobProblem(1, SourceFilters.GlobProblemKind.TooLong));
    }

    [Fact]
    public void AListAlreadyStoredPastTheCapsIsStillReadAsStored()
    {
        // The caps are judged where a list is saved. A list stored before them is read by the walk unchanged.
        var stored = Enumerable.Range(0, SourceFilters.MaxGlobsPerList + 100).Select(i => $"d{i}/").ToList();
        var corpus = new Corpus { Id = "c", Name = "c", DefaultIncludeGlobs = SourceFilters.Store(stored) };
        var source = new Source { Id = "s", CorpusId = "c", Kind = SourceKind.Workspace, RootPath = "" };

        SourceFilters.Resolve(corpus, source, new IndexingOptions()).IncludeGlobs
            .Count.ShouldBe(SourceFilters.MaxGlobsPerList + 100);
    }

    private static readonly SourceFilters.GlobReader[] GitReaders = [SourceFilters.GlobReader.Git, SourceFilters.GlobReader.WalkAndGit];

    /// <summary>
    /// Each element as the readers that pass it to git judge it, and the problem it has or null. A kind is a
    /// pathspec on which git 2.54.0 on Linux (the shipped image) exits 128 or aborts when it is given to
    /// `git log -- <element>` in a scratch repository. Null is an element the validator accepts: git exits 0 for
    /// it, except where a row says otherwise, which are `/docs`, `/` and `/*.md`, whose single leading slash
    /// `GitHistory.Pathspecs` removes before git sees them. The cases were measured one by one, and the
    /// validator was compared with git on 20,000 generated elements. Elements are judged by the validator alone
    /// here and are not given to git, and a backslash is an ordinary character on Linux.
    /// </summary>
    public static TheoryData<string, SourceFilters.GlobProblemKind?> GitElements => new()
    {
        // Inside the repository, whatever the shape.
        { "docs", null }, { "docs/", null }, { "/docs", null }, { "/", null }, { "/*.md", null }, { "*.md", null },
        { "docs/*.md", null }, { "**/*.md", null }, { " ", null }, { "a/../b", null }, { "a/..", null }, { "docs/..", null },
        { "x/../docs", null }, { "a/./b/..", null }, { "a//..", null }, { "a/../b/..", null }, { "..x", null }, { "x..", null },
        { "a..b", null }, { "...", null }, { "a/..b/c", null }, { "a/.../c", null },

        // A backslash is part of a name, so nothing around it is a segment.
        { @"docs\", null }, { @"\docs", null }, { @"x\..\docs", null }, { @"..\x", null }, { @"a\..\b", null },

        // Paths that climb out of the repository. An empty segment is no level: git folds a doubled slash.
        { "..", SourceFilters.GlobProblemKind.ClimbsOut }, { "../x", SourceFilters.GlobProblemKind.ClimbsOut },
        { "/../x", SourceFilters.GlobProblemKind.ClimbsOut }, { "a/../..", SourceFilters.GlobProblemKind.ClimbsOut },
        { "a//../..", SourceFilters.GlobProblemKind.ClimbsOut }, { "a///../..", SourceFilters.GlobProblemKind.ClimbsOut },
        { "a/./../..", SourceFilters.GlobProblemKind.ClimbsOut }, { "./a/../..", SourceFilters.GlobProblemKind.ClimbsOut },
        { "a/../../b", SourceFilters.GlobProblemKind.ClimbsOut }, { "./..", SourceFilters.GlobProblemKind.ClimbsOut },
        { "docs/../../x", SourceFilters.GlobProblemKind.ClimbsOut },

        // The path after magic is judged the same way, unless the magic is top.
        { ":!../x", SourceFilters.GlobProblemKind.ClimbsOut }, { ":^../x", SourceFilters.GlobProblemKind.ClimbsOut },
        { "::../x", SourceFilters.GlobProblemKind.ClimbsOut }, { ":!:../x", SourceFilters.GlobProblemKind.ClimbsOut },
        { ":!a//../..", SourceFilters.GlobProblemKind.ClimbsOut },
        { ":(glob)../x", SourceFilters.GlobProblemKind.ClimbsOut }, { ":(exclude)../x", SourceFilters.GlobProblemKind.ClimbsOut },
        { ":(literal)../x", SourceFilters.GlobProblemKind.ClimbsOut }, { ":(icase)../x", SourceFilters.GlobProblemKind.ClimbsOut },
        { ":(attr:foo)../x", SourceFilters.GlobProblemKind.ClimbsOut }, { ":(glob)a/../..", SourceFilters.GlobProblemKind.ClimbsOut },
        { ":(glob)a/../b", null },

        // Top: git reads the path from the root and does not reject a rooted path or one with "..".
        { ":(top)../x", null }, { ":(top)..", null }, { ":(top)/docs", null }, { ":(glob,top)/x", null },
        { ":(top,glob)../x", null }, { ":(top)docs/../..", null }, { ":(top)a//../..", null }, { ":(top,exclude)../x", null },
        { ":(top,top)../x", null }, { ":/..", null }, { ":/../x", null }, { ":/a/../..", null }, { ":!/../x", null },
        { ":!/docs", null }, { ":!//docs", null }, { ":/:/x", null }, { ":/docs", null },

        // Rooted: still a rooted path after the one slash git's argument may lose, or after magic that is not top.
        { "//docs", SourceFilters.GlobProblemKind.RootedPath }, { "//", SourceFilters.GlobProblemKind.RootedPath },
        { ":(glob)/docs", SourceFilters.GlobProblemKind.RootedPath }, { ":(exclude)/docs", SourceFilters.GlobProblemKind.RootedPath },
        { ":(literal)/docs", SourceFilters.GlobProblemKind.RootedPath }, { ":(icase)/docs", SourceFilters.GlobProblemKind.RootedPath },
        { ":(attr:foo)/docs", SourceFilters.GlobProblemKind.RootedPath }, { "::/docs", SourceFilters.GlobProblemKind.RootedPath },

        // Removing the slash would make a name into magic.
        { "/:(bad)x", SourceFilters.GlobProblemKind.SlashThenMagic }, { "/:(exclude)docs", SourceFilters.GlobProblemKind.SlashThenMagic },
        { "/:x", SourceFilters.GlobProblemKind.SlashThenMagic }, { "/:", SourceFilters.GlobProblemKind.SlashThenMagic },

        // Long magic git accepts, including empty words, repeated words and every flag together.
        { ":()x", null }, { ":(,glob)x", null }, { ":(glob,)x", null }, { ":(glob,glob)x", null },
        { ":(literal,literal)x", null }, { ":(glob,icase)docs/*.md", null }, { ":(icase,glob)docs/*.md", null },
        { ":(glob,icase,top)docs/*.md", null }, { ":(literal,icase)docs", null }, { ":(literal,exclude)docs", null },
        { ":(exclude,glob)docs/*.md", null }, { ":(exclude)docs", null }, { ":(glob)*.md", null }, { ":(glob)docs/*.md", null },
        { ":(attr:foo)x", null }, { ":(attr:foo=bar)x", null }, { ":(attr:foo=bar)", null }, { ":(attr:-foo)x", null },
        { ":(attr:!foo)x", null }, { ":(attr:foo bar)x", null }, { @":(attr:a=\,b)x", null }, { ":(attr:a=)x", null },
        { ":(attr:foo,glob)x", null }, { ":(glob,attr:foo)x", null },

        // Long magic git rejects: no closing parenthesis, an unknown word (matched exactly), glob with literal,
        // an empty attr: or one holding \) or \\, and a word after an attr: that git reads as its own.
        { ":(glob", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(bad)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(GLOB)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":( glob)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(glob )x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(from-file)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(glob,literal)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(literal,glob)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:)x", SourceFilters.GlobProblemKind.MalformedMagic }, { @":(attr:a=\))x", SourceFilters.GlobProblemKind.MalformedMagic },
        { @":(attr:a=\)x", SourceFilters.GlobProblemKind.MalformedMagic }, { @":(attr:a=b\)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { @":(attr:a=\\)x", SourceFilters.GlobProblemKind.MalformedMagic }, { @":(attr:foo=bar\))docs", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:foo=bar,baz)x", SourceFilters.GlobProblemKind.MalformedMagic },

        // Short magic: only ! ^ / are implemented, and a character outside git's magic set starts the path.
        { ":!x", null }, { ":^x", null }, { ":/x", null }, { "::x", null }, { ":!:x", null }, { ":x", null }, { ":docs", null },
        { ":a:x", null }, { ":1x", null }, { ": x", null }, { ":.x", null }, { ":*x", null }, { ":?x", null }, { ":$x", null },
        { ":)x", null }, { ":+x", null }, { ":[x", null }, { ":]x", null }, { ":{x", null }, { ":|x", null }, { ":}x", null },
        { @":\x", null }, { ":!!x", null }, { ":^^x", null }, { ":!^x", null }, { ":!/^x", null }, { ":!:-x", null },
        { ":::x", null }, { ":!(glob)x", null }, { ":!(x", null }, { ":", null }, { ":!", null }, { ":/", null }, { "::", null },
        { ":-x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":,x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":;x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":#x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":@x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":%x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":&x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":'x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":\"x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":=x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":<x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":>x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":~x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":`x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":_x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":!-x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":!,x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":/-x", SourceFilters.GlobProblemKind.MalformedMagic },
        // The attr word alone is accepted, with or without prefix:. Only one attr: specification is allowed.
        { ":(attr)x", null }, { ":(attr,glob)x", null }, { ":(attr)", null }, { ":(attr,attr)x", null },
        { ":(attr,attr:a)x", null }, { ":(attr:a,attr)x", null },
        { ":(attr:a=b,attr:c=d)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a,attr:b)x", SourceFilters.GlobProblemKind.MalformedMagic },

        // attr: names are ASCII letters, digits, - . _ and do not start with -, after an optional - or ! prefix.
        { ":(attr:A)x", null }, { ":(attr:1)x", null }, { ":(attr:a-b)x", null }, { ":(attr:a.b)x", null },
        { ":(attr:a_b)x", null }, { ":(attr:-a)x", null }, { ":(attr:!a)x", null }, { ":(attr:a.)x", null },
        { ":(attr:.a)x", null }, { ":(attr:_a)x", null },
        { ":(attr:-)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:!)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:=a)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:=)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:--a)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:!!a)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:-!a)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:!-a)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:-a=b)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:!a=b)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a =b)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a\tb)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a\t)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a/b)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a:b)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:aé)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a+b)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a*b)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a[b)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a(b)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:é=1)x", SourceFilters.GlobProblemKind.MalformedMagic },

        // attr: items are split at spaces, and a specification of spaces alone is accepted where an empty one is not.
        { ":(attr:a b)x", null }, { ":(attr:a  b)x", null }, { ":(attr: a)x", null }, { ":(attr:a )x", null },
        { ":(attr:a b c)x", null }, { ":(attr:a=b c=d)x", null }, { ":(attr:a=b  c)x", null }, { ":(attr:a=b c)x", null },
        { ":(attr: )x", null }, { ":(attr:  )x", null }, { ":(attr:-a b=\\,c)x", null }, { ":(attr:!a  -b)x", null },
        { ":(attr:,)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b=c d)x", SourceFilters.GlobProblemKind.MalformedMagic },

        // attr: values are ASCII letters, digits, - _ and a comma written \, and a backslash escapes the next character.
        { ":(attr:a=B)x", null }, { ":(attr:a=1)x", null }, { ":(attr:a=b-c)x", null }, { ":(attr:a=b_c)x", null },
        { ":(attr:a=\\a)x", null }, { ":(attr:a=b\\a)x", null }, { ":(attr:a=\\-)x", null },
        { ":(attr:a=b.c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b=c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=é)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b/c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b:c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b+c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b*c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b!c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b@c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b~c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b[c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b(c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b%c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b#c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b$c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b&c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b;c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b<c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b\"c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b'c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b`c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b^c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b|c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b?c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b{c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b}c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:a=b]c)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(attr:a=b,c)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { @":(attr:a\,b)x", SourceFilters.GlobProblemKind.MalformedMagic }, { @":(attr:\,)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { @":(attr:\a)x", SourceFilters.GlobProblemKind.MalformedMagic }, { @":(attr:a=\\b)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { @":(attr:a=\ b)x", SourceFilters.GlobProblemKind.MalformedMagic }, { @":(attr:a\)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { @":(attr:a\", SourceFilters.GlobProblemKind.MalformedMagic }, { @":(attr:a=\", SourceFilters.GlobProblemKind.MalformedMagic },
        { @":(attr:a=é\)x", SourceFilters.GlobProblemKind.MalformedMagic },

        // prefix:N is a number read as C's strtol (spaces, a sign, digits; empty is zero) and then stored in 32 bits.
        // With N of zero or more git does not look at the path, so a rooted one or one with .. is accepted. N may not
        // be more than the bytes of the path (git aborts), and the last prefix: is the one that counts.
        { ":(prefix:0)x", null }, { ":(prefix:)x", null }, { ":(prefix:1)x", null }, { ":(prefix:1)docs", null },
        { ":(prefix:4)docs", null }, { ":(prefix:-1)x", null }, { ":(prefix:-2)x", null }, { ":(prefix: 0)x", null },
        { ":(prefix:+0)x", null }, { ":(prefix:00)x", null }, { ":(prefix:+1)x", null }, { ":(prefix:0,glob)x", null },
        { ":(prefix:0)docs", null }, { ":(prefix:2)docs/a", null }, { ":(prefix:5)docs/", null }, { ":(prefix:6)a/../b", null },
        { ":(prefix:3)./x", null }, { ":(prefix:4)a//b", null }, { ":(prefix:1) ", null }, { ":(prefix:1,glob)ab", null },
        { ":(prefix:1)é", null }, { ":(prefix:2)é", null }, { ":(prefix:0)", null }, { ":(prefix:-5)", null },
        { ":(prefix:0,prefix:0)x", null }, { ":(prefix:3,prefix:0)", null }, { ":(prefix:5,prefix:2)docs", null },
        { ":(prefix:0)::x", null }, { ":(prefix:1)::x", null }, { ":(prefix:0)x:", null },
        { ":(prefix:99999999999999999999)x", null }, { ":(prefix:-99999999999999999999)x", null },
        { ":(prefix:0000000000000000000001)x", null }, { ":(prefix:\t\t1)x", null },
        { ":(prefix:0)../x", null }, { ":(prefix:0)/x", null }, { ":(prefix:1)../x", null }, { ":(prefix:3)../x", null },
        { ":(prefix:1)/x", null }, { ":(prefix:2)/x", null }, { ":(prefix:0,top)../x", null },
        { ":(prefix:4294967296)../x", null }, { ":(prefix:-99999999999999999999)a/../..", null },
        { ":(prefix:-1)../x", SourceFilters.GlobProblemKind.ClimbsOut }, { ":(prefix:99999999999999999999)../x", SourceFilters.GlobProblemKind.ClimbsOut },
        { ":(prefix:+1,prefix:99999999999999999999)../x", SourceFilters.GlobProblemKind.ClimbsOut },
        { ":(prefix:4294967297,prefix:-99999999999999999999,prefix:-1)/x", SourceFilters.GlobProblemKind.RootedPath },
        { ":(prefix:2)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:5)docs", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:99)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:abc)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:0 )x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:0x1)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:1x)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:- 1)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:-)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:+)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:1)", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:2)", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:9)docs/a/b", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:7)a/../b", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:6)docs/", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:4)./x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:5)a//b", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:2) ", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:3,glob)ab", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(prefix:3)é", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(prefix:0,glob,literal)x", SourceFilters.GlobProblemKind.MalformedMagic },

        // Words that look like known ones are not: matched exactly.
        { ":(topx)../x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(icx)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(ATTR:foo)x", SourceFilters.GlobProblemKind.MalformedMagic }, { ":(glob,literal,exclude)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(exclude,glob,literal)x", SourceFilters.GlobProblemKind.MalformedMagic },
        { ":(attr:foo,glob,literal)x", SourceFilters.GlobProblemKind.MalformedMagic },

        // Colons: git ends short magic at the first one and reads the rest as the path.
        { ":::../x", null },    };

    [Theory]
    [MemberData(nameof(GitElements))]
    public void AnIncludeElementIsJudgedAsGitWillReadItAndTheWalkLeavesItAlone(string glob, SourceFilters.GlobProblemKind? kind)
    {
        foreach (var reader in GitReaders)
            SourceFilters.Check(["docs/", glob], reader).ShouldBe(
                kind is { } k ? new SourceFilters.GlobProblem(1, k) : null, $"{reader}: {glob}");

        // The walk matches against paths below its root: such an element compiles and matches nothing, or matches as a pattern.
        SourceFilters.Check(["docs/", glob], SourceFilters.GlobReader.Walk).ShouldBeNull(glob);
    }

    [Theory]
    [InlineData("", "", false, false)]
    [InlineData("docs", "docs", false, false)]
    [InlineData("/docs", "/docs", false, false)]
    [InlineData(":(glob)docs/*.md", "docs/*.md", false, false)]
    [InlineData(":(exclude)a)b", "a)b", false, false)]
    [InlineData(":(top)docs", "docs", true, false)]
    [InlineData(":(glob,top)docs", "docs", true, false)]
    [InlineData(":!docs", "docs", false, false)]
    [InlineData(":^docs", "docs", false, false)]
    [InlineData(":/docs", "docs", true, false)]
    [InlineData(":!/^docs", "docs", true, false)]
    [InlineData(":!:docs", "docs", false, false)]
    [InlineData(":docs", "docs", false, false)]
    [InlineData(":", "", false, false)]
    [InlineData(":-x", "x", false, true)]
    [InlineData(":/-x", "x", true, true)]
    [InlineData(":(glob,literal)x", "x", false, true)]
    [InlineData(":(bad)x", "x", false, true)]
    [InlineData(@":(attr:a=\,b)x", "x", false, false)]
    [InlineData(@":(attr:a=\))x", "x", false, true)]
    [InlineData(":(prefix:0)../x", "../x", true, false)]
    [InlineData(":(prefix:-1)../x", "../x", false, false)]
    [InlineData(":(prefix:2)x", "x", false, true)]
    [InlineData(":(prefix:3,prefix:0)", "", true, false)]
    [InlineData(":(attr)x", "x", false, false)]
    [InlineData(":(attr:a=b,attr:c=d)x", "x", false, true)]
    public void PathspecMagicIsSplitFromItsPathAsGitDoes(string pathspec, string path, bool pathUnchecked, bool malformed)
    {
        PathspecSyntax.Parse(pathspec).ShouldBe(new PathspecMagic(path, pathUnchecked, malformed));
    }

    [Theory]
    [InlineData(":(glob")]
    [InlineData(":(")]
    [InlineData(":(glob../x")]
    [InlineData(@":(attr:a=\)x")]
    public void ALongMagicWithNoClosingParenthesisIsMalformedAndHasNoPath(string pathspec)
    {
        PathspecSyntax.Parse(pathspec).ShouldBe(new PathspecMagic(null, false, true));
    }
    [Fact]
    public void TheRefusalNamesTheCapAndTheListAndDoesNotEchoTheElement()
    {
        var secret = "LEAKED" + new string('x', SourceFilters.MaxGlobLength);
        var tooMany = Enumerable.Repeat("LEAKED/", SourceFilters.MaxGlobsPerList + 1).ToList();

        var longOne = CorpusEndpoints.UnusableGlobs(["docs/", secret], null).ShouldNotBeNull();
        var many = CorpusEndpoints.UnusableGlobs(null, tooMany).ShouldNotBeNull();
        var parent = CorpusEndpoints.UnusableGlobs(["LEAKED/../../x"], null, SourceFilters.GlobReader.Git).ShouldNotBeNull();
        var rooted = CorpusEndpoints.UnusableGlobs([":(glob)/LEAKED"], null, SourceFilters.GlobReader.WalkAndGit).ShouldNotBeNull();
        var magic = CorpusEndpoints.UnusableGlobs(["/:(exclude)LEAKED"], null, SourceFilters.GlobReader.Git).ShouldNotBeNull();
        var malformed = CorpusEndpoints.UnusableGlobs([":(LEAKED)x"], null, SourceFilters.GlobReader.Git).ShouldNotBeNull();

        longOne.Status.ShouldBe(400);
        longOne.Detail.ShouldContain("includeGlobs[1]");
        longOne.Detail.ShouldContain($"longer than {SourceFilters.MaxGlobLength} characters");
        many.Status.ShouldBe(400);
        many.Detail.ShouldContain("excludeGlobs");
        many.Detail.ShouldContain($"more than {SourceFilters.MaxGlobsPerList} patterns");
        parent.Detail.ShouldContain("includeGlobs[0]");
        parent.Detail.ShouldContain("climbs out of the repository");
        parent.Detail.ShouldContain("'..'");
        rooted.Detail.ShouldContain("is a rooted path", Case.Sensitive);
        rooted.Detail.ShouldContain("'//'");
        rooted.Detail.ShouldNotContain("rejects", Case.Insensitive, "git accepts a rooted path that names a place inside the repository");
        magic.Detail.ShouldContain("'/:'");
        malformed.Detail.ShouldContain("pathspec magic that git rejects");
        foreach (var reason in new[]
                 {
                     "a ':(' with no closing ')'", "a word other than top, literal, icase, glob, exclude, attr, attr:<specification> and prefix:<number>",
                     "glob together with literal", "more than one attr:", "an attr: with a name or value git cannot use",
                     "a prefix: that is not a number or is longer than the path",
                     "right after ':', one of - , ; # % & ' \" = < > @ _ ~ and the backtick, which git does not implement as magic",
                 })
            malformed.Detail.ShouldContain(reason, Case.Sensitive, reason);
        malformed.Detail.ShouldNotContain("!, ^ and /", Case.Sensitive, "a character outside git's magic set starts the path and is accepted");
        foreach (var refusal in new[] { parent, rooted, magic, malformed })
            refusal.Detail.ShouldContain("inherited by history sources", Case.Sensitive, "the reason is given to a caller with no history source");

        foreach (var refusal in new[] { longOne, many, parent, rooted, magic, malformed })
        {
            refusal.Detail.ShouldNotContain("LEAKED");
            refusal.Detail.ShouldEndWith("Nothing was saved.");
        }
    }

    /// <summary>A harness with one workspace corpus, and a context on it.</summary>
    private sealed class Seeded : IAsyncDisposable
    {
        private Seeded(IndexingHarness harness, CatalogDbContext db, Corpus corpus)
        {
            Harness = harness;
            Db = db;
            Corpus = corpus;
        }

        public IndexingHarness Harness { get; }
        public CatalogDbContext Db { get; }
        public Corpus Corpus { get; }
        public CorpusConfiguration Config => Harness.NewConfiguration(Db);

        public static async Task<Seeded> StartAsync()
        {
            var harness = await IndexingHarness.StartAsync("notes");
            await harness.SeedCorpusAsync(SourceKind.Workspace);
            var db = harness.NewContext();
            return new Seeded(harness, db, await db.Corpora.SingleAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASourceAddedWithAnUnusableIncludeOrExcludeGlobIsRefusedAndNothingIsSaved()
    {
        await using var s = await Seeded.StartAsync();
        var before = await s.Db.Sources.CountAsync();

        var include = await s.Config.AddSourceAsync(
            s.Corpus, new AddSourceRequest("", IncludeGlobs: ["**/*.md", "[z-a]"]), default);
        var exclude = await s.Config.AddSourceAsync(
            s.Corpus, new AddSourceRequest("", ExcludeGlobs: NullAmongPatterns), default);

        include.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        include.Refusal.Detail.ShouldContain("includeGlobs[1]");
        include.Refusal.Detail.ShouldContain("include[1] for the configure tools");
        exclude.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        exclude.Refusal.Detail.ShouldContain("excludeGlobs[1]");
        (await s.Db.Sources.CountAsync()).ShouldBe(before, "a refused source is not saved");
    }

    [Fact]
    public async Task ASourceAddedWithGlobsTheWalkReadsIsAccepted()
    {
        await using var s = await Seeded.StartAsync();

        var added = await s.Config.AddSourceAsync(
            s.Corpus, new AddSourceRequest("", IncludeGlobs: ["**/*.md", "[a-c]*.txt"], ExcludeGlobs: ["!drafts/"]), default);

        added.Refusal.ShouldBeNull();
        added.Value!.Source.IncludeGlobs.ShouldBe(["**/*.md", "[a-c]*.txt"]);
    }

    [Fact]
    public async Task AHistorySourceIsAddedWithTheGitRuleForItsIncludeList()
    {
        // The glob check comes before the check that the folder is a repository, so which refusal comes back says which rule ran.
        await using var s = await Seeded.StartAsync();

        var empty = await s.Config.AddSourceAsync(s.Corpus, new AddSourceRequest("", GitHistory: true, IncludeGlobs: [""]), default);
        var oddClass = await s.Config.AddSourceAsync(s.Corpus, new AddSourceRequest("", GitHistory: true, IncludeGlobs: ["[z-a]"]), default);

        empty.Refusal.ShouldNotBeNull().Title.ShouldBe("Unusable glob");
        (oddClass.Refusal?.Title).ShouldNotBe("Unusable glob", "git reads the list as pathspecs, so the walk's parser does not judge it");
    }

    [Theory]
    [InlineData("INCLUDEGLOBS")]
    [InlineData("includeGlobs")]
    public async Task AListIsClearedWhateverTheCaseOfItsName(string name)
    {
        await using var s = await Seeded.StartAsync();

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0),
            new UpdateSourceRequest(IncludeGlobs: ["[z-a]"], Clear: [name]), default);

        outcome.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task ClearingTheExcludeListLeavesTheIncludeListJudged()
    {
        // Each list is skipped for its own name only.
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);

        var excludeCleared = await s.Config.UpdateSourceAsync(s.Corpus, id,
            new UpdateSourceRequest(ExcludeGlobs: ["[z-a]"], Clear: ["excludeGlobs"]), default);
        var includeStillJudged = await s.Config.UpdateSourceAsync(s.Corpus, id,
            new UpdateSourceRequest(IncludeGlobs: ["[z-a]"], Clear: ["excludeGlobs"]), default);

        excludeCleared.Refusal.ShouldBeNull();
        includeStillJudged.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
    }

    [Theory]
    [InlineData("[z-a]", "does not compile")]
    [InlineData("a\0b", "null character")]
    public async Task ACorpusCreatedWithADefaultIncludeBothReadersRefuseIsNotCreated(string pattern, string reason)
    {
        // The create site reads the default with the same two rules as the update site.
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var outcome = await harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("includeGlobs", pattern));

        outcome.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        outcome.Refusal.Detail.ShouldContain(reason);
        (await db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
    }

    [Fact]
    public async Task AnEmptyDefaultIncludePatternIsRefusedBecauseAHistorySourceWouldInheritIt()
    {
        await using var s = await Seeded.StartAsync();

        var update = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("includeGlobs", "")), default);
        var create = await s.Config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("includeGlobs", ""));

        update.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        create.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        (await s.Db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
    }

    private static CorpusDefaults DefaultsWith(string field, params string[] globs) => field == "includeGlobs"
        ? new CorpusDefaults(null, null, globs, null)
        : new CorpusDefaults(null, null, null, globs);

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task ASourceUpdatedWithAnUnusableGlobIsRefusedAndKeepsItsOwn(string field)
    {
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);
        (await s.Config.UpdateSourceAsync(s.Corpus, id, field == "includeGlobs"
            ? new UpdateSourceRequest(IncludeGlobs: ["**/*.md"])
            : new UpdateSourceRequest(ExcludeGlobs: ["**/*.md"]), default)).Refusal.ShouldBeNull();

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, id, field == "includeGlobs"
            ? new UpdateSourceRequest(IncludeGlobs: ["[z-a]"])
            : new UpdateSourceRequest(ExcludeGlobs: ["[z-a]"]), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain($"{field}[0]");
        outcome.Refusal.Detail.ShouldContain("does not compile", Case.Sensitive, "a file source's list is read by the walk's parser");
        s.Db.ChangeTracker.Clear();
        var saved = await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id);
        SourceFilters.Globs(field == "includeGlobs" ? saved.IncludeGlobs : saved.ExcludeGlobs)
            .ShouldBe(["**/*.md"], "the refused list is not saved over the one the source has");
    }

    [Fact]
    public async Task AListTheRequestClearsIsNotJudged()
    {
        // ApplyFilters stores nothing for a field named in clear, so a pattern sent beside it is not stored.
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, id,
            new UpdateSourceRequest(IncludeGlobs: ["[z-a]"], Clear: ["includeGlobs"]), default);

        outcome.Refusal.ShouldBeNull();
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).IncludeGlobs.ShouldBeNull();
    }

    [Fact]
    public async Task AHistorySourceTakesAnIncludePathspecTheWalksParserWouldRefuse()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        var config = harness.NewConfiguration(db);
        var id = IndexingHarness.SourceIdFor(0);

        (await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["[z-a]"]), default))
            .Refusal.ShouldBeNull();
        var blank = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["docs/", ""]), default);

        blank.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        blank.Refusal.Detail.ShouldContain("includeGlobs[1]");
        blank.Refusal.Detail.ShouldContain("pathspec");
        blank.Refusal.Detail.ShouldContain("null character");
    }

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task CorpusDefaultsWithAnUnusableGlobAreRefusedAndTheOldOnesKept(string field)
    {
        await using var s = await Seeded.StartAsync();
        (await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith(field, "**/*.md")), default))
            .Refusal.ShouldBeNull();

        var outcome = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith(field, "[z-a]")), default);

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain($"{field}[0]");
        s.Db.ChangeTracker.Clear();
        var saved = await s.Db.Corpora.AsNoTracking().SingleAsync();
        SourceFilters.Globs(field == "includeGlobs" ? saved.DefaultIncludeGlobs : saved.DefaultExcludeGlobs).ShouldBe(["**/*.md"]);
    }

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task ACorpusCreatedWithAnUnusableDefaultGlobIsRefusedAndNotCreated(string field)
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await using var db = harness.NewContext();

        var outcome = await harness.NewConfiguration(db).CreateCorpusAsync(
            new CreateCorpusRequest("papers"), default, defaults: DefaultsWith(field, "**/*.md", null!));

        outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        outcome.Refusal.Detail.ShouldContain($"{field}[1]");
        (await db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
    }

    private static string[] OverTheCap() =>
        [.. Enumerable.Range(0, SourceFilters.MaxGlobsPerList + 1).Select(i => $"d{i}/")];

    [Theory]
    [InlineData("includeGlobs")]
    [InlineData("excludeGlobs")]
    public async Task AListPastTheCapIsRefusedAtEverySiteThatStoresOne(string field)
    {
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);
        var sources = await s.Db.Sources.CountAsync();
        var over = OverTheCap();

        var added = await s.Config.AddSourceAsync(s.Corpus, field == "includeGlobs"
            ? new AddSourceRequest("", IncludeGlobs: over)
            : new AddSourceRequest("", ExcludeGlobs: over), default);
        var updated = await s.Config.UpdateSourceAsync(s.Corpus, id, field == "includeGlobs"
            ? new UpdateSourceRequest(IncludeGlobs: over)
            : new UpdateSourceRequest(ExcludeGlobs: over), default);
        var corpusUpdated = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith(field, over)), default);
        var corpusCreated = await s.Config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith(field, over));

        foreach (var (site, outcome) in new[]
                 {
                     ("AddSource", added.Refusal), ("UpdateSource", updated.Refusal),
                     ("UpdateCorpus", corpusUpdated.Refusal), ("CreateCorpus", corpusCreated.Refusal),
                 })
        {
            outcome.ShouldNotBeNull(site).Status.ShouldBe(400, site);
            outcome.Title.ShouldBe("Unusable glob", site);
            outcome.Detail.ShouldContain(field, Case.Sensitive, site);
            outcome.Detail.ShouldContain($"more than {SourceFilters.MaxGlobsPerList} patterns", Case.Sensitive, site);
        }

        (await s.Db.Sources.CountAsync()).ShouldBe(sources, "a refused source is not saved");
        (await s.Db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
        s.Db.ChangeTracker.Clear();
        (await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).IncludeGlobs.ShouldBeNull();
        (await s.Db.Corpora.AsNoTracking().SingleAsync()).DefaultIncludeGlobs.ShouldBeNull();
    }

    [Fact]
    public async Task AListAtTheCapIsAcceptedBySourceAndCorpus()
    {
        await using var s = await Seeded.StartAsync();
        var atCap = OverTheCap()[..SourceFilters.MaxGlobsPerList];

        var updated = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(IncludeGlobs: atCap), default);
        var corpus = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("excludeGlobs", atCap)), default);

        updated.Refusal.ShouldBeNull();
        corpus.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task AHistorySourceTakesALeadingSlashIncludeAndRefusesWhatGitRejects()
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        var config = harness.NewConfiguration(db);
        var id = IndexingHarness.SourceIdFor(0);

        var rooted = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["/docs", "/"]), default);
        var inside = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["/docs", "/", "a/../b", "docs/.."]), default);
        var parent = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["docs/", "LEAKED/../../x"]), default);
        var doubled = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["//docs"]), default);
        var magicRooted = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: [":(glob)/docs"]), default);
        var slashMagic = await config.UpdateSourceAsync(corpus, id, new UpdateSourceRequest(IncludeGlobs: ["/:(exclude)docs"]), default);

        rooted.Refusal.ShouldBeNull();
        inside.Refusal.ShouldBeNull("git resolves a/../b inside the repository, so a list holding it must still save");
        parent.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
        parent.Refusal.Detail.ShouldContain("includeGlobs[1]");
        parent.Refusal.Detail.ShouldContain("'..'");
        parent.Refusal.Detail.ShouldNotContain("LEAKED");
        doubled.Refusal.ShouldNotBeNull().Detail.ShouldContain("'//'");
        magicRooted.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        magicRooted.Refusal.Detail.ShouldContain("pathspec magic");
        slashMagic.Refusal.ShouldNotBeNull().Detail.ShouldContain("'/:'");
        db.ChangeTracker.Clear();
        SourceFilters.Globs((await db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).IncludeGlobs)
            .ShouldBe(["/docs", "/", "a/../b", "docs/.."], "the refused lists are not saved over the accepted one");
    }

    [Fact]
    public async Task AFileSourceKeepsAPathThatClimbsOutBecauseItsWalkMatchesNothingWithIt()
    {
        await using var s = await Seeded.StartAsync();

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(IncludeGlobs: ["../x"]), default);

        outcome.Refusal.ShouldBeNull();
    }

    [Fact]
    public async Task ACorpusDefaultIncludeListIsHeldToGitsRulesBecauseAHistorySourceInheritsIt()
    {
        await using var s = await Seeded.StartAsync();

        var parent = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("includeGlobs", "docs/", "a/../../x")), default);
        var created = await s.Config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("includeGlobs", ":!../b"));
        var rooted = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("includeGlobs", "/build", "/docs", "a/../b")), default);

        parent.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[1]");
        parent.Refusal.Detail.ShouldContain("'..'");
        created.Refusal.ShouldNotBeNull().Detail.ShouldContain("includeGlobs[0]");
        (await s.Db.Corpora.AnyAsync(c => c.Name == "papers")).ShouldBeFalse();
        rooted.Refusal.ShouldBeNull("a leading slash is valid in a default: the walk anchors it and the git boundary removes it");
    }

    [Fact]
    public async Task TheExcludeListOfACorpusDefaultIsReadByTheWalkAloneSoAnEmptyOrGitRejectedElementIsAccepted()
    {
        // The exclude list is never given to git, so it is not held to git's rules: the walk takes these.
        await using var s = await Seeded.StartAsync();
        string[] excludes = ["", "../x", "//x", "/:x", ":(bad)x"];

        var updated = await s.Config.UpdateCorpusAsync(s.Corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("excludeGlobs", excludes)), default);
        var created = await s.Config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("excludeGlobs", excludes));
        var source = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(ExcludeGlobs: excludes), default);

        updated.Refusal.ShouldBeNull();
        created.Refusal.ShouldBeNull();
        source.Refusal.ShouldBeNull();
    }

    [Theory]
    [InlineData(":(glob")]
    [InlineData(":(bad)x")]
    [InlineData(":(glob,literal)x")]
    [InlineData(":-x")]
    [InlineData(":(attr:)x")]
    public async Task MalformedMagicIsRefusedAtEverySiteThatStoresAnIncludeListGitReads(string element)
    {
        await using var harness = await IndexingHarness.StartAsync("notes");
        await harness.SeedCorpusAsync(SourceKind.GitHistory);
        await using var db = harness.NewContext();
        var corpus = await db.Corpora.SingleAsync();
        var config = harness.NewConfiguration(db);

        var source = await config.UpdateSourceAsync(corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(IncludeGlobs: [element]), default);
        var added = await config.AddSourceAsync(corpus, new AddSourceRequest("", GitHistory: true, IncludeGlobs: [element]), default);
        var updated = await config.UpdateCorpusAsync(corpus, new UpdateCorpusRequest(Defaults: DefaultsWith("includeGlobs", element)), default);
        var created = await config.CreateCorpusAsync(new CreateCorpusRequest("papers"), default, defaults: DefaultsWith("includeGlobs", element));

        foreach (var (site, refusal) in new[]
                 {
                     ("UpdateSource", source.Refusal), ("AddSource", added.Refusal),
                     ("UpdateCorpus", updated.Refusal), ("CreateCorpus", created.Refusal),
                 })
        {
            refusal.ShouldNotBeNull(site).Title.ShouldBe("Unusable glob", site);
            refusal.Detail.ShouldContain("pathspec magic that git rejects", Case.Sensitive, site);
            refusal.Detail.ShouldNotContain(element, Case.Sensitive, site);
        }
    }

    [Fact]
    public async Task ANullEntryInClearIsRefusedAndNothingIsChanged()
    {
        await using var s = await Seeded.StartAsync();
        var id = IndexingHarness.SourceIdFor(0);
        (await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(MaxFileBytes: 4096), default)).Refusal.ShouldBeNull();

        var alone = await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(Clear: [null!]), default);
        var beside = await s.Config.UpdateSourceAsync(s.Corpus, id, new UpdateSourceRequest(Clear: ["maxFileBytes", null!]), default);

        foreach (var outcome in new[] { alone, beside })
        {
            outcome.Refusal.ShouldNotBeNull().Status.ShouldBe(400);
            outcome.Refusal.Title.ShouldBe("Unknown filter");
            outcome.Refusal.Detail.ShouldContain("null entry");
            outcome.Refusal.Detail.ShouldContain("clear takes field names");
            outcome.Refusal.Detail.ShouldContain("includeGlobs");
            outcome.Refusal.Detail.ShouldNotContain("Name one of");
        }

        s.Db.ChangeTracker.Clear();
        (await s.Db.Sources.AsNoTracking().FirstAsync(x => x.Id == id)).MaxFileBytes.ShouldBe(4096, "a refused request clears nothing");
    }

    [Fact]
    public async Task AnUnknownClearNameIsShownOnOneLineAndCutShort()
    {
        await using var s = await Seeded.StartAsync();
        var name = "first\r\nsecond\u001b[2J" + new string('x', 500) + "TAILMARKER";

        var outcome = await s.Config.UpdateSourceAsync(s.Corpus, IndexingHarness.SourceIdFor(0), new UpdateSourceRequest(Clear: [name]), default);

        var detail = outcome.Refusal.ShouldNotBeNull().Detail;
        detail.ShouldContain("Name one of: useGitignore, maxFileBytes, includeGlobs, excludeGlobs", Case.Sensitive, "an unknown name is answered with the names it could have been");
        detail.ShouldContain("first");
        detail.ShouldNotContain("\n");
        detail.ShouldNotContain("\r");
        detail.ShouldNotContain("\u001b");
        detail.ShouldNotContain("TAILMARKER");
        detail.Length.ShouldBeLessThan(250);
    }
}
