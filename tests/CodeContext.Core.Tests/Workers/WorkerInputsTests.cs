using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// What the bundled workers report as their inputs (protocol <c>inputs</c>), and that the check a one-shot query makes
/// sees through them what the review found it missed: files read outside the walked tree and outside the root. Each
/// test runs a real worker, takes the inputs the host recorded beside its shard, then changes the file the way the
/// review did and checks.
/// </summary>
public sealed class CSharpWorkerInputsTests : IAsyncLifetime
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-inputs-cs-").FullName;
    private string Root => Path.Combine(_base, "sub");
    private CSharpWorkerPipeline _pipeline = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_base, ".git"));
        Directory.CreateDirectory(Root);
        _pipeline = new CSharpWorkerPipeline(Root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        Directory.Delete(_base, recursive: true);
    }

    private string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<(WorkerInputs Inputs, DateTime Captured)> ScanAsync()
    {
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(Root, null, CancellationToken.None);
        var inputs = WorkerInputsAssert.Recorded(_pipeline.RepositoryFactory.Database, "csharp");
        Thread.Sleep(50);
        var captured = DateTime.UtcNow;
        Thread.Sleep(50);
        return (inputs, captured);
    }

    private SnapshotFreshnessResult Check(WorkerInputs inputs, DateTime captured)
        => WorkerInputsAssert.Check(Root, captured, inputs, ".cs");

    private void Gate()
    {
        Write(Path.Combine(Root, "Sub.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write(Path.Combine(Root, "Gate.cs"), """
            namespace Demo;
            public class Gate {
            #if FEATURE_A
              public void OnlyA() { }
            #else
              public void OnlyB() { }
            #endif
            }
            """);
    }

    [Fact]
    public async Task DirectoryBuildPropsAboveTheRoot_EditedInPlace_IsSeen()
    {
        // The review's repository: the props file is at the git root, the index one folder down.
        var props = Write(Path.Combine(_base, "Directory.Build.props"),
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);FEATURE_A</DefineConstants></PropertyGroup></Project>");
        Gate();
        var (inputs, captured) = await ScanAsync();
        Assert.Contains(props, inputs.Files);
        Assert.True(Check(inputs, captured).Unchanged, Check(inputs, captured).FirstMoved);

        File.WriteAllText(props, "<Project><PropertyGroup><DefineConstants>$(DefineConstants);FEATURE_B</DefineConstants></PropertyGroup></Project>");

        Assert.Equal(props, Check(inputs, captured).FirstMoved);
    }

    [Fact]
    public async Task DirectoryBuildPropsCreatedAboveTheRoot_IsSeen()
    {
        Gate();
        var (inputs, captured) = await ScanAsync();
        var props = Path.Combine(_base, "Directory.Build.props");
        Assert.Contains(props, inputs.Absent);
        Assert.Contains(Path.Combine(Root, ".git"), inputs.Absent);

        Write(props, "<Project />");

        var result = Check(inputs, captured);
        Assert.Equal(props, result.FirstMoved);
        Assert.Contains("exists now", result.Why);
    }

    [Fact]
    public async Task RestoreDataUnderACustomExtensionsPath_IsSeen()
    {
        // project.assets.json where MSBuildProjectExtensionsPath puts it, in a folder no scan walks.
        Write(Path.Combine(Root, ".gitignore"), "artifacts/\n");
        Directory.CreateDirectory(Path.Combine(Root, "artifacts", "obj"));
        Write(Path.Combine(Root, "Sub.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><MSBuildProjectExtensionsPath>artifacts/obj/</MSBuildProjectExtensionsPath></PropertyGroup>
            </Project>
            """);
        Write(Path.Combine(Root, "A.cs"), "namespace Demo; public class A { }");
        var (inputs, captured) = await ScanAsync();
        var assets = Path.Combine(Root, "artifacts", "obj", "project.assets.json");
        Assert.Contains(assets, inputs.Absent);

        Write(assets, "{}");

        // The folder the worker listed to find restore data says so first; the file would as well.
        Assert.StartsWith("artifacts/obj", Check(inputs, captured).FirstMoved);
        Assert.Equal(assets, WorkerInputsAssert.Check(Root, captured, inputs with { Files = [] }, ".cs").FirstMoved is { } moved
            ? Path.Combine(Root, moved) : null);
    }

    [Fact]
    public async Task AReferencedAssembly_RewrittenInPlace_IsSeen()
    {
        var library = Path.Combine(Root, "lib", "Library.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        File.Copy(typeof(Xunit.Assert).Assembly.Location, library);
        Write(Path.Combine(Root, "Sub.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Reference Include="Library"><HintPath>lib/Library.dll</HintPath></Reference></ItemGroup>
            </Project>
            """);
        Write(Path.Combine(Root, "A.cs"), "namespace Demo; public class A { }");
        var (inputs, captured) = await ScanAsync();
        Assert.Contains(library, inputs.Files);

        File.Copy(typeof(Xunit.Assert).Assembly.Location, library, overwrite: true);

        Assert.Equal("lib/Library.dll", Check(inputs, captured).FirstMoved);
    }

    [Fact]
    public async Task NothingChanged_IsUnchanged_AndTheSourceFilesAreNotReportedAsInputs()
    {
        Gate();
        var (inputs, captured) = await ScanAsync();

        Assert.DoesNotContain(Path.Combine(Root, "Gate.cs"), inputs.Files);
        Assert.Contains(Path.Combine(Root, "Sub.csproj"), inputs.Files);
        Assert.True(Check(inputs, captured).Unchanged, Check(inputs, captured).FirstMoved);
    }
}

/// <summary>The TypeScript worker's inputs: the review's path-mapping repository, and the same edit to a running instance.</summary>
[Trait("Category", "ExternalTooling")]
public sealed class TypeScriptWorkerInputsTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("cc-inputs-ts-").FullName;
    private CSharpWorkerPipeline _pipeline = null!;

    private string Web => Path.Combine(_root, "web");
    private string Config => Path.Combine(Web, "tsconfig.json");

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(_root, TypeScriptWorkerProtocolTests.TypeScriptWorkerRegistration());
        Directory.CreateDirectory(Path.Combine(Web, "lib"));
        Directory.CreateDirectory(Path.Combine(Web, "lib2"));
        File.WriteAllText(Config, Mapping("lib/*"));
        File.WriteAllText(Path.Combine(Web, "package.json"), """{"name":"web","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(Web, "lib", "util.ts"), "export function helper(): number { return 1; }\n");
        File.WriteAllText(Path.Combine(Web, "lib2", "util.ts"), "export function helper(): number { return 2; }\nexport function other(): number { return 3; }\n");
        File.WriteAllText(Path.Combine(Web, "main.ts"), "import { helper } from \"@lib/util\";\nexport function mainFn(): number { return helper(); }\n");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static string Mapping(string target)
        => $$"""{ "compilerOptions": { "strict": true, "baseUrl": ".", "paths": { "@lib/*": ["{{target}}"] } }, "include": ["**/*.ts"] }""";

    private async Task<string?> HelperTargetFileAsync()
    {
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        var nodes = (await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync()).ToDictionary(node => node.Id!);
        var call = edges.Single(edge => edge.Type == "CALLS" && edge.SourceId!.Contains("mainFn", StringComparison.Ordinal));
        return nodes.TryGetValue(call.TargetId!, out var target) ? Path.GetRelativePath(_root, target.FilePath!).Replace('\\', '/') : null;
    }

    [Fact]
    public async Task PathMappingEditedInPlace_IsSeenByTheCheck()
    {
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        Assert.Equal("web/lib/util.ts", await HelperTargetFileAsync());
        var inputs = WorkerInputsAssert.Recorded(_pipeline.RepositoryFactory.Database, "typescript");
        Assert.Contains(Config, inputs.Files);
        Thread.Sleep(50);
        var captured = DateTime.UtcNow;
        Thread.Sleep(50);
        var extensions = new[] { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".vue" };
        Assert.True(WorkerInputsAssert.Check(_root, captured, inputs, extensions).Unchanged,
            WorkerInputsAssert.Check(_root, captured, inputs, extensions).FirstMoved);

        // The review's edit: the mapping moved by an in-place write; no folder's time moves.
        File.WriteAllText(Config, Mapping("lib2/*"));

        Assert.Equal("web/tsconfig.json", WorkerInputsAssert.Check(_root, captured, inputs, extensions).FirstMoved);
    }

    [Fact]
    public async Task PathMappingEditedInPlace_ReachesARunningInstance()
    {
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        Assert.Equal("web/lib/util.ts", await HelperTargetFileAsync());

        File.WriteAllText(Config, Mapping("lib2/*"));
        // What the watcher hands on for the config: a project input of the TypeScript worker.
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(Config, FileChangeType.Changed, CancellationToken.None);

        Assert.Equal("web/lib2/util.ts", await HelperTargetFileAsync());
    }
}

internal static class WorkerInputsAssert
{
    /// <summary>The inputs the store recorded beside the parser's shard, as a snapshot would write them.</summary>
    public static WorkerInputs Recorded(CodeContext.Core.Repositories.InMemory.InMemoryDatabase database, string parserId)
    {
        Assert.True(database.TryLatchCommitted(out _, out var annotations, out _));
        var annotation = Assert.Single(annotations, pair => pair.Key.ParserId == parserId).Value;
        return Assert.IsType<WorkerInputs>(annotation.Inputs);
    }

    public static SnapshotFreshnessResult Check(string root, DateTime captured, WorkerInputs inputs, params string[] extensions)
        => SnapshotFreshness.Check(
            new RepositoryFileSelector(Options.Create(new CodeContextOptions { RootPath = root })),
            root, captured, extension => extensions.Contains(extension, StringComparer.OrdinalIgnoreCase), [inputs],
            tolerance: TimeSpan.Zero);
}
