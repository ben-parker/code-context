using System.Diagnostics;
using System.Net;
using CodeContext.Api.Commands;
using CodeContext.Core.Instances;

namespace CodeContext.Core.Tests.Commands;

public class DetachedStartOrchestratorTests
{
    private const string Root = "C:\\repo";

    [Fact]
    public async Task ExistingInstanceShortCircuitsUnderStartLock()
    {
        var existing = Instance("existing", 7000, 10);
        var registry = new SequencedRegistry([existing]);
        var lockHandle = new TrackingAsyncDisposable();
        var processStarted = false;
        var runtime = Runtime(
            startProcess: _ =>
            {
                processStarted = true;
                return new FakeProcess();
            },
            acquireLock: (_, _) => Task.FromResult<IAsyncDisposable>(lockHandle));

        var result = await new DetachedStartOrchestrator(registry, runtime).StartAsync(
            Root, null, 120, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.WasStarted);
        Assert.Same(existing, result.Instance);
        Assert.False(processStarted);
        Assert.True(lockHandle.Disposed);
    }

    [Fact]
    public async Task ExplicitOccupiedPortFailsBeforeLaunchingProcess()
    {
        var processStarted = false;
        var runtime = Runtime(
            startProcess: _ =>
            {
                processStarted = true;
                return new FakeProcess();
            },
            isPortFree: _ => false);

        var result = await new DetachedStartOrchestrator(new SequencedRegistry([]), runtime).StartAsync(
            Root, 8123, 120, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Port 8123 is already in use", result.ErrorMessage);
        Assert.False(processStarted);
    }

    [Fact]
    public async Task HealthyChildMustRegisterExactIdentityAndReceivesExpectedArguments()
    {
        const int port = 8123;
        const int pid = 456;
        ProcessStartInfo? capturedStartInfo = null;
        var process = new FakeProcess(pid);
        var registered = Instance("generated-at-launch", port, pid);
        var registry = new SequencedRegistry([], [registered]);
        var requests = new List<Uri>();
        var runtime = Runtime(
            startProcess: startInfo =>
            {
                capturedStartInfo = startInfo;
                var idIndex = startInfo.ArgumentList.IndexOf("--instance-id");
                registered.InstanceId = startInfo.ArgumentList[idIndex + 1];
                return process;
            },
            allocatePort: () => port,
            httpHandler: request =>
            {
                requests.Add(request.RequestUri!);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var result = await new DetachedStartOrchestrator(registry, runtime).StartAsync(
            Root, null, 37, "C:\\logs\\query.log", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.WasStarted);
        Assert.Same(registered, result.Instance);
        Assert.True(process.Disposed);
        Assert.Equal(["start", "--path", Root, "--port", port.ToString(),
            "--idle-timeout", "37", "--log-file", "C:\\logs\\query.log", "--instance-id"],
            capturedStartInfo!.ArgumentList.Take(10));
        Assert.False(string.IsNullOrWhiteSpace(capturedStartInfo.ArgumentList[10]));
        Assert.Equal(Root, capturedStartInfo.WorkingDirectory);
        Assert.Equal($"http://localhost:{port}/healthz", Assert.Single(requests).ToString());
    }

    [Fact]
    public async Task EarlyChildExitReturnsExitCodeAndLogPath()
    {
        var runtime = Runtime(startProcess: _ => new FakeProcess(hasExited: true, exitCode: 7));

        var result = await new DetachedStartOrchestrator(new SequencedRegistry([]), runtime).StartAsync(
            Root, null, 120, "C:\\logs\\query.log", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("exited early with code 7", result.ErrorMessage);
        Assert.Contains("C:\\logs\\query.log", result.ErrorMessage);
    }

    [Fact]
    public async Task HealthyChildWithMismatchedRegistrationTimesOut()
    {
        var now = DateTimeOffset.UtcNow;
        var wrong = Instance("wrong-id", 8123, 456);
        var runtime = Runtime(
            startProcess: _ => new FakeProcess(456),
            utcNow: () => now,
            delay: (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            startupTimeout: TimeSpan.FromMilliseconds(100));
        var registry = new SequencedRegistry([], [wrong]);

        var result = await new DetachedStartOrchestrator(registry, runtime).StartAsync(
            Root, 8123, 120, "C:\\logs\\query.log", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not become healthy and register", result.ErrorMessage);
    }

    private static DetachedStartRuntime Runtime(
        Func<ProcessStartInfo, IDetachedProcess?>? startProcess = null,
        Func<int, bool>? isPortFree = null,
        Func<int>? allocatePort = null,
        Func<HttpRequestMessage, HttpResponseMessage>? httpHandler = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, CancellationToken, Task<IAsyncDisposable>>? acquireLock = null,
        TimeSpan? startupTimeout = null)
        => new(
            () => "codecontext-test.exe",
            startProcess ?? (_ => new FakeProcess()),
            () => new HttpClient(new StubHttpHandler(httpHandler ?? (_ => new HttpResponseMessage(HttpStatusCode.OK))))
            {
                Timeout = TimeSpan.FromSeconds(2),
            },
            isPortFree ?? (_ => true),
            allocatePort ?? (() => 8123),
            utcNow ?? (() => DateTimeOffset.UtcNow),
            delay ?? ((_, _) => Task.CompletedTask),
            acquireLock ?? ((_, _) => Task.FromResult<IAsyncDisposable>(new TrackingAsyncDisposable())),
            startupTimeout ?? TimeSpan.FromSeconds(1));

    private static InstanceRecord Instance(string id, int port, int pid) => new()
    {
        RootPath = Root,
        Port = port,
        Pid = pid,
        InstanceId = id,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private sealed class SequencedRegistry(params IReadOnlyList<InstanceRecord>[] results) : IInstanceRegistry
    {
        private int _readIndex;

        public IReadOnlyList<InstanceRecord> GetAll()
        {
            if (results.Length == 0) return [];
            var index = Math.Min(_readIndex++, results.Length - 1);
            return results[index];
        }

        public void Register(InstanceRecord record) => throw new NotSupportedException();
        public void Unregister(string rootPath, string? instanceId = null) => throw new NotSupportedException();
        public InstanceRecord? FindForPath(string path) => throw new NotSupportedException();
    }

    private sealed class FakeProcess(int id = 456, bool hasExited = false, int exitCode = 0) : IDetachedProcess
    {
        public int Id { get; } = id;
        public bool HasExited { get; } = hasExited;
        public int ExitCode { get; } = exitCode;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
