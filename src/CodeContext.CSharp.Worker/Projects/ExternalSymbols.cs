using CodeContext.Parser.Protocol;
using Microsoft.CodeAnalysis;
using static CodeContext.CSharp.Worker.CSharpWorkspaceAnalyzer;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// The library symbols one file uses, as stub nodes. A library is never read beyond what the
/// code names: a stub says a symbol exists and where it came from, which is what lets a query
/// on a library type list its uses in the project. Each stub also brings the types containing
/// it, so a member can be found through its type.
///
/// A stub is identified by display name alone, so it survives a package upgrade, and a type
/// forwarded between assemblies stays one node. It lives in a virtual file named for its
/// namespace, so the ordinary file-scoped delta adds and removes it.
/// </summary>
internal sealed class ExternalSymbols(string workspacePrefix)
{
    public const string AssemblyKey = "assembly";

    private const string ExternalKey = "external";
    private const string IdInfix = "ext:";
    private const string FilePrefix = "external:";
    private const string GlobalNamespace = "<global>";

    private readonly Dictionary<string, ProtocolNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<ProtocolEdge> _edges = [];

    /// <summary>True for a symbol that comes from a referenced assembly rather than source.</summary>
    public static bool IsExternal(ISymbol symbol)
        => symbol is not IErrorTypeSymbol
            && symbol.ContainingAssembly is not (null or ISourceAssemblySymbol)
            && !symbol.Locations.Any(location => location.IsInSource);

    /// <summary>Records the symbol and returns its id.</summary>
    public string Add(ISymbol symbol)
    {
        var id = workspacePrefix + IdInfix + DisplayNameOf(symbol);
        if (_nodes.ContainsKey(id)) return id;

        _nodes[id] = ToNode(id, symbol);
        if (symbol.ContainingType is { } type)
        {
            var typeId = Add(type.OriginalDefinition);
            var kind = ContainmentKind(symbol);
            _edges.Add(new ProtocolEdge($"{typeId}=[{kind}]=>{id}", typeId, id, kind));
        }
        return id;
    }

    private static ProtocolNode ToNode(string id, ISymbol symbol)
    {
        var @namespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        return new ProtocolNode(
            Id: id,
            Identifier: IdPrefix + DisplayNameOf(symbol),
            Name: symbol.Name,
            Kind: KindOf(symbol),
            Language: "csharp",
            FilePath: FilePrefix + (@namespace.Length > 0 ? @namespace : GlobalNamespace),
            Namespace: @namespace,
            Visibility: symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            Signature: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Metadata: new Dictionary<string, string>
            {
                [ExternalKey] = "true",
                [AssemblyKey] = symbol.ContainingAssembly.Name,
            });
    }

    private static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true } => "Record",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "Interface",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "Struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "Enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "Delegate",
        INamedTypeSymbol => "Class",
        IPropertySymbol => "Property",
        IEventSymbol => "Event",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "EnumMember",
        IFieldSymbol => "Field",
        _ => "Method",
    };

    private static string ContainmentKind(ISymbol symbol) => symbol switch
    {
        IPropertySymbol => "HAS_PROPERTY",
        IFieldSymbol or IEventSymbol => "HAS_FIELD",
        INamedTypeSymbol => "CONTAINS",
        _ => "HAS_METHOD",
    };

    /// <summary>
    /// The stubs of a whole walk, one bucket per virtual file. Which assembly a symbol
    /// names can differ between projects, so the choice cannot depend on walk order.
    /// </summary>
    public static IEnumerable<FileFacts> ToBuckets(IEnumerable<ExternalSymbols> walked)
    {
        var nodes = new Dictionary<string, ProtocolNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, ProtocolEdge>(StringComparer.Ordinal);
        foreach (var symbols in walked)
        {
            foreach (var (id, node) in symbols._nodes)
            {
                if (!nodes.TryGetValue(id, out var current)
                    || string.CompareOrdinal(node.Metadata![AssemblyKey], current.Metadata![AssemblyKey]) < 0)
                {
                    nodes[id] = node;
                }
            }
            foreach (var edge in symbols._edges) edges[edge.Id] = edge;
        }

        var edgesBySource = edges.Values.ToLookup(edge => edge.SourceId, StringComparer.Ordinal);
        return nodes.Values
            .GroupBy(node => node.FilePath!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new FileFacts(
                group.Key,
                group.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
                group.SelectMany(node => edgesBySource[node.Id]).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToList()));
    }
}
