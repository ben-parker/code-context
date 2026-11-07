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
            options ?? TestWorkerOptions.Patient(),
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
        AnalysisProgress? progress = null;
        var result = await supervisor.IndexWorkspaceAsync(
            new IndexWorkspaceParams("ws-1", 1, ["a.fake", "b.fake"]),
            progressHandler: update => progress = update);

        Assert.True(result.Complete);
        Assert.NotNull(progress);
        Assert.Equal(2, progress!.FilesProcessed);
        Assert.Equal(2, progress.FilesTotal);
        lock (deltas)
        {
            var delta = Assert.Single(deltas);
            Assert.Equal(0, delta.Nodes[0].StartLine);
            Assert.Equal(1, delta.Nodes[0].EndColumn); // inclusive 1:1 -> exclusive 0:1
        }
    }

    [Theory]
    [InlineData("missing-progress")]
    [InlineData("invalid-progress-total")]
    [InlineData("regressing-progress")]
    [InlineData("invalid-progress-file")]
    [InlineData("invalid-progress-request")]
    public async Task InvalidIndexProgress_FailsTheWorkerRequest(string behavior)
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", behavior));
        supervisor.DeltaHandler = (_, _) => Task.FromResult(true);

        await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => supervisor.IndexWorkspaceAsync(
                new IndexWorkspaceParams("ws-1", 1, ["a.fake", "b.fake"])));
    }

    [Fact]
    public async Task IndexWorkspace_DuplicateApprovedPaths_AreRejectedBeforeDispatch()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec());

        await Assert.ThrowsAsync<ArgumentException>(
            () => supervisor.IndexWorkspaceAsync(
                new IndexWorkspaceParams("ws-1", 1, ["a.fake", "a.fake"])));
    }

    [Fact]
    public async Task ProgressObserverException_DoesNotFailWorkerOrIndexRequest()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec());
        supervisor.DeltaHandler = (_, _) => Task.FromResult(true);

        var result = await supervisor.IndexWorkspaceAsync(
            new IndexWorkspaceParams("ws-1", 1, ["a.fake"]),
            progressHandler: _ => throw new InvalidOperationException("observer failed"));

        Assert.True(result.Complete);
        Assert.Equal(ParserSessionState.Ready, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task SlowProgressObserver_DoesNotBlockProtocolResponses()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec());
        supervisor.DeltaHandler = (_, _) => Task.FromResult(true);
        var observerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseObserver = new ManualResetEventSlim();

        var indexing = supervisor.IndexWorkspaceAsync(
            new IndexWorkspaceParams("ws-1", 1, ["a.fake"]),
            progressHandler: _ =>
            {
                observerEntered.TrySetResult();
                releaseObserver.Wait(TimeSpan.FromSeconds(10));
            });

        await observerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var opened = await supervisor.OpenWorkspaceAsync(
                new OpenWorkspaceParams("ws-2", "/repo", [], [])).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(opened.Opened);
        }
        finally
        {
            releaseObserver.Set();
        }

        Assert.True((await indexing).Complete);
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

    [Fact]
    public void BuildStartInfo_SpecEnvironment_IsApplied()
    {
        var spec = new WorkerLaunchSpec("csharp", "C#", "worker", [],
            Environment: new Dictionary<string, string>
            {
                ["DOTNET_gcServer"] = "1",
                ["DOTNET_GCDynamicAdaptationMode"] = "1",
            });

        var startInfo = ParserProcessSupervisor.BuildStartInfo(spec, new ParserWorkerOptions());

        Assert.Equal("1", startInfo.Environment["DOTNET_gcServer"]);
        Assert.Equal("1", startInfo.Environment["DOTNET_GCDynamicAdaptationMode"]);
    }

    [Fact]
    public void BuildStartInfo_NoEnvironmentMaps_LeavesInheritedEnvironmentIntact()
    {
        var spec = new WorkerLaunchSpec("csharp", "C#", "worker", []);

        var startInfo = ParserProcessSupervisor.BuildStartInfo(spec, new ParserWorkerOptions());

        // A known inherited variable must survive when no overlay is supplied.
        Assert.True(startInfo.Environment.ContainsKey("PATH") || startInfo.Environment.ContainsKey("Path"),
            "Inherited PATH should still be present.");
    }

    [Fact]
    public void BuildStartInfo_OptionsEnvironment_AppliesOnlyMatchingParserId()
    {
        var spec = new WorkerLaunchSpec("csharp", "C#", "worker", []);
        var options = new ParserWorkerOptions
        {
            WorkerEnvironment = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["csharp"] = new Dictionary<string, string> { ["MINE"] = "yes" },
                ["typescript"] = new Dictionary<string, string> { ["THEIRS"] = "no" },
            },
        };

        var startInfo = ParserProcessSupervisor.BuildStartInfo(spec, options);

        Assert.Equal("yes", startInfo.Environment["MINE"]);
        Assert.False(startInfo.Environment.ContainsKey("THEIRS"),
            "A different parser's env must not leak into this worker.");
    }

    [Fact]
    public void BuildStartInfo_OptionsEntry_OverridesSpecEntryOnCollision()
    {
        var spec = new WorkerLaunchSpec("csharp", "C#", "worker", [],
            Environment: new Dictionary<string, string> { ["KEY"] = "from-spec" });
        var options = new ParserWorkerOptions
        {
            WorkerEnvironment = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["csharp"] = new Dictionary<string, string> { ["KEY"] = "from-options" },
            },
        };

        var startInfo = ParserProcessSupervisor.BuildStartInfo(spec, options);

        Assert.Equal("from-options", startInfo.Environment["KEY"]);
    }

    [Fact]
    public void BuildStartInfo_UpsertingInheritedVariable_DoesNotThrowAndTakesNewValue()
    {
        // PATH is inherited; upserting it must overwrite, not throw (Add would).
        var spec = new WorkerLaunchSpec("csharp", "C#", "worker", [],
            Environment: new Dictionary<string, string> { ["PATH"] = "overridden" });

        var startInfo = ParserProcessSupervisor.BuildStartInfo(spec, new ParserWorkerOptions());

        Assert.Equal("overridden", startInfo.Environment["PATH"]);
    }

    [Fact]
    public void BuildStartInfo_PreservesStdioAndWindowInvariants()
    {
        var workingDirectory = Path.GetTempPath();
        var spec = new WorkerLaunchSpec("csharp", "C#", "worker", ["--flag", "value"], workingDirectory);

        var startInfo = ParserProcessSupervisor.BuildStartInfo(spec, new ParserWorkerOptions());

        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
        Assert.Equal(["--flag", "value"], startInfo.ArgumentList);
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

    // ---- Persistent state (plan phase 5) ----

    /// <summary>A capable worker indexes <paramref name="files"/>, saves its state into <paramref name="directory"/>, and stops.</summary>
    private static async Task<IndexBaseline> SavedBaselineAsync(string directory, IReadOnlyList<string> files)
    {
        IReadOnlyList<UnitFingerprint>? fingerprints = null;
        await using var first = CreateSupervisor(FakeWorkerSpec("--behavior", "persistent-state"));
        first.DeltaHandler = (delta, _) =>
        {
            fingerprints = delta.Fingerprints;
            return Task.FromResult(true);
        };
        await first.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, files));
        var stateId = new string('5', 32);
        var path = Path.Combine(directory, "worker-fake.0000000001.55555555.state");
        var saved = await first.SaveStateAsync(new SaveStateParams("ws-1", path, stateId));
        Assert.True(saved.Saved);
        Assert.Equal(stateId, saved.StateId);
        Assert.Equal(new FileInfo(path).Length, saved.Bytes);
        Assert.False(File.Exists(path + ".tmp"));
        await first.ShutdownAsync();
        return new IndexBaseline(path, stateId, saved.Sha256!, fingerprints!);
    }

    private static List<AnalysisDelta> Recording(ParserProcessSupervisor supervisor)
    {
        var deltas = new List<AnalysisDelta>();
        supervisor.DeltaHandler = (delta, _) =>
        {
            lock (deltas) deltas.Add(delta);
            return Task.FromResult(true);
        };
        return deltas;
    }

    [Fact]
    public async Task Baseline_IsSentOnlyToACapableWorker()
    {
        // The strict worker fails any request that carries a baseline: the supervisor drops it first.
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "strict"));
        var deltas = Recording(supervisor);
        var result = await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"],
            new IndexBaseline("x.state", new string('1', 32), new string('2', 64), [])));
        Assert.Null(result.Baseline);
        Assert.True(Assert.Single(deltas).ReplacesWorkspace);
        Assert.False(supervisor.DeclaresPersistentState);
    }

    [Fact]
    public async Task AcceptedBaseline_CommitsAFileScopedDelta()
    {
        var directory = Directory.CreateTempSubdirectory("cc-state-").FullName;
        try
        {
            var baseline = await SavedBaselineAsync(directory, ["a.fake", "b.fake"]);

            // A new process, the same files: accepted, one empty file-scoped delta with fingerprints.
            await using var second = CreateSupervisor(FakeWorkerSpec("--behavior", "persistent-state"));
            var deltas = Recording(second);
            AnalysisProgress? progress = null;
            var result = await second.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake", "b.fake"], baseline),
                progressHandler: update => progress = update);
            Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
            var delta = Assert.Single(deltas);
            Assert.False(delta.ReplacesWorkspace);
            Assert.Empty(delta.Nodes);
            Assert.Empty(delta.ReplacesFiles);
            Assert.Equal(baseline.Fingerprints, delta.Fingerprints);
            Assert.Equal(2, progress!.FilesProcessed);

            // A second index of the same process is offered no baseline, whatever the caller passes.
            deltas.Clear();
            result = await second.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 2, ["a.fake", "b.fake"], baseline));
            Assert.Null(result.Baseline);
            Assert.True(Assert.Single(deltas).ReplacesWorkspace);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptedBaseline_SendsOnlyTheFilesThatChangedWhileDown()
    {
        var directory = Directory.CreateTempSubdirectory("cc-state-").FullName;
        try
        {
            var baseline = await SavedBaselineAsync(directory, ["a.fake", "b.fake"]);
            await using var second = CreateSupervisor(FakeWorkerSpec("--behavior", "persistent-state"));
            var deltas = Recording(second);
            var result = await second.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake", "c.fake"], baseline));
            Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
            var delta = Assert.Single(deltas);
            Assert.False(delta.ReplacesWorkspace);
            Assert.Equal(["c.fake", "b.fake"], delta.ReplacesFiles);
            Assert.All(delta.Nodes, node => Assert.Equal("c.fake", node.FilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("baseline-reject", null)]
    [InlineData("persistent-state", "wrong-hash")]
    [InlineData("persistent-state", "missing")]
    public async Task RejectedBaseline_ReplacesTheWorkspace(string behavior, string? damage)
    {
        var directory = Directory.CreateTempSubdirectory("cc-state-").FullName;
        try
        {
            var baseline = await SavedBaselineAsync(directory, ["a.fake"]);
            if (damage == "wrong-hash") baseline = baseline with { StateSha256 = new string('0', 64) };
            if (damage == "missing") File.Delete(baseline.StatePath);

            await using var second = CreateSupervisor(FakeWorkerSpec("--behavior", behavior));
            var deltas = Recording(second);
            var result = await second.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"], baseline));
            Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
            Assert.NotNull(result.BaselineReason);
            var delta = Assert.Single(deltas);
            Assert.True(delta.ReplacesWorkspace);
            Assert.Equal(2, delta.Nodes.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("accept-with-replacement", true)]
    [InlineData("reject-with-file-scope", true)]
    [InlineData("unsolicited-baseline", false)]
    public async Task BaselineAnswerThatDoesNotMatchTheRequest_FailsIt(string behavior, bool sendBaseline)
    {
        var directory = Directory.CreateTempSubdirectory("cc-state-").FullName;
        try
        {
            var baseline = sendBaseline ? await SavedBaselineAsync(directory, ["a.fake"]) : null;
            await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", behavior));
            supervisor.DeltaHandler = (_, _) => Task.FromResult(true);
            var before = supervisor.LastRequest;
            var failure = await Assert.ThrowsAsync<ParserWorkerFailedException>(() =>
                supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"], baseline)));
            Assert.Contains("baseline", failure.Message);
            Assert.Equal(ParserSessionState.Failed, supervisor.Snapshot.State);
            // The failed request leaves a gap in the numbering: nothing it committed can be extended.
            Assert.Equal(before + 2, supervisor.LastRequest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveState_RequiresTheCapability()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "strict"));
        // No process: nothing is started to save state.
        await Assert.ThrowsAsync<ParserWorkerUnavailableException>(() =>
            supervisor.SaveStateAsync(new SaveStateParams("ws-1", "x.state", new string('1', 32))));
        Assert.Null(supervisor.Snapshot.ProcessId);

        await supervisor.EnsureInitializedAsync();
        var pid = supervisor.Snapshot.ProcessId;
        // The strict worker would fail the request; it is never sent.
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            supervisor.SaveStateAsync(new SaveStateParams("ws-1", "x.state", new string('1', 32))));
        Assert.Equal(ParserSessionState.Ready, supervisor.Snapshot.State);
        Assert.Equal(pid, supervisor.Snapshot.ProcessId);
    }

    [Fact]
    public async Task Requests_AreNumberedPerSupervisor_AndEachProcessIsANewIncarnation()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"cc-crash-once-{Guid.NewGuid():N}");
        try
        {
            await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "crash-once", "--marker", marker));
            supervisor.DeltaHandler = (_, _) => Task.FromResult(true);
            await supervisor.EnsureInitializedAsync();
            var first = supervisor.Incarnation;
            await Assert.ThrowsAsync<ParserWorkerFailedException>(() => supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"])));
            Assert.Equal(2, supervisor.LastRequest);
            await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 2, ["a.fake"]));
            Assert.Equal(3, supervisor.LastRequest);
            Assert.NotEqual(first, supervisor.Incarnation);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task AbandonedSave_WhenItsCancelLands_LeavesTheSessionStateAlone()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "save-state-hangs"));
        supervisor.DeltaHandler = (_, _) => Task.FromResult(true);
        await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"]));
        var before = supervisor.Snapshot;

        // The host gave up on the save; its cancel lands later, possibly while a later mutation is indexing, so it
        // must not set the session to Ready (or touch it at all).
        using var abandon = new CancellationTokenSource();
        var save = supervisor.SaveStateAsync(new SaveStateParams("ws-1", Path.Combine(Path.GetTempPath(), "never.state"), new string('1', 32)),
            abandon.Token);
        await abandon.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Same(before, supervisor.Snapshot);
        Assert.Equal(ParserSessionState.Ready, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task Fingerprints_FromAWorkerWithoutTheCapability_AreDropped()
    {
        await using var supervisor = CreateSupervisor(FakeWorkerSpec("--behavior", "fingerprints-without-capability"));
        var deltas = Recording(supervisor);
        await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, ["a.fake"]));
        await supervisor.ApplyChangesAsync(new ApplyChangesParams("ws-1", 2, [new FileChangeDto("a.fake", FileChangeKinds.Changed)]));
        Assert.Equal(2, deltas.Count);
        // The worker sent them; nothing will ever hand them back to a worker without the capability, so none is kept.
        Assert.All(deltas, delta => Assert.Null(delta.Fingerprints));
        Assert.False(supervisor.DeclaresPersistentState);
    }
}
