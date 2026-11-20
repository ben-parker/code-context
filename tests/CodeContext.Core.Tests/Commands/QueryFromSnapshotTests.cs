using System.Text.RegularExpressions;
using CodeContext.Api;
using CodeContext.Api.Commands;
using CodeContext.Api.Endpoints;
using CodeContext.Core.Instances;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Tests.Workers;
using CodeContext.Core.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Commands;

/// <summary>
/// <c>codecontext query --snapshot</c> (or the setting) with no instance running and a snapshot on disk (memory plan
/// M5): it answers in its own process, labelled <c>snapshot</c>, exactly as a served answer apart from that label, writes
/// nothing, starts no worker; anything changed, a refused snapshot, the cache turned off fall back to starting an
/// instance; without the flag or the setting, and with <c>--start</c>, an instance is started as always; a running
/// instance is always preferred. The snapshot is written by a production-wired instance with the
/// real fake worker; the served answers come from a verified instance behind the real REST endpoints.
/// </summary>
public sealed class QueryFromSnapshotTests : IAsyncLifetime
{
    private const string InstanceId = "0123456789abcdef0123456789abcdef";
    private readonly string _base = Directory.CreateTempSubdirectory("cc-oneshot-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string KeyPath => Path.Combine(_base, "home", "cache.key");
    private string Marker => Path.Combine(_base, "worker-started");

    // Served output per query, from a verified instance, captured before it stopped and wrote the snapshot.
    private readonly Dictionary<string, string> _served = new(StringComparer.Ordinal);

    private static readonly string[][] Queries =
    [
        ["aClass"],
        ["aClass", "--json"],
        ["aClass", "--human"],
        ["aClass", "--tests"],
        ["aMethod", "--depth", "2"],
        ["Missing"],
        ["multi", "aClass", "bMethod", "aClass", "Missing"],
        ["multi", "aClass", "bMethod", "--json"],
        ["multi", "aClass", "bMethod", "--human"],
        ["Method"],
        // Characters the API writes as themselves and a default encoder would escape.
        ["O'Brien+é&Class", "--json"],
        ["multi", "O'Brien+é&Method", "aClass", "--json"],
        ["O'Brien+é&Class", "--human"],
        // Whole-graph queries, through the same service.
        ["path", "aClass", "aMethod"],
        ["path", "aClass", "bMethod", "--json"],
        ["path", "aClass", "Missing", "--human"],
        ["affected", "aMethod", "--json"],
        ["affected", "fake:b.Class"],
        ["hubs", "--human"],
        ["hubs", "--json"],
    ];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "a.fake"), "a");
        File.WriteAllText(Path.Combine(Root, "b.fake"), "b");
        File.WriteAllText(Path.Combine(Root, "O'Brien+é&.fake"), "o");
        File.WriteAllText(Path.Combine(Root, "notes.txt"), "not indexed");

        // A first start scans and writes a snapshot; a second, cached start verifies it and serves.
        await using (var first = new CachedInstance(Root, KeyPath, "normal"))
        {
            await first.StartAsync();
            await first.WaitForAsync(() => first.ScanState.Phase == ScanPhase.Ready, "the first scan");
            await first.StopAsync();
        }
        await using (var served = new CachedInstance(Root, KeyPath, "normal", InstanceId))
        {
            await served.StartAsync();
            await served.WaitForAsync(() => served.ScanState.Phase == ScanPhase.Ready && served.CacheState.GetNotice() is null, "verification");
            await using var app = await StartRestAsync(served);
            foreach (var query in Queries)
            {
                var (exit, output, error) = await RunAsync(query, new Registry(Record()), app.GetTestClient(), open: null);
                Assert.True(exit is 0 or 2, error);
                _served[Key(query)] = output;
            }
            await served.StopAsync();
        }
        Assert.NotEmpty(Directory.GetFiles(Workspace, "*.ccsnap"));
        // Time passes: nothing in the tree is newer than the snapshot now.
        Age();
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_base, recursive: true);
        return Task.CompletedTask;
    }

    private string Workspace => Path.Combine(Root, ".codecontext", "cache", "default");

    [Fact]
    public async Task NoInstance_FreshSnapshot_AnswersInProcess_AsTheServedAnswerApartFromTheLabel()
    {
        foreach (var query in Queries)
        {
            var (exit, output, error) = await RunAsync(query, new Registry(null), http: null, Open());

            Assert.True(exit is 0 or 2, $"{Key(query)}: {error}");
            Assert.Equal(query.Contains("Missing") ? 2 : 0, exit);
            Assert.Empty(error);
            var served = _served[Key(query)];
            Assert.Equal(served, WithoutLabel(output, query));
            Assert.NotEqual(served, output);
        }
        Assert.Contains("O'Brien+é&Class", _served[Key(["O'Brien+é&Class", "--json"])]);
    }

    [Fact]
    public async Task Label_SaysSnapshotAndTheCaptureTime()
    {
        var (_, compact, _) = await RunAsync(["aClass"], new Registry(null), http: null, Open());
        var (_, human, _) = await RunAsync(["aClass", "--human"], new Registry(null), http: null, Open());
        var (_, json, _) = await RunAsync(["aClass", "--json"], new Registry(null), http: null, Open());

        Assert.Matches(@"^cache\tsnapshot\t\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\tanswers come from the saved index, read without a running instance; ", compact);
        Assert.StartsWith("Cached, snapshot: answers come from the index saved at ", human);
        Assert.Contains("\"cache\":{\"state\":\"snapshot\",\"snapshotAt\":\"", json);
    }

    [Fact]
    public async Task NoInstance_WritesNothing_StartsNoWorker_AndLeavesTheKeyAlone()
    {
        var before = Listing(Path.Combine(Root, ".codecontext"));
        var key = File.ReadAllBytes(KeyPath);
        var keyTime = File.GetLastWriteTimeUtc(KeyPath);

        var (exit, _, error) = await RunAsync(["multi", "aClass", "bMethod"], new Registry(null), http: null, Open(markerWorker: true));

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.Equal(before, Listing(Path.Combine(Root, ".codecontext")));
        Assert.Equal(key, File.ReadAllBytes(KeyPath));
        Assert.Equal(keyTime, File.GetLastWriteTimeUtc(KeyPath));
        Assert.False(File.Exists(Marker), "a worker process was started");
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("add")]
    [InlineData("delete")]
    public async Task ChangedFile_FallsBackToStartingAnInstance(string change)
    {
        switch (change)
        {
            case "edit": File.AppendAllText(Path.Combine(Root, "a.fake"), "changed"); break;
            case "add": File.WriteAllText(Path.Combine(Root, "c.fake"), "c"); break;
            default: File.Delete(Path.Combine(Root, "b.fake")); break;
        }
        var starts = 0;

        var (exit, output, error) = await RunAsync(["aClass"], new Registry(null), http: null, Open(),
            start: (_, _) => { starts++; return Task.FromResult(DetachedStartResult.Failed("not in this test")); });

        Assert.Equal(4, exit);
        Assert.Equal(1, starts);
        Assert.Empty(output);
        Assert.Contains("Not answering from the saved index: ", error);
        Assert.Contains("changed after the saved index", error);
        Assert.Contains("No running instance found. Starting CodeContext", error);
    }

    [Fact]
    public async Task UnindexedFileEditedInPlace_StillAnswersFromTheSnapshot()
    {
        File.AppendAllText(Path.Combine(Root, "notes.txt"), "more");

        var (exit, output, _) = await RunAsync(["aClass"], new Registry(null), http: null, Open());

        Assert.Equal(0, exit);
        Assert.StartsWith("cache\tsnapshot\t", output);
    }

    [Fact]
    public async Task RunningInstance_IsPreferred_AndTheSnapshotIsNotOpened()
    {
        await using var served = new CachedInstance(Root, KeyPath, "normal", InstanceId);
        await served.StartAsync();
        await served.WaitForAsync(() => served.ScanState.Phase == ScanPhase.Ready && served.CacheState.GetNotice() is null, "verification");
        await using var app = await StartRestAsync(served);
        var opened = 0;

        var (exit, output, error) = await RunAsync(["aClass"], new Registry(Record()), app.GetTestClient(),
            root => { opened++; return Open()(root); });

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.Equal(0, opened);
        Assert.Equal(_served[Key(["aClass"])], output);
        await served.StopAsync();
    }

    [Fact]
    public async Task WithoutTheFlagOrTheSetting_StartsAnInstance_AndTheSnapshotIsNotOpened()
    {
        var opened = 0;
        var starts = 0;
        var (exit, _, error) = await RunAsync(["aClass"], new Registry(null), http: null,
            root => { opened++; return Open()(root); },
            start: (_, _) => { starts++; return Task.FromResult(DetachedStartResult.Failed("not in this test")); },
            snapshotFlag: false);

        Assert.Equal(4, exit);
        Assert.Equal(0, opened);
        Assert.Equal(1, starts);
        Assert.DoesNotContain("saved index", error);
    }

    [Fact]
    public async Task Setting_MakesTheSnapshotTheDefault()
    {
        var (exit, output, error) = await RunAsync(["aClass"], new Registry(null), http: null, Open(),
            snapshotFlag: false, snapshotByDefault: true);

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.StartsWith("cache\tsnapshot\t", output);
    }

    [Fact]
    public async Task StartOption_WinsOverTheSetting()
    {
        var opened = 0;
        var (exit, _, error) = await RunAsync(["aClass", "--start"], new Registry(null), http: null,
            root => { opened++; return Open()(root); }, snapshotFlag: false, snapshotByDefault: true);

        Assert.Equal(4, exit);
        Assert.Equal(0, opened);
        Assert.DoesNotContain("saved index", error);
    }

    [Fact]
    public async Task SnapshotAndStart_AreRefusedTogether()
    {
        var (exit, _, error) = await RunAsync(["aClass", "--start"], new Registry(null), http: null, Open());

        Assert.Equal(1, exit);
        Assert.Contains("--snapshot and --start cannot be combined", error);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    public void Setting_IsReadAsABoolean(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [QueryCommandHandler.SnapshotByDefaultKey] = value })
            .Build();

        Assert.Equal(expected, QueryCommandHandler.SnapshotByDefault(configuration));
    }

    [Fact]
    public async Task SnapshotSignedWithAnotherKey_IsNeverAnswered()
    {
        var other = Path.Combine(_base, "other", "cache.key");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(other, options)) stream.Write(Enumerable.Repeat((byte)7, SnapshotKey.Length).ToArray());

        var (exit, output, error) = await RunAsync(["aClass"], new Registry(null), http: null, Open(keyPath: other));

        Assert.Equal(4, exit);
        Assert.Empty(output);
        Assert.Contains("Authentication", error);
    }

    [Fact]
    public async Task NoKeyYet_FallsBack_AndCreatesNoKey()
    {
        var missing = Path.Combine(_base, "nobody", "cache.key");

        var (exit, _, error) = await RunAsync(["aClass"], new Registry(null), http: null, Open(keyPath: missing));

        Assert.Equal(4, exit);
        Assert.Contains("there is no snapshot key yet", error);
        Assert.False(File.Exists(missing));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public async Task CacheTurnedOff_FallsBack()
    {
        var (exit, _, error) = await RunAsync(["aClass"], new Registry(null), http: null, Open(cacheEnabled: false));

        Assert.Equal(4, exit);
        Assert.Contains("the cache is turned off", error);
    }

    [Fact]
    public async Task NoSnapshotDirectory_FallsBack_AndCreatesNone()
    {
        var bare = Directory.CreateDirectory(Path.Combine(_base, "bare")).FullName;
        File.WriteAllText(Path.Combine(bare, "a.fake"), "a");

        var result = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = bare, CacheKeyPath = KeyPath }, new StaticWorkerCatalog(CachedInstance.FakeWorker("normal")));

        Assert.Null(result.Snapshot);
        Assert.StartsWith("no saved index", result.Reason);
        Assert.False(Directory.Exists(Path.Combine(bare, ".codecontext")));
    }

    [Fact]
    public async Task AWorkerThatReportsNoInputs_IsNeverAnsweredFromWithoutAnInstance()
    {
        var root = await WriteSnapshotAsync("no-inputs", "no-inputs");
        Age();

        var result = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = KeyPath }, new StaticWorkerCatalog(CachedInstance.FakeWorker("no-inputs")), _checkAgainst);

        Assert.Null(result.Snapshot);
        Assert.Contains("reported no inputs", result.Reason);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("create")]
    public async Task AReportedInputOutsideTheRoot_ThatChanges_FallsBack(string change)
    {
        var props = Path.Combine(_base, $"{change}.props");
        if (change != "create") File.WriteAllText(props, "<Project />");
        var root = await WriteSnapshotAsync($"input-{change}", "normal", props);
        Age();
        var worker = CachedInstance.FakeWorker("normal", props);
        OneShotSnapshotOpen Open() => OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = KeyPath }, new StaticWorkerCatalog(worker), _checkAgainst);
        Assert.NotNull(Open().Snapshot);

        switch (change)
        {
            case "edit": File.WriteAllText(props, "<Project><PropertyGroup /></Project>"); break;
            case "delete": File.Delete(props); break;
            default: File.WriteAllText(props, "<Project />"); break;
        }

        var result = Open();
        Assert.Null(result.Snapshot);
        Assert.Contains(props, result.Reason);
    }

    [Fact]
    public async Task AWorkerInstalledSinceTheSnapshot_FallsBack()
    {
        var root = await WriteSnapshotAsync("worker-added", "normal");
        Age();
        var other = CachedInstance.FakeWorker("normal");
        other = other with { Manifest = other.Manifest with { ParserId = "other", Extensions = [".other"] } };

        var result = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = KeyPath },
            new StaticWorkerCatalog(CachedInstance.FakeWorker("normal"), other), _checkAgainst);

        Assert.Null(result.Snapshot);
        Assert.Contains("another set of workers", result.Reason);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("extensions")]
    [InlineData("projectInputs")]
    public async Task AWorkerWhoseManifestChanged_FallsBack(string what)
    {
        var root = await WriteSnapshotAsync($"worker-{what}", "normal");
        Age();
        var worker = CachedInstance.FakeWorker("normal");
        worker = worker with
        {
            Manifest = what switch
            {
                "version" => worker.Manifest with { Version = "2.0.0-test" },
                "extensions" => worker.Manifest with { Extensions = [".fake", ".fake2"] },
                _ => worker.Manifest with { ProjectInputs = [".props"] },
            },
        };

        var result = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = KeyPath }, new StaticWorkerCatalog(worker), _checkAgainst);

        Assert.Null(result.Snapshot);
        Assert.Contains("another set of workers", result.Reason);
    }

    [Fact]
    public async Task ASnapshotThatRecordsNoWorkerSet_IsNeverAnsweredFromWithoutAnInstance()
    {
        var root = Directory.CreateDirectory(Path.Combine(_base, "no-worker-set")).FullName;
        File.WriteAllText(Path.Combine(root, "a.fake"), "a");
        await using (var instance = new CachedInstance(root, KeyPath, "normal", recordWorkerSet: false))
        {
            await instance.StartAsync();
            await instance.WaitForAsync(() => instance.ScanState.Phase == ScanPhase.Ready, "the scan");
            await instance.StopAsync();
        }
        Age();

        var result = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = KeyPath }, new StaticWorkerCatalog(CachedInstance.FakeWorker("normal")), _checkAgainst);

        Assert.Null(result.Snapshot);
        Assert.Contains("records no set of workers", result.Reason);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("sibling")]
    public async Task AWorkerInstallationTouchedSinceTheSnapshot_FallsBack(string what)
    {
        var install = Directory.CreateDirectory(Path.Combine(_base, $"install-{what}")).FullName;
        var manifestPath = Path.Combine(install, "worker-manifest.json");
        File.WriteAllText(manifestPath, "{}");
        var root = Directory.CreateDirectory(Path.Combine(_base, $"touched-{what}")).FullName;
        File.WriteAllText(Path.Combine(root, "a.fake"), "a");
        await using (var instance = new CachedInstance(root, KeyPath, "normal", manifestPath: manifestPath))
        {
            await instance.StartAsync();
            await instance.WaitForAsync(() => instance.ScanState.Phase == ScanPhase.Ready, "the scan");
            await instance.StopAsync();
        }
        Age();
        var worker = CachedInstance.FakeWorker("normal") with { ManifestPath = manifestPath };
        OneShotSnapshotOpen Open() => OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = KeyPath }, new StaticWorkerCatalog(worker), _checkAgainst);
        Assert.True(Open().Snapshot is not null, Open().Reason);

        // The manifest rewritten with the same content, or a file added beside it (a worker's binary replaced).
        if (what == "manifest") File.WriteAllText(manifestPath, "{}");
        else File.WriteAllText(Path.Combine(install, "worker.dll"), "new");

        var result = Open();
        Assert.Null(result.Snapshot);
        Assert.Contains(install, result.Reason);
    }

    [Fact]
    public async Task TheFirstSnapshotOfASmallRepository_IsUsableAtOnce()
    {
        // The review's case: making .codecontext/ moves the root's time inside the slack before the first capture.
        var root = Directory.CreateDirectory(Path.Combine(_base, "small")).FullName;
        File.WriteAllText(Path.Combine(root, "a.fake"), "a");
        Thread.Sleep(SnapshotFreshness.Tolerance + TimeSpan.FromMilliseconds(300));
        await using (var instance = new CachedInstance(root, KeyPath, "normal"))
        {
            await instance.StartAsync();
            await instance.WaitForAsync(() => instance.ScanState.Phase == ScanPhase.Ready, "the scan");
            await instance.StopAsync();
        }

        var result = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = KeyPath }, new StaticWorkerCatalog(CachedInstance.FakeWorker("normal")));

        Assert.True(result.Snapshot is not null, result.Reason);
    }

    // ---- helpers ----

    /// <summary>A repository of its own with one fake file, scanned by an instance that then stops and leaves its snapshot.</summary>
    private async Task<string> WriteSnapshotAsync(string name, string behavior, params string[] inputs)
    {
        var root = Directory.CreateDirectory(Path.Combine(_base, name)).FullName;
        File.WriteAllText(Path.Combine(root, "a.fake"), "a");
        await using var instance = new CachedInstance(root, KeyPath, behavior, inputs: inputs);
        await instance.StartAsync();
        await instance.WaitForAsync(() => instance.ScanState.Phase == ScanPhase.Ready, "the scan");
        await instance.StopAsync();
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, ".codecontext", "cache", "default"), "*.ccsnap"));
        return root;
    }

    private Func<string, OneShotSnapshotOpen> Open(string? keyPath = null, bool cacheEnabled = true, bool markerWorker = false)
        => root => OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = root, CacheKeyPath = keyPath ?? KeyPath, CacheEnabled = cacheEnabled },
            new StaticWorkerCatalog(markerWorker ? MarkerWorker() : CachedInstance.FakeWorker("normal")), _checkAgainst);

    /// <summary>The fake worker's manifest with a command that leaves a file behind if it is ever run.</summary>
    private RegisteredWorker MarkerWorker()
    {
        var fake = CachedInstance.FakeWorker("normal");
        var spec = OperatingSystem.IsWindows()
            ? new WorkerLaunchSpec("fake", "Fake Worker", "cmd.exe", ["/c", $"type nul > \"{Marker}\""])
            : new WorkerLaunchSpec("fake", "Fake Worker", "/bin/sh", ["-c", $"touch '{Marker}'"]);
        return fake with { LaunchSpec = spec };
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(
        string[] query, IInstanceRegistry registry, HttpClient? http, Func<string, OneShotSnapshotOpen>? open,
        Func<string, CancellationToken, Task<DetachedStartResult>>? start = null,
        bool snapshotFlag = true, bool snapshotByDefault = false)
    {
        var multi = query[0] == "multi";
        var graph = query[0] is "path" or "affected" or "hubs" ? new GraphQuerySettings(query[0]) : null;
        var identifiers = query.Skip(multi || graph is not null ? 1 : 0).TakeWhile(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
        var depthIndex = Array.IndexOf(query, "--depth");
        var settings = new QuerySettings(
            identifiers, Root, depthIndex < 0 ? 1 : int.Parse(query[depthIndex + 1]), query.Contains("--tests"), null, false,
            query.Contains("--json"), multi, query.Contains("--human"), Start: query.Contains("--start"), Snapshot: snapshotFlag,
            Graph: graph);
        var output = new StringWriter();
        var error = new StringWriter();
        var runtime = new QueryRuntime(
            registry,
            http ?? new HttpClient(new RefusingHandler()),
            start ?? ((_, _) => Task.FromResult(DetachedStartResult.Failed("not in this test"))),
            output, error, TimeProvider.System, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(30),
            OpenSnapshot: open,
            SnapshotByDefault: snapshotByDefault);
        var exit = await QueryCommandHandler.ExecuteAsync(settings, runtime, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private static string Key(string[] query) => string.Join(' ', query);

    /// <summary>The output with the label taken out: the compact line, the human sentence, the JSON property.</summary>
    private static string WithoutLabel(string output, string[] query)
    {
        if (query.Contains("--json")) return Regex.Replace(output, "\"cache\":\\{\"state\":\"snapshot\",\"snapshotAt\":\"[^\"]*\"\\},", string.Empty);
        if (query.Contains("--human")) return Regex.Replace(output, "Cached, snapshot: [^\n]*\n", string.Empty);
        return Regex.Replace(output, "cache\tsnapshot\t[^\n]*\n", string.Empty);
    }

    private InstanceRecord Record() => new()
    {
        RootPath = Root,
        Port = 80,
        Pid = Environment.ProcessId,
        InstanceId = InstanceId,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<WebApplication> StartRestAsync(CachedInstance instance)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IContextService>(instance.Context);
        builder.Services.AddSingleton<IGraphQueryService>(instance.Context);
        builder.Services.AddSingleton<IStatusService>(instance.Status);
        builder.Services.AddSingleton<IIndexCoordinator>(instance.Coordinator);
        builder.Services.AddSingleton<ILanguageWorkerService>(instance.Workers);
        builder.Services.AddSingleton(Options.Create(new CodeContextOptions()));
        ProgramHelpers.AddRestApi(builder.Services);
        var app = builder.Build();
        app.MapCodeContextEndpoints();
        await app.StartAsync();
        return app;
    }

    // What a one-shot open compares files with in place of the capture time, once the tree has "aged".
    private DateTime? _checkAgainst;

    /// <summary>
    /// As if the snapshot were captured after every file: opens from here on compare with now in place of the capture
    /// time (a status change time cannot be set back to age a tree), so only what changes after this call counts.
    /// </summary>
    private void Age()
    {
        _checkAgainst = DateTime.UtcNow + SnapshotFreshness.Tolerance;
        Thread.Sleep(20);
    }

    private static List<string> Listing(string directory)
        => Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? $"{path} {new FileInfo(path).Length} {File.GetLastWriteTimeUtc(path):O} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}"
                : $"{path}/ {Directory.GetLastWriteTimeUtc(path):O}")
            .ToList();

    private sealed class Registry(InstanceRecord? instance) : IInstanceRegistry
    {
        public IReadOnlyList<InstanceRecord> GetAll() => instance is null ? [] : [instance];
        public void Register(InstanceRecord record) => throw new NotSupportedException();
        public void Unregister(string rootPath, string? instanceId = null) => throw new NotSupportedException();
        public InstanceRecord? FindForPath(string path) => instance;
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no instance answers HTTP in this test");
    }
}
