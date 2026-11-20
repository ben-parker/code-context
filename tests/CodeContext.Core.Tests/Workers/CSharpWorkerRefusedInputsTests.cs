using CodeContext.CSharp.Worker;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The host looks at every path a worker reports as an input, with no guard of its own. So a path the worker's
/// guard refused is never reported, as read or as absent: a repository that names <c>\\host\share</c> would
/// otherwise have the host open it, which is what the guard exists to prevent.
/// </summary>
public sealed class CSharpWorkerRefusedInputsTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-csharp-refused-inputs-").FullName;
    private string Root => Path.Combine(_base, "repo");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string ProjectPuttingRestoreDataAt(string folder) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><MSBuildProjectExtensionsPath>{folder}</MSBuildProjectExtensionsPath></PropertyGroup>
        </Project>
        """;

    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze(Func<string, bool>? refusedInput = null)
    {
        var analyzer = refusedInput is null
            ? new CSharpWorkspaceAnalyzer("test", Root)
            : new CSharpWorkspaceAnalyzer("test", Root) { RefusedInput = refusedInput };
        analyzer.ReplaceFiles(
            Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList(),
            CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    [Fact]
    public void APathTheGuardRefuses_IsReportedNeitherAsReadNorAsAbsent()
    {
        var elsewhere = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(Path.Combine(_base, ".git"));
        Write("App/App.csproj", ProjectPuttingRestoreDataAt(Path.Combine(elsewhere, "obj") + Path.DirectorySeparatorChar));
        Write("App/Code.cs", "namespace W; public class Code { }");
        var assets = Path.Combine(elsewhere, "obj", "project.assets.json");

        // As this platform's guard decides: the path is looked for, found absent, and reported.
        var open = Analyze().Inputs;
        Assert.Contains(assets, open.Absent);

        // The same repository where the guard refuses that place: the facts are the same and the path is not reported.
        bool Refused(string path) => path.StartsWith(elsewhere, StringComparison.Ordinal);
        var guarded = Analyze(Refused).Inputs;
        Assert.DoesNotContain(guarded.Absent, Refused);
        Assert.DoesNotContain(guarded.Files, Refused);
        Assert.Equal(open.Files, guarded.Files);
        Assert.Equal(open.Absent.Where(path => !Refused(path)), guarded.Absent);
    }

    [WindowsFact]
    public void AShareARepositoryNames_IsNotReported()
    {
        Directory.CreateDirectory(Path.Combine(_base, ".git"));
        Write("App/App.csproj", ProjectPuttingRestoreDataAt(@"\\evil.invalid\share\obj\"));
        Write("App/Code.cs", "namespace W; public class Code { }");

        var inputs = Analyze().Inputs;

        Assert.DoesNotContain(inputs.Absent.Concat(inputs.Files), path => path.Contains("evil.invalid", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "The guard refuses nothing off Windows.";
        }
    }
}
