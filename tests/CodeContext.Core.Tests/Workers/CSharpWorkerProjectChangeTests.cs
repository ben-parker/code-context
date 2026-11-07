using System.Diagnostics;
using System.Reflection;
using System.Text;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.CSharp.Worker.Projects;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Changes to the shape of a workspace rather than to its code: a file changing owner, a
/// project appearing, a worker restarting. Each must leave the graph a fresh scan would give.
/// These came from adversarial review, and each failed before it was fixed.
/// </summary>
public sealed class CSharpWorkerProjectChangeTests : IAsyncLifetime
{
    private const string F = "namespace W; public class F { public void Go() { } }";
    private const string User = "namespace W; public class User { public void Run() { new F().Go(); } }";

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-change-").FullName;
    private CSharpWorkerPipeline _pipeline = null!;

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static string Project(string items = "", string properties = "", string framework = "net10.0") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>{framework}</TargetFramework><ImplicitUsings>disable</ImplicitUsings>{properties}</PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
        </Project>
        """;

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private Task Scan() => _pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);

    private Task Apply(string path, FileChangeType kind = FileChangeType.Changed)
        => _pipeline.GraphUpdateService.ProcessFileChangeAsync(path, kind, CancellationToken.None);

    private static async Task<List<string>> Facts(CSharpWorkerPipeline pipeline)
    {
        var nodes = await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        return nodes.Select(node => $"N {node.Id}|{node.Identifier}|{node.FilePath}|{node.StartLine}:{node.StartCol}|{node.Signature}")
            .Concat(edges.Select(edge => $"E {edge.Id}|{edge.SourceId}|{edge.TargetId}|{edge.Type}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private async Task AssertMatchesAFreshScan()
    {
        await using var fresh = new CSharpWorkerPipeline(_root);
        await fresh.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        var expected = await Facts(fresh);
        var actual = await Facts(_pipeline);

        var message = new StringBuilder("Missing:\n").AppendJoin('\n', expected.Except(actual).Take(12))
            .Append("\nStale:\n").AppendJoin('\n', actual.Except(expected).Take(12));
        Assert.True(expected.SequenceEqual(actual), message.ToString());
    }

    private async Task<List<string>> EdgesFrom(string source)
    {
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        return edges.Where(edge => edge.SourceId!.Contains(source, StringComparison.Ordinal))
            .Select(edge => $"{edge.Type} {edge.TargetId}")
            .ToList();
    }

    [Fact]
    public async Task AProjectLinkingAFileDoesNotTakeItFromItsOwnProject()
    {
        Write("A/A.csproj", Project());
        Write("A/F.cs", F);
        Write("A/User.cs", User);
        var linker = Write("LongerDirectoryName/D.csproj", Project());
        Write("LongerDirectoryName/Own.cs", "namespace O; public class Own { }");
        await Scan();

        Write("LongerDirectoryName/D.csproj", Project("<Compile Include=\"..\\A\\F.cs\" />"));
        await Apply(linker);

        await AssertMatchesAFreshScan();
        Assert.Contains(await EdgesFrom("User.Run"), edge => edge.StartsWith("CALLS", StringComparison.Ordinal) && edge.EndsWith("W.F.Go()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingALinkLeavesTheFilesFactsWhereTheyBelong()
    {
        Write("A/A.csproj", Project());
        var file = Write("A/F.cs", F);
        Write("A/User.cs", User);
        var linker = Write("D/D.csproj", Project("<Compile Include=\"..\\A\\F.cs\" />"));
        Write("D/Own.cs", "namespace O; public class Own { }");
        await Scan();

        Write("D/D.csproj", Project());
        await Apply(linker);
        await AssertMatchesAFreshScan();

        File.WriteAllText(file, "namespace W; public class F { public void Go() { int x = 1; } }");
        await Apply(file);
        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task ANestedProjectAppearingTakesItsFilesAndKeepsTheirCallers()
    {
        Write("Root.csproj", Project());
        Write("User.cs", User);
        Write("sub/F.cs", F);
        await Scan();

        await Apply(Write("sub/Sub.csproj", Project()), FileChangeType.Created);

        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task ANestedProjectGoingGivesItsFilesBack()
    {
        Write("Root.csproj", Project());
        Write("User.cs", User);
        Write("sub/F.cs", F);
        var nested = Write("sub/Sub.csproj", Project());
        await Scan();

        File.Delete(nested);
        await Apply(nested, FileChangeType.Deleted);

        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task ASecondProjectOfTheSameNameRequalifiesIdentifiers()
    {
        Write("A/Core.csproj", Project());
        Write("A/Same.cs", "namespace W; public class Same { }");
        Write("X/X.csproj", Project());
        Write("X/Same.cs", "namespace W; public class Same { }");
        await Scan();

        var other = Write("B/Other.cs", "namespace Q; public class Other { }");
        var project = Write("B/Core.csproj", Project());
        await _pipeline.GraphUpdateService.ProcessFileChangesAsync(
            [new FileChange(project, FileChangeType.Created), new FileChange(other, FileChangeType.Created)],
            CancellationToken.None);

        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task TheFirstFileOfAnEmptyProjectGoesToThatProject()
    {
        Write("Root.csproj", Project());
        Write("User.cs", "namespace W; public class User { }");
        await Scan();

        await Apply(Write("sub/Sub.csproj", Project()), FileChangeType.Created);
        await Apply(Write("sub/F.cs", F), FileChangeType.Created);

        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task AFileUnreadableDuringAScanIsPickedUpLater()
    {
        Write("A/A.csproj", Project());
        var file = Write("A/F.cs", F);
        var user = Write("A/User.cs", User);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Scan();
        }

        File.AppendAllText(user, "\n// touched\n");
        await Apply(user);

        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task AProjectRereadAfterAnUnreadableFileBringsItsUsersBackToo()
    {
        var project = Write("A/A.csproj", Project());
        var file = Write("A/F.cs", F);
        Write("A/User.cs", User);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Scan();
        }

        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddMinutes(1));
        await Apply(project);

        await AssertMatchesAFreshScan();
    }

    private static Process WorkerProcess(CSharpWorkerPipeline pipeline)
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var workers = (System.Collections.IDictionary)typeof(LanguageWorkerService).GetField("_workers", Private)!.GetValue(pipeline.WorkerService)!;
        var supervisor = workers["csharp"]!.GetType().GetProperty("Supervisor")!.GetValue(workers["csharp"])!;
        return (Process)supervisor.GetType().GetField("_process", Private)!.GetValue(supervisor)!;
    }

    [Fact]
    public async Task AWorkerRestartedBeforeADeletionLeavesNothingStale()
    {
        Write("A/A.csproj", Project());
        var file = Write("A/F.cs", "namespace W; public class F { public void Go() { System.Console.Beep(); } }");
        Write("A/User.cs", "namespace W; public class User { }");
        await Scan();
        var worker = WorkerProcess(_pipeline);
        worker.Kill();
        await worker.WaitForExitAsync();
        await Task.Delay(300);

        File.Delete(file);
        await Apply(file, FileChangeType.Deleted);

        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task ALinkedPartialTypeSurvivesItsFileLeavingAndReturning()
    {
        Write("Aa/Aa.csproj", Project());
        Write("Bb/Bb.csproj", Project("<Compile Include=\"..\\Aa\\Common.cs\" />"));
        var common = Write("Aa/Common.cs", "namespace W; public partial class Foo { public void Common() { } }");
        Write("Bb/FooB.cs", "namespace W; public partial class Foo { public void OnlyB() { } }");
        await Scan();

        var text = File.ReadAllText(common);
        File.Delete(common);
        await Apply(common, FileChangeType.Deleted);
        await AssertMatchesAFreshScan();

        File.WriteAllText(common, text);
        await Apply(common, FileChangeType.Created);
        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task AReferencedProjectLosingItsLastFileConverges()
    {
        Write("Lib/Lib.csproj", Project());
        var file = Write("Lib/F.cs", F);
        Write("App/App.csproj", Project("<ProjectReference Include=\"..\\Lib\\Lib.csproj\" />"));
        Write("App/User.cs", User);
        await Scan();

        File.Delete(file);
        await Apply(file, FileChangeType.Deleted);

        await AssertMatchesAFreshScan();
    }

    [Fact]
    public async Task ASharedProjectItemsEditIsPickedUpOnItsOwn()
    {
        var items = Write("Sh/Sh.projitems", "<Project><ItemGroup></ItemGroup></Project>");
        Write("Sh/X.cs", "namespace S; public class X { }");
        Write("App/App.csproj", Project().Replace("</Project>", "<Import Project=\"..\\Sh\\Sh.projitems\" Label=\"Shared\" /></Project>", StringComparison.Ordinal));
        Write("App/P.cs", "namespace W; public class P { }");
        await Scan();

        Write("Sh/Sh.projitems", "<Project><ItemGroup><Compile Include=\"$(MSBuildThisFileDirectory)X.cs\" /></ItemGroup></Project>");
        await Apply(items);

        await AssertMatchesAFreshScan();
    }

    private const string Hidden = """
        namespace L;
        internal class Hidden { public void Go() { } public void Go(int x) { } public int Val; public Hidden Next() => this; }
        """;

    private const string UsesHidden = """
        namespace T1;
        using L;
        public class Test { public void M() { var h = new Hidden(); h.Go(); h.Go(1); var v = h.Val; h.Next().Go(); } }
        """;

    private async Task AssertInternalsReached()
    {
        await Scan();
        var edges = await EdgesFrom("Test.M");
        Assert.Contains(edges, edge => edge.EndsWith("L.Hidden.Go()", StringComparison.Ordinal));
        Assert.Contains(edges, edge => edge.EndsWith("L.Hidden.Go(int)", StringComparison.Ordinal));
        Assert.Contains(edges, edge => edge.EndsWith("L.Hidden.Next()", StringComparison.Ordinal));
        Assert.Contains(edges, edge => edge.EndsWith("L.Hidden.Val", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFriendNamedWithAPublicKeyReachesInternals()
    {
        Write("Lib/Lib.csproj", Project());
        Write("Lib/Hidden.cs", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"T, PublicKey=0024000004800000\")]\n" + Hidden);
        Write("T/T.csproj", Project("<ProjectReference Include=\"..\\Lib\\Lib.csproj\" />"));
        Write("T/Test.cs", UsesHidden);

        await AssertInternalsReached();
    }

    [Fact]
    public async Task AFriendDeclaredAsAnAssemblyAttributeItemReachesInternals()
    {
        Write("Lib/Lib.csproj", Project(
            "<AssemblyAttribute Include=\"System.Runtime.CompilerServices.InternalsVisibleToAttribute\"><_Parameter1>T</_Parameter1></AssemblyAttribute>"));
        Write("Lib/Hidden.cs", Hidden);
        Write("T/T.csproj", Project("<ProjectReference Include=\"..\\Lib\\Lib.csproj\" />"));
        Write("T/Test.cs", UsesHidden);

        await AssertInternalsReached();
    }

    [Fact]
    public async Task CodeOnlyANonOwnerCompilesIsReported()
    {
        var coverage = new AnalysisCoverageRegistry();
        await using var pipeline = new CSharpWorkerPipeline(_root, coverage);
        Write("Aa/Aa.csproj", Project());
        Write("Bb/Bb.csproj", Project("<Compile Include=\"..\\Aa\\Shared.cs\" />", "<DefineConstants>$(DefineConstants);ONLY_B</DefineConstants>"));
        Write("Aa/Shared.cs", "namespace W; public class Shared {\n#if ONLY_B\n public void OnlyB() { }\n#endif\n}");

        await pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);

        var bb = Assert.Single(coverage.GetProjects(), project => project.Name == "Bb");
        Assert.Contains(bb.Notes!, note => note.Contains("different constants", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProjectNamingNoLanguageVersionGetsItsFrameworksDefault()
    {
        Write("App/App.csproj", Project(framework: "net8.0"));
        Write("App/C.cs", """
            using System.Linq;
            namespace W;
            public class C
            {
                private int field;
                public int X { get { return field; } }
                public int Run(int[] a) { var list = a.Reverse().ToList(); return list.Count; }
            }
            """);

        await Scan();

        Assert.Contains(await EdgesFrom("W.C.X"), edge => edge.EndsWith("W.C.field", StringComparison.Ordinal));
        Assert.Contains(await EdgesFrom("W.C.Run"), edge => edge.Contains("Enumerable.Reverse", StringComparison.Ordinal));
        Assert.Contains(await EdgesFrom("W.C.Run"), edge => edge.Contains("Enumerable.ToList", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReferenceKeptOnlyForBuildOrderBindsNothing()
    {
        Write("Gen/Gen.csproj", Project());
        Write("Gen/Tool.cs", "namespace Shared.Api; public class Tool { public void Go() { } }");
        Write("App/App.csproj", Project("<ProjectReference Include=\"../Gen/Gen.csproj\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />"));
        Write("App/C.cs", "namespace W; public class C { public void Run() { new Shared.Api.Tool().Go(); } }");

        await Scan();

        Assert.DoesNotContain(await EdgesFrom("W.C.Run"), edge => edge.EndsWith("Shared.Api.Tool.Go()", StringComparison.Ordinal));
    }

    [SkippableFactOnWindows]
    public void ADirectoryLinkBackToTheRootEnds()
    {
        Write("App/App.csproj", Project());
        var source = Write("App/P.cs", "class P { }");
        foreach (var name in new[] { "l1", "l2" })
        {
            using var link = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_root, name)}\" \"{_root}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            link.WaitForExit();
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var toolchain = ToolchainLocations.Detect();
            Assert.Single(ProjectModel.Load(new Disk(_root, toolchain), _root, [source], toolchain, timeout.Token).Projects);
        }
        finally
        {
            foreach (var name in new[] { "l1", "l2" }) Directory.Delete(Path.Combine(_root, name));
        }
    }

    private sealed class SkippableFactOnWindowsAttribute : FactAttribute
    {
        public SkippableFactOnWindowsAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Directory junctions are a Windows feature.";
        }
    }
}
