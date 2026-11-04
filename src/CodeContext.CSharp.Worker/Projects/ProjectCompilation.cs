using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// One compilation with its trees: a project, or the fallback holding files no project
/// compiles. The tree map and the compilation move together, and a change to either marks
/// this unit stale so the units depending on it pick up a new reference.
/// </summary>
internal sealed class ProjectCompilation
{
    /// <summary>What a project's identifier is qualified with when it has no project.</summary>
    public const string FallbackQualifier = "<workspace>";

    private const string FallbackAssemblyName = "CodeContextWorkspace";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Top-level statements are ordinary here, and unsafe code must bind whether or not the
    /// project allowed it. <c>Nullable</c> stays off on purpose: it changes no fact, slows
    /// binding, and would rewrite parameter types in method identities.
    /// </summary>
    private static readonly CSharpCompilationOptions Options =
        new CSharpCompilationOptions(OutputKind.ConsoleApplication).WithAllowUnsafe(true);

    private readonly Dictionary<string, SyntaxTree> _trees = new(PathComparer);
    private readonly SyntaxTree? _generatedTree;
    private readonly ImmutableArray<MetadataReference> _metadataReferences;

    public ProjectCompilation(
        ProjectInfo? project,
        string idPrefix,
        CSharpParseOptions parseOptions,
        ImmutableArray<MetadataReference> metadataReferences,
        string fallbackUsings = "")
    {
        Project = project;
        IdPrefix = idPrefix;
        ParseOptions = parseOptions;
        _metadataReferences = metadataReferences;
        _generatedTree = (project is null ? fallbackUsings : GeneratedSource(project)) is { Length: > 0 } text
            ? CSharpSyntaxTree.ParseText(text, parseOptions, path: $"{Qualifier}.Project.g.cs")
            : null;
        Compilation = CSharpCompilation.Create(
            project?.AssemblyName ?? FallbackAssemblyName,
            _generatedTree is null ? [] : [_generatedTree],
            metadataReferences,
            Options);
    }

    public ProjectInfo? Project { get; }

    /// <summary>The prefix of every id this unit's files own.</summary>
    public string IdPrefix { get; }

    public CSharpParseOptions ParseOptions { get; }

    public string Qualifier => Project?.Name ?? FallbackQualifier;

    public CSharpCompilation Compilation { get; private set; }

    /// <summary>The units whose compilations this one references.</summary>
    public List<ProjectCompilation> References { get; } = [];

    /// <summary>True when this unit changed since the units depending on it last looked.</summary>
    public bool Changed { get; private set; } = true;

    public IReadOnlyDictionary<string, SyntaxTree> Trees => _trees;

    public static CSharpParseOptions ParseOptionsFor(ProjectInfo? project)
    {
        var options = CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.None);
        if (project is null) return options;

        options = options.WithPreprocessorSymbols(project.PreprocessorSymbols);
        return LanguageVersionFacts.TryParse(project.LanguageVersion, out var version)
            ? options.WithLanguageVersion(version)
            : options;
    }

    /// <summary>
    /// What a build generates into the project without it being on disk here: its global
    /// usings, and the <c>InternalsVisibleTo</c> items the SDK turns into attributes.
    /// </summary>
    private static string GeneratedSource(ProjectInfo project)
    {
        var text = new StringBuilder(project.GlobalUsings);
        foreach (var friend in project.InternalsVisibleTo)
        {
            text.Append("[assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(")
                .Append(SymbolDisplay.FormatLiteral(friend, quote: true))
                .AppendLine(")]");
        }
        return text.ToString();
    }

    public void SetText(string path, SourceText text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path);
        Compilation = _trees.TryGetValue(path, out var old)
            ? Compilation.ReplaceSyntaxTree(old, tree)
            : Compilation.AddSyntaxTrees(tree);
        _trees[path] = tree;
        Changed = true;
    }

    /// <summary>Takes over a tree parsed with these options, sparing a reparse.</summary>
    public void Adopt(string path, SyntaxTree tree)
    {
        Compilation = Compilation.AddSyntaxTrees(tree);
        _trees[path] = tree;
        Changed = true;
    }

    public void Remove(string path)
    {
        if (!_trees.Remove(path, out var tree)) return;
        Compilation = Compilation.RemoveSyntaxTrees(tree);
        Changed = true;
    }

    /// <summary>
    /// Points this unit at the current compilations of what it references. Dependencies
    /// come first in the order callers walk, so <see cref="Changed"/> has settled by now.
    /// </summary>
    public void RefreshReferences()
    {
        if (References.Count == 0 || !(Changed || References.Exists(reference => reference.Changed))) return;
        Compilation = Compilation.WithReferences(
            _metadataReferences.AddRange(References.Select(reference => (MetadataReference)reference.Compilation.ToMetadataReference())));
        Changed = true;
    }

    public void ClearChanged() => Changed = false;
}
