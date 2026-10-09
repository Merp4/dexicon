using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dexicon.Tests;

/// <summary>
/// Every place the MCP tools and the proposal service put a string into text a model reads, and every log
/// call in the application, is read with the compiler's own view of it: the application's source is
/// compiled and each interpolated string, each message of an <c>McpException</c> and each log argument is
/// followed back to what it is made of. A string that is a constant, the result of one of the wrappers in
/// <see cref="Wrappers"/>, or built of such parts passes; any other is a failure unless it is on
/// <see cref="Allowed"/> with the reason it is safe. A comment or an unused string cannot satisfy it, which a
/// search of the source text could be made to.
/// </summary>
public sealed class EchoScanTests
{
    /// <summary>
    /// The methods whose result is a caller's or a stored value held to one line (see <c>LogText</c>), or a
    /// list of names the resolver bounds. <c>Line</c> is the proposal service's own name for
    /// <c>LogText.OneLine</c>.
    /// </summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "Echo", "OneLine", "Line", "Quoted", "Listed", "QuotedUnknown", "Neutralise", "Cut",
    };

    /// <summary>
    /// A refusal helper may be given anything, because it puts what it is given on one line and cuts it: a
    /// message passed to it is not read for what it is made of.
    /// </summary>
    private static readonly HashSet<string> RefusalHelpers = new(StringComparer.Ordinal) { "Refusal", "Refused" };

    /// <summary>
    /// Methods that change a string without adding to it, so what matters is what they are called on.
    /// </summary>
    private static readonly HashSet<string> Transforms = new(StringComparer.Ordinal)
    {
        "ToString", "ToLowerInvariant", "ToUpperInvariant", "Trim", "TrimStart", "TrimEnd", "Substring", "PadLeft",
        "PadRight", "Replace", "ReplaceLineEndings", "Normalize",
    };

    /// <summary>
    /// Text that is safe without a wrapper, by the file and the expression as written and the reason.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["DexiconTools.cs: ex.Message"] = "the messages of UnknownSearchModeException and EmbeddingDimensionMismatchException, which the repository writes",
        ["*: p.Id"] = "an id the catalogue generated",
        ["*: job.Id"] = "an id the catalogue generated",
        ["*: added.Value!.IndexJob.Id"] = "an id the catalogue generated",
        ["*: jobId"] = "an id the catalogue generated",
        ["*: principal.Scopes"] = "the scope names of a key, from the repository's list",
        ["DexiconTools.cs: scope"] = "Require: the scope the tool needs, written at each call",
        ["ConfigureTools.cs: action"] = "Audit: a verb written at each call",
        ["ConfigureTools.cs: set"] = "Audit: the names of what changed, written at each call",
        ["ConfigureTools.cs: what1"] = "AuditSource: a verb written at each call",
        ["ConfigureTools.cs: name"] = "Kilobytes: the name of the argument, written at each call",
        ["ConfigureTools.cs: g.Name"] = "Clashes: names of arguments, written at each call",
        ["ConfigureTools.cs: allowed"] = "Resets: the names reset may take, written at each call",
        ["ConfigureTools.cs: valid"] = "Resets: the names reset may take, written at each call",
        ["ConfigureTools.cs: hereBy"] = "names of corpora, passed through OneLine where IndexedFoldersAsync builds them",
        ["ConfigureTools.cs: by"] = "names of corpora, passed through OneLine where IndexedFoldersAsync builds them",
        ["DexiconTools.cs: s.State"] = "the name of a state, from the repository's list",
        ["DexiconTools.cs: set.State"] = "the name of a state, from the repository's list",
        ["DexiconTools.cs: summary.State"] = "the name of a state, from the repository's list",
        ["DexiconTools.cs: g.Status"] = "the name of a file status, from the repository's list",
        ["DexiconTools.cs: distance"] = "a count of commits ahead and behind",
        ["DexiconTools.cs: newest.Sha[..7]"] = "seven hexadecimal digits",
        ["DexiconTools.cs: up.ShortName"] = "a git ref name, which git does not allow control characters in",
        ["DexiconTools.cs: root"] = "a source root in a sample of problem files, passed through OneLine in RenderProblemFiles",
        ["DexiconTools.cs: r.RelativePath"] = "a path in a sample of problem files, passed through OneLine in RenderProblemFiles",
        ["ProposalService.cs: WorkspaceDiscovery.Canonical(root, s.RootPath)"] = "Describe: its result goes through Line at both its uses",
        ["DexiconTools.cs: Literal"] = "serializer options, not text",
        ["ConfigureTools.cs: ex.Message"] = "the message of ScopeResolutionException, passed through the Refusal it is passed to",
        ["Bootstrapper.cs: options.Storage.CatalogPath"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs: options.Qdrant.Endpoint"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs: target.Provider"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs: target.Model"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs: refused.Detail"] = "the startup check's own text, built from operator configuration",
        ["Bootstrapper.cs: generated"] = "the generated admin password, printed once on purpose (docs/10)",
        ["DexiconAuth.cs: ctx.Connection.RemoteIpAddress?.ToString()"] = "an IP address",
        ["DexiconAuth.cs: Agent(ctx.Request.Headers.UserAgent.FirstOrDefault())"] = "passed through OneLine by Agent, which cuts and passes it through OneLine",
        ["DexiconAuth.cs: CallerDigest(presented)"] = "a hex digest",
        ["DocumentEndpoints.cs: corpus.Id"] = "an id the catalogue generated",
        ["SystemEndpoints.cs: token.Scopes"] = "the scope names of a key, from the repository's list",
        ["ScopeExceptionHandler.cs: title"] = "a title written in the handler",
    };

    private static readonly Lazy<Scan> Source = new(() => new Scan());

    private sealed class Scan
    {
        public Scan()
        {
            var program = SourceFiles.Find("src", "Dexicon", "Program.cs");
            var root = Path.GetDirectoryName(program)!;
            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "obj"), "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
                .ToList();
            var options = new CSharpParseOptions(LanguageVersion.Latest);
            Trees = [.. files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f))];

            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(p => Path.GetFileNameWithoutExtension(p) is not ("Dexicon" or "Dexicon.Tests"))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();
            Compilation = CSharpCompilation.Create(
                "DexiconScan", Trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        }

        public IReadOnlyList<SyntaxTree> Trees { get; }

        public CSharpCompilation Compilation { get; }
    }

    private static SemanticModel ModelOf(SyntaxNode node) => Source.Value.Compilation.GetSemanticModel(node.SyntaxTree);

    private static string FileOf(SyntaxNode node) => Path.GetFileName(node.SyntaxTree.FilePath);

    private static string Where(SyntaxNode node) =>
        $"{FileOf(node)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    private static ExpressionSyntax Bare(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
        return expression;
    }

    /// <summary>
    /// What in <paramref name="start"/> is not safe to print, or null when it is all a constant, a wrapper's
    /// result, text built of such parts, a value that is not a string, or allowed. Followed back through
    /// local variables and the application's own methods.
    /// </summary>
    private static string? Unsafe(ExpressionSyntax start, HashSet<ISymbol>? seen = null, int depth = 0)
    {
        var expression = Bare(start);
        var model = ModelOf(expression);
        if (model.GetConstantValue(expression).HasValue) return null;
        if (model.GetTypeInfo(expression).Type?.SpecialType != SpecialType.System_String) return null;
        if (Allowed.ContainsKey($"{FileOf(expression)}: {expression}") || Allowed.ContainsKey($"*: {expression}")) return null;
        if (depth > 8) return $"{expression} (too deep to follow)";
        seen ??= [];

        switch (expression)
        {
            case LiteralExpressionSyntax:
                return null;

            case MemberAccessExpressionSyntax { Expression: PredefinedTypeSyntax, Name.Identifier.Text: "Empty" }:
                return null;

            case InterpolatedStringExpressionSyntax interpolated:
                return interpolated.Contents.OfType<InterpolationSyntax>()
                    .Select(i => Unsafe(i.Expression, seen, depth + 1)).FirstOrDefault(x => x is not null);

            case ConditionalExpressionSyntax conditional:
                return Unsafe(conditional.WhenTrue, seen, depth + 1) ?? Unsafe(conditional.WhenFalse, seen, depth + 1);

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression)
                                                    || binary.IsKind(SyntaxKind.AddExpression):
                return Unsafe(binary.Left, seen, depth + 1) ?? Unsafe(binary.Right, seen, depth + 1);

            case SwitchExpressionSyntax switchExpression:
                return switchExpression.Arms.Select(a => Unsafe(a.Expression, seen, depth + 1)).FirstOrDefault(x => x is not null);

            case ElementAccessExpressionSyntax access:
                return Unsafe(access.Expression, seen, depth + 1);

            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return Unsafe(postfix.Operand, seen, depth + 1);

            case InvocationExpressionSyntax invocation:
                return UnsafeInvocation(invocation, seen, depth);

            case IdentifierNameSyntax identifier:
                return UnsafeIdentifier(identifier, seen, depth);
        }

        return expression.ToString();
    }

    private static string? UnsafeInvocation(InvocationExpressionSyntax invocation, HashSet<ISymbol> seen, int depth)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => string.Empty,
        };
        if (Wrappers.Contains(name)) return null;

        if (Transforms.Contains(name) && invocation.Expression is MemberAccessExpressionSyntax receiver)
            return Unsafe(receiver.Expression, seen, depth + 1);

        if (name is "Join" or "Concat" && invocation.ArgumentList.Arguments.Count >= 1)
            return invocation.ArgumentList.Arguments.Skip(name == "Join" ? 1 : 0)
                .Select(a => UnsafeSequence(a.Expression, seen, depth + 1)).FirstOrDefault(x => x is not null);

        if (name == "Serialize" && invocation.ArgumentList.Arguments.Count > 0)
            return Unsafe(invocation.ArgumentList.Arguments[0].Expression, seen, depth + 1);

        // A method of the application: what it returns.
        if (ModelOf(invocation).GetSymbolInfo(invocation).Symbol is IMethodSymbol method && seen.Add(method))
        {
            var bodies = method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).ToList();
            if (bodies.Count > 0)
            {
                var returned = bodies.SelectMany(ReturnedExpressions).ToList();
                return returned.Count == 0
                    ? invocation.ToString()
                    : returned.Select(r => Unsafe(r, seen, depth + 1)).FirstOrDefault(x => x is not null);
            }
        }

        return invocation.ToString();
    }

    private static IEnumerable<ExpressionSyntax> ReturnedExpressions(SyntaxNode declaration)
    {
        var arrow = declaration switch
        {
            MethodDeclarationSyntax m => m.ExpressionBody?.Expression,
            LocalFunctionStatementSyntax l => l.ExpressionBody?.Expression,
            _ => null,
        };
        if (arrow is not null) return [arrow];

        return declaration.DescendantNodes(n => n is not (LocalFunctionStatementSyntax or LambdaExpressionSyntax))
            .OfType<ReturnStatementSyntax>().Select(r => r.Expression).OfType<ExpressionSyntax>();
    }

    private static string? UnsafeIdentifier(IdentifierNameSyntax identifier, HashSet<ISymbol> seen, int depth)
    {
        var symbol = ModelOf(identifier).GetSymbolInfo(identifier).Symbol;
        if (symbol is null || !seen.Add(symbol)) return symbol is null ? identifier.ToString() : null;

        if (symbol is ILocalSymbol)
        {
            var declarators = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().ToList();
            if (declarators.Count > 0)
            {
                var scope = declarators[0].Ancestors().OfType<BlockSyntax>().LastOrDefault() as SyntaxNode
                            ?? declarators[0].Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault()!;
                var assigned = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.Left is IdentifierNameSyntax left
                                && SymbolEqualityComparer.Default.Equals(ModelOf(a).GetSymbolInfo(left).Symbol, symbol))
                    .Select(a => a.Right);
                var initial = declarators.Select(d => d.Initializer?.Value).OfType<ExpressionSyntax>();
                var found = initial.Concat(assigned).Select(e => Unsafe(e, seen, depth + 1)).FirstOrDefault(x => x is not null);
                if (found is not null) return found;
                return declarators.Any(d => d.Initializer is null) && !assigned.Any() ? identifier.ToString() : null;
            }
        }

        if (symbol is IFieldSymbol or IPropertySymbol)
        {
            var initializers = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax())
                .Select(s => (s as VariableDeclaratorSyntax)?.Initializer?.Value).OfType<ExpressionSyntax>().ToList();
            if (initializers.Count > 0)
                return initializers.Select(e => Unsafe(e, seen, depth + 1)).FirstOrDefault(x => x is not null);
        }

        return identifier.ToString();
    }

    /// <summary>What in a sequence of strings (the second argument of string.Join) is not safe.</summary>
    private static string? UnsafeSequence(ExpressionSyntax start, HashSet<ISymbol> seen, int depth)
    {
        var expression = Bare(start);
        if (Allowed.ContainsKey($"{FileOf(expression)}: {expression}") || Allowed.ContainsKey($"*: {expression}")) return null;
        if (depth > 8) return $"{expression} (too deep to follow)";

        switch (expression)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } call:
                if (member.Name.Identifier.Text == "Select")
                {
                    var selector = call.ArgumentList.Arguments.Last().Expression;
                    return selector switch
                    {
                        SimpleLambdaExpressionSyntax simple => Unsafe((ExpressionSyntax)simple.Body, seen, depth + 1),
                        ParenthesizedLambdaExpressionSyntax parenthesized => Unsafe((ExpressionSyntax)parenthesized.Body, seen, depth + 1),
                        _ => MethodGroup(selector, seen, depth),
                    };
                }

                if (member.Name.Identifier.Text is "Where" or "Order" or "OrderBy" or "ToList" or "ToArray" or "Take" or "Distinct" or "Skip" or "OfType")
                    return UnsafeSequence(member.Expression, seen, depth + 1);
                break;

            case ObjectCreationExpressionSyntax { Initializer: null }:
                return null;

            case ObjectCreationExpressionSyntax { Initializer: { } created }:
                return created.Expressions.Select(e => Unsafe(e, seen, depth + 1)).FirstOrDefault(x => x is not null);

            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return implicitArray.Initializer.Expressions.Select(e => Unsafe(e, seen, depth + 1)).FirstOrDefault(x => x is not null);

            case ArrayCreationExpressionSyntax { Initializer: { } initializer }:
                return initializer.Expressions.Select(e => Unsafe(e, seen, depth + 1)).FirstOrDefault(x => x is not null);

            case CollectionExpressionSyntax collection:
                return collection.Elements.OfType<ExpressionElementSyntax>().Select(e => Unsafe(e.Expression, seen, depth + 1))
                    .FirstOrDefault(x => x is not null);

            case IdentifierNameSyntax identifier when ModelOf(identifier).GetSymbolInfo(identifier).Symbol is ILocalSymbol local:
                if (!seen.Add(local)) return null;
                var scope = local.DeclaringSyntaxReferences.First().GetSyntax().Ancestors().OfType<BlockSyntax>().Last();
                var added = scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Add", Expression: IdentifierNameSyntax target }
                                && SymbolEqualityComparer.Default.Equals(ModelOf(i).GetSymbolInfo(target).Symbol, local))
                    .Select(i => Unsafe(i.ArgumentList.Arguments[0].Expression, seen, depth + 1)).FirstOrDefault(x => x is not null);
                if (added is not null) return added;
                var declarator = local.DeclaringSyntaxReferences.First().GetSyntax() as VariableDeclaratorSyntax;
                return declarator?.Initializer is { } init ? UnsafeSequence(init.Value, seen, depth + 1) : null;
        }

        return expression.ToString();
    }

    private static string? MethodGroup(ExpressionSyntax selector, HashSet<ISymbol> seen, int depth)
    {
        if (selector is IdentifierNameSyntax name && Wrappers.Contains(name.Identifier.Text)) return null;
        if (ModelOf(selector).GetSymbolInfo(selector).Symbol is IMethodSymbol method && seen.Add(method))
        {
            var returned = method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).SelectMany(ReturnedExpressions).ToList();
            if (returned.Count > 0)
                return returned.Select(r => Unsafe(r, seen, depth + 1)).FirstOrDefault(x => x is not null);
        }

        return selector.ToString();
    }

    private static IEnumerable<SyntaxTree> Trees(Func<string, bool> pick) => Source.Value.Trees.Where(t => pick(t.FilePath));

    private static bool InMcp(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}Mcp{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.EndsWith("ProposalService.cs", StringComparison.Ordinal);

    /// <summary>
    /// Whether the node is inside an argument of a wrapper or a refusal helper, which puts what it is given on one line.
    /// </summary>
    private static bool InsideAWrapperOrRefusalHelper(SyntaxNode node) => node.Ancestors().OfType<InvocationExpressionSyntax>()
        .Any(i => i.Expression switch
        {
            MemberAccessExpressionSyntax member => RefusalHelpers.Contains(member.Name.Identifier.Text) || Wrappers.Contains(member.Name.Identifier.Text),
            IdentifierNameSyntax identifier => RefusalHelpers.Contains(identifier.Identifier.Text) || Wrappers.Contains(identifier.Identifier.Text),
            _ => false,
        });

    [Fact]
    public void TheCompilationResolvesTheTypesTheScanReadsAndTheScanFindsItsSites()
    {
        var holes = 0;
        var unresolved = 0;
        foreach (var tree in Trees(InMcp))
        {
            var model = Source.Value.Compilation.GetSemanticModel(tree);
            foreach (var hole in tree.GetRoot().DescendantNodes().OfType<InterpolationSyntax>())
            {
                holes++;
                if (model.GetTypeInfo(hole.Expression).Type is null or IErrorTypeSymbol) unresolved++;
            }
        }

        holes.ShouldBeGreaterThan(150, "the scan has to have found the interpolations it checks");
        unresolved.ShouldBeLessThan(holes / 20, "most of the expressions have to resolve to a type");
    }

    [Fact]
    public void EveryStringTheToolsAndTheProposalServicePutIntoATextIsConstantHeldOrAllowed()
    {
        var found = new List<string>();
        foreach (var tree in Trees(InMcp))
            foreach (var hole in tree.GetRoot().DescendantNodes().OfType<InterpolationSyntax>())
                if (!InsideAWrapperOrRefusalHelper(hole) && Unsafe(hole.Expression) is { } text)
                    found.Add($"{Where(hole)}  {{{hole.Expression}}} <- {text}");

        found.ShouldBeEmpty("these interpolate a string that no wrapper has held:\n" + string.Join("\n", found));
    }

    [Fact]
    public void EveryStringAnMcpExceptionIsMadeOfIsConstantHeldOrAllowed()
    {
        var found = new List<string>();
        var sites = 0;
        foreach (var tree in Trees(InMcp))
        {
            foreach (var creation in tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                         .Where(c => c.Type.ToString() == "McpException"))
            {
                sites++;
                foreach (var argument in creation.ArgumentList?.Arguments ?? [])
                    if (Unsafe(argument.Expression) is { } text)
                        found.Add($"{Where(creation)}  new McpException({argument.Expression}) <- {text}");
            }

            sites += tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(i => i.Expression.ToString() is "Refusal" or "DexiconTools.Refusal");
        }

        sites.ShouldBeGreaterThan(30, "the scan has to have found the sites it checks");
        found.ShouldBeEmpty("these pass a string to the caller that no wrapper has held:\n" + string.Join("\n", found));
    }

    [Fact]
    public void EveryStringALogCallCarriesIsConstantHeldOrAllowed()
    {
        var found = new List<string>();
        var calls = 0;
        foreach (var tree in Trees(_ => true))
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                         .Where(i => i.Expression is MemberAccessExpressionSyntax
                         {
                             Name.Identifier.Text: "LogInformation" or "LogWarning" or "LogError" or "LogDebug" or "LogCritical"
                                 or "LogTrace" or "Log",
                         }))
            {
                calls++;
                foreach (var argument in call.ArgumentList.Arguments.Skip(1))
                    if (Unsafe(argument.Expression) is { } text)
                        found.Add($"{Where(call)}  {argument.Expression} <- {text}");
            }

        calls.ShouldBeGreaterThan(20, "the scan has to have found the log calls it checks");
        found.ShouldBeEmpty("these log a string that no wrapper has held:\n" + string.Join("\n", found));
    }

    [Fact]
    public void AnAllowedEntryThatNoSiteUsesIsRemoved()
    {
        // An entry for text that is no longer there would excuse the next one that looks the same.
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in Source.Value.Trees)
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                present.Add($"{Path.GetFileName(tree.FilePath)}: {Bare(node)}");
                present.Add($"*: {Bare(node)}");
            }

        Allowed.Keys.Where(k => !present.Contains(k)).ShouldBeEmpty("these allowed entries match no expression in the source");
    }
}
