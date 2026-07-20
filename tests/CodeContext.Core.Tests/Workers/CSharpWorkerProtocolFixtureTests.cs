using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Phase 3 exit gate: existing C# graph behavior must pass through protocol fixtures —
/// a real CodeContext.CSharp.Worker process spoken to over stdio, deltas committed
/// through the generational store — and the host must carry no Roslyn dependency.
/// </summary>
public class CSharpWorkerProtocolFixtureTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-csharp-fixture-").FullName;
    private CSharpWorkerPipeline _pipeline = null!;

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(_tempDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<string> WriteFileAsync(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public void HostAssemblies_HaveNoRoslynDependency()
    {
        // The architecture rule behind the whole phase: the host may depend on
        // protocol contracts, never on a language compiler.
        var hostAssemblies = new[]
        {
            typeof(GraphUpdateService).Assembly,              // CodeContext.Core
            typeof(Parser.Protocol.ParserProtocol).Assembly,  // CodeContext.Parser.Protocol
        };
        foreach (var assembly in hostAssemblies)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
                reference => reference.Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task InitialIndex_CommitsCrossFileRelationshipsThroughProtocol()
    {
        await WriteFileAsync("IGreeter.cs", @"
namespace Fixture
{
    public interface IGreeter { string Greet(string name); }
}");
        await WriteFileAsync("Greeter.cs", @"
namespace Fixture
{
    public class Greeter : IGreeter
    {
        public string Greet(string name) => $""Hello {name}"";
    }

    public class Caller
    {
        public string Run()
        {
            var greeter = new Greeter();
            return greeter.Greet(""world"");
        }
    }
}");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();

        Assert.Contains(nodes, n => n.Name == "IGreeter" && n.Type == "Interface");
        Assert.Contains(nodes, n => n.Name == "Greeter" && n.Type == "Class");
        Assert.Contains(edges, e =>
            e.Type == "IMPLEMENTS"
            && e.SourceId == CSharpWorkerPipeline.Id("Fixture.Greeter")
            && e.TargetId == CSharpWorkerPipeline.Id("Fixture.IGreeter"));
        Assert.Contains(edges, e =>
            e.Type == "CALLS"
            && e.SourceId == CSharpWorkerPipeline.Id("Fixture.Caller.Run()")
            && e.TargetId == CSharpWorkerPipeline.Id("Fixture.Greeter.Greet(string)"));

        // Every committed C# fact carries worker/workspace ownership metadata.
        Assert.All(nodes, n =>
        {
            Assert.Equal("csharp", n.Language);
            Assert.Equal("csharp", n.Metadata?["parserId"]);
        });
    }

    [Fact]
    public async Task InitialIndex_UnreadableApprovedFile_ReportsProgressAndCommitsReadableFiles()
    {
        var readable = await WriteFileAsync(
            "Readable.cs", "namespace Fixture { public class Readable { } }");
        var missing = Path.Combine(_tempDir, "Disappeared.cs");
        var progress = new List<AnalysisProgress>();

        await _pipeline.WorkerService.IndexWorkspaceAsync(
            "csharp", [readable, missing], CancellationToken.None,
            update => progress.Add(update));

        var terminal = Assert.Single(progress, update => update.FilesProcessed == 2);
        Assert.Equal(2, terminal.FilesTotal);
        Assert.Equal(missing, terminal.CurrentFile);
        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, node => node.Name == "Readable");
    }

    [Fact]
    public async Task IncrementalChange_ReplacesFactsWithoutLosingCrossFileResolution()
    {
        var baseFile = await WriteFileAsync("Base.cs", @"
namespace Fixture { public class BaseThing { } }");
        var derivedFile = await WriteFileAsync("Derived.cs", @"
namespace Fixture { public class DerivedThing : BaseThing { } }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        // Change only the derived file; the INHERITS edge must survive because the
        // worker still holds the base file's compilation state.
        await File.WriteAllTextAsync(derivedFile, @"
namespace Fixture { public class DerivedThing : BaseThing { public void Extra() { } } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            derivedFile, FileChangeType.Changed, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Name == "Extra" && n.Type == "Method");
        Assert.Contains(edges, e =>
            e.Type == "INHERITS"
            && e.SourceId == CSharpWorkerPipeline.Id("Fixture.DerivedThing")
            && e.TargetId == CSharpWorkerPipeline.Id("Fixture.BaseThing"));

        // Deleting the derived file replaces its facts with nothing.
        File.Delete(derivedFile);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            derivedFile, FileChangeType.Deleted, CancellationToken.None);

        nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.DoesNotContain(nodes, n => n.Name == "DerivedThing");
        Assert.Contains(nodes, n => n.Name == "BaseThing");
        _ = baseFile;
    }

    [Fact]
    public async Task WorkerSession_ReportsReadyThroughRegistryAfterIndexing()
    {
        await WriteFileAsync("Solo.cs", "namespace Fixture { public class Solo { } }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var session = Assert.Single(_pipeline.SessionRegistry.GetSnapshots(),
            s => s.ParserId == "csharp");
        Assert.Equal(ParserSessionState.Ready, session.State);
        Assert.NotNull(session.ParserVersion);
        Assert.Equal(1, session.ProtocolVersion);
        Assert.True(session.ProcessId is > 0, "the worker runs as a real child process");
    }

    [Fact]
    public async Task NativeSyntaxTree_IsAvailableForAnIndexedFile()
    {
        var file = await WriteFileAsync(
            "Native.cs", "namespace Fixture { public record Native(int Value); }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var result = await _pipeline.WorkerService.GetNativeSyntaxTreeAsync(file, maxDepth: 2);

        Assert.Equal("csharp", result.ParserId);
        Assert.Equal("roslyn-csharp-syntax-v1", result.Format);
        Assert.Equal("CompilationUnit", result.Tree.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task StaleGeneration_CannotOverwriteNewerCommit()
    {
        // Two writes in quick succession must land newest-last regardless of the
        // applier's internal generation bookkeeping.
        var file = await WriteFileAsync("Versioned.cs", "namespace Fixture { public class V1 { } }");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        await File.WriteAllTextAsync(file, "namespace Fixture { public class V2 { } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(file, FileChangeType.Changed, CancellationToken.None);

        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Name == "V2");
        Assert.DoesNotContain(nodes, n => n.Name == "V1");
    }

    [Fact]
    public async Task GitIgnoreSelection_IsSharedByScansReconciliationAndSingleFileRefresh()
    {
        var ignoreFile = await WriteFileAsync(".gitignore", "Ignored.cs\n*.generated.cs\n");
        await WriteFileAsync("Kept.cs", "namespace Fixture { public class Kept { } }");
        await WriteFileAsync("Ignored.cs", "namespace Fixture { public class Ignored { } }");

        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        var nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, node => node.Name == "Kept");
        Assert.DoesNotContain(nodes, node => node.Name == "Ignored");

        // A full generation after an ignore edit atomically removes newly ignored
        // facts and adds newly included files.
        await File.WriteAllTextAsync(ignoreFile, "Kept.cs\n*.generated.cs\n");
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
        nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.DoesNotContain(nodes, node => node.Name == "Kept");
        Assert.Contains(nodes, node => node.Name == "Ignored");

        var generated = await WriteFileAsync(
            "Watcher.generated.cs", "namespace Fixture { public class WatcherGenerated { } }");
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            generated, FileChangeType.Created, CancellationToken.None);
        nodes = await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.DoesNotContain(nodes, node => node.Name == "WatcherGenerated");
    }
}
