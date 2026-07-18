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

    // -----------------------------------------------------------------------
    // Incremental-delta emission (mirrors CSharpWorkerIncrementalDeltaTests):
    // whole-tree convergence vs a fresh scan, cross-file dirty propagation, the
    // emitted delta shape, within-file declaration-merging dedup, and no-op
    // suppression — all through the real node worker process.
    // -----------------------------------------------------------------------

    /// <summary>Builds a pipeline whose delta sink is recorded, so a test can assert the
    /// exact shape of what the TypeScript worker emitted while the graph still commits.</summary>
    private CSharpWorkerPipeline RecordingPipeline(out RecordingDeltaSink recorder)
    {
        RecordingDeltaSink? captured = null;
        var pipeline = new CSharpWorkerPipeline(
            _tempDir,
            inner => captured = new RecordingDeltaSink(inner),
            [TypeScriptWorkerRegistration()]);
        recorder = captured!;
        return pipeline;
    }

    [Fact]
    public async Task Incremental_CrossFileEditAddDelete_ConvergesToTheSameGraphAsAFreshScan()
    {
        var basePath = await WriteFileAsync("base.ts",
            "export class Base { greet(): string { return 'hi'; } }\n");
        var derived = await WriteFileAsync("derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        // The whole incremental repertoire: a cross-file edit (Base.greet -> Base.salute,
        // which rebinds Derived's untouched CALLS/heritage facts), an addition, a deletion.
        await File.WriteAllTextAsync(basePath,
            "export class Base { salute(): string { return 'hi'; } }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var newcomer = await WriteFileAsync("newcomer.ts",
            "export function ping(): number { return 42; }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            newcomer, FileChangeType.Created, CancellationToken.None);

        File.Delete(derived);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            derived, FileChangeType.Deleted, CancellationToken.None);

        var incrementalNodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var incrementalEdges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        // A fresh full scan of the final on-disk tree must produce the identical graph —
        // compared on full records so a FilePath/span divergence cannot hide behind equal ids.
        await using var fresh = new CSharpWorkerPipeline(_tempDir, TypeScriptWorkerRegistration());
        await fresh.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        var freshNodes = await fresh.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var freshEdges = await fresh.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.NodeRecords(freshNodes),
            CSharpWorkerIncrementalDeltaTests.NodeRecords(incrementalNodes));
        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(freshEdges),
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(incrementalEdges));
    }

    [Fact]
    public async Task Incremental_CrossFileRebind_EmitsFileScopedReplacementIncludingUntouchedDependent()
    {
        await using var pipeline = RecordingPipeline(out var recorder);

        var basePath = await WriteFileAsync("base.ts",
            "export class Base { greet(): string { return 'hi'; } }\n");
        var derived = await WriteFileAsync("derived.ts",
            "import { Base } from './base';\n" +
            "export class Derived extends Base { run(): string { return this.greet(); } }\n");
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        recorder.Clear();

        // Editing ONLY base.ts renames greet; Derived's call no longer resolves to
        // base.ts#Base.greet(), so derived's bucket hash flips and it joins the dirty set
        // even though its own bytes never changed — the load-bearing cross-file property.
        await File.WriteAllTextAsync(basePath,
            "export class Base { salute(): string { return 'hi'; } }\n");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            basePath, FileChangeType.Changed, CancellationToken.None);

        var applyDeltas = recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        // The emission was incremental (never a whole-workspace replacement)...
        Assert.All(applyDeltas, d => Assert.False(d.ReplacesWorkspace));
        // ...and its scope pulled in the cross-file dependent alongside the edited file.
        var replaced = applyDeltas.SelectMany(d => d.ReplacesFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(basePath, replaced);
        Assert.Contains(derived, replaced);

        // The stale cross-file edge is gone from the committed graph.
        var edges = await pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.DoesNotContain(edges, e =>
            e.Type == "CALLS" && e.TargetId == "typescript:default:base.ts#Base.greet()");
    }

    [Fact]
    public async Task Incremental_DeltaShape_IndexReplacesWorkspace_ApplyScopesToTheDirtyFileVerbatim()
    {
        await using var pipeline = RecordingPipeline(out var recorder);

        var alpha = await WriteFileAsync("alpha.ts", "export class Alpha { m(): void { } }\n");
        await WriteFileAsync("beta.ts", "export class Beta { n(): void { } }\n");
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        // Index deltas replace the whole workspace and never scope to files.
        var indexDeltas = recorder.Deltas;
        Assert.NotEmpty(indexDeltas);
        Assert.All(indexDeltas, d =>
        {
            Assert.True(d.ReplacesWorkspace);
            Assert.Empty(d.ReplacesFiles);
        });

        recorder.Clear();
        // alpha and beta are independent, so editing alpha dirties exactly alpha.
        await File.WriteAllTextAsync(alpha, "export class Alpha { renamed(): void { } }\n");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            alpha, FileChangeType.Changed, CancellationToken.None);

        var applyDeltas = recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        Assert.All(applyDeltas, d => Assert.False(d.ReplacesWorkspace));
        var replaced = applyDeltas.SelectMany(d => d.ReplacesFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Single(replaced);
        Assert.Contains(alpha, replaced);

        // Emitted nodes belong only to dirty files, and ReplacesFiles carries the VERBATIM
        // path string the worker stamped on those nodes' filePath.
        var emittedNodes = applyDeltas.SelectMany(d => d.Nodes).ToList();
        Assert.NotEmpty(emittedNodes);
        Assert.All(emittedNodes, node => Assert.Contains(node.FilePath!, replaced));
        Assert.Contains(emittedNodes, node => node.Name == "renamed");
    }

    [Fact]
    public async Task DeclarationMerging_WithinFile_KeepsOneNodePerId_LastWins_AndEqualsFreshScan()
    {
        // Cross-file duplicate ids are IMPOSSIBLE in this worker: every node id and edge
        // source id embeds the WALKED file's relative path (typescript:<ws>:<fileRel>#...),
        // so two files can never mint the same id. The dedup that actually bites is
        // within-file declaration merging: two `interface Shared` in ONE file emit the same
        // node id twice from one walk, and last-occurrence-wins must collapse them to one.
        var merge = await WriteFileAsync("merge.ts",
            "export interface Shared { a(): void; }\n" +
            "export interface Shared { b(): void; }\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodeRepo = _pipeline.RepositoryFactory.CreateNodeRepository();
        var nodes = await nodeRepo.GetAllAsync();
        const string sharedId = "typescript:default:merge.ts#Shared";
        var shared = nodes.Where(n => n.Id == sharedId).ToList();
        Assert.Single(shared);
        // Last occurrence wins: the surviving type node is the SECOND declaration. The
        // worker emits it 1-based on source line 2; the host normalizes lineBase:1 to
        // 0-based, so the committed StartLine is 1 (the first declaration would be 0).
        Assert.Equal(1, shared[0].StartLine);
        // Both merged members survive under their own distinct ids.
        Assert.Contains(nodes, n => n.Id == "typescript:default:merge.ts#Shared.a()");
        Assert.Contains(nodes, n => n.Id == "typescript:default:merge.ts#Shared.b()");

        // Editing the file keeps exactly one Shared node (dedup is applied every walk).
        await File.WriteAllTextAsync(merge,
            "export interface Shared { a(): void; }\n" +
            "export interface Shared { c(): void; }\n");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            merge, FileChangeType.Changed, CancellationToken.None);
        nodes = await nodeRepo.GetAllAsync();
        Assert.Single(nodes, n => n.Id == sharedId);

        // The incremental end state equals a fresh scan of the same tree (full records).
        var incrementalNodes = await nodeRepo.GetAllAsync();
        var incrementalEdges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        await using var fresh = new CSharpWorkerPipeline(_tempDir, TypeScriptWorkerRegistration());
        await fresh.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.NodeRecords(await fresh.RepositoryFactory.CreateNodeRepository().GetAllAsync()),
            CSharpWorkerIncrementalDeltaTests.NodeRecords(incrementalNodes));
        Assert.Equal(
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(await fresh.RepositoryFactory.CreateEdgeRepository().GetAllAsync()),
            CSharpWorkerIncrementalDeltaTests.EdgeRecords(incrementalEdges));
    }

    [Fact]
    public async Task Incremental_NoOpEdit_EmitsSingleEmptyTerminalDelta_AndLeavesGraphUnchanged()
    {
        await using var pipeline = RecordingPipeline(out var recorder);

        // A comment on its own line, replaced later with a SAME-LENGTH comment: file bytes
        // change (so the host forwards the edit) but no emitted fact — not a span, not the
        // module node's end line — moves, so every bucket hashes the same.
        var file = await WriteFileAsync("noop.ts",
            "// aaa\nexport class A { m(): void { } }\n");
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var before = CSharpWorkerIncrementalDeltaTests.NodeRecords(
            await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync());
        recorder.Clear();

        await File.WriteAllTextAsync(file, "// bbb\nexport class A { m(): void { } }\n");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            file, FileChangeType.Changed, CancellationToken.None);

        // The worker still emits exactly one terminal delta (the supervisor requires ≥1 per
        // mutation), but it carries no facts and no replacesFiles.
        var applyDeltas = recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        Assert.All(applyDeltas, d =>
        {
            Assert.False(d.ReplacesWorkspace);
            Assert.Empty(d.ReplacesFiles);
            Assert.Empty(d.Nodes);
            Assert.Empty(d.Edges);
        });

        var after = CSharpWorkerIncrementalDeltaTests.NodeRecords(
            await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync());
        Assert.Equal(before, after);
    }
}
