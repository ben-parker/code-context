using System.Diagnostics;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Conformance tests spawning the real fake-worker executable (built into the test
/// output directory). Everything here is plain dotnet — no external tooling trait.
/// </summary>
public class ParserProcessSupervisorTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);

    private static WorkerLaunchSpec FakeWorkerSpec(params string[] workerArgs)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var apphost = Path.Combine(baseDirectory,
            OperatingSystem.IsWindows() ? "CodeContext.FakeWorker.exe" : "CodeContext.FakeWorker");
        if (File.Exists(apphost))
        {
            return new WorkerLaunchSpec("fake", "Fake Worker", apphost, workerArgs);
        }

        var dll = Path.Combine(baseDirectory, "CodeContext.FakeWorker.dll");
        Assert.True(File.Exists(dll), $"Fake worker not found beside the tests: {dll}");
        return new WorkerLaunchSpec("fake", "Fake Worker", "dotnet",
            new List<string> { "exec", dll }.Concat(workerArgs).ToList());
    }

    private static ParserProcessSupervisor CreateSupervisor(
        WorkerLaunchSpec spec,
        ParserWorkerOptions? options = null,
        IParserSessionRegistry? registry = null)
        => new(spec,
            options ?? new ParserWorkerOptions(),
            NullLogger<ParserProcessSupervisor>.Instance,
            registry,
            new CodeContextOptions { RootPath = Path.GetTempPath() });

    private static async Task WaitForStateAsync(ParserProcessSupervisor supervisor, ParserSessionState state)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (supervisor.Snapshot.State != state)
        {
            Assert.True(DateTime.UtcNow < deadline,
                $"Timed out waiting for {state}; last state was {supervisor.Snapshot.State} ({supervisor.Snapshot.LastError}).");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Handshake_NegotiatesProtocolAndReportsReady()
    {
        var registry = new ParserSessionRegistry();
        await using var supervisor = CreateSupervisor(FakeWorkerSpec(), registry: registry);

        var result = await supervisor.EnsureInitializedAsync();

        Assert.Equal("fake", result.ParserId);
        Assert.Equal(ParserProtocol.Version, result.ProtocolVersion);
        Assert.Equal(1, result.SpanSemantics.LineBase);
        Assert.Equal(ParserSessionState.Ready, supervisor.Snapshot.State);
        Assert.NotNull(supervisor.Snapshot.ProcessId);

        var session = Assert.Single(registry.GetSnapshots());
        Assert.Equal(ParserSessionState.Ready, session.State);
    }

    [Fact]
    public async Task Handshake_IncompatibleProtocolVersion_MarksUnavailableAndKillsWorker()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "protocol-too-new"));

        var ex = await Assert.ThrowsAsync<ParserWorkerUnavailableException>(
            () => supervisor.EnsureInitializedAsync());

        Assert.Contains("9999", ex.Message);
        Assert.Equal(ParserSessionState.Unavailable, supervisor.Snapshot.State);
        // Unavailable is terminal: no respawn on the next call.
        await Assert.ThrowsAsync<ParserWorkerUnavailableException>(() => supervisor.EnsureInitializedAsync());
        Assert.Equal(0, supervisor.Snapshot.RestartCount);
    }

    [Fact]
    public async Task Handshake_WorkerHangs_TimesOutAndFails()
    {
        var options = new ParserWorkerOptions { InitializeTimeout = TimeSpan.FromSeconds(2) };
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "hang-on-initialize"), options);

        var ex = await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => supervisor.EnsureInitializedAsync());

        Assert.Contains("did not answer initialize", ex.Message);
        Assert.Equal(ParserSessionState.Failed, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task RepeatedHandshakeFailures_ExhaustRestartBudget()
    {
        var options = new ParserWorkerOptions
        {
            InitializeTimeout = TimeSpan.FromMilliseconds(250),
            MaxRestarts = 1,
        };
        await using var supervisor = CreateSupervisor(
            FakeWorkerSpec("--behavior", "hang-on-initialize"), options);

        await Assert.ThrowsAsync<ParserWorkerFailedException>(() => supervisor.EnsureInitializedAsync());
        await Assert.ThrowsAsync<ParserWorkerFailedException>(() => supervisor.EnsureInitializedAsync());
        await Assert.ThrowsAsync<ParserWorkerUnavailableException>(() => supervisor.EnsureInitializedAsync());

        Assert.Equal(ParserSessionState.Unavailable, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task MalformedOutput_FailsSessionAndTerminatesWorker()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "malformed-output"));

        await Assert.ThrowsAsync<ParserWorkerFailedException>(() => supervisor.EnsureInitializedAsync());

        await WaitForStateAsync(supervisor, ParserSessionState.Failed);
        Assert.Contains("malformed", supervisor.Snapshot.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StderrFlood_DoesNotDeadlockHandshakeOrIndexing()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "stderr-flood"));
        var deltas = new List<AnalysisDelta>();
        supervisor.DeltaHandler = (delta, _) =>
        {
            lock (deltas) deltas.Add(delta);
            return Task.FromResult(true);
        };

        await supervisor.EnsureInitializedAsync();
        await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams("ws-1", "/repo", [], []));
        var result = await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake", "b.fake"]));

        Assert.True(result.Complete);
        lock (deltas)
        {
            var delta = Assert.Single(deltas);
            Assert.Equal(0, delta.Nodes[0].StartLine);
            Assert.Equal(1, delta.Nodes[0].EndColumn); // inclusive 1:1 -> exclusive 0:1
        }
    }

    [Fact]
    public async Task CrashDuringIndex_FailsRequest_ThenRestartsWithinBudget()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"cc-crash-once-{Guid.NewGuid():N}");
        try
        {
            await using var supervisor = CreateSupervisor(
                FakeWorkerSpec("--behavior", "crash-once", "--marker", marker));
            supervisor.DeltaHandler = (_, _) => Task.FromResult(true);

            await supervisor.EnsureInitializedAsync();
            var firstPid = supervisor.Snapshot.ProcessId;

            await Assert.ThrowsAsync<ParserWorkerFailedException>(
                () => supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"])));
            Assert.Equal(ParserSessionState.Failed, supervisor.Snapshot.State);

            // The next request respawns the worker and succeeds.
            var result = await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 2, ["a.fake"]));

            Assert.True(result.Complete);
            Assert.Equal(1, supervisor.Snapshot.RestartCount);
            Assert.NotEqual(firstPid, supervisor.Snapshot.ProcessId);
            Assert.Equal(ParserSessionState.Ready, supervisor.Snapshot.State);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task CrashingRepeatedly_ExhaustsRestartBudget_BecomesUnavailable()
    {
        var options = new ParserWorkerOptions { MaxRestarts = 1 };
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "crash-on-index"), options);

        await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"])));
        await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 2, ["a.fake"])));

        var ex = await Assert.ThrowsAsync<ParserWorkerUnavailableException>(
            () => supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 3, ["a.fake"])));

        Assert.Contains("giving up", ex.Message);
        Assert.Equal(ParserSessionState.Unavailable, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task Cancellation_SlowIndexIsCancelled_WorkerStaysUsable()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "slow-index"));
        await supervisor.EnsureInitializedAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"]), cts.Token));

        // The same process must still answer requests after the cooperative cancel.
        var pid = supervisor.Snapshot.ProcessId;
        var open = await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams("ws-2", "/repo", [], []));
        Assert.True(open.Opened);
        Assert.Equal(pid, supervisor.Snapshot.ProcessId);
        Assert.Equal(0, supervisor.Snapshot.RestartCount);
    }

    [Fact]
    public async Task RequestLevelRemoteError_DoesNotFailHealthySession()
    {
        await using var supervisor = CreateSupervisor(
            FakeWorkerSpec("--behavior", "native-advertised-missing"));

        await Assert.ThrowsAsync<JsonRpcRemoteException>(() =>
            supervisor.GetNativeSyntaxTreeAsync(new NativeSyntaxTreeParams(
                "ws-1", Path.Combine(Path.GetTempPath(), "x.fake"))));

        Assert.Equal(ParserSessionState.Ready, supervisor.Snapshot.State);
        Assert.Equal(0, supervisor.Snapshot.RestartCount);
    }

    [Fact]
    public async Task GracefulShutdown_WorkerExitsAndSessionStops()
    {
        var supervisor = CreateSupervisor(FakeWorkerSpec());
        await supervisor.EnsureInitializedAsync();
        var pid = supervisor.Snapshot.ProcessId!.Value;

        await supervisor.ShutdownAsync();

        Assert.Equal(ParserSessionState.Stopped, supervisor.Snapshot.State);
        Assert.True(HasProcessExited(pid), "Worker process should have exited after graceful shutdown.");
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task StdinEof_MakesWorkerExitOnItsOwn()
    {
        // Spawned directly (no supervisor) so nothing can kill the process for us:
        // the worker must exit purely because its stdin reached EOF.
        var spec = FakeWorkerSpec();
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        _ = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();

        process.StandardInput.Close(); // EOF

        using var timeout = new CancellationTokenSource(WaitBudget);
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task FullGenerationFlow_InitialAndIncremental_CommitThroughGenerationalStore()
    {
        // Phase 2 exit gate: a fake worker completes an initial and an incremental
        // generation, committed atomically through the in-memory generational store,
        // without registering a global instance or opening a port (the worker's only
        // I/O is stdio by construction).
        var store = (IGenerationalGraphStore)new InMemoryRepositoryFactory(
            NullLogger<InMemoryRepositoryFactory>.Instance).CreateGraphRepository();
        var applier = new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance);

        await using var supervisor = CreateSupervisor(FakeWorkerSpec());
        supervisor.DeltaHandler = (delta, ct) => applier.ApplyAsync(delta, ct);

        await supervisor.EnsureInitializedAsync();
        await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams("ws-1", "/repo", ["fake.proj"], ["a.fake", "b.fake"]));

        // Initial generation: 2 files x (class + method) = 4 nodes, 2 contains-edges.
        var initial = await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake", "b.fake"]));
        Assert.True(initial.Complete);
        Assert.Equal(4, store.GetStatistics().NodeCount);
        Assert.Equal(2, store.GetStatistics().EdgeCount);

        // Incremental generation: b.fake deleted, a.fake changed → only a.fake's facts remain.
        var incremental = await supervisor.ApplyChangesAsync(new ApplyChangesParams("ws-1", 2,
        [
            new FileChangeDto("a.fake", FileChangeKinds.Changed),
            new FileChangeDto("b.fake", FileChangeKinds.Deleted),
        ]));
        Assert.True(incremental.Complete);
        Assert.Equal(2, store.GetStatistics().NodeCount);
        Assert.Equal(1, store.GetStatistics().EdgeCount);

        var repo = (ICodeGraphRepository)store;
        var nodes = (await repo.GetGraphAsync())!.Nodes;
        Assert.All(nodes, n => Assert.Equal("a.fake", n.FilePath));
    }

    private static bool HasProcessExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true; // no such process
        }
    }
}
