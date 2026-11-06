using System.Net;
using System.Text;
using System.Text.Json;
using CodeContext.Api.Commands;
using CodeContext.Core.Instances;

namespace CodeContext.Core.Tests.Commands;

/// <summary>
/// <c>acceptCached</c>: <c>query</c> takes an instance that answers from a saved index not yet verified;
/// <c>init --wait</c> and every other waiter still wait for <c>ready</c>, which still means scanned and committed.
/// </summary>
public class ReadinessWaiterTests
{
    private const string Root = "C:\\repo";

    private static string Status(string status, bool? servingCached)
        => JsonSerializer.Serialize(new
        {
            system = new { instanceId = "id" },
            indexing = servingCached is { } serving
                ? (object)new { status, rootPath = Root, cache = new { state = serving ? "verifying" : "verified", servingCached = serving } }
                : new { status, rootPath = Root },
            api = new { contractVersion = 1 },
        });

    private static Task<ReadinessOutcome> WaitAsync(string body, bool acceptCached)
    {
        var http = new HttpClient(new Respond(body));
        var instance = new InstanceRecord { RootPath = Root, Port = 7890, Pid = 1, InstanceId = "id", StartedAt = DateTimeOffset.UtcNow };
        return ReadinessWaiter.WaitUntilReadyAsync(instance, Root, http, new StringWriter(), TimeProvider.System,
            (_, _) => Task.CompletedTask, TimeSpan.Zero, CancellationToken.None, acceptCached);
    }

    [Theory]
    [InlineData("scanning", true, true, "Ready")]     // query: cached answers are answers
    [InlineData("error", true, true, "Ready")]        // unverified: still served, labelled
    [InlineData("scanning", true, false, "Timeout")]  // init --wait: only ready counts
    [InlineData("scanning", false, true, "Timeout")]  // nothing cached is served
    [InlineData("scanning", null, true, "Timeout")]   // no cache object at all (0.10 behaviour)
    [InlineData("ready", null, false, "Ready")]
    [InlineData("ready", false, true, "Ready")]
    public async Task AnswerableWhenReady_OrServingCachedForQuery(string status, bool? servingCached, bool acceptCached, string expected)
        => Assert.Equal(expected, (await WaitAsync(Status(status, servingCached), acceptCached)).Result.ToString());

    [Fact]
    public async Task InitWait_DoesNotAcceptCachedAnswers()
    {
        var runtime = new InitRuntime(
            new HttpClient(new Respond(Status("scanning", servingCached: true))),
            (_, _, _, _) => Task.FromResult(DetachedStartResult.Existing(
                new InstanceRecord { RootPath = Root, Port = 7890, Pid = 1, InstanceId = "id", StartedAt = DateTimeOffset.UtcNow })),
            new StringWriter(), new StringWriter(), TimeProvider.System, (_, _) => Task.CompletedTask, TimeSpan.Zero, _ => true);
        Assert.Equal(3, await InitCommandHandler.ExecuteAsync(new InitSettings(Root, null, 120, Wait: true, Json: false), runtime, CancellationToken.None));
    }

    private sealed class Respond(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
