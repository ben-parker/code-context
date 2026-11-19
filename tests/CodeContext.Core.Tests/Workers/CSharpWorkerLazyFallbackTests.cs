using System.Text.Json;
using CodeContext.Core.Services;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The runtime references a file in no project binds against are 167 assemblies held in memory. They
/// come into being when a file first falls back and not before, and a file binds the same whichever
/// way it got there: on a first scan, after an edit, or after a restore.
/// </summary>
public sealed class CSharpWorkerLazyFallbackTests : IDisposable
{
    private const string Code = "namespace W; public class Code { public int Run(System.Collections.Generic.List<int> items) => items.Count; }";

    /// <summary>Binds only with the runtime: the list, its count, the string and LINQ.</summary>
    private const string Loose = """
        using System.Linq;
        namespace W;
        public class Loose
        {
            public int Go(System.Collections.Generic.List<int> items) => items.Where(item => item > 0).Count() + new Code().Run(items) + "x".Length;
        }
        """;

    private readonly string _base = Directory.CreateTempSubdirectory("cc-csharp-lazy-fallback-").FullName;
    private string Root => Path.Combine(_base, "repo");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private List<string> SourceFiles()
        => Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

    private CSharpWorkspaceAnalyzer Scan()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test", Root);
        analyzer.ReplaceFiles(SourceFiles(), CancellationToken.None);
        return analyzer;
    }

    /// <summary>Every field of every fact and coverage entry, as a fresh analysis would emit them.</summary>
    private static List<string> Facts(CSharpWorkspaceAnalyzer.AnalysisResult result)
        => result.Nodes.Select(node => "N " + JsonSerializer.Serialize(node, ParserProtocolJsonContext.Default.ProtocolNode))
            .Concat(result.Edges.Select(edge => "E " + JsonSerializer.Serialize(edge, ParserProtocolJsonContext.Default.ProtocolEdge)))
            .Order(StringComparer.Ordinal)
            .Append(JsonSerializer.Serialize(result.Coverage.ToList(), ParserProtocolJsonContext.Default.ListProjectCoverage))
            .ToList();

    private void AssertMatchesAFreshScan(CSharpWorkspaceAnalyzer analyzer)
    {
        var actual = Facts(analyzer.Analyze(CancellationToken.None));
        var fresh = Scan();
        var expected = Facts(fresh.Analyze(CancellationToken.None));
        Assert.Equal(expected, actual);
    }

    /// <summary>The runtime's own assemblies among the inputs an analysis reports.</summary>
    private static List<string> RuntimeInputs(CSharpWorkspaceAnalyzer.AnalysisResult result)
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)! + Path.DirectorySeparatorChar;
        return result.Inputs.Files.Where(path => path.StartsWith(runtime, StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void NoRuntimeReferencesAreLoadedWhileEveryFileHasAProject()
    {
        Write("App/App.csproj", CSharpWorkerBaselineTests.Project());
        var code = Write("App/Code.cs", Code);
        var analyzer = Scan();
        Assert.Equal(0, analyzer.RuntimeReferenceLoads);

        // Reporting what the facts were read from must not be what loads them: nothing was read from them.
        Assert.Empty(RuntimeInputs(analyzer.Analyze(CancellationToken.None)));
        File.WriteAllText(code, Code.Replace("items.Count", "items.Count + 1", StringComparison.Ordinal));
        analyzer.ApplyChanges([new FileChangeDto(code, FileChangeKinds.Changed)], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);
        analyzer.ApplyChanges([], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        Assert.Equal(0, analyzer.RuntimeReferenceLoads);
    }

    [Fact]
    public void AnEmptyWorkspaceLoadsNone()
    {
        Directory.CreateDirectory(Root);
        var analyzer = Scan();
        var result = analyzer.Analyze(CancellationToken.None);

        Assert.Equal(0, analyzer.RuntimeReferenceLoads);
        Assert.Empty(RuntimeInputs(result));
    }

    [Fact]
    public void TheRuntimeReferencesAreReachedOnlyThroughTheCountedLoad()
    {
        // The count above is the proof that nothing loads them early, so nothing may go around it.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeContext.sln"))) directory = directory.Parent;
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "CodeContext.CSharp.Worker", "CSharpWorkspaceAnalyzer.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"SharedReferences\.Value"));
    }

    [Fact]
    public void AFileInNoProjectLoadsThemAndBindsAgainstTheRuntime()
    {
        Write("App/App.csproj", CSharpWorkerBaselineTests.Project());
        Write("App/Code.cs", Code);
        Write("Loose.cs", Loose);
        var analyzer = Scan();
        var result = analyzer.Analyze(CancellationToken.None);

        Assert.Equal(1, analyzer.RuntimeReferenceLoads);
        Assert.Contains(RuntimeInputs(result), path => Path.GetFileName(path) == "System.Linq.dll");
        Assert.Contains(result.Edges, edge => edge.SourceId.EndsWith(":W.Loose.Go(System.Collections.Generic.List<int>)", StringComparison.Ordinal)
            && edge.TargetId.Contains("Enumerable.Where", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableProjectsFilesLoadThem()
    {
        Write("App/App.csproj", "<Project><broken");
        Write("App/Loose.cs", Loose);
        Write("App/Code.cs", Code);
        var analyzer = Scan();
        var result = analyzer.Analyze(CancellationToken.None);

        Assert.Equal(1, analyzer.RuntimeReferenceLoads);
        Assert.Contains(result.Edges, edge => edge.TargetId.Contains("Enumerable.Where", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileCreatedOutsideEveryProjectBindsAsOnAFreshScan()
    {
        Write("App/App.csproj", CSharpWorkerBaselineTests.Project());
        Write("App/Code.cs", Code);
        var analyzer = Scan();
        analyzer.Analyze(CancellationToken.None);

        var loose = Write("Loose.cs", Loose);
        analyzer.SyncApprovedFiles(SourceFiles(), CancellationToken.None);
        analyzer.ApplyChanges([new FileChangeDto(loose, FileChangeKinds.Created)], CancellationToken.None);

        Assert.Equal(1, analyzer.RuntimeReferenceLoads);
        AssertMatchesAFreshScan(analyzer);
    }

    [Fact]
    public void AFileAProjectStopsCompilingBindsAsOnAFreshScan()
    {
        var project = Write("App/App.csproj", CSharpWorkerBaselineTests.Project());
        Write("App/Code.cs", Code);
        Write("App/Loose.cs", Loose);
        var analyzer = Scan();
        analyzer.Analyze(CancellationToken.None);
        Assert.Equal(0, analyzer.RuntimeReferenceLoads);

        File.WriteAllText(project, CSharpWorkerBaselineTests.Project("<Compile Remove=\"Loose.cs\" />"));
        analyzer.ApplyChanges([], CancellationToken.None);

        Assert.Equal(1, analyzer.RuntimeReferenceLoads);
        AssertMatchesAFreshScan(analyzer);
    }

    [Fact]
    public void AProjectThatBecomesUnreadableBindsAsOnAFreshScan()
    {
        var project = Write("App/App.csproj", CSharpWorkerBaselineTests.Project());
        Write("App/Code.cs", Code);
        Write("App/Loose.cs", Loose);
        var analyzer = Scan();
        analyzer.Analyze(CancellationToken.None);

        File.WriteAllText(project, "<Project><broken");
        analyzer.ApplyChanges([], CancellationToken.None);

        AssertMatchesAFreshScan(analyzer);
    }

    [Fact]
    public void AFileLeavingTheFallbackAndComingBackBindsAsOnAFreshScan()
    {
        Write("Loose.cs", Loose);
        Write("Code.cs", Code);
        var analyzer = Scan();
        analyzer.Analyze(CancellationToken.None);

        var project = Write("App.csproj", CSharpWorkerBaselineTests.Project());
        analyzer.ApplyChanges([], CancellationToken.None);
        AssertMatchesAFreshScan(analyzer);

        File.Delete(project);
        analyzer.ApplyChanges([], CancellationToken.None);
        AssertMatchesAFreshScan(analyzer);
    }

    [Fact]
    public async Task ARestoredWorkerLoadsThemOnlyWhenAFileStartsFallingBack()
    {
        Write("App/App.csproj", CSharpWorkerBaselineTests.Project());
        Write("App/Code.cs", Code);
        var stateDirectory = Path.Combine(Root, ".codecontext", "cache", "default");
        Directory.CreateDirectory(stateDirectory);
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var baseline = await host.SaveAsync(stateDirectory);

        host.Restart();
        Assert.Equal(BaselineOutcomes.Accepted, (await host.IndexAsync(baseline)).Baseline);
        Assert.Equal(0, host.Analyzer.RuntimeReferenceLoads);

        var loose = Write("Loose.cs", Loose);
        await host.ApplyAsync([new FileChange(loose, FileChangeType.Created)]);
        Assert.Equal(1, host.Analyzer.RuntimeReferenceLoads);

        var fresh = new InProcessCSharpHost(Root);
        await fresh.IndexAsync();
        Assert.Equal(fresh.Facts(), host.Facts());
        Assert.Equal(fresh.Coverage(), host.Coverage());
    }
}
