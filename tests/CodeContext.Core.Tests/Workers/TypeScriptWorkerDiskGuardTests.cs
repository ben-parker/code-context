using System.Diagnostics;
using CodeContext.Core.Models;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The TypeScript worker must not open a network path a repository names: see
/// <c>disk-guard.js</c>. The guard's own decisions are tested in Node, which this runs; the
/// rest indexes a repository through the real worker. Requires Node.js on PATH and an
/// npm-installed src/CodeContext.TypeScript.Worker.
/// </summary>
[Trait("Category", "ExternalTooling")]
public sealed class TypeScriptWorkerDiskGuardTests : IAsyncLifetime
{
    private readonly string _outside = Directory.CreateTempSubdirectory("cc-ts-guard-outside-").FullName;
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-ts-guard-").FullName;
    private CSharpWorkerPipeline? _pipeline;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_pipeline is not null) await _pipeline.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private static string Share(string path) => $"//localhost/c$/{path[3..].Replace('\\', '/')}";

    private static bool ServesLocalShare
        => OperatingSystem.IsWindows()
            && Path.GetTempPath().StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists($@"\\localhost\c$\{Path.GetTempPath()[3..]}");

    private string Write(string root, string name, string content)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<IReadOnlyList<CodeEdge>> ScanEdgesAsync(string root)
    {
        _pipeline = new CSharpWorkerPipeline(root, TypeScriptWorkerProtocolTests.TypeScriptWorkerRegistration());
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(root, null, CancellationToken.None);
        return [.. await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync()];
    }

    private static bool Imports(IEnumerable<CodeEdge> edges, string from, string to)
        => edges.Any(edge => edge.SourceId == $"typescript:default:{from}" && edge.TargetId == $"typescript:default:{to}");

    /// <summary>A repository whose alias is declared only by a config outside it, named by <paramref name="extends"/>.</summary>
    private void WriteRepositoryExtending(string extends)
    {
        var library = Path.Combine(_tempDir, "lib").Replace('\\', '/');
        Write(_outside, "base.json", $$"""{ "compilerOptions": { "paths": { "@lib/*": ["{{library}}/*"] } } }""");
        Write(_tempDir, "tsconfig.json", $$"""{ "extends": "{{extends}}" }""");
        Write(_tempDir, "lib/thing.ts", "export function thing() {}\n");
        Write(_tempDir, "src/a.ts", "import { thing } from '@lib/thing';\nexport function run() { thing(); }\n");
    }

    [Fact]
    public async Task TheGuardsOwnTestsPass()
    {
        var tests = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tests", "CodeContext.TypeScript.Worker.Tests", "disk-guard.test.js"));
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--test");
        start.ArgumentList.Add(tests);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, await output + await errors);
    }

    [Fact]
    public async Task AConfigOutsideTheRepositoryOnADriveIsRead()
    {
        // The control for the test below: the same repository, with the config named by its
        // drive path, resolves the alias only that config declares.
        WriteRepositoryExtending(Path.Combine(_outside, "base.json").Replace('\\', '/'));

        var edges = await ScanEdgesAsync(_tempDir);

        Assert.True(Imports(edges, "src/a.ts", "lib/thing.ts"));
    }

    [LocalShareFact]
    public async Task AConfigOnAShareIsNotRead()
    {
        WriteRepositoryExtending(Share(Path.Combine(_outside, "base.json")));

        var edges = await ScanEdgesAsync(_tempDir);

        // Had the worker opened the share it would have learned the alias and bound the import.
        Assert.False(Imports(edges, "src/a.ts", "lib/thing.ts"));
        Assert.Contains(edges, edge => edge.SourceId == "typescript:default:src/a.ts#run()");
    }

    [LocalShareFact]
    public async Task ARepositoryOnAShareKeepsWorking()
    {
        Write(_tempDir, "tsconfig.json", """{ "compilerOptions": { "paths": { "@lib/*": ["./lib/*"] } } }""");
        Write(_tempDir, "lib/thing.ts", "export function thing() {}\n");
        Write(_tempDir, "src/a.ts", "import { thing } from '@lib/thing';\nexport function run() { thing(); }\n");

        var edges = await ScanEdgesAsync(Share(_tempDir).Replace('/', '\\'));

        Assert.True(Imports(edges, "src/a.ts", "lib/thing.ts"));
    }

    private sealed class LocalShareFactAttribute : FactAttribute
    {
        public LocalShareFactAttribute()
        {
            if (!ServesLocalShare) Skip = "This machine does not serve its administrative share of the temp drive.";
        }
    }
}
