using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Records every <see cref="AnalysisDelta"/> the worker emits (as the host observes it,
/// post span-normalization) and forwards to the real applier, so tests can assert the
/// delta SHAPE — replacesWorkspace / replacesFiles / which files' facts were emitted —
/// while the graph still commits normally.
/// </summary>
public sealed class RecordingDeltaSink(IAnalysisDeltaSink inner) : IAnalysisDeltaSink
{
    private readonly object _gate = new();
    private readonly List<AnalysisDelta> _deltas = [];

    public IReadOnlyList<AnalysisDelta> Deltas
    {
        get { lock (_gate) { return _deltas.ToList(); } }
    }

    public void Clear()
    {
        lock (_gate) { _deltas.Clear(); }
    }

    public Task<bool> ApplyAsync(AnalysisDelta delta, CancellationToken ct = default)
    {
        lock (_gate) { _deltas.Add(delta); }
        return inner.ApplyAsync(delta, ct);
    }
}

/// <summary>
/// Analyzer-level (no worker process) tests of the per-file hash diff that drives the
/// incremental emission: which files land in the dirty/removed sets under edits, no-op
/// content changes, deletions, cross-file rebinds, and a full reseed.
/// </summary>
public sealed class CSharpWorkerIncrementalAnalyzerTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-csharp-incr-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Full index: seed the hash baseline, mirroring HandleIndexWorkspaceAsync.</summary>
    private static void Index(CSharpWorkspaceAnalyzer analyzer, IReadOnlyList<string> paths)
    {
        analyzer.ReplaceFiles(paths, CancellationToken.None);
        analyzer.SeedFactHashes(analyzer.Analyze(CancellationToken.None));
    }

    /// <summary>Incremental apply: build the emission and commit hashes, mirroring
    /// HandleApplyChangesAsync (which commits only after every chunk is accepted).</summary>
    private static CSharpWorkspaceAnalyzer.IncrementalEmission Apply(
        CSharpWorkspaceAnalyzer analyzer, params FileChangeDto[] changes)
    {
        analyzer.ApplyChanges(changes, CancellationToken.None);
        var emission = analyzer.BuildIncrementalEmission(analyzer.Analyze(CancellationToken.None));
        analyzer.CommitIncremental(emission);
        return emission;
    }

    [Fact]
    public void EditOneFile_MarksOnlyThatFileDirty()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Write("A.cs", "public class A { public void One() { } }");
        var b = Write("B.cs", "public class B { public void Two() { } }");
        Index(analyzer, [a, b]);

        File.WriteAllText(a, "public class A { public void OneRenamed() { } }");
        var emission = Apply(analyzer, new FileChangeDto(a, FileChangeKinds.Changed));

        Assert.Equal([a], emission.ReplacesFiles);
        Assert.All(emission.Nodes, node => Assert.Equal(a, node.FilePath));
        Assert.Contains(emission.Nodes, node => node.Name == "OneRenamed");
    }

    [Fact]
    public void WhitespaceOnlyChange_ProducesEmptyEmission()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Write("A.cs", "public class A { public void One() { } }");
        Index(analyzer, [a]);

        // Appended trivia changes no emitted fact (spans are line/column and the body is
        // untouched), so the bucket hash is unchanged and nothing is dirty.
        File.WriteAllText(a, "public class A { public void One() { } }   // trailing comment");
        var emission = Apply(analyzer, new FileChangeDto(a, FileChangeKinds.Changed));

        Assert.Empty(emission.ReplacesFiles);
        Assert.Empty(emission.Nodes);
        Assert.Empty(emission.Edges);
    }

    [Fact]
    public void DeletedFile_LandsInRemovedSet()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Write("A.cs", "public class A { }");
        var b = Write("B.cs", "public class B { }");
        Index(analyzer, [a, b]);

        File.Delete(b);
        var emission = Apply(analyzer, new FileChangeDto(b, FileChangeKinds.Deleted));

        // The removed file carries no facts but must appear in ReplacesFiles so the host
        // replaces its facts with nothing.
        Assert.Equal([b], emission.ReplacesFiles);
        Assert.Empty(emission.Nodes);
    }

    [Fact]
    public void CrossFileEdit_MarksDependentDirtyWithoutEditingIt()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var baseFile = Write("Base.cs", "namespace Fx { public class Base { public int Do(int x) => x; } }");
        var derived = Write("Derived.cs",
            "namespace Fx { public class Derived { private Base _b = new Base(); public long Go() => _b.Do(1); } }");
        Index(analyzer, [baseFile, derived]);

        // Editing ONLY Base re-types Do; Derived's call rebinds to Do(long), so Derived's
        // CALLS edge target id changes and its bucket hash flips — Derived joins the dirty
        // set even though its own bytes never changed. This is the load-bearing property.
        File.WriteAllText(baseFile, "namespace Fx { public class Base { public long Do(long x) => x; } }");
        var emission = Apply(analyzer, new FileChangeDto(baseFile, FileChangeKinds.Changed));

        Assert.Contains(baseFile, emission.ReplacesFiles);
        Assert.Contains(derived, emission.ReplacesFiles);
        Assert.Contains(emission.Edges, e =>
            e.Kind == "CALLS" && e.TargetId == CSharpWorkspaceAnalyzer.IdPrefix + "test:Fx.Base.Do(long)");
        Assert.DoesNotContain(emission.Edges, e =>
            e.Kind == "CALLS" && e.TargetId == CSharpWorkspaceAnalyzer.IdPrefix + "test:Fx.Base.Do(int)");
    }

    [Fact]
    public void PartialType_DuplicateId_LivesOnlyInCanonicalBucket()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Write("PartA.cs", "namespace Fx { public partial class Foo { public void FromA() { } } }");
        var b = Write("PartB.cs", "namespace Fx { public partial class Foo { public void FromB() { } } }");
        Index(analyzer, [a, b]);

        // Both files declare Fx.Foo, producing one node id from two walks. The ordinally
        // smallest FilePath owns it; every other bucket drops its copy, and the flat
        // whole-workspace result carries exactly one copy from the same owner.
        var analysis = analyzer.Analyze(CancellationToken.None);
        var fooId = CSharpWorkspaceAnalyzer.IdPrefix + "test:Fx.Foo";
        var owners = analysis.Buckets
            .Where(bucket => bucket.Nodes.Any(n => n.Id == fooId))
            .Select(bucket => bucket.FilePath)
            .ToList();
        Assert.Equal([a], owners);
        var flat = analysis.Nodes.Where(n => n.Id == fooId).ToList();
        Assert.Single(flat);
        Assert.Equal(a, flat[0].FilePath);
    }

    [Fact]
    public void DeletingCanonicalPartialFile_DirtiesTheSurvivingDeclaration()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Write("PartA.cs", "namespace Fx { public partial class Foo { public void FromA() { } } }");
        var b = Write("PartB.cs", "namespace Fx { public partial class Foo { public void FromB() { } } }");
        Index(analyzer, [a, b]);

        // Deleting the canonical owner moves the id into the survivor's bucket, so the
        // survivor's hash flips and it re-emits the node under its own FilePath — without
        // any explicit cross-file bookkeeping.
        File.Delete(a);
        var emission = Apply(analyzer, new FileChangeDto(a, FileChangeKinds.Deleted));

        var fooId = CSharpWorkspaceAnalyzer.IdPrefix + "test:Fx.Foo";
        Assert.Contains(a, emission.ReplacesFiles);
        Assert.Contains(b, emission.ReplacesFiles);
        Assert.Contains(emission.Nodes, n => n.Id == fooId && n.FilePath == b);
    }

    [Fact]
    public void EditingNonCanonicalPartialFile_DoesNotFlipTheWinner()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Write("PartA.cs", "namespace Fx { public partial class Foo { public void FromA() { } } }");
        var b = Write("PartB.cs", "namespace Fx { public partial class Foo { public void FromB() { } } }");
        Index(analyzer, [a, b]);

        File.WriteAllText(b, "namespace Fx { public partial class Foo { public void FromBRenamed() { } } }");
        var emission = Apply(analyzer, new FileChangeDto(b, FileChangeKinds.Changed));

        // B's member facts changed so B is dirty, but the type node stays owned by A —
        // the emission must not carry a competing copy of the id that would flip the
        // stored node's FilePath to B.
        var fooId = CSharpWorkspaceAnalyzer.IdPrefix + "test:Fx.Foo";
        Assert.Contains(b, emission.ReplacesFiles);
        Assert.DoesNotContain(emission.Nodes, n => n.Id == fooId);
    }

    [Fact]
    public void ReplaceFiles_ReseedsHashMap()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Write("A.cs", "public class A { }");
        var b = Write("B.cs", "public class B { }");
        Index(analyzer, [a, b]);

        // A full re-index over a smaller set must forget B entirely: a subsequent no-op
        // apply must NOT report B as removed (its hash entry is gone, not stale).
        Index(analyzer, [a]);
        var emission = Apply(analyzer, new FileChangeDto(a, FileChangeKinds.Changed));

        Assert.Empty(emission.ReplacesFiles);
    }
}

/// <summary>
/// End-to-end incremental-delta behavior through a real C# worker process: overall graph
/// equivalence vs a fresh scan, cross-file dirty propagation, and the emitted delta shape.
/// </summary>
public sealed class CSharpWorkerIncrementalDeltaTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-csharp-incr-e2e-").FullName;
    private CSharpWorkerPipeline _pipeline = null!;
    private RecordingDeltaSink _recorder = null!;

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(
            _tempDir, inner => _recorder = new RecordingDeltaSink(inner), []);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task IncrementalSeries_ConvergesToTheSameGraphAsAFreshScan()
    {
        var baseFile = await WriteAsync("Base.cs",
            "namespace Fx { public class Base { public int Do(int x) => x; } }");
        var derived = await WriteAsync("Derived.cs",
            "namespace Fx { public class Derived { private Base _b = new Base(); public long Go() => _b.Do(1); } }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        // A cross-file edit, an addition, and a deletion — the whole incremental repertoire.
        await File.WriteAllTextAsync(baseFile,
            "namespace Fx { public class Base { public long Do(long x) => x; } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            baseFile, FileChangeType.Changed, CancellationToken.None);

        var newcomer = await WriteAsync("Newcomer.cs",
            "namespace Fx { public class Newcomer { public int Ping() => 42; } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            newcomer, FileChangeType.Created, CancellationToken.None);

        File.Delete(derived);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            derived, FileChangeType.Deleted, CancellationToken.None);

        var incrementalNodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var incrementalEdges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        // A fresh full scan of the final on-disk tree must produce the identical graph.
        await using var fresh = new CSharpWorkerPipeline(_tempDir);
        await fresh.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        var freshNodes = await fresh.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var freshEdges = await fresh.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        // Full-record comparison, not id sets: a duplicate-id winner flip (partial types)
        // converges to the same id set with different FilePath/span content attached, so
        // id-set equality alone would mask exactly the divergence this test must catch.
        Assert.Equal(NodeRecords(freshNodes), NodeRecords(incrementalNodes));
        Assert.Equal(EdgeRecords(freshEdges), EdgeRecords(incrementalEdges));
    }

    internal static IReadOnlyList<string> NodeRecords(IEnumerable<CodeNode> nodes) =>
        nodes.Select(n =>
                $"{n.Id}|{n.FilePath}|{n.StartLine}-{n.EndLine}|{n.Signature}|{n.Name}|{n.Type}")
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    internal static IReadOnlyList<string> EdgeRecords(IEnumerable<CodeEdge> edges) =>
        edges.Select(e => $"{e.Id}|{e.SourceId}|{e.TargetId}|{e.Type}")
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task PartialClass_WinnerIsEditOrderIndependent_AndMovesOnCanonicalDelete()
    {
        var a = await WriteAsync("PartA.cs",
            "namespace Fx { public partial class Foo { public void FromA() { } } }");
        var b = await WriteAsync("PartB.cs",
            "namespace Fx { public partial class Foo { public void FromB() { } } }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var fooId = CSharpWorkerPipeline.Id("Fx.Foo");
        var nodeRepo = _pipeline.RepositoryFactory.CreateNodeRepository();

        // (i) The winner after a full index is the ordinally smallest declaring path.
        var foo = await nodeRepo.GetByIdAsync(fooId);
        Assert.Equal(a, foo!.FilePath);

        // (ii) Editing the NON-canonical declaration must not flip the winner — this is
        // the exact regression the reviewer reproduced pre-fix (edit recency deciding
        // which partial declaration the graph reports).
        await File.WriteAllTextAsync(b,
            "namespace Fx { public partial class Foo { public void FromBRenamed() { } } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            b, FileChangeType.Changed, CancellationToken.None);
        foo = await nodeRepo.GetByIdAsync(fooId);
        Assert.Equal(a, foo!.FilePath);

        // (iii) Editing the canonical declaration keeps it the winner.
        await File.WriteAllTextAsync(a,
            "namespace Fx { public partial class Foo { public void FromARenamed() { } } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            a, FileChangeType.Changed, CancellationToken.None);
        foo = await nodeRepo.GetByIdAsync(fooId);
        Assert.Equal(a, foo!.FilePath);

        // (iv) Deleting the canonical file hands ownership to the surviving declaration.
        File.Delete(a);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            a, FileChangeType.Deleted, CancellationToken.None);
        foo = await nodeRepo.GetByIdAsync(fooId);
        Assert.NotNull(foo);
        Assert.Equal(b, foo!.FilePath);

        // (v) The incremental end state equals a fresh scan of the same tree, compared on
        // full records so a FilePath/span divergence cannot hide behind equal id sets.
        var incrementalNodes = await nodeRepo.GetAllAsync();
        var incrementalEdges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        await using var fresh = new CSharpWorkerPipeline(_tempDir);
        await fresh.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        Assert.Equal(
            NodeRecords(await fresh.RepositoryFactory.CreateNodeRepository().GetAllAsync()),
            NodeRecords(incrementalNodes));
        Assert.Equal(
            EdgeRecords(await fresh.RepositoryFactory.CreateEdgeRepository().GetAllAsync()),
            EdgeRecords(incrementalEdges));
    }

    [Fact]
    public async Task CrossFileRebind_ReplacesDependentFactsIncrementally()
    {
        var baseFile = await WriteAsync("Base.cs",
            "namespace Fx { public class Base { public int Do(int x) => x; } }");
        var derived = await WriteAsync("Derived.cs",
            "namespace Fx { public class Derived { private Base _b = new Base(); public long Go() => _b.Do(1); } }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        _recorder.Clear();

        await File.WriteAllTextAsync(baseFile,
            "namespace Fx { public class Base { public long Do(long x) => x; } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            baseFile, FileChangeType.Changed, CancellationToken.None);

        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();

        // The stale resolution is gone and the new one is present, even though Derived.cs
        // was never edited.
        Assert.Contains(edges, e => e.Type == "CALLS"
            && e.SourceId == CSharpWorkerPipeline.Id("Fx.Derived.Go()")
            && e.TargetId == CSharpWorkerPipeline.Id("Fx.Base.Do(long)"));
        Assert.DoesNotContain(edges, e => e.Type == "CALLS"
            && e.TargetId == CSharpWorkerPipeline.Id("Fx.Base.Do(int)"));
        Assert.DoesNotContain(nodes, n => n.Id == CSharpWorkerPipeline.Id("Fx.Base.Do(int)"));

        // The emission was incremental (never a whole-workspace replacement) and its
        // scope pulled in the cross-file dependent alongside the edited file.
        var applyDeltas = _recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        Assert.All(applyDeltas, d => Assert.False(d.ReplacesWorkspace));
        var replaced = applyDeltas.SelectMany(d => d.ReplacesFiles).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(baseFile, replaced);
        Assert.Contains(derived, replaced);
    }

    [Fact]
    public async Task DeltaShape_IndexReplacesWorkspace_ApplyScopesToDirtyFilesVerbatim()
    {
        var a = await WriteAsync("Alpha.cs", "namespace Fx { public class Alpha { public void M() { } } }");
        await WriteAsync("Beta.cs", "namespace Fx { public class Beta { public void N() { } } }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        // Index deltas replace the whole workspace and never scope to files.
        var indexDeltas = _recorder.Deltas;
        Assert.NotEmpty(indexDeltas);
        Assert.All(indexDeltas, d =>
        {
            Assert.True(d.ReplacesWorkspace);
            Assert.Empty(d.ReplacesFiles);
        });

        _recorder.Clear();
        await File.WriteAllTextAsync(a, "namespace Fx { public class Alpha { public void MRenamed() { } } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            a, FileChangeType.Changed, CancellationToken.None);

        var applyDeltas = _recorder.Deltas;
        Assert.NotEmpty(applyDeltas);
        // Every apply delta is file-scoped, and only the edited file is in scope.
        Assert.All(applyDeltas, d => Assert.False(d.ReplacesWorkspace));
        var replaced = applyDeltas.SelectMany(d => d.ReplacesFiles).ToHashSet(StringComparer.Ordinal);
        Assert.Equal([a], replaced);

        // Emitted nodes belong only to dirty files, and ReplacesFiles carries the VERBATIM
        // path string the worker stamped on those nodes' FilePath.
        var emittedNodes = applyDeltas.SelectMany(d => d.Nodes).ToList();
        Assert.NotEmpty(emittedNodes);
        Assert.All(emittedNodes, node => Assert.Contains(node.FilePath!, replaced));
        Assert.Contains(emittedNodes, node => node.Name == "MRenamed");
    }
}
