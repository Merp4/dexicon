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
/// So this compiles <c>src/</c> and, in every method, local function and lambda that takes a
/// <see cref="CancellationToken"/>, follows the control-flow graph from each write and reports any use of
/// that token the write can reach. A write is <c>SaveChanges</c>, <c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c> or <c>ExecuteSql</c> on the catalogue, or a call to a method in <c>src/</c> that
/// makes one, followed through calls and interfaces declared in <c>src/</c> until nothing new is found.
///
/// After a write a function passes <see cref="CancellationToken.None"/>, records what it did through a
/// callback at the write (<c>committed:</c> on <c>CorpusConfiguration</c>), or saves the follow-up in the
/// same save. A use inside a <c>try</c> whose <c>catch</c> takes <see cref="OperationCanceledException"/>
/// is allowed: that is how a loop over several writes turns a late cancel into what it saved
/// (<c>DocumentEndpoints.UploadAsync</c>).
///
/// A function or type is exempt only through
/// <c>[SuppressMessage("Dexicon.Cancellation", "TokenAfterCommit", Justification = "...")]</c>, with a
/// reason, and optionally a <c>MessageId</c> naming the one call it covers. A suppression that no longer
/// suppresses anything fails, as does one without a justification.
///
/// Blind to: a token that is not a parameter of the function using it (a field, a linked source's token);
/// a <c>catch</c> reached by an exception thrown after a write, since the graph has no edge for an
/// exception and treats every catch as following a failed write; a callback passed to the writing call
/// and run inside it; writes to Qdrant and the blob store, which are not catalogue writes; and the
/// conditions on a path, so a branch taken only when nothing was written is followed too.
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

        public static Source Load()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "LICENSE"))) root = root.Parent;
            var src = Path.Combine(root?.FullName ?? throw new InvalidOperationException("repository root not found"), "src");

            // What the test host loaded, less the built Dexicon assemblies, whose types the source declares.
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("Dexicon", StringComparison.Ordinal))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

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

    private sealed record Function(ISymbol? Symbol, ControlFlowGraph Graph, IReadOnlyList<IParameterSymbol> Tokens, string Name);

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
        var suppressions = new Dictionary<AttributeData, bool>();

        foreach (var compilation in compilations)
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is BaseTypeDeclarationSyntax typeDeclaration
                    && model.GetDeclaredSymbol(typeDeclaration) is INamedTypeSymbol type)
                {
                    types.Add(type);
                    Track(type, suppressions);
                }

                if (node is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)) continue;
                if (model.GetDeclaredSymbol(node) is not IMethodSymbol method) continue;
                Track(method, suppressions);
                if (model.GetOperation(node) is { } body && Graph(body) is { } graph)
                    Collect(functions, bodies, graph, method, $"{method.ContainingType.Name}.{method.Name}");
            }

            if (tree.GetRoot() is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().Any()
                && model.GetOperation(unit) is { } main && Graph(main) is { } mainGraph)
                Collect(functions, bodies, mainGraph, null, "<top-level>");

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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in functions.Where(f => f.Tokens.Count > 0))
        foreach (var finding in Check(f, committing))
        {
            if (!seen.Add($"{finding.Site} {finding.Use} {finding.Function}")) continue;
            var by = SuppressionFor(f.Symbol, finding, suppressions);
            if (by is null) Unsuppressed.Add(finding);
            else suppressions[by] = true;
        }

        foreach (var (attribute, used) in suppressions)
        {
            var at = attribute.ApplicationSyntaxReference is { } r ? SiteOf(r.GetSyntax().GetLocation()) : "?";
            if (!used) UnusedSuppressions.Add(at);
            if (!attribute.NamedArguments.Any(a => a.Key == "Justification" && a.Value.Value is string { Length: > 0 }))
                UnjustifiedSuppressions.Add(at);
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

    /// <summary>Adds the graph's function and every local function and lambda inside it.</summary>
    private static void Collect(List<Function> functions, Dictionary<IMethodSymbol, List<IOperation>> bodies,
        ControlFlowGraph graph, IMethodSymbol? symbol, string name)
    {
        functions.Add(new Function(symbol, graph, symbol?.Parameters.Where(IsToken).ToList() ?? [], name));
        if (symbol is not null) bodies[symbol.OriginalDefinition] = [.. graph.Blocks.SelectMany(Operations)];

        foreach (var local in graph.LocalFunctions)
            Collect(functions, bodies, graph.GetLocalFunctionControlFlowGraph(local), local, $"{name}/{local.Name}");

        foreach (var lambda in graph.Blocks.SelectMany(Operations).SelectMany(o => o.DescendantsAndSelf())
                     .OfType<IFlowAnonymousFunctionOperation>())
            Collect(functions, bodies, graph.GetAnonymousFunctionControlFlowGraph(lambda), lambda.Symbol,
                $"{name}/lambda at line {lambda.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
    }

    private static bool IsToken(IParameterSymbol p) => p.Type.ToDisplayString() == "System.Threading.CancellationToken";

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

    private static IEnumerable<Finding> Check(Function f, HashSet<IMethodSymbol> committing)
    {
        var graph = f.Graph;
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
                foreach (var use in after.OfType<IParameterReferenceOperation>()
                             .Where(r => f.Tokens.Contains(r.Parameter, SymbolEqualityComparer.Default)))
                {
                    if (CaughtAsCancellation(graph, use)) continue;
                    yield return new Finding(SiteOf(use.Syntax.GetLocation()), f.Name, use.Syntax.ToString(),
                        TargetOf(use), afterWrite);
                }

                // A lambda made after the write that captures the token can run after it too.
                foreach (var lambda in after.OfType<IFlowAnonymousFunctionOperation>())
                {
                    var model = graph.OriginalOperation.SemanticModel!;
                    foreach (var name in lambda.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
                        if (model.GetSymbolInfo(name).Symbol is IParameterSymbol p
                            && f.Tokens.Contains(p, SymbolEqualityComparer.Default))
                            yield return new Finding(SiteOf(name.GetLocation()), f.Name, name.ToString(), "lambda", afterWrite);
                }

                break; // the first write in a block reaches everything after it
            }
        }
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

    /// <summary>Whether the use is inside a <c>try</c> with a <c>catch</c> for <see cref="OperationCanceledException"/>.</summary>
    private static bool CaughtAsCancellation(ControlFlowGraph graph, IOperation use)
    {
        var block = graph.Blocks.FirstOrDefault(b => Operations(b).Any(o => o.DescendantsAndSelf().Contains(use)));
        for (var region = block?.EnclosingRegion; region is not null; region = region.EnclosingRegion)
        {
            if (region is not { Kind: ControlFlowRegionKind.Try, EnclosingRegion: { Kind: ControlFlowRegionKind.TryAndCatch } tryAndCatch })
                continue;
            if (tryAndCatch.NestedRegions.Any(r => r.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler
                                                   && r.ExceptionType?.ToDisplayString() is "System.OperationCanceledException"
                                                       or "System.Threading.Tasks.TaskCanceledException"))
                return true;
        }

        return false;
    }

    private static void Track(ISymbol symbol, Dictionary<AttributeData, bool> suppressions)
    {
        foreach (var attribute in symbol.GetAttributes().Where(IsOurs))
            suppressions.TryAdd(attribute, false);
    }

    private static bool IsOurs(AttributeData a) =>
        a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute"
        && a.ConstructorArguments is [{ Value: Category }, { Value: string id }]
        && (id == CheckId || id.StartsWith(CheckId + ":", StringComparison.Ordinal));

    /// <summary>The suppression on the function, or on anything that contains it, that covers this finding.</summary>
    private static AttributeData? SuppressionFor(ISymbol? function, Finding finding, Dictionary<AttributeData, bool> tracked)
    {
        for (var s = function; s is not null and not INamespaceSymbol; s = s.ContainingSymbol)
            foreach (var attribute in s.GetAttributes().Where(IsOurs))
            {
                var only = attribute.NamedArguments.FirstOrDefault(a => a.Key == "MessageId").Value.Value as string;
                if (only is null || only == finding.Target)
                    return tracked.Keys.FirstOrDefault(k => k.ApplicationSyntaxReference?.Span == attribute.ApplicationSyntaxReference?.Span
                                                            && k.ApplicationSyntaxReference?.SyntaxTree == attribute.ApplicationSyntaxReference?.SyntaxTree)
                           ?? attribute;
            }

        return null;
    }
}
