using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Dexicon.Tests;

/// <summary>
/// Nothing that runs after a catalogue write is cancellable by the caller.
///
/// A write commits on its own, and what follows it in the same request (the job that applies it, its
/// collection, the audit line, the reply) is what makes it take effect or says that it happened. On the
/// caller's token a cancel between the two left a saved change with no job, a key mapped to nothing, or
/// a saved change answered with an error. Review raised that fourteen times across PRs #174, #184, #195,
/// #212 and #214, each fix was made at the site named, and the next new path missed it again.
///
/// So this compiles <c>src/</c> and, in every method, local function and lambda, follows the control-flow
/// graph from each write and reports any use of the caller's token the write can reach. A write is
/// <c>SaveChanges</c>, <c>ExecuteUpdate</c>, <c>ExecuteDelete</c> or <c>ExecuteSql</c> on the catalogue, or a
/// call to a method in <c>src/</c> that makes one, followed through calls and interfaces declared in
/// <c>src/</c> until nothing new is found.
///
/// The caller's token is a <see cref="CancellationToken"/> parameter of the function or of one around it
/// (a lambda or local function can capture it), a local of type <see cref="CancellationToken"/> or
/// <see cref="CancellationTokenSource"/> that was given it (a copy, or a source linked to it), and
/// <c>HttpContext.RequestAborted</c>. A use after a write is the token named there, a local function or
/// lambda that captures it called or passed on there, or such a lambda or local function held in a local
/// made before it.
///
/// After a write a function passes <see cref="CancellationToken.None"/>, records what it did through a
/// callback at the write (<c>committed:</c> on <c>CorpusConfiguration</c>), or saves the follow-up in the
/// same save. Catching <see cref="OperationCanceledException"/> around the use is not enough: swallowing
/// the cancellation is how the follow-up is skipped.
///
/// A function, type or member is exempt only through
/// <c>[SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", Justification = "...")]</c>, with a
/// reason, and a <c>MessageId</c> naming the one call it covers where the function has other uses that
/// must stay reported. A loop that stops on the token is one: <c>DocumentEndpoints.UploadAsync</c> reads
/// and stores the next file on it, and its queuing of the job after the loop is not exempt. A suppression
/// that no longer suppresses anything fails, wherever it is written, as does one without a justification.
///
/// Blind to: a token held in a field, a property, a collection, a tuple or an object it was passed into, and
/// a delegate held in a field; a <c>catch</c> reached by an exception thrown after a write, since the graph
/// has no edge for an exception and treats every catch as following a failed write; a callback passed to
/// the writing call and run inside it; writes to Qdrant and the blob store, which are not catalogue writes;
/// and the conditions on a path, so a branch taken only when nothing was written is followed too.
/// <c>TheScanReportsEveryWayTheTokenReachesWorkAfterAWrite</c> and its companions pin what is covered.
/// </summary>
public sealed class RequestTokenAfterCommitTests
{
    private static readonly Lazy<CommitScan.Source> Tree = new(CommitScan.Source.Load);

    [Fact]
    public void NoFunctionUsesItsCancellationTokenAfterACatalogueWrite()
    {
        var source = Tree.Value;
        source.UnexpectedErrors.ShouldBeEmpty(
            "src/ did not compile as the scan builds it, so calls may have bound to nothing and been missed");

        var scan = CommitScan.Run(source.Compilations);

        scan.WriteCalls.ShouldBeGreaterThan(40, "the scan found too few catalogue writes to have read src/");
        scan.UnresolvedWrites.ShouldBeEmpty("these look like catalogue writes and bound to nothing the scan recognises");
        scan.Committing.ShouldContain("Dexicon.Core.Indexing.IndexJobQueue.EnqueueAsync",
            "a method that saves is a write to its callers");
        scan.Committing.ShouldContain("Dexicon.Api.IVectorStoreCleanup.RemoveAttachmentAsync",
            "an interface method whose implementation saves is a write to its callers");

        scan.Unsuppressed.ShouldBeEmpty(
            "the caller's token is used after a catalogue write. Pass CancellationToken.None from the write on, "
            + "record through a callback at the write, or save the follow-up in the same save");
        scan.UnusedSuppressions.ShouldBeEmpty("these suppressions no longer suppress anything");
        scan.UnjustifiedSuppressions.ShouldBeEmpty("a suppression needs a Justification");
    }

    private const string Signature = "public async Task Run(Db db, CancellationToken ct)";

    private const string Suppress =
        "[SuppressMessage(\"Dexicon.Cancellation\", \"TokenAfterCommit\", Justification = \"x\")]";

    private static string Run(string body) => $"{Signature}\n{{\n{body}\n}}";

    /// <summary>
    /// Ways the caller's token reaches work after a write other than naming its parameter there. Each is
    /// a bypass a reviewer found or that follows from one, and each has to be reported.
    /// </summary>
    public static TheoryData<string, string> Reported => new()
    {
        { "the parameter itself", Run("await db.SaveChangesAsync(ct); await Task.Delay(1, ct);") },
        { "a copy made before the write", Run("var later = ct; await db.SaveChangesAsync(ct); await Task.Delay(1, later);") },
        { "a copy of a copy", Run("var a = ct; var b = a; await db.SaveChangesAsync(); await Task.Delay(1, b);") },
        {
            "a source linked to the token",
            Run("using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); await db.SaveChangesAsync(); "
                + "await Task.Delay(1, linked.Token);")
        },
        {
            "a local function that captures it, called after the write",
            Run("Task Follow() => Task.Delay(1, ct); await db.SaveChangesAsync(); await Follow();")
        },
        {
            "a local function that captures it, passed on after the write",
            Run("Task Follow() => Task.Delay(1, ct); await db.SaveChangesAsync(); await Task.Run(Follow);")
        },
        {
            "a local function that calls one that captures it",
            Run("Task Inner() => Task.Delay(1, ct); Task Outer() => Inner(); await db.SaveChangesAsync(); await Outer();")
        },
        {
            "a lambda that captures it, made before the write and called after",
            Run("Func<Task> follow = () => Task.Delay(1, ct); await db.SaveChangesAsync(); await follow();")
        },
        {
            "a lambda that captures it, made after the write",
            Run("await db.SaveChangesAsync(); await Task.Run(() => Task.Delay(1, ct));")
        },
        {
            "a lambda that writes and then uses the token it captured",
            Run("Func<Task> work = async () => { await db.SaveChangesAsync(); await Task.Delay(1, ct); }; await work();")
        },
        {
            "a copy of a lambda that captures it",
            Run("Func<Task> a = () => Task.Delay(1, ct); var b = a; await db.SaveChangesAsync(); await b();")
        },
        {
            "a lambda that calls a held lambda that captures it",
            Run("Func<Task> a = () => Task.Delay(1, ct); Func<Task> b = () => a(); await db.SaveChangesAsync(); await b();")
        },
        {
            "a copy of a local function that captures it",
            Run("Task Follow() => Task.Delay(1, ct); Func<Task> a = Follow; var b = a; await db.SaveChangesAsync(); await b();")
        },
        {
            "a use inside a try that swallows the cancellation",
            Run("try { await db.SaveChangesAsync(ct); await Task.Delay(1, ct); } catch (OperationCanceledException) { }")
        },
        {
            "the request's own token on the HttpContext",
            "public async Task Run(Db db, HttpContext http) { await db.SaveChangesAsync(); await Task.Delay(1, http.RequestAborted); }"
        },
    };

    [Theory]
    [MemberData(nameof(Reported))]
    public void TheScanReportsEveryWayTheTokenReachesWorkAfterAWrite(string shape, string members)
    {
        var scan = CommitScan.Run([CommitScan.Source.FromSnippet(members)]);

        scan.WriteCalls.ShouldBeGreaterThan(0, $"'{shape}' has to contain a write for the case to mean anything");
        scan.Unsuppressed.ShouldNotBeEmpty($"'{shape}' was not reported");
    }

    /// <summary>The same shapes arranged so that the token is not used after a write, which must not be reported.</summary>
    public static TheoryData<string, string> Allowed => new()
    {
        { "a different token", Run("await db.SaveChangesAsync(ct); await Task.Delay(1, CancellationToken.None);") },
        { "the token before the write only", Run("await Task.Delay(1, ct); await db.SaveChangesAsync();") },
        { "a copy used before the write only", Run("var early = ct; await Task.Delay(1, early); await db.SaveChangesAsync();") },
        {
            "a local function that captures it, called before the write only",
            Run("Task Follow() => Task.Delay(1, ct); await Follow(); await db.SaveChangesAsync();")
        },
        {
            "a local function that captures nothing",
            Run("Task Follow() => Task.Delay(1); await db.SaveChangesAsync(); await Follow();")
        },
        {
            "a linked source that is only disposed after the write",
            Run("using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); await Task.Delay(1, linked.Token); "
                + "await db.SaveChangesAsync();")
        },
        {
            "a value read with the token and used after the write",
            Run("var rows = await Task.FromResult(1).WaitAsync(ct); await db.SaveChangesAsync(); rows.ToString();")
        },
        {
            "a copy of a lambda that captures nothing",
            Run("Func<Task> a = () => Task.Delay(1); var b = a; await db.SaveChangesAsync(); await b();")
        },
        {
            "a copy of a lambda that captures it, called before the write only",
            Run("Func<Task> a = () => Task.Delay(1, ct); var b = a; await b(); await db.SaveChangesAsync();")
        },
        {
            "a use covered by a justified suppression",
            Suppress + "\n" + Run("await db.SaveChangesAsync(ct); await Task.Delay(1, ct);")
        },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void TheScanLeavesAShapeAloneThatDoesNotUseTheTokenAfterAWrite(string shape, string members)
    {
        var scan = CommitScan.Run([CommitScan.Source.FromSnippet(members)]);

        scan.WriteCalls.ShouldBeGreaterThan(0, $"'{shape}' has to contain a write for the case to mean anything");
        scan.Unsuppressed.ShouldBeEmpty($"'{shape}' was reported");
        scan.UnusedSuppressions.ShouldBeEmpty($"'{shape}' has a suppression that was not used");
    }

    /// <summary>A suppression on anything that holds no use of the token fails, wherever it is written.</summary>
    public static TheoryData<string, string> Stale => new()
    {
        { "a method", Suppress + "\n" + Run("await db.SaveChangesAsync(ct);") },
        {
            "a local function",
            Run(Suppress + "\nTask Follow() => Task.Delay(1); await db.SaveChangesAsync(ct); await Follow();")
        },
        {
            "a lambda",
            Run("Func<Task> follow = " + Suppress + " () => Task.Delay(1); await db.SaveChangesAsync(ct); await follow();")
        },
        { "a property", Suppress + "\npublic int Count => 1;" },
        { "a field", Suppress + "\nprivate int _count;" },
    };

    [Theory]
    [MemberData(nameof(Stale))]
    public void TheScanReportsASuppressionThatSuppressesNothingWhereverItIsWritten(string where, string members)
    {
        var scan = CommitScan.Run([CommitScan.Source.FromSnippet(members)]);

        scan.UnusedSuppressions.Count.ShouldBe(1, $"the suppression on {where} suppresses nothing");
    }

    [Fact]
    public void TheScanReportsASuppressionWithNoJustificationWhereverItIsWritten()
    {
        var scan = CommitScan.Run([CommitScan.Source.FromSnippet(
            "[SuppressMessage(\"Dexicon.Cancellation\", \"TokenAfterCommit\")]\n"
            + Run("await db.SaveChangesAsync(ct); await Task.Delay(1, ct);"))]);

        scan.UnjustifiedSuppressions.Count.ShouldBe(1);
    }

    /// <summary>
    /// The functions that pass <see cref="CancellationToken.None"/> after a write, as review found them and
    /// as this change fixed them. Each such argument is given the function's own token back here, and the
    /// scan has to report every one: a check that only ever passes has not shown it can fail.
    /// </summary>
    private static readonly string[] NonCancellableAfterAWrite =
    [
        "ChunkSetEndpoints.CreateAsync", "ChunkSetEndpoints.UpdateAsync",
        "CorpusConfiguration.CreateCorpusAsync", "CorpusConfiguration.UpdateCorpusAsync",
        "CorpusConfiguration.AddSourceAsync", "CorpusConfiguration.UpdateSourceAsync",
        "CorpusEndpoints.CreateAsync", "CorpusEndpoints.UpdateAsync",
        "DocumentEndpoints.UploadAsync", "DocumentEndpoints.AttachAsync",
        "SystemEndpoints.SetScopesAsync", "SystemEndpoints.SaveModelProfileAsync",
        "ConfigureTools.ConfigureCorpusAsync", "ConfigureTools.ConfigureSourceAsync",
    ];

    [Fact]
    public void EveryCallMadeNonCancellableAfterAWriteIsReportedWhenGivenTheTokenBack()
    {
        var source = Tree.Value;
        var (mutated, expected) = CommitScan.GiveTheTokenBack(source.Compilations, NonCancellableAfterAWrite);

        expected.Count.ShouldBeGreaterThanOrEqualTo(NonCancellableAfterAWrite.Length,
            "each listed function passes CancellationToken.None at least once");

        var reported = CommitScan.Run(mutated).Unsuppressed.Select(f => f.Site).ToHashSet();
        expected.Where(site => !reported.Contains(site)).ShouldBeEmpty(
            "given the caller's token back, these were not reported");
    }
}

/// <summary>The scan <see cref="RequestTokenAfterCommitTests"/> runs, over compilations of <c>src/</c>.</summary>
internal sealed class CommitScan
{
    public const string Category = "Dexicon.Cancellation";
    public const string CheckId = "TokenAfterCommit";

    /// <param name="Site">File name and line of the token's use.</param>
    public sealed record Finding(string Site, string Function, string Use, string Target, string After)
    {
        public override string ToString() => $"{Site} {Function}: '{Use}' after {After}";
    }

    public int WriteCalls { get; private set; }
    public List<string> UnresolvedWrites { get; } = [];
    public HashSet<string> Committing { get; } = new(StringComparer.Ordinal);
    public List<Finding> Unsuppressed { get; } = [];
    public List<string> UnusedSuppressions { get; } = [];
    public List<string> UnjustifiedSuppressions { get; } = [];

    private static readonly HashSet<string> WriteNames = new(StringComparer.Ordinal)
    {
        "SaveChanges", "SaveChangesAsync", "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete",
        "ExecuteDeleteAsync", "ExecuteSql", "ExecuteSqlAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync",
        "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
    };

    /// <summary><c>src/Dexicon.Core</c> and <c>src/Dexicon</c>, compiled as their projects are.</summary>
    public sealed class Source
    {
        public required Compilation[] Compilations { get; init; }

        /// <summary>
        /// Errors other than CS8795, which is a <c>[GeneratedRegex]</c> partial whose body a source
        /// generator writes and this compilation does not run.
        /// </summary>
        public required List<string> UnexpectedErrors { get; init; }

        // The SDKs' implicit usings, which the projects enable and a bare compilation does not have.
        private static readonly string[] CoreUsings =
        [
            "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
            "System.Threading", "System.Threading.Tasks",
        ];

        private static readonly string[] WebUsings =
        [
            .. CoreUsings, "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
        ];

        /// <summary>What the test host loaded, less the built Dexicon assemblies, whose types the source declares.</summary>
        private static readonly Lazy<List<MetadataReference>> References = new(() =>
            [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("Dexicon", StringComparison.Ordinal))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))]);

        /// <summary>
        /// A compilation of one class, <c>Handler</c>, holding <paramref name="members"/>, beside a
        /// <c>Db</c> that is a <c>DbContext</c>. It throws when the snippet does not compile, so a case
        /// that reports nothing because it did not bind is a failure of the case and not a pass.
        /// </summary>
        public static Compilation FromSnippet(string members)
        {
            var options = new CSharpParseOptions(LanguageVersion.Latest);
            var code = "using System.Diagnostics.CodeAnalysis;\n"
                       + "public sealed class Db : Microsoft.EntityFrameworkCore.DbContext;\n"
                       + $"public sealed class Handler\n{{\n{members}\n}}\n";
            var compilation = CSharpCompilation.Create("Snippet",
                [
                    CSharpSyntaxTree.ParseText(code, options, "Snippet.cs"),
                    CSharpSyntaxTree.ParseText(
                        string.Concat(WebUsings.Select(u => $"global using global::{u};\n")), options, "ImplicitUsings.cs"),
                ],
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException("the snippet does not compile:\n" + string.Join("\n", errors));
            return compilation;
        }

        public static Source Load()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "LICENSE"))) root = root.Parent;
            var src = Path.Combine(root?.FullName ?? throw new InvalidOperationException("repository root not found"), "src");

            var references = References.Value;

            var options = new CSharpParseOptions(LanguageVersion.Latest);
            var core = CSharpCompilation.Create("Dexicon.Core", Parse(Path.Combine(src, "Dexicon.Core"), CoreUsings, options),
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));
            var web = CSharpCompilation.Create("Dexicon", Parse(Path.Combine(src, "Dexicon"), WebUsings, options),
                [.. references, core.ToMetadataReference()], new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                    nullableContextOptions: NullableContextOptions.Enable));

            return new Source
            {
                Compilations = [core, web],
                UnexpectedErrors = [.. new Compilation[] { core, web }.SelectMany(c => c.GetDiagnostics())
                    .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS8795")
                    .Select(d => d.ToString())],
            };
        }

        private static List<SyntaxTree> Parse(string project, string[] usings, CSharpParseOptions options)
        {
            // Relative to the project, so a checkout under a folder named obj or bin is still read.
            var trees = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                .Where(f => Path.GetRelativePath(project, f).Replace('\\', '/') is var rel
                            && !rel.StartsWith("obj/", StringComparison.Ordinal)
                            && !rel.StartsWith("bin/", StringComparison.Ordinal))
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f))
                .ToList();
            if (trees.Count < 10) throw new InvalidOperationException($"read {trees.Count} files under {project}");

            trees.Add(CSharpSyntaxTree.ParseText(
                string.Concat(usings.Select(u => $"global using global::{u};\n")), options, "ImplicitUsings.cs"));
            return trees;
        }
    }

    /// <summary>
    /// Replaces every <c>CancellationToken.None</c> in the named functions (<c>Type.Method</c>) with the
    /// function's own token, and returns the changed compilations and where each replacement is.
    /// </summary>
    public static (Compilation[] Compilations, List<string> Sites) GiveTheTokenBack(
        Compilation[] compilations, string[] functions)
    {
        var sites = new List<string>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var changed = new Compilation[compilations.Length];

        for (var i = 0; i < compilations.Length; i++)
        {
            var compilation = compilations[i];
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var name = $"{(method.Parent as TypeDeclarationSyntax)?.Identifier.Text}.{method.Identifier.Text}";
                    if (!functions.Contains(name, StringComparer.Ordinal)) continue;
                    found.Add(name);

                    var token = method.ParameterList.Parameters
                        .Single(p => p.Type?.ToString() == nameof(CancellationToken)).Identifier.Text;
                    foreach (var none in method.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                                 .Where(m => m.ToString() == "CancellationToken.None"))
                    {
                        replacements[none] = SyntaxFactory.IdentifierName(token).WithTriviaFrom(none);
                        sites.Add(SiteOf(none.GetLocation()));
                    }
                }

                if (replacements.Count > 0)
                    compilation = compilation.ReplaceSyntaxTree(tree,
                        tree.WithRootAndOptions(root.ReplaceNodes(replacements.Keys, (o, _) => replacements[o]), tree.Options));
            }

            changed[i] = compilation;
        }

        var missing = functions.Except(found, StringComparer.Ordinal).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"no function named {string.Join(", ", missing)}");
        return (changed, sites);
    }

    private static string SiteOf(Location location)
    {
        var span = location.GetLineSpan();
        return $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    /// <param name="Tokens">The token parameters the function can name: its own and those of the functions around it.</param>
    /// <param name="Aliases">
    /// Locals of type <see cref="CancellationToken"/> or <see cref="CancellationTokenSource"/> that were
    /// given something carrying one of those tokens, so that renaming the token is not a way past the scan.
    /// </param>
    private sealed record Function(
        ISymbol? Symbol, ControlFlowGraph Graph, IReadOnlyList<IParameterSymbol> Tokens,
        IReadOnlySet<ILocalSymbol> Aliases, string Name);

    /// <summary>One <c>[SuppressMessage]</c> for this check, found in the source wherever it is written.</summary>
    private sealed class Suppression(string site, bool justified)
    {
        public string Site { get; } = site;
        public bool Justified { get; } = justified;
        public bool Used { get; set; }
    }

    public static CommitScan Run(Compilation[] compilations)
    {
        var scan = new CommitScan();
        scan.Analyse(compilations);
        return scan;
    }

    private void Analyse(Compilation[] compilations)
    {
        var functions = new List<Function>();
        var bodies = new Dictionary<IMethodSymbol, List<IOperation>>(SymbolEqualityComparer.Default);
        var types = new List<INamedTypeSymbol>();

        // Every suppression for this check, found by its syntax. Found on the symbols of methods and types
        // alone, one on a local function, a lambda, a property or a field never entered the set, and the
        // check that a suppression still suppresses something passed for it.
        var suppressions = new Dictionary<(SyntaxTree, int), Suppression>();

        foreach (var compilation in compilations)
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    if (node is AttributeSyntax attribute && SuppressionIn(attribute, model) is { } found)
                        suppressions[(tree, attribute.Span.Start)] = found;

                    if (node is BaseTypeDeclarationSyntax typeDeclaration
                        && model.GetDeclaredSymbol(typeDeclaration) is INamedTypeSymbol type)
                        types.Add(type);

                    if (node is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)) continue;
                    if (model.GetDeclaredSymbol(node) is not IMethodSymbol method) continue;
                    if (model.GetOperation(node) is { } body && Graph(body) is { } graph)
                        Collect(functions, bodies, graph, method, $"{method.ContainingType.Name}.{method.Name}", [], new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default));
                }

                if (tree.GetRoot() is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().Any()
                    && model.GetOperation(unit) is { } main && Graph(main) is { } mainGraph)
                    Collect(functions, bodies, mainGraph, null, "<top-level>", [], new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default));

                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = call.Expression switch
                    {
                        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                        IdentifierNameSyntax n => n.Identifier.Text,
                        _ => null,
                    };
                    if (name is null || !WriteNames.Contains(name)) continue;

                    WriteCalls++;
                    if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol bound || !IsWrite(bound))
                        UnresolvedWrites.Add($"{SiteOf(call.GetLocation())} {call}");
                }
            }

        var committing = Writers(bodies, types);
        foreach (var m in committing) Committing.Add($"{m.ContainingType.ToDisplayString()}.{m.Name}");

        // Every function, whether or not it names a token parameter: the request's token can also come
        // from the HttpContext.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in functions)
            foreach (var finding in Check(f, committing))
            {
                if (!seen.Add($"{finding.Site} {finding.Use} {finding.Target} {finding.Function}")) continue;
                if (SuppressionFor(f.Symbol, finding, suppressions) is { } by) by.Used = true;
                else Unsuppressed.Add(finding);
            }

        foreach (var suppression in suppressions.Values)
        {
            if (!suppression.Used) UnusedSuppressions.Add(suppression.Site);
            if (!suppression.Justified) UnjustifiedSuppressions.Add(suppression.Site);
        }
    }

    private static ControlFlowGraph? Graph(IOperation operation) => operation switch
    {
        IMethodBodyOperation m => ControlFlowGraph.Create(m),
        IConstructorBodyOperation c => ControlFlowGraph.Create(c),
        IBlockOperation b => ControlFlowGraph.Create(b),
        _ => null,
    };

    private static IEnumerable<IOperation> Operations(BasicBlock block) =>
        block.BranchValue is null ? block.Operations : block.Operations.Append(block.BranchValue);

    /// <summary>
    /// Adds the graph's function and every local function and lambda inside it. A nested function can name
    /// the tokens and aliases of the functions around it, which it captures.
    /// </summary>
    private static void Collect(List<Function> functions, Dictionary<IMethodSymbol, List<IOperation>> bodies,
        ControlFlowGraph graph, IMethodSymbol? symbol, string name,
        IReadOnlyList<IParameterSymbol> outerTokens, IReadOnlySet<ILocalSymbol> outerAliases)
    {
        List<IParameterSymbol> tokens = [.. symbol?.Parameters.Where(IsToken) ?? [], .. outerTokens];
        var aliases = AliasesIn(graph, tokens, outerAliases);
        functions.Add(new Function(symbol, graph, tokens, aliases, name));
        if (symbol is not null) bodies[symbol.OriginalDefinition] = [.. graph.Blocks.SelectMany(Operations)];

        foreach (var local in graph.LocalFunctions)
            Collect(functions, bodies, graph.GetLocalFunctionControlFlowGraph(local), local, $"{name}/{local.Name}",
                tokens, aliases);

        foreach (var lambda in graph.Blocks.SelectMany(Operations).SelectMany(o => o.DescendantsAndSelf())
                     .OfType<IFlowAnonymousFunctionOperation>())
            Collect(functions, bodies, graph.GetAnonymousFunctionControlFlowGraph(lambda), lambda.Symbol,
                $"{name}/lambda at line {lambda.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1}",
                tokens, aliases);
    }

    private static bool IsToken(IParameterSymbol p) => p.Type.ToDisplayString() == "System.Threading.CancellationToken";

    /// <summary>
    /// By name and namespace, because the compiler types the local it makes for a <c>using var</c> as
    /// nullable, and a display name then ends in a question mark.
    /// </summary>
    private static bool IsTokenLocal(ILocalSymbol l) =>
        l.Type is INamedTypeSymbol { Name: "CancellationToken" or "CancellationTokenSource" } type
        && type.ContainingNamespace.ToDisplayString() == "System.Threading";

    /// <summary>
    /// The token locals in the graph that are given something carrying the function's tokens: a copy, a copy
    /// of a copy, or a source linked to one. Followed to a fixed point. Only these two types, because a local
    /// of any type that was merely read with the token (a corpus loaded on it) is the result of the work and
    /// not the means of cancelling the next.
    /// </summary>
    private static HashSet<ILocalSymbol> AliasesIn(
        ControlFlowGraph graph, IReadOnlyList<IParameterSymbol> tokens, IReadOnlySet<ILocalSymbol> outer)
    {
        var aliases = new HashSet<ILocalSymbol>(outer, SymbolEqualityComparer.Default);
        var all = graph.Blocks.SelectMany(Operations).SelectMany(o => o.DescendantsAndSelf()).ToList();

        // A declaration with an initializer reaches the graph as an assignment or kept as a declarator.
        var given = all.OfType<ISimpleAssignmentOperation>()
            .Where(a => a.Target is ILocalReferenceOperation)
            .Select(a => (Local: ((ILocalReferenceOperation)a.Target).Local, Value: (IOperation)a.Value))
            .Concat(all.OfType<IVariableDeclaratorOperation>().Where(d => d.Initializer is not null)
                .Select(d => (Local: d.Symbol, Value: (IOperation)d.Initializer!.Value)))
            .Where(g => IsTokenLocal(g.Local))
            .ToList();

        bool grew;
        do
        {
            grew = false;
            foreach (var (local, value) in given)
                if (!aliases.Contains(local) && CarriesToken(value, tokens, aliases))
                    grew |= aliases.Add(local);
        } while (grew);

        return aliases;
    }

    /// <summary>Whether the value holds a token the function can name, an alias of one, or the HttpContext's.</summary>
    private static bool CarriesToken(IOperation value, IReadOnlyList<IParameterSymbol> tokens, HashSet<ILocalSymbol> aliases) =>
        value.DescendantsAndSelf().Any(o => o switch
        {
            IParameterReferenceOperation p => tokens.Contains(p.Parameter, SymbolEqualityComparer.Default),
            ILocalReferenceOperation l => aliases.Contains(l.Local),
            IPropertyReferenceOperation property => IsRequestAborted(property.Property),
            _ => false,
        });

    /// <summary>
    /// <c>HttpContext.RequestAborted</c> is the request's token by another route: nothing is passed to the
    /// function, so the parameter check cannot see it.
    /// </summary>
    private static bool IsRequestAborted(IPropertySymbol property) =>
        property.Name == "RequestAborted" && property.Type.ToDisplayString() == "System.Threading.CancellationToken";

    private static bool IsWrite(IMethodSymbol m)
    {
        if (!WriteNames.Contains(m.Name)) return false;
        if (m.Name.StartsWith("SaveChanges", StringComparison.Ordinal))
        {
            for (var t = m.ContainingType; t is not null; t = t.BaseType)
                if (t.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbContext") return true;
            return false;
        }

        return m.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// The methods in src that write, directly or through another: followed to a fixed point, with an
    /// interface method declared in src counted when an implementation writes.
    /// </summary>
    private static HashSet<IMethodSymbol> Writers(
        Dictionary<IMethodSymbol, List<IOperation>> bodies, List<INamedTypeSymbol> types)
    {
        var committing = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        bool grew;
        do
        {
            grew = false;
            foreach (var (method, operations) in bodies)
                if (!committing.Contains(method) && operations.Any(o => o.DescendantsAndSelf()
                        .OfType<IInvocationOperation>().Any(i => Writes(i.TargetMethod, committing))))
                    grew |= committing.Add(method);

            foreach (var type in types)
                foreach (var contract in type.AllInterfaces.Where(i => i.Locations.Any(l => l.IsInSource)))
                    foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
                        if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation
                            && committing.Contains(implementation.OriginalDefinition))
                            grew |= committing.Add(member.OriginalDefinition);
        } while (grew);

        return committing;
    }

    private static bool Writes(IMethodSymbol m, HashSet<IMethodSymbol> committing) =>
        IsWrite(m) || committing.Contains(m.OriginalDefinition)
                   || (m.ReducedFrom is { } extension && committing.Contains(extension.OriginalDefinition));

    /// <summary>
    /// What after a write can run on the function's token: the parameter, an alias of it, the HttpContext's
    /// token, a local function or lambda that captures one (called or passed on after the write, or held in
    /// a local made before it), and a nested function's own use of a token it captured from outside.
    /// </summary>
    private static IEnumerable<Finding> Check(Function f, HashSet<IMethodSymbol> committing)
    {
        var graph = f.Graph;
        var model = graph.OriginalOperation.SemanticModel!;
        var delegates = DelegatesCapturingAToken(f, model);

        foreach (var block in graph.Blocks)
        {
            var operations = Operations(block).ToList();
            for (var i = 0; i < operations.Count; i++)
            {
                var write = operations[i].DescendantsAndSelf().OfType<IInvocationOperation>()
                    .FirstOrDefault(c => Writes(c.TargetMethod, committing));
                if (write is null) continue;

                // The rest of this operation, the rest of the block, and every block the write can reach.
                var after = operations[i].DescendantsAndSelf().Where(o => o.Syntax.SpanStart > write.Syntax.Span.End)
                    .Concat(operations.Skip(i + 1).SelectMany(o => o.DescendantsAndSelf()))
                    .Concat(Reachable(graph, block).SelectMany(Operations).SelectMany(o => o.DescendantsAndSelf()))
                    .ToList();

                var afterWrite = $"{write.TargetMethod.Name} at line {write.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                foreach (var op in after)
                {
                    // Where the token is named, and what it is called on; null when this is not a use.
                    (SyntaxNode Where, string Target)? use = op switch
                    {
                        IParameterReferenceOperation p when f.Tokens.Contains(p.Parameter, SymbolEqualityComparer.Default)
                            => (p.Syntax, TargetOf(p)),
                        ILocalReferenceOperation l when f.Aliases.Contains(l.Local) && !IsDisposal(l) => (l.Syntax, TargetOf(l)),
                        ILocalReferenceOperation l when delegates.Contains(l.Local) => (l.Syntax, "delegate"),
                        IPropertyReferenceOperation property when IsRequestAborted(property.Property)
                            => (property.Syntax, TargetOf(property)),
                        IFlowAnonymousFunctionOperation lambda when MentionIn(lambda.Syntax, model, f, delegates) is { } named
                            => (named, "lambda"),
                        IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction } call
                            when CapturesAToken(call.TargetMethod, model, f, delegates) => (call.Syntax, call.TargetMethod.Name),
                        IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } reference
                            when CapturesAToken(reference.Method, model, f, delegates) => (reference.Syntax, reference.Method.Name),
                        _ => null,
                    };

                    if (use is not { } found) continue;
                    yield return new Finding(SiteOf(found.Where.GetLocation()), f.Name, found.Where.ToString(),
                        found.Target, afterWrite);
                }

                break; // the first write in a block reaches everything after it
            }
        }
    }

    /// <summary>
    /// The null check and <c>Dispose</c> the compiler makes for a <c>using var</c>, in the <c>finally</c> block
    /// it adds after the write. Disposing a linked source does not use the token.
    /// </summary>
    private static bool IsDisposal(ILocalReferenceOperation local)
    {
        var parent = local.Parent;
        while (parent is IConversionOperation) parent = parent.Parent;
        return parent is IIsNullOperation or IInvocationOperation { TargetMethod.Name: "Dispose" or "DisposeAsync" };
    }

    /// <summary>
    /// Locals that hold a lambda or local function which captures a token, so that making it before the write
    /// and calling it after is as much a use as naming the token there. Followed to a fixed point, as the
    /// aliases are: a copy of such a local holds it too, and so does a lambda that calls one.
    /// </summary>
    private static HashSet<ILocalSymbol> DelegatesCapturingAToken(Function f, SemanticModel model)
    {
        var all = f.Graph.Blocks.SelectMany(Operations).SelectMany(o => o.DescendantsAndSelf()).ToList();
        var given = all.OfType<ISimpleAssignmentOperation>()
            .Where(a => a.Target is ILocalReferenceOperation)
            .Select(a => (Local: ((ILocalReferenceOperation)a.Target).Local, Value: (IOperation)a.Value))
            .Concat(all.OfType<IVariableDeclaratorOperation>().Where(d => d.Initializer is not null)
                .Select(d => (Local: d.Symbol, Value: (IOperation)d.Initializer!.Value)))
            .ToList();

        var held = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        bool grew;
        do
        {
            grew = false;
            foreach (var (local, value) in given)
                if (!held.Contains(local) && value.DescendantsAndSelf().Any(v => v switch
                {
                    IFlowAnonymousFunctionOperation lambda => MentionIn(lambda.Syntax, model, f, held) is not null,
                    IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } reference
                        => CapturesAToken(reference.Method, model, f, held),
                    ILocalReferenceOperation other => held.Contains(other.Local),
                    _ => false,
                }))
                    grew |= held.Add(local);
        } while (grew);

        return held;
    }

    private static bool CapturesAToken(
        IMethodSymbol localFunction, SemanticModel model, Function f, HashSet<ILocalSymbol> held) =>
        localFunction.DeclaringSyntaxReferences.Any(r => MentionIn(r.GetSyntax(), model, f, held) is not null);

    /// <summary>
    /// The first place in the code that names one of the function's tokens or aliases, or the HttpContext's
    /// token, or a delegate held in a local that captures one, or calls a local function that does. Null when
    /// the code uses none of them.
    /// </summary>
    private static IdentifierNameSyntax? MentionIn(
        SyntaxNode code, SemanticModel model, Function f, HashSet<ILocalSymbol> held, HashSet<ISymbol>? visiting = null)
    {
        visiting ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var name in code.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            switch (model.GetSymbolInfo(name).Symbol)
            {
                case IParameterSymbol p when f.Tokens.Contains(p, SymbolEqualityComparer.Default):
                case ILocalSymbol l when f.Aliases.Contains(l) || held.Contains(l):
                case IPropertySymbol property when IsRequestAborted(property):
                    return name;
                case IMethodSymbol { MethodKind: MethodKind.LocalFunction } local when visiting.Add(local):
                    if (local.DeclaringSyntaxReferences.Any(r => MentionIn(r.GetSyntax(), model, f, held, visiting) is not null))
                        return name;
                    break;
            }

        return null;
    }

    /// <summary>The call the token is passed to, or the member read from it.</summary>
    private static string TargetOf(IOperation use) => use.Parent switch
    {
        IArgumentOperation { Parent: IInvocationOperation call } => call.TargetMethod.Name,
        IArgumentOperation { Parent: IObjectCreationOperation create } => create.Type?.Name ?? "new",
        IPropertyReferenceOperation property => property.Property.Name,
        IInvocationOperation call => call.TargetMethod.Name,
        _ => use.Parent?.Kind.ToString() ?? "use",
    };

    private static IEnumerable<BasicBlock> Reachable(ControlFlowGraph graph, BasicBlock from)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<BasicBlock>();

        void Follow(ControlFlowBranch? branch)
        {
            if (branch is null) return;
            foreach (var region in branch.FinallyRegions)
                for (var n = region.FirstBlockOrdinal; n <= region.LastBlockOrdinal; n++)
                    if (seen.Add(n)) queue.Enqueue(graph.Blocks[n]);
            if (branch.Destination is { } next && seen.Add(next.Ordinal)) queue.Enqueue(next);
        }

        Follow(from.FallThroughSuccessor);
        Follow(from.ConditionalSuccessor);
        while (queue.TryDequeue(out var block))
        {
            yield return block;
            Follow(block.FallThroughSuccessor);
            Follow(block.ConditionalSuccessor);
        }
    }

    private static bool IsOurs(AttributeData a) =>
        a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute"
        && a.ConstructorArguments is [{ Value: Category }, { Value: string id }]
        && (id == CheckId || id.StartsWith(CheckId + ":", StringComparison.Ordinal));

    /// <summary>
    /// The suppression this attribute is, read from its syntax so that it is found wherever it is written,
    /// or null when it is some other attribute.
    /// </summary>
    private static Suppression? SuppressionIn(AttributeSyntax attribute, SemanticModel model)
    {
        if (model.GetSymbolInfo(attribute).Symbol is not IMethodSymbol constructor
            || constructor.ContainingType.ToDisplayString() != "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute")
            return null;

        var arguments = attribute.ArgumentList?.Arguments.ToList() ?? [];
        var positional = arguments.Where(a => a.NameEquals is null && a.NameColon is null).ToList();
        if (positional.Count < 2
            || model.GetConstantValue(positional[0].Expression).Value as string != Category
            || model.GetConstantValue(positional[1].Expression).Value as string is not { } id
            || !(id == CheckId || id.StartsWith(CheckId + ":", StringComparison.Ordinal)))
            return null;

        var justification = arguments.Find(a => a.NameEquals?.Name.Identifier.Text == "Justification");
        return new Suppression(
            SiteOf(attribute.GetLocation()),
            justification is not null && model.GetConstantValue(justification.Expression).Value is string { Length: > 0 });
    }

    /// <summary>The suppression on the function, or on anything that contains it, that covers this finding.</summary>
    private static Suppression? SuppressionFor(
        ISymbol? function, Finding finding, Dictionary<(SyntaxTree, int), Suppression> all)
    {
        for (var s = function; s is not null and not INamespaceSymbol; s = s.ContainingSymbol)
            foreach (var attribute in s.GetAttributes().Where(IsOurs))
            {
                var only = attribute.NamedArguments.FirstOrDefault(a => a.Key == "MessageId").Value.Value as string;
                if ((only is null || only == finding.Target)
                    && attribute.ApplicationSyntaxReference is { } at
                    && all.TryGetValue((at.SyntaxTree, at.Span.Start), out var suppression))
                    return suppression;
            }

        return null;
    }
}
