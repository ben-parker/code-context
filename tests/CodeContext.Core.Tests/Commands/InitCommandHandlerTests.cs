using System.Net;
using System.Text;
using System.Text.Json;
using CodeContext.Api;
using CodeContext.Api.Commands;
using CodeContext.Core.Instances;

namespace CodeContext.Core.Tests.Commands;

public class InitCommandHandlerTests
{
    private const string Root = "C:\\repo";

    [Fact]
    public void CommandModel_ExposesInitWithExpectedOptions()
    {
        var root = Program.CreateRootCommand();

        var init = Assert.Single(root.Subcommands, command => command.Name == "init");
        Assert.Contains(init.Options, option => option.Name == "--path");
        Assert.Contains(init.Options, option => option.Name == "--port");
        Assert.Contains(init.Options, option => option.Name == "--idle-timeout");
        Assert.Contains(init.Options, option => option.Name == "--wait");
        Assert.Contains(init.Options, option => option.Name == "--json");
        Assert.Empty(root.Parse(["init"]).Errors);
        Assert.Empty(root.Parse(["init", "--path", Root, "--wait", "--json"]).Errors);
    }

    [Fact]
    public async Task PathNotFound_ReturnsOneWithoutStarting()
    {
        var started = false;
        var (runtime, output, error) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()),
            (_, _, _, _) =>
            {
                started = true;
                return Task.FromResult(DetachedStartResult.Failed("unexpected"));
            },
            directoryExists: _ => false);

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, false, false), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(started);
        Assert.Empty(output.ToString());
        Assert.Contains("Path does not exist", error.ToString());
    }

    [Fact]
    public async Task NoWait_FreshStart_ReportsStartedOnStderrAndExitsZero()
    {
        var (runtime, output, error) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()),
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Started(Instance(), "C:\\logs\\repo.log")));

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, false, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("CodeContext started for", error.ToString());
        Assert.Contains("Indexing in background", error.ToString());
        Assert.Contains("C:\\logs\\repo.log", error.ToString());
    }

    [Fact]
    public async Task NoWait_ExistingExactRoot_ReportsAlreadyRunningWithoutCovers()
    {
        var (runtime, _, error) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()),
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Existing(Instance())));

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, false, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("already running for", error.ToString());
        Assert.DoesNotContain("it covers", error.ToString());
    }

    [Fact]
    public async Task NoWait_ExistingAncestorRoot_ReportsCoversRequestedPath()
    {
        var requested = Root + "\\src";
        var (runtime, _, error) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()),
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Existing(Instance())));

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(requested, null, 120, false, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("already running for", error.ToString());
        Assert.Contains($"it covers {requested}", error.ToString());
    }

    [Fact]
    public async Task Wait_ReachesReady_PrintsCountsAndExitsZero()
    {
        var handler = new RecordingHandler(_ => Json(Status("ready", fileCount: 12, nodeCount: 340)));
        var (runtime, _, error) = Runtime(
            handler,
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Existing(Instance())));

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, true, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains($"Index ready for {Root} (12 files, 340 nodes).", error.ToString());
    }

    [Fact]
    public async Task Wait_Timeout_ReturnsThreeAndPointsAtStatus()
    {
        var handler = new RecordingHandler(_ => Json(Status("indexing")));
        var (runtime, _, error) = Runtime(
            handler,
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Existing(Instance())),
            readinessTimeout: TimeSpan.Zero);

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, true, false), runtime, CancellationToken.None);

        Assert.Equal(3, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("did not become ready within 0 minutes", error.ToString());
        Assert.Contains("codecontext status --path", error.ToString());
    }

    [Fact]
    public async Task WaitTimeoutWithJson_StillEmitsInstanceRecordOnceBeforeTimingOut()
    {
        var handler = new RecordingHandler(_ => Json(Status("indexing")));
        var (runtime, output, error) = Runtime(
            handler,
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Started(Instance(), "C:\\logs\\repo.log")),
            readinessTimeout: TimeSpan.Zero);

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, true, true), runtime, CancellationToken.None);

        Assert.Equal(3, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(Root, document.RootElement.GetProperty("rootPath").GetString());
        Assert.Equal("id", document.RootElement.GetProperty("instanceId").GetString());
        Assert.Contains("did not become ready within 0 minutes", error.ToString());
    }

    [Fact]
    public async Task WaitWithInvalidStatus_ReturnsOne()
    {
        var handler = new RecordingHandler(_ => Json(Status("ready", contract: 2)));
        var (runtime, _, error) = Runtime(
            handler,
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Existing(Instance())));

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, true, false), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("contract mismatch", error.ToString());
    }

    [Fact]
    public async Task Json_WritesInstanceRecordToStdout()
    {
        var (runtime, output, _) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()),
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Started(Instance(), "C:\\logs\\repo.log")));

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, false, true), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(Root, document.RootElement.GetProperty("rootPath").GetString());
        Assert.Equal(7890, document.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("id", document.RootElement.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task StartupFailure_ReturnsFourWithoutWritingStdout()
    {
        var (runtime, output, error) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()),
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Failed("could not launch")));

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, null, 120, false, true), runtime, CancellationToken.None);

        Assert.Equal(4, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("could not launch", error.ToString());
    }

    [Fact]
    public async Task StartExplicitPortAndIdleTimeoutAreForwarded()
    {
        int? capturedPort = null;
        int capturedIdle = -1;
        var (runtime, _, _) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()),
            (_, port, idle, _) =>
            {
                capturedPort = port;
                capturedIdle = idle;
                return Task.FromResult(DetachedStartResult.Started(Instance()));
            });

        var exit = await InitCommandHandler.ExecuteAsync(
            new InitSettings(Root, 8080, 0, false, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(8080, capturedPort);
        Assert.Equal(0, capturedIdle);
    }

    private static (InitRuntime Runtime, StringWriter Output, StringWriter Error) Runtime(
        RecordingHandler handler,
        Func<string, int?, int, CancellationToken, Task<DetachedStartResult>>? start = null,
        TimeSpan? readinessTimeout = null,
        Func<string, bool>? directoryExists = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runtime = new InitRuntime(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
            start ?? ((_, _, _, _) => Task.FromResult(DetachedStartResult.Failed("unexpected start"))),
            output,
            error,
            TimeProvider.System,
            (_, _) => Task.CompletedTask,
            readinessTimeout ?? TimeSpan.FromMinutes(5),
            directoryExists ?? (_ => true));
        return (runtime, output, error);
    }

    private static InstanceRecord Instance() => new()
    {
        RootPath = Root,
        Port = 7890,
        Pid = 123,
        InstanceId = "id",
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static string Status(
        string indexingStatus,
        string instanceId = "id",
        string root = Root,
        int contract = 1,
        int fileCount = 0,
        int nodeCount = 0)
        => JsonSerializer.Serialize(new
        {
            system = new { instanceId },
            indexing = new { status = indexingStatus, rootPath = root },
            database = new { fileCount, nodeCount },
            api = new { contractVersion = contract },
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
