using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Phase 4 exit-gate fixtures for the persistent TypeScript worker. Requires Node.js
/// on PATH and an npm-installed src/CodeContext.TypeScript.Worker, hence quarantined
/// as external tooling (see DEVELOPMENT.md).
/// </summary>
[Trait("Category", "ExternalTooling")]
public class TypeScriptWorkerProtocolTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-ts-worker-").FullName;
    private CSharpWorkerPipeline _pipeline = null!;

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(_tempDir, TypeScriptWorkerRegistration());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    public static RegisteredWorker TypeScriptWorkerRegistration()
    {
        // The worker runs from the source tree (script + node_modules live there).
        var workerDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CodeContext.TypeScript.Worker"));
        var script = Path.Combine(workerDir, "typescript-worker.js");
        if (!File.Exists(script))
        {
            throw new FileNotFoundException($"TypeScript worker script not found at {script}.");
        }

        var manifest = new WorkerManifest(
            ManifestVersion: 1,
            ParserId: "typescript",
            DisplayName: "TypeScript",
            Version: "1.0.0",
            Command: "node",
            Args: ["typescript-worker.js"],
            MinProtocolVersion: ParserProtocol.Version,
            MaxProtocolVersion: ParserProtocol.Version,
            Languages: ["typescript", "javascript"],
            Extensions: [".ts", ".tsx", ".js", ".jsx"],
            ProjectMarkers: ["tsconfig.json", "package.json"]);
        var spec = new WorkerLaunchSpec(
            "typescript", "TypeScript", "node", ["typescript-worker.js"], WorkingDirectory: workerDir);
        return new RegisteredWorker(manifest, spec, Path.Combine(workerDir, "worker-manifest.json"));
    }

    private async Task<string> WriteFileAsync(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task MixedCSharpAndTypeScriptFixture_ReachesReadyWithBothLanguagesIndexed()
    {
        await WriteFileAsync("Service.cs", @"
namespace Mixed { public class Service { public string Run() => ""ok""; } }");
        await WriteFileAsync("client.ts",
            "export class Client { fetch(): string { return 'GET /api/service'; } }\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Language == "csharp" && n.Name == "Service");
        Assert.Contains(nodes, n => n.Language == "typescript" && n.Name == "Client");

        var sessions = _pipeline.SessionRegistry.GetSnapshots();
        Assert.Equal(ParserSessionState.Ready, sessions.Single(s => s.ParserId == "csharp").State);
        Assert.Equal(ParserSessionState.Ready, sessions.Single(s => s.ParserId == "typescript").State);
    }

    [Fact]
    public async Task CrossFileTypeScriptRelationships_ResolveWithProjectSemantics()
    {
        await WriteFileAsync("base.ts",
            "export class Base { greet(): string { return 'hi'; } }\n");
        await WriteFileAsync("derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        // The EXTENDS target is the *resolved* node in the other file, not a bare name:
        // that's the project-semantics half of the Phase 4 exit gate.
        Assert.Contains(edges, e =>
            e.Type == "EXTENDS"
            && e.SourceId == "typescript:default:derived.ts#Derived"
            && e.TargetId == "typescript:default:base.ts#Base");
        Assert.Contains(edges, e =>
            e.Type == "CALLS"
            && e.SourceId == "typescript:default:derived.ts#Derived.run()"
            && e.TargetId == "typescript:default:base.ts#Base.greet()");
    }

    [Fact]
    public async Task ExplicitHeritageEmitsMemberFamiliesButStructuralCompatibilityDoesNot()
    {
        await WriteFileAsync("family.ts", """
            export interface IService { run(): void; }
            export class Implementation implements IService { run(): void { } }
            export class StructuralOnly { run(): void { } }
            """);
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.All(nodes.Where(node => node.Language == "typescript"),
            node => Assert.False(string.IsNullOrWhiteSpace(node.Identifier)));
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        var memberEdge = Assert.Single(edges, edge => edge.Type == "IMPLEMENTS_MEMBER");
        Assert.Contains("#Implementation.run()", memberEdge.SourceId);
        Assert.Contains("#IService.run()", memberEdge.TargetId);
        Assert.DoesNotContain(edges, edge => edge.Type == "IMPLEMENTS_MEMBER"
            && edge.SourceId!.Contains("StructuralOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TopLevelInvocation_HasARealModuleScopeCaller()
    {
        await WriteFileAsync("entry.ts",
            "function startReadLoop(): void { }\nstartReadLoop();\n");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(nodes, node => node.Id == "typescript:default:entry.ts" && node.Type == "Module");
        Assert.Contains(edges, edge =>
            edge.Type == "CALLS"
            && edge.SourceId == "typescript:default:entry.ts"
            && edge.TargetId == "typescript:default:entry.ts#startReadLoop()");
    }

    [Fact]
    public async Task OneFileEdit_ReusesTheWorkerProcessAndUpdatesFacts()
    {
        var basePath = await WriteFileAsync("base.ts", "export class Base { }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var pidBeforeEdit = _pipeline.SessionRegistry.GetSnapshots()
            .Single(s => s.ParserId == "typescript").ProcessId;
        Assert.True(pidBeforeEdit is > 0);

        await File.WriteAllTextAsync(basePath, "export class Base { extra(): number { return 1; } }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Name == "extra" && n.Language == "typescript");

        var pidAfterEdit = _pipeline.SessionRegistry.GetSnapshots()
            .Single(s => s.ParserId == "typescript").ProcessId;
        Assert.Equal(pidBeforeEdit, pidAfterEdit); // no process-per-file respawn

        // Deleting the file replaces its facts with nothing.
        File.Delete(basePath);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Deleted, CancellationToken.None);
        nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.DoesNotContain(nodes, n => n.Language == "typescript");
    }

    [Fact]
    public async Task OneFileEdit_RecomputesSemanticEdgesInUntouchedDependents()
    {
        var basePath = await WriteFileAsync(
            "base.ts", "export class Base { greet(): string { return 'hi'; } }\n");
        await WriteFileAsync(
            "derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        await File.WriteAllTextAsync(
            basePath, "export class Base { salute(): string { return 'hi'; } }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.DoesNotContain(edges, e =>
            e.Type == "CALLS"
            && e.TargetId == "typescript:default:base.ts#Base.greet()");
    }

    [Fact]
    public async Task NativeSyntaxTree_IsAvailableForAnIndexedTypeScriptFile()
    {
        var file = await WriteFileAsync(
            "native.ts", "export const value = (input: number) => input + 1;\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var result = await _pipeline.WorkerService.GetNativeSyntaxTreeAsync(file, maxDepth: 2);

        Assert.Equal("typescript", result.ParserId);
        Assert.Equal("typescript-compiler-syntax-v1", result.Format);
        Assert.Equal("SourceFile", result.Tree.GetProperty("kind").GetString());
    }
}
