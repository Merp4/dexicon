using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dexicon.Tests;

/// <summary>
/// Reads the application's source with the compiler's own view of it and fails when text that can come from a
/// caller or from storage reaches an MCP reply, an MCP error or a log call without passing through one of the
/// methods in <see cref="Trusted"/>.
///
/// Sites read: in the files of the MCP tools and the proposal service, every interpolation hole, every
/// <c>+</c> that builds a string, every <c>string.Concat</c>, <c>Format</c> and <c>Join</c>, every append to a
/// <c>StringBuilder</c>, every <c>McpException</c> argument and every string a tool or resource method
/// returns; in all of the application, every string argument of a log call, the template included (the
/// <c>ILogger</c> extension methods and Serilog's static <c>Log</c>). Each is followed back through
/// conditionals, <c>??</c>, <c>+</c>, switch arms, locals (every assignment, in any order), fields with
/// initialisers and the bodies of the application's own methods. An expression of a type that is not a string
/// is accepted when it is a number, a bool, an enum, a Guid or a date or time, and is otherwise reported (an
/// exception, an object, a span, a <c>StringBuilder</c>, a record), because it can render text. An expression
/// whose type does not resolve is reported.
///
/// Not read, so it can be defeated by: text carried in a collection, a record or a field that is built in one
/// place and printed in another (the printing site is read, the building site is not); a
/// <c>StringBuilder</c> handed to another method; a non-string log argument, whose text the log sink makes
/// safe (<c>OneLineLogSink</c> is tested on its own); and anything written in a file that is not one of those
/// above. <see cref="Trusted"/> is trusted by name, and <c>LogTextTests</c> and <c>ScopeMessageTests</c> hold
/// what each does. The other guard is the wrapper's own test at each site (<c>McpEchoTests</c>,
/// <c>EchoSiteTests</c>), which drives the tools with hostile values.
///
/// <see cref="Allowed"/> excuses one expression at one place, keyed by file, member and expression text, with
/// the reason. An entry that excuses nothing fails <see cref="AnAllowedEntryThatExcusesNoSiteIsRemoved"/>.
/// </summary>
public sealed class EchoScanTests
{
    /// <summary>
    /// The methods whose result is a caller's or a stored value held to one line and cut, or a list of names
    /// the resolver bounds, by <c>Type.Method</c>. Anything else the application defines is read by what it
    /// returns, including the proposal service's <c>Line</c> and <c>Quoted</c>.
    /// </summary>
    private static readonly HashSet<string> Trusted = new(StringComparer.Ordinal)
    {
        "LogText.OneLine", "LogText.Echo", "DexiconAuthMiddleware.OneLine", "ScopeResolver.Listed",
        "ScopeResolver.QuotedUnknown",
    };

    /// <summary>
    /// A refusal helper is given a message that it puts on one line and cuts (<c>DexiconTools.Refusal</c> builds
    /// the exception from <c>Echo(message, MessageMax)</c>, which the <c>McpException</c> scan reads), so what
    /// is passed to it is not read for what it is made of.
    /// </summary>
    private static readonly HashSet<string> RefusalHelpers = new(StringComparer.Ordinal)
    {
        "DexiconTools.Refusal", "ConfigureTools.Refused",
    };

    /// <summary>
    /// Methods that change a string without adding to it, so what matters is what they are called on.
    /// </summary>
    private static readonly HashSet<string> Transforms = new(StringComparer.Ordinal)
    {
        "ToString", "ToLowerInvariant", "ToUpperInvariant", "Trim", "TrimStart", "TrimEnd", "Substring", "PadLeft",
        "PadRight", "Replace", "ReplaceLineEndings", "Normalize",
    };

    private static readonly HashSet<string> BuilderMethods = new(StringComparer.Ordinal)
    {
        "Append", "AppendLine", "Insert", "AppendFormat", "AppendJoin",
    };

    /// <summary>
    /// Text that is safe without a wrapper, as <c>file|member|expression</c> and the reason. Each excuses that
    /// expression in that member and no other.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["ProposalService.cs|ResolveAsync|WorkspaceDiscovery.Canonical(root, s.RootPath)"] = "Describe: its result goes through Line at both its uses",
        ["ConfigureTools.cs|ListFoldersAsync|hereBy"] = "a value of the dictionary IndexedFoldersAsync builds, whose corpus names go through OneLine there",
        ["ConfigureTools.cs|ListFoldersAsync|by"] = "a value of the dictionary IndexedFoldersAsync builds, whose corpus names go through OneLine there",
        ["ConfigureTools.cs|ConfigureSourceAsync|updated.Value.IndexJob?.Id"] = "an id the catalogue generated",
        ["DexiconResources.cs|FileAsync|Passage.Stitch(file.Chunks.Select(h => (h.StartLine, h.EndLine, h.Content)))"] = "the indexed text of a file, which the resource returns",
        ["ConfigureTools.cs|ConfigureSourceAsync|added.Value!.IndexJob.Id"] = "an id the catalogue generated",
        ["ConfigureTools.cs|Resets|allowed"] = "the names reset may take, written at each call of Resets",
        ["ConfigureTools.cs|Clashes|g.Name"] = "the names of arguments, written in the table that Clashes reads",
        ["ConfigureTools.cs|Kilobytes|name"] = "the name of an argument, written at each call of Kilobytes",
        ["ConfigureTools.cs|ConfigureCorpusAsync|action"] = "Audit: a verb written at each call",
        ["ConfigureTools.cs|ConfigureCorpusAsync|set"] = "Audit: the names of what changed, written at each call",
        ["ConfigureTools.cs|ConfigureSourceAsync|what1"] = "AuditSource: a verb written at each call",
        ["DexiconResources.cs|CorpusAsync|summary"] = "JSON from the serializer, which writes every character outside Basic Latin as an escape",
        ["DexiconResources.cs|FileAsync|document?.Text"] = "the indexed file's own text, which is what the resource returns",
        ["DexiconResources.cs|ResolveAsync|principal.Scopes"] = "the scope names of a key, from the repository's list",
        ["DexiconTools.cs|SearchIndexAsync|ex.Message"] = "the messages of UnknownSearchModeException and EmbeddingDimensionMismatchException, which the repository writes",
        ["DexiconTools.cs|Render|hit.Content"] = "the indexed text of a hit, which is what search returns",
        ["DexiconTools.cs|RenderCorpus|s.State"] = "the name of a state, from the repository's list",
        ["DexiconTools.cs|RenderCorpus|set.State"] = "the name of a state, from the repository's list",
        ["DexiconTools.cs|GetContextAsync|Passage.Stitch([(gotLo, gotHi, text)], lineNumbers)"] = "the indexed text of a file, which get_context returns",
        ["DexiconTools.cs|GetContextAsync|Passage.Stitch(pieces.Select(p => (p.StartLine, p.EndLine, p.Content)), lineNumbers, (lo, hi))"] = "the indexed text of a file, which get_context returns",
        ["DexiconTools.cs|IndexRefreshAsync|job.Id"] = "an id the catalogue generated",
        ["DexiconTools.cs|IndexStatusAsync|summary.State"] = "the name of a state, from the repository's list",
        ["DexiconTools.cs|IndexStatusAsync|set.State"] = "the name of a state, from the repository's list",
        ["DexiconTools.cs|IndexStatusAsync|job.Id"] = "an id the catalogue generated",
        ["DexiconTools.cs|RenderSources|newest.Sha"] = "seven hexadecimal digits of a commit id",
        ["DexiconTools.cs|ProblemFilesAsync|roots.GetValueOrDefault(r.SourceId)"] = "a path in a sample of problem files, which RenderProblemFiles passes through OneLine",
        ["DexiconTools.cs|ProblemFilesAsync|r.RelativePath"] = "a path in a sample of problem files, which RenderProblemFiles passes through OneLine",
        ["DexiconTools.cs|RenderProblemFiles|g.Status"] = "the name of a file status, from the repository's list",
        ["DexiconTools.cs|Require|principal.Scopes"] = "the scope names of a key, from the repository's list",
        ["DexiconTools.cs|Require|scope"] = "Require: the scope the tool needs, written at each call",
        ["ProposeTools.cs|ProposeRemovalAsync|p.Id"] = "an id the catalogue generated",
        ["ProposeTools.cs|RenderOwnAsync|p.Id"] = "an id the catalogue generated",
        ["Program.cs|<top-level>|app.Services.GetService<IServer>()?.Features .Get<IServerAddressesFeature>()?.Addresses"] = "the addresses the server listens on, operator configuration",
        ["DocumentEndpoints.cs|UploadAsync|corpus.Id"] = "an id the catalogue generated",
        ["ProposalService.cs|RejectHeldAsync|p.Id"] = "an id the catalogue generated",
        ["ProposalService.cs|ApproveHeldAsync|p.Id"] = "an id the catalogue generated",
        ["ProposalService.cs|FailAsync|p.Id"] = "an id the catalogue generated",
        ["SystemEndpoints.cs|SetScopesAsync|token.Scopes"] = "the scope names of a key, from the repository's list",
        ["Bootstrapper.cs|InitialiseAsync|options.Storage.CatalogPath"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs|CheckChunkDefaults|refused.Detail"] = "the startup check's own text, built from operator configuration",
        ["Bootstrapper.cs|VerifyDependenciesAsync|options.Qdrant.Endpoint"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs|VerifyDependenciesAsync|target.Provider"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs|VerifyDependenciesAsync|target.Model"] = "operator configuration at startup, not a caller's",
        ["Bootstrapper.cs|EnsureAdminPasswordAsync|Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))"] = "the generated admin password, printed once on purpose (docs/10)",
        ["DexiconAuth.cs|InvokeAsync|ctx.Connection.RemoteIpAddress?.ToString()"] = "the text of an IP address, from the connection",
        ["DexiconAuth.cs|CallerDigest|Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(presented)))"] = "a hex digest of the presented credential, from CallerDigest",
        ["ScopeExceptionHandler.cs|TryHandleAsync|title"] = "a title written in the handler",
    };

    private static readonly Lazy<Compiled> Source = new(() => new Compiled());

    private sealed class Compiled
    {
        public Compiled()
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
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    break;
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = postfix.Operand;
                    break;
                default:
                    return expression;
            }
        }
    }

    private static string MemberOf(SyntaxNode node) => node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
        PropertyDeclarationSyntax property => property.Identifier.Text,
        FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier.Text,
        GlobalStatementSyntax => "<top-level>",
        null => "<top-level>",
        var other => other.Kind().ToString(),
    };

    private static string KeyOf(ExpressionSyntax expression) =>
        $"{FileOf(expression)}|{MemberOf(expression)}|{System.Text.RegularExpressions.Regex.Replace(Bare(expression).ToString(), @"\s+", " ")}";

    private static string? NameOf(IMethodSymbol? method) => method is null ? null : $"{method.ContainingType.Name}.{method.Name}";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<InvocationExpressionSyntax, IMethodSymbol?> Methods = new();

    private static IMethodSymbol? MethodOf(InvocationExpressionSyntax invocation) =>
        Methods.GetOrAdd(invocation, i => ModelOf(i).GetSymbolInfo(i).Symbol as IMethodSymbol);

    private static bool IsPlain(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        if (type.TypeKind == TypeKind.Enum) return true;

        return type.SpecialType is SpecialType.System_Boolean or SpecialType.System_SByte or SpecialType.System_Byte
                   or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
                   or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
                   or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
                   or SpecialType.System_DateTime
               || type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.TimeSpan"
                   or "System.DateOnly" or "System.TimeOnly";
    }

    /// <summary>Whether the node is inside an argument of a method in <see cref="Trusted"/> or <see cref="RefusalHelpers"/>.</summary>
    private static bool InsideATrustedCall(SyntaxNode node) => node.Ancestors().OfType<InvocationExpressionSyntax>()
        .Select(MethodOf)
        .Any(method => method is not null && (IsWrapper(method, 0) || RefusalHelpers.Contains(NameOf(method)!)));

    /// <summary>
    /// Whether a method is in <see cref="Trusted"/>, or is defined here and returns nothing but the result of
    /// such a method (<c>DexiconTools.OneLine</c> and <c>Echo</c>, the proposal service's <c>Line</c>).
    /// </summary>
    private static bool IsWrapper(IMethodSymbol method, int depth)
    {
        if (Trusted.Contains(NameOf(method)!)) return true;
        if (depth > 3 || method.DeclaringSyntaxReferences.Length == 0) return false;

        var returned = method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).SelectMany(ReturnedExpressions).ToList();
        return returned.Count > 0 && returned.All(r => Bare(r) is InvocationExpressionSyntax call
                                                       && MethodOf(call) is { } inner && IsWrapper(inner, depth + 1));
    }

    /// <summary>One scan: what it excused is recorded, so that an entry that excused nothing can be found.</summary>
    private sealed class Scanner
    {
        public HashSet<string> Used { get; } = new(StringComparer.Ordinal);

        /// <summary>The key to paste into <see cref="Allowed"/> for the expression that is not safe, with a note.</summary>
        private static string Flag(ExpressionSyntax expression, string note = "") => KeyOf(expression) + note;
        private bool IsAllowed(ExpressionSyntax expression)
        {
            var key = KeyOf(expression);
            if (!Allowed.ContainsKey(key)) return false;

            Used.Add(key);
            return true;
        }

        /// <summary>
        /// What in <paramref name="start"/> is not safe to print, or null when it is all a constant, a trusted
        /// method's result, text built of such parts, a number or similar, or allowed.
        /// </summary>
        public string? Unsafe(ExpressionSyntax start, HashSet<ISymbol>? seen = null, int depth = 0)
        {
            var expression = Bare(start);
            var model = ModelOf(expression);
            if (expression is LiteralExpressionSyntax) return null;
            if (model.GetConstantValue(expression).HasValue) return null;
            if (IsAllowed(expression)) return null;

            var type = model.GetTypeInfo(expression).Type;
            if (type is null or IErrorTypeSymbol) return Flag(expression, "  (its type did not resolve)");
            if (type.SpecialType != SpecialType.System_String)
                return IsPlain(type) ? null : Flag(expression, $"  (a {type.ToDisplayString()}, which can render text)");

            if (depth > 14) return Flag(expression, "  (too deep to follow)");
            seen ??= [];

            switch (expression)
            {
                case MemberAccessExpressionSyntax { Expression: PredefinedTypeSyntax, Name.Identifier.Text: "Empty" }:
                    return null;

                case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Environment" }, Name.Identifier.Text: "NewLine" }:
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
                    // A range of a string is text from it. An element of an array or a list is read as a sequence.
                    return ModelOf(access.Expression).GetTypeInfo(access.Expression).Type?.SpecialType == SpecialType.System_String
                        ? Unsafe(access.Expression, seen, depth + 1)
                        : UnsafeSequence(access.Expression, seen, depth + 1);

                case InvocationExpressionSyntax invocation:
                    return UnsafeInvocation(invocation, seen, depth);

                case AwaitExpressionSyntax { Expression: InvocationExpressionSyntax awaited }:
                    return UnsafeInvocation(awaited, seen, depth);

                case IdentifierNameSyntax identifier:
                    return UnsafeIdentifier(identifier, seen, depth);
            }

            return Flag(expression);
        }

        private string? UnsafeInvocation(InvocationExpressionSyntax invocation, HashSet<ISymbol> seen, int depth)
        {
            var method = MethodOf(invocation);
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => string.Empty,
            };
            if (method is not null && IsWrapper(method, 0)) return null;

            if (Transforms.Contains(name) && invocation.Expression is MemberAccessExpressionSyntax receiver)
            {
                // The text a builder holds was checked where it was appended to.
                if (name == "ToString" && ModelOf(receiver.Expression).GetTypeInfo(receiver.Expression).Type?.Name == "StringBuilder")
                    return null;
                return Unsafe(receiver.Expression, seen, depth + 1);
            }

            if (method?.ContainingType.SpecialType == SpecialType.System_String && name is "Join" or "Concat" or "Format")
                return invocation.ArgumentList.Arguments
                    .Select(a => UnsafeArgument(a.Expression, seen, depth + 1)).FirstOrDefault(x => x is not null);

            if (name == "Serialize" && invocation.ArgumentList.Arguments.Count > 0)
                return Unsafe(invocation.ArgumentList.Arguments[0].Expression, seen, depth + 1);

            // A method of the application: what it returns.
            // A method that is being read already is a recursion, which adds nothing to what the first read finds.
            if (method is not null && method.DeclaringSyntaxReferences.Length > 0)
            {
                if (!seen.Add(method)) return null;

                var returned = method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).SelectMany(ReturnedExpressions).ToList();
                var result = returned.Count == 0
                    ? Flag(invocation)
                    : returned.Select(r => Unsafe(r, seen, depth + 1)).FirstOrDefault(x => x is not null);
                seen.Remove(method);
                return result;
            }

            return Flag(invocation);
        }

        /// <summary>An argument of Join, Concat or Format: a string, a sequence of strings, or something else.</summary>
        public string? UnsafeArgument(ExpressionSyntax argument, HashSet<ISymbol> seen, int depth)
        {
            var type = ModelOf(argument).GetTypeInfo(argument).Type;
            return type is not null and not IErrorTypeSymbol && type.SpecialType != SpecialType.System_String && !IsPlain(type)
                   && (type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                       || type.AllInterfaces.Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T))
                ? UnsafeSequence(argument, seen, depth)
                : Unsafe(argument, seen, depth);
        }

        private string? UnsafeIdentifier(IdentifierNameSyntax identifier, HashSet<ISymbol> seen, int depth)
        {
            var symbol = ModelOf(identifier).GetSymbolInfo(identifier).Symbol;
            if (symbol is null) return Flag(identifier);
            if (!seen.Add(symbol)) return null;

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
                    return declarators.Any(d => d.Initializer is null) && !assigned.Any() ? Flag(identifier) : null;
                }
            }

            if (symbol is ILocalSymbol)
            {
                var declaration = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();

                // `x is { } name`: the variable holds what was tested.
                if (declaration is SingleVariableDesignationSyntax designation
                    && designation.Ancestors().OfType<IsPatternExpressionSyntax>().FirstOrDefault() is { } tested)
                    return Unsafe(tested.Expression, seen, depth + 1);

                // `foreach (var line in text.Split(...))`: each element of the sequence.
                if (declaration is ForEachStatementSyntax loop)
                    return UnsafeSequence(loop.Expression, seen, depth + 1);
            }

            if (symbol is IFieldSymbol or IPropertySymbol)
            {
                var initializers = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax())
                    .Select(s => (s as VariableDeclaratorSyntax)?.Initializer?.Value).OfType<ExpressionSyntax>().ToList();
                if (initializers.Count > 0)
                    return initializers.Select(e => Unsafe(e, seen, depth + 1)).FirstOrDefault(x => x is not null);
            }

            return Flag(identifier);
        }

        /// <summary>What in a sequence of strings (the second argument of string.Join) is not safe.</summary>
        public string? UnsafeSequence(ExpressionSyntax start, HashSet<ISymbol> seen, int depth)
        {
            var expression = Bare(start);
            if (IsAllowed(expression)) return null;
            if (depth > 14) return Flag(expression, "  (too deep to follow)");

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

                    if (member.Name.Identifier.Text is "Split")
                        return Unsafe(member.Expression, seen, depth + 1);

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
                    var declaration = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                    if (declaration is not VariableDeclaratorSyntax declarator) return Flag(expression);

                    var scope = declarator.Ancestors().OfType<BlockSyntax>().LastOrDefault() as SyntaxNode
                                ?? declarator.Ancestors().OfType<MemberDeclarationSyntax>().First();
                    var added = scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Add", Expression: IdentifierNameSyntax target }
                                    && SymbolEqualityComparer.Default.Equals(ModelOf(i).GetSymbolInfo(target).Symbol, local))
                        .Select(i => Unsafe(i.ArgumentList.Arguments[0].Expression, seen, depth + 1)).FirstOrDefault(x => x is not null);
                    if (added is not null) return added;
                    return declarator.Initializer is { } init ? UnsafeSequence(init.Value, seen, depth + 1) : Flag(expression);
            }

            return Flag(expression);
        }

        private string? MethodGroup(ExpressionSyntax selector, HashSet<ISymbol> seen, int depth)
        {
            var symbol = ModelOf(selector).GetSymbolInfo(selector).Symbol as IMethodSymbol;
            if (symbol is not null && Trusted.Contains(NameOf(symbol)!)) return null;
            if (symbol is not null && symbol.DeclaringSyntaxReferences.Length > 0)
            {
                if (!seen.Add(symbol)) return null;

                var returned = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).SelectMany(ReturnedExpressions).ToList();
                var result = returned.Count > 0
                    ? returned.Select(r => Unsafe(r, seen, depth + 1)).FirstOrDefault(x => x is not null)
                    : Flag(selector);
                seen.Remove(symbol);
                return result;
            }

            return Flag(selector);
        }
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

    private static IEnumerable<SyntaxTree> Trees(Func<string, bool> pick) => Source.Value.Trees.Where(t => pick(t.FilePath));

    private static bool InMcp(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}Mcp{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.EndsWith("ProposalService.cs", StringComparison.Ordinal);

    private static bool IsToolMethod(MethodDeclarationSyntax method) => method.AttributeLists.SelectMany(l => l.Attributes)
        .Any(a => a.Name.ToString() is "McpServerTool" or "McpServerResource" or "McpServerPrompt");

    /// <summary>A site that composes text in the files of the tools: what it reads, with where it is and why it fails.</summary>
    private sealed record Site(SyntaxNode Node, ExpressionSyntax Read, string What);

    /// <summary>Every place in the files of the tools and the proposal service that puts a string into text a model reads.</summary>
    private static readonly Lazy<List<Site>> ReplySitesOnce = new(BuildReplySites);

    private static List<Site> ReplySites() => ReplySitesOnce.Value;

    private static List<Site> BuildReplySites()
    {
        var sites = new List<Site>();
        foreach (var tree in Trees(InMcp))
        {
            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case InterpolationSyntax hole when !InsideATrustedCall(hole):
                        sites.Add(new Site(hole, hole.Expression, "hole"));
                        break;

                    case BinaryExpressionSyntax add when add.IsKind(SyntaxKind.AddExpression)
                                                         && ModelOf(add).GetTypeInfo(add).Type?.SpecialType == SpecialType.System_String
                                                         && add.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }
                                                         && !InsideATrustedCall(add):
                        sites.Add(new Site(add, add, "+"));
                        break;

                    case InvocationExpressionSyntax call when !InsideATrustedCall(call):
                        var method = MethodOf(call);
                        var name = method?.Name ?? string.Empty;
                        if (method?.ContainingType.SpecialType == SpecialType.System_String && name is "Join" or "Concat" or "Format")
                            sites.Add(new Site(call, call, name));
                        else if (method?.ContainingType.Name == "StringBuilder" && BuilderMethods.Contains(name))
                            foreach (var argument in call.ArgumentList.Arguments)
                                sites.Add(new Site(call, argument.Expression, "StringBuilder." + name));
                        break;

                    case ObjectCreationExpressionSyntax creation when !InsideATrustedCall(creation):
                        var type = ModelOf(creation).GetTypeInfo(creation).Type;
                        if (type?.Name == "StringBuilder")
                            foreach (var argument in creation.ArgumentList?.Arguments ?? [])
                                sites.Add(new Site(creation, argument.Expression, "new StringBuilder"));
                        else if (creation.Type.ToString() == "McpException")
                            foreach (var argument in creation.ArgumentList?.Arguments ?? [])
                                sites.Add(new Site(creation, argument.Expression, "McpException"));
                        break;

                    case MethodDeclarationSyntax tool when IsToolMethod(tool):
                        foreach (var returned in ReturnedExpressions(tool))
                            sites.Add(new Site(returned, returned, "returned by a tool"));
                        break;
                }
            }
        }

        return sites;
    }

    private static string? Read(Scanner scanner, Site site) => scanner.Unsafe(site.Read);

    private static readonly Lazy<List<InvocationExpressionSyntax>> LogCallsOnce = new(BuildLogCalls);

    private static List<InvocationExpressionSyntax> LogCalls() => LogCallsOnce.Value;

    private static List<InvocationExpressionSyntax> BuildLogCalls() => Trees(_ => true)
        .SelectMany(t => t.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        .Where(i =>
        {
            var method = MethodOf(i);
            if (method is null) return i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "LogInformation" or "LogWarning" or "LogError" or "LogDebug" or "LogCritical" or "LogTrace" };

            return (method.ContainingType.Name is "LoggerExtensions" or "ILogger" && method.Name.StartsWith("Log", StringComparison.Ordinal))
                   || (method.ContainingType is { Name: "Log", ContainingNamespace.Name: "Serilog" }
                       && method.Name is "Verbose" or "Debug" or "Information" or "Warning" or "Error" or "Fatal" or "Write");
        }).ToList();

    private static void ReadLogCall(Scanner scanner, InvocationExpressionSyntax call, List<string> found)
    {
        foreach (var argument in call.ArgumentList.Arguments)
        {
            var type = ModelOf(argument.Expression).GetTypeInfo(argument.Expression).Type;
            // A property value that is not a string is rendered by the log sink, which makes it safe.
            if (type is not null && type.SpecialType != SpecialType.System_String && type is not IErrorTypeSymbol) continue;
            if (scanner.Unsafe(argument.Expression) is { } text)
                found.Add($"{text}   (first read at {Where(call)})");
        }
    }

    [Fact]
    public void TheCompilationResolvesTheTypesTheScanReadsAndTheScanFindsItsSites()
    {
        var sites = ReplySites();
        var unresolved = sites.Where(s =>
            ModelOf(s.Read).GetTypeInfo(s.Read).Type is null or IErrorTypeSymbol
            && s.Read is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }).Select(s => $"{Where(s.Node)} {s.Read}").ToList();

        sites.Count(s => s.What == "hole").ShouldBeGreaterThan(150, "the scan has to have found the interpolations it checks");
        sites.Count(s => s.What == "McpException").ShouldBeGreaterThan(25);
        sites.Count(s => s.What == "returned by a tool").ShouldBeGreaterThan(8);
        sites.Count(s => s.What.StartsWith("StringBuilder.", StringComparison.Ordinal)).ShouldBeGreaterThan(30);
        unresolved.ShouldBeEmpty("every expression the scan reads has to resolve to a type");
        LogCalls().Count.ShouldBeGreaterThan(20, "the scan has to have found the log calls it checks");
    }

    [Fact]
    public void EveryStringTheToolsAndTheProposalServiceReturnOrThrowOrBuildIsConstantTrustedOrAllowed()
    {
        var scanner = new Scanner();
        var found = new List<string>();
        foreach (var site in ReplySites())
            if (Read(scanner, site) is { } text)
                found.Add($"{text}   (first read at {Where(site.Node)}, {site.What})");

        var distinct = found.GroupBy(f => f[..f.IndexOf("   (first read", StringComparison.Ordinal)]).Select(g => g.First()).ToList();
        distinct.ShouldBeEmpty("these put text into a reply that no trusted method has held:\n" + string.Join("\n", distinct));
    }

    [Fact]
    public void EveryStringALogCallCarriesIsConstantTrustedOrAllowed()
    {
        var scanner = new Scanner();
        var found = new List<string>();
        foreach (var call in LogCalls()) ReadLogCall(scanner, call, found);

        var distinct = found.GroupBy(f => f[..f.IndexOf("   (first read", StringComparison.Ordinal)]).Select(g => g.First()).ToList();
        distinct.ShouldBeEmpty("these log a string that no trusted method has held:\n" + string.Join("\n", distinct));
    }

    [Fact]
    public void AnAllowedEntryThatExcusesNoSiteIsRemoved()
    {
        // An entry that excuses nothing would excuse the next expression that looks the same.
        var scanner = new Scanner();
        foreach (var site in ReplySites()) Read(scanner, site);
        foreach (var call in LogCalls()) ReadLogCall(scanner, call, []);

        Allowed.Keys.Except(scanner.Used).ShouldBeEmpty("these allowed entries excuse no expression the scan reads");
    }

    [Fact]
    public void EveryAllowedEntryStatesItsReason()
    {
        Allowed.Where(e => string.IsNullOrWhiteSpace(e.Value) || e.Value.Length < 20).ShouldBeEmpty();
    }
}
