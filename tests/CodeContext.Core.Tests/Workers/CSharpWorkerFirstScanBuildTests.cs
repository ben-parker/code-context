using CodeContext.CSharp.Worker;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The host opens a workspace and then indexes it, so a first scan used to read every project
/// and parse every file twice. One build must serve both, unless anything moved between them.
/// </summary>
public sealed class CSharpWorkerFirstScanBuildTests : IDisposable
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-first-scan-").FullName;
    private readonly string _project;
    private readonly string _code;
    private readonly CSharpWorkspaceAnalyzer _analyzer;

    public CSharpWorkerFirstScanBuildTests()
    {
        _project = Write("App/App.csproj", Project);
        _code = Write("App/Code.cs", "public class First { }");
        _analyzer = new CSharpWorkspaceAnalyzer("test", _root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static void AfterTheBuild(string path)
        => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

    [Fact]
    public void OpenThenIndexBuildsOnce()
    {
        _analyzer.SyncApprovedFiles([_code], CancellationToken.None);
        _analyzer.ReplaceFiles([_code], CancellationToken.None);

        var result = _analyzer.Analyze(CancellationToken.None);

        Assert.Equal(1, _analyzer.BuildCount);
        Assert.Contains(result.Nodes, node => node.Name == "First");
    }

    [Fact]
    public void ASourceChangeBetweenOpenAndIndexIsRead()
    {
        _analyzer.SyncApprovedFiles([_code], CancellationToken.None);
        File.WriteAllText(_code, "public class Second { }");
        AfterTheBuild(_code);

        _analyzer.ReplaceFiles([_code], CancellationToken.None);

        Assert.Equal(2, _analyzer.BuildCount);
        Assert.Contains(_analyzer.Analyze(CancellationToken.None).Nodes, node => node.Name == "Second");
    }

    [Fact]
    public void AProjectChangeBetweenOpenAndIndexIsRead()
    {
        _analyzer.SyncApprovedFiles([_code], CancellationToken.None);
        File.WriteAllText(_project, Project.Replace("net10.0", "net9.0", StringComparison.Ordinal));
        AfterTheBuild(_project);

        _analyzer.ReplaceFiles([_code], CancellationToken.None);

        Assert.Equal(2, _analyzer.BuildCount);
    }

    [Fact]
    public void ADifferentFileListBuildsAgain()
    {
        var other = Write("App/Other.cs", "public class Other { }");
        _analyzer.SyncApprovedFiles([_code], CancellationToken.None);

        _analyzer.ReplaceFiles([_code, other], CancellationToken.None);

        Assert.Equal(2, _analyzer.BuildCount);
        Assert.Contains(_analyzer.Analyze(CancellationToken.None).Nodes, node => node.Name == "Other");
    }

    [Fact]
    public void AFullIndexOfAWalkedWorkspaceStillStartsOver()
    {
        _analyzer.SyncApprovedFiles([_code], CancellationToken.None);
        _analyzer.ReplaceFiles([_code], CancellationToken.None);
        _analyzer.Analyze(CancellationToken.None);

        _analyzer.ReplaceFiles([_code], CancellationToken.None);

        Assert.Equal(2, _analyzer.BuildCount);
    }
}
