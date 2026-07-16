using CodeContext.Api;
using CodeContext.Api.Endpoints;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

public class CodeContextEndpointTests
{
    [Fact]
    public async Task CompleteContext_DefaultsToCompactView()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompactContextAsync(
                "Target", null, 1, false, false, null, false, false, 5, 10, false)
            .Returns(new CompactContextResponse());
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().GetAsync("/api/context/complete?identifier=Target");
        var json = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"view\":\"compact\"", json);
        await service.Received(1).GetCompactContextAsync(
            "Target", null, 1, false, false, null, false, false, 5, 10, false);
    }

    [Fact]
    public async Task CompleteContext_FullViewIsExplicit()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompleteContextAsync(
                "Target", null, 1, false, false, false, false, false)
            .Returns(new CompleteContextResponse());
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().GetAsync(
            "/api/context/complete?identifier=Target&view=full");

        response.EnsureSuccessStatusCode();
        await service.Received(1).GetCompleteContextAsync(
            "Target", null, 1, false, false, false, false, false);
    }

    private static async Task<WebApplication> StartAppAsync(IContextService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(Substitute.For<IStatusService>());
        builder.Services.AddSingleton(Substitute.For<IIndexCoordinator>());
        builder.Services.AddSingleton(Substitute.For<ILanguageWorkerService>());
        builder.Services.AddSingleton(Options.Create(new CodeContextOptions()));
        ProgramHelpers.AddRestApi(builder.Services);
        var app = builder.Build();
        app.MapCodeContextEndpoints();
        await app.StartAsync();
        return app;
    }
}
