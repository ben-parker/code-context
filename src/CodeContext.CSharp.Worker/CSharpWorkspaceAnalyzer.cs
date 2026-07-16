using CodeContext.Parser.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeContext.CSharp.Worker;

/// <summary>
/// Owns the Roslyn state for one logical workspace: cached syntax trees per file and
/// the compilation built from them. Incremental means "reparse only the files that
/// changed"; the compilation itself is recreated per mutation (cheap next to parsing)
/// and the emitted facts always replace the whole workspace, mirroring the whole-scope
/// C# generation semantics the host commits atomically.
/// </summary>
public sealed class CSharpWorkspaceAnalyzer
{
    /// <summary>Prefix shared by all C# facts; a workspace component follows it.</summary>
    public const string IdPrefix = "csharp:";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, SyntaxTree> _syntaxTrees = new(PathComparer);
    private readonly List<ProtocolDiagnostic> _pendingDiagnostics = [];
    private readonly string _workspaceIdPrefix;

    public CSharpWorkspaceAnalyzer(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _workspaceIdPrefix = IdPrefix + Uri.EscapeDataString(workspaceId) + ":";
    }

    public int FileCount => _syntaxTrees.Count;

    /// <summary>
    /// Reconciles the cached tree set with the approved file list: files no longer
    /// approved are dropped, newly approved files are loaded from disk. Cached trees
    /// for still-approved files are kept as-is (changes arrive via
    /// <see cref="ApplyChanges"/>). This runs on every workspace/open, which the host
    /// sends before each mutation, so a restarted worker self-heals its state here.
    /// </summary>
    public void SyncApprovedFiles(IReadOnlyList<string> approvedFiles, CancellationToken ct)
    {
        var approved = new HashSet<string>(approvedFiles, PathComparer);
        foreach (var stale in _syntaxTrees.Keys.Where(path => !approved.Contains(path)).ToList())
        {
            _syntaxTrees.Remove(stale);
        }
        foreach (var path in approvedFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!_syntaxTrees.ContainsKey(path))
            {
                TryLoadFile(path);
            }
        }
    }

    /// <summary>Replaces the workspace file set entirely and reparses everything.</summary>
    public void ReplaceFiles(IReadOnlyList<string> files, CancellationToken ct)
    {
        _syntaxTrees.Clear();
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            TryLoadFile(path);
        }
    }

    /// <summary>Applies an ordered change batch: deleted files drop their tree, all
    /// other change kinds re-read the file from disk.</summary>
    public void ApplyChanges(IReadOnlyList<FileChangeDto> changes, CancellationToken ct)
    {
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            if (change.ChangeType == FileChangeKinds.Renamed && change.OldPath is { Length: > 0 } oldPath)
            {
                _syntaxTrees.Remove(oldPath);
            }
            if (change.ChangeType == FileChangeKinds.Deleted)
            {
                _syntaxTrees.Remove(change.Path);
            }
            else
            {
                TryLoadFile(change.Path);
            }
        }
    }

    private void TryLoadFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            _syntaxTrees[path] = CSharpSyntaxTree.ParseText(content, path: path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file disappeared or is unreadable: treat it as absent so its stale
            // facts are replaced by nothing, and tell the host why.
            _syntaxTrees.Remove(path);
            _pendingDiagnostics.Add(new ProtocolDiagnostic(
                path, "warning", $"File could not be read and was skipped: {ex.Message}"));
        }
    }

    /// <summary>
    /// Compiles the current tree set and walks every file into normalized nodes/edges.
    /// The result always represents the complete workspace.
    /// </summary>
    public AnalysisResult Analyze(CancellationToken ct)
    {
        var references = new List<MetadataReference>();
        try
        {
            // Basic runtime references give the semantic model real symbols for core
            // types; when unavailable, syntax-level analysis still works.
            var objectAssembly = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(objectAssembly) && File.Exists(objectAssembly))
            {
                references.Add(MetadataReference.CreateFromFile(objectAssembly));
            }
            var consoleAssembly = typeof(Console).Assembly.Location;
            if (!string.IsNullOrEmpty(consoleAssembly) && File.Exists(consoleAssembly))
            {
                references.Add(MetadataReference.CreateFromFile(consoleAssembly));
            }
        }
        catch
        {
            // Reference resolution is best-effort.
        }

        var compilation = CSharpCompilation.Create(
            "CodeContextWorkspace",
            syntaxTrees: _syntaxTrees.Values,
            references: references);

        var nodes = new List<ProtocolNode>();
        var edges = new List<ProtocolEdge>();
        foreach (var tree in _syntaxTrees.Values)
        {
            ct.ThrowIfCancellationRequested();
            var semanticModel = compilation.GetSemanticModel(tree);
            var walker = new GraphWalker(semanticModel, tree.FilePath, _workspaceIdPrefix, nodes, edges);
            walker.Visit(tree.GetRoot(ct));
        }

        var diagnostics = _pendingDiagnostics.ToList();
        _pendingDiagnostics.Clear();
        return new AnalysisResult(nodes, edges, diagnostics, _syntaxTrees.Keys.ToList());
    }

    public sealed record AnalysisResult(
        List<ProtocolNode> Nodes,
        List<ProtocolEdge> Edges,
        List<ProtocolDiagnostic> Diagnostics,
        List<string> Files);

    public sealed record NativeTreeResult(JsonElement Tree, bool Truncated);

    /// <summary>Builds a bounded Roslyn-native node/token tree for one cached file.</summary>
    public NativeTreeResult GetNativeSyntaxTree(
        string filePath, int? start, int? length, int maxDepth, CancellationToken ct)
    {
        if (!_syntaxTrees.TryGetValue(Path.GetFullPath(filePath), out var syntaxTree))
        {
            throw new ArgumentException("The file is not open in this workspace.", nameof(filePath));
        }
        if ((start is null) != (length is null))
        {
            throw new ArgumentException("start and length must be supplied together.");
        }
        if (maxDepth is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "maxDepth must be between 0 and 32.");
        }

        var root = syntaxTree.GetRoot(ct);
        SyntaxNode selected = root;
        if (start is { } offset && length is { } count)
        {
            if (offset < 0 || count < 0 || (long)offset + count > root.FullSpan.End)
            {
                throw new ArgumentOutOfRangeException(nameof(start), "The requested range is outside the file.");
            }
            selected = root.FindNode(new TextSpan(offset, count), getInnermostNodeForTie: true);
        }

        var state = new NativeTreeBuildState(maxDepth, maxNodes: 10_000);
        var json = SerializeNode(selected, depth: 0, state, ct);
        return new NativeTreeResult(
            JsonSerializer.SerializeToElement(json), state.Truncated);
    }

    private sealed class NativeTreeBuildState(int maxDepth, int maxNodes)
    {
        public int MaxDepth { get; } = maxDepth;
        public int MaxNodes { get; } = maxNodes;
        public int NodesWritten { get; set; }
        public bool Truncated { get; set; }
    }

    private static JsonObject SerializeNode(
        SyntaxNode node, int depth, NativeTreeBuildState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        state.NodesWritten++;
        var result = new JsonObject
        {
            ["kind"] = node.Kind().ToString(),
            ["rawKind"] = node.RawKind,
            ["span"] = SpanJson(node.Span),
            ["fullSpan"] = SpanJson(node.FullSpan),
        };

        var children = node.ChildNodesAndTokens();
        if (children.Count == 0) return result;
        if (depth >= state.MaxDepth || state.NodesWritten >= state.MaxNodes)
        {
            state.Truncated = true;
            result["childrenTruncated"] = true;
            return result;
        }

        var childArray = new JsonArray();
        foreach (var child in children)
        {
            if (state.NodesWritten >= state.MaxNodes)
            {
                state.Truncated = true;
                result["childrenTruncated"] = true;
                break;
            }
            childArray.Add(child.IsNode
                ? SerializeNode(child.AsNode()!, depth + 1, state, ct)
                : SerializeToken(child.AsToken(), state));
        }
        result["children"] = childArray;
        return result;
    }

    private static JsonObject SerializeToken(SyntaxToken token, NativeTreeBuildState state)
    {
        state.NodesWritten++;
        const int maxTokenTextLength = 4096;
        var textTruncated = token.Text.Length > maxTokenTextLength
            || token.ValueText.Length > maxTokenTextLength;
        var result = new JsonObject
        {
            ["kind"] = token.Kind().ToString(),
            ["rawKind"] = token.RawKind,
            ["span"] = SpanJson(token.Span),
            ["fullSpan"] = SpanJson(token.FullSpan),
            ["text"] = token.Text.Length > maxTokenTextLength
                ? token.Text[..maxTokenTextLength]
                : token.Text,
            ["valueText"] = token.ValueText.Length > maxTokenTextLength
                ? token.ValueText[..maxTokenTextLength]
                : token.ValueText,
            ["isMissing"] = token.IsMissing,
        };
        if (textTruncated) result["textTruncated"] = true;
        return result;
    }

    private static JsonObject SpanJson(TextSpan span) => new()
    {
        ["start"] = span.Start,
        ["length"] = span.Length,
        ["end"] = span.End,
    };

    private sealed class GraphWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;
        private readonly string _filePath;
        private readonly string _idPrefix;
        private readonly List<ProtocolNode> _nodes;
        private readonly List<ProtocolEdge> _edges;

        public GraphWalker(SemanticModel semanticModel, string filePath, string idPrefix, List<ProtocolNode> nodes, List<ProtocolEdge> edges)
        {
            _semanticModel = semanticModel;
            _filePath = filePath;
            _idPrefix = idPrefix;
            _nodes = nodes;
            _edges = edges;
        }

        private string NodeId(ISymbol symbol) => _idPrefix + symbol.ToDisplayString();

        private ProtocolNode BuildNode(
            ISymbol symbol,
            SyntaxNode syntax,
            string kind,
            string signature,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var span = syntax.GetLocation().GetLineSpan();
            return new ProtocolNode(
                Id: NodeId(symbol),
                Name: symbol.Name,
                Kind: kind,
                Language: "csharp",
                FilePath: _filePath,
                StartLine: span.StartLinePosition.Line,
                EndLine: span.EndLinePosition.Line,
                StartColumn: span.StartLinePosition.Character,
                EndColumn: span.EndLinePosition.Character,
                Namespace: symbol.ContainingNamespace.ToDisplayString(),
                Visibility: symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
                Signature: signature,
                Metadata: metadata);
        }

        private void AddEdge(string sourceId, string targetId, string kind, IReadOnlyDictionary<string, string>? metadata = null)
        {
            _edges.Add(new ProtocolEdge(
                Id: $"{sourceId}=[{kind}]=>{targetId}" + (metadata is not null && metadata.TryGetValue("line", out var line)
                    ? $"@{line}:{metadata.GetValueOrDefault("column", "0")}"
                    : string.Empty),
                SourceId: sourceId,
                TargetId: targetId,
                Kind: kind,
                Metadata: metadata));
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                var signature = node.Identifier.ToString()
                    + (node.TypeParameterList?.ToString() ?? "")
                    + (node.BaseList?.ToString() ?? "");
                _nodes.Add(BuildNode(symbol, node, "Class", signature));

                if (symbol.BaseType is { } baseType && baseType.SpecialType != SpecialType.System_Object)
                {
                    AddEdge(NodeId(symbol), NodeId(baseType), "INHERITS");
                }
                foreach (var interfaceSymbol in symbol.AllInterfaces)
                {
                    AddEdge(NodeId(symbol), NodeId(interfaceSymbol), "IMPLEMENTS");
                }
            }
            base.VisitClassDeclaration(node);
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                var signature = node.Identifier.ToString()
                    + (node.TypeParameterList?.ToString() ?? "")
                    + (node.BaseList?.ToString() ?? "");
                _nodes.Add(BuildNode(symbol, node, "Interface", signature));

                foreach (var baseInterface in symbol.Interfaces)
                {
                    AddEdge(NodeId(symbol), NodeId(baseInterface), "INHERITS");
                }
            }
            base.VisitInterfaceDeclaration(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                var signature = node.Identifier.ToString()
                    + (node.TypeParameterList?.ToString() ?? "")
                    + node.ParameterList.ToString();
                var metadata = IsTestMethod(node)
                    ? new Dictionary<string, string> { ["isTest"] = "true" }
                    : null;
                _nodes.Add(BuildNode(symbol, node, "Method", signature, metadata));
            }
            base.VisitMethodDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                _nodes.Add(BuildNode(symbol, node, "Property", node.Identifier.ToString()));
            }
            base.VisitPropertyDeclaration(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var calleeSymbol = ResolveMethodSymbol(node);
            var enclosingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (enclosingMethod is not null
                && calleeSymbol is not null
                && _semanticModel.GetDeclaredSymbol(enclosingMethod) is { } callerSymbol)
            {
                var position = node.GetLocation().GetLineSpan().StartLinePosition;
                var kind = IsMockOrFluentTestInvocation(node, enclosingMethod)
                    ? "MOCK_CALLS"
                    : "CALLS";
                AddEdge(NodeId(callerSymbol), NodeId(calleeSymbol), kind, new Dictionary<string, string>
                {
                    ["line"] = position.Line.ToString(),
                    ["column"] = position.Character.ToString(),
                });
            }
            base.VisitInvocationExpression(node);
        }

        private IMethodSymbol? ResolveMethodSymbol(InvocationExpressionSyntax invocation)
        {
            var info = _semanticModel.GetSymbolInfo(invocation);
            var method = info.Symbol as IMethodSymbol;
            if (method is null)
            {
                // Roslyn reports candidate symbols for some fluent/mock expressions.
                // Only accept an unambiguous candidate: guessing among overloads would
                // trade a visible unresolved call for a silently wrong edge.
                var candidates = new List<IMethodSymbol>();
                foreach (var candidate in info.CandidateSymbols.OfType<IMethodSymbol>().Select(NormalizeMethod))
                {
                    if (!candidates.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate)))
                        candidates.Add(candidate);
                }
                method = candidates.Count == 1 ? candidates[0] : null;
            }
            return method is null ? null : NormalizeMethod(method);
        }

        private static IMethodSymbol NormalizeMethod(IMethodSymbol method)
        {
            // Reduced extension and constructed generic methods must converge on the
            // declaration ID emitted by the walker. OriginalDefinition preserves the
            // containing type and parameter signature, so unrelated overloads remain
            // distinct.
            var normalized = method.ReducedFrom ?? method;
            return normalized.OriginalDefinition;
        }

        private static bool IsMockOrFluentTestInvocation(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax enclosingMethod)
        {
            if (!IsTestMethod(enclosingMethod)) return false;
            return invocation.DescendantNodesAndSelf().Concat(invocation.Ancestors())
                .OfType<InvocationExpressionSyntax>()
                .Select(candidate => candidate.Expression)
                .OfType<MemberAccessExpressionSyntax>()
                .Select(member => member.Name.Identifier.ValueText)
                .Any(name => name is "Received" or "DidNotReceive" or "Returns"
                    or "ReturnsForAnyArgs" or "When" or "WhenForAnyArgs");
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            AddTypeReference(node);
            base.VisitIdentifierName(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            AddTypeReference(node);
            base.VisitGenericName(node);
        }

        private void AddTypeReference(NameSyntax node)
        {
            // Base-list relationships already have the more specific INHERITS or
            // IMPLEMENTS edge; emitting REFERENCES as well only duplicates them.
            if (node.Ancestors().Any(ancestor => ancestor is BaseListSyntax) ||
                _semanticModel.GetSymbolInfo(node).Symbol is not INamedTypeSymbol referencedType ||
                !IsIndexedSourceType(referencedType) ||
                FindEnclosingIndexedSymbol(node) is not { } sourceSymbol)
            {
                return;
            }

            var sourceId = NodeId(sourceSymbol);
            var targetId = NodeId(referencedType.OriginalDefinition);
            if (sourceId == targetId)
                return;

            var position = node.GetLocation().GetLineSpan().StartLinePosition;
            AddEdge(sourceId, targetId, "REFERENCES", new Dictionary<string, string>
            {
                ["line"] = position.Line.ToString(),
                ["column"] = position.Character.ToString(),
            });
        }

        private ISymbol? FindEnclosingIndexedSymbol(SyntaxNode node)
        {
            if (node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is { } method &&
                _semanticModel.GetDeclaredSymbol(method) is { } methodSymbol)
            {
                return methodSymbol;
            }

            if (node.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is { } property &&
                _semanticModel.GetDeclaredSymbol(property) is { } propertySymbol)
            {
                return propertySymbol;
            }

            var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            return type is null ? null : _semanticModel.GetDeclaredSymbol(type);
        }

        private static bool IsIndexedSourceType(INamedTypeSymbol symbol)
            => symbol.Locations.Any(location => location.IsInSource) &&
               symbol.DeclaringSyntaxReferences.Any(reference =>
                   reference.GetSyntax() is ClassDeclarationSyntax or InterfaceDeclarationSyntax);

        private static bool IsTestMethod(MethodDeclarationSyntax method)
        {
            foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
            {
                var name = attribute.Name.ToString();
                var simpleName = name.Split('.').Last();
                if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    simpleName = simpleName[..^"Attribute".Length];
                }

                if (simpleName is "Fact" or "Theory" or "Test" or "TestCase" or
                    "TestMethod" or "DataTestMethod")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
