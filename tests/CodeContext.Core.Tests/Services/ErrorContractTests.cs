using System.Text.Json;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Mcp;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Byte-for-byte contract tests for the JSON error envelopes emitted by the MCP tools
/// (<see cref="CodeContextTools"/>) and the REST endpoints. Phase 3a replaces the anonymous
/// objects behind these shapes with declared, source-generated DTOs; the wire output must be
/// identical before and after. The MCP tests exercise the real tool methods; the REST tests
/// serialize the endpoint DTOs through <see cref="CodeContextJsonContext"/>, which carries the
/// same camelCase + WhenWritingNull options the ASP.NET host applies.
/// </summary>
public class ErrorContractTests
{
    private static IContextService ThrowingContextService(string message)
    {
        var service = Substitute.For<IContextService>();
        service.GetCompactContextAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<bool>(), Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns<Task<CompactContextResponse>>(_ => throw new InvalidOperationException(message));
        service.GetMultipleCompactContextAsync(Arg.Any<MultiContextRequest>())
            .Returns<Task<List<CompactContextResponse>>>(_ => throw new InvalidOperationException(message));
        return service;
    }

    [Fact]
    public async Task McpGetContext_ErrorEnvelope_IsByteIdentical()
    {
        var service = ThrowingContextService("boom-ctx");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CodeContextTools.GetContext(service, "Foo"));

        Assert.Equal(
            "{\"error\":{\"code\":\"CONTEXT_ERROR\",\"message\":\"boom-ctx\",\"details\":{\"identifier\":\"Foo\",\"type\":null,\"depth\":1,\"includeTests\":false,\"includeContent\":false,\"exact\":null,\"includeRelated\":false,\"includeMetrics\":false,\"maxMatches\":5,\"maxRelationships\":10,\"maxCallSites\":3,\"maxTestFiles\":5,\"maxTestMethods\":5,\"expandAmbiguous\":false,\"containingType\":null,\"namespace\":null,\"signature\":null,\"sourceFile\":null,\"relation\":null,\"view\":\"Compact\"}}}",
            ex.Message);
    }

    [Fact]
    public async Task McpGetMultiContext_ErrorEnvelope_IsByteIdentical()
    {
        var service = ThrowingContextService("boom-multi");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CodeContextTools.GetMultiContext(service, new[] { "A", "B" }));

        Assert.Equal(
            "{\"error\":{\"code\":\"MULTI_CONTEXT_ERROR\",\"message\":\"boom-multi\",\"details\":{\"Identifiers\":[\"A\",\"B\"],\"Type\":null,\"Depth\":1,\"View\":\"Compact\",\"IncludeTests\":false,\"IncludeContent\":false,\"Exact\":null,\"IncludeRelated\":false,\"IncludeMetrics\":false,\"MaxMatches\":5,\"MaxRelationships\":3,\"MaxCallSites\":3,\"MaxTestFiles\":5,\"MaxTestMethods\":5,\"ExpandAmbiguous\":false,\"ContainingType\":null,\"Namespace\":null,\"Signature\":null,\"SourceFile\":null,\"RelationshipTypes\":[]}}}",
            ex.Message);
    }

    [Fact]
    public async Task McpGetStatus_ErrorEnvelope_IsByteIdentical()
    {
        var status = Substitute.For<IStatusService>();
        status.GetStatusAsync().Returns<Task<StatusResponseDto>>(
            _ => throw new InvalidOperationException("boom-status"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CodeContextTools.GetStatus(status));

        Assert.Equal(
            "{\"error\":{\"code\":\"STATUS_ERROR\",\"message\":\"boom-status\"}}",
            ex.Message);
    }

    [Fact]
    public void RestContextError_SerializesByteIdentical()
    {
        var dto = new ContextErrorResponse(new ContextErrorBody(
            "CONTEXT_ERROR", "boom-ctx",
            new ContextErrorDetails(
                "Foo", null, 1, false, false, null, false, false, 5, 10, 3, 5, 5,
                false, null, null, null, null, null, "compact")));

        var json = JsonSerializer.Serialize(dto, CodeContextJsonContext.Default.ContextErrorResponse);

        Assert.Equal(
            "{\"error\":{\"code\":\"CONTEXT_ERROR\",\"message\":\"boom-ctx\",\"details\":{\"identifier\":\"Foo\",\"depth\":1,\"includeTests\":false,\"includeContent\":false,\"includeRelated\":false,\"includeMetrics\":false,\"maxMatches\":5,\"maxRelationships\":10,\"maxCallSites\":3,\"maxTestFiles\":5,\"maxTestMethods\":5,\"expandAmbiguous\":false,\"view\":\"compact\"}}}",
            json);
    }

    [Fact]
    public void RestMultiContextError_SerializesByteIdentical()
    {
        var dto = new MultiContextErrorResponse(new MultiContextErrorBody(
            "MULTI_CONTEXT_ERROR", "boom-multi",
            new MultiContextRequest { Identifiers = new() { "A", "B" } }));

        var json = JsonSerializer.Serialize(dto, CodeContextJsonContext.Default.MultiContextErrorResponse);

        Assert.Equal(
            "{\"error\":{\"code\":\"MULTI_CONTEXT_ERROR\",\"message\":\"boom-multi\",\"details\":{\"identifiers\":[\"A\",\"B\"],\"depth\":1,\"view\":\"Compact\",\"includeTests\":false,\"includeContent\":false,\"includeRelated\":false,\"includeMetrics\":false,\"maxMatches\":5,\"maxRelationships\":3,\"maxCallSites\":3,\"maxTestFiles\":5,\"maxTestMethods\":5,\"expandAmbiguous\":false,\"relationshipTypes\":[]}}}",
            json);
    }

    [Fact]
    public void RestSimpleError_SerializesByteIdentical()
    {
        var dto = new ApiErrorResponse(new ApiError("SCAN_IN_PROGRESS", "A scan is already running."));

        var json = JsonSerializer.Serialize(dto, CodeContextJsonContext.Default.ApiErrorResponse);

        Assert.Equal(
            "{\"error\":{\"code\":\"SCAN_IN_PROGRESS\",\"message\":\"A scan is already running.\"}}",
            json);
    }

    [Fact]
    public void RestRefreshError_WithPath_SerializesByteIdentical()
    {
        var dto = new RefreshErrorResponse(new RefreshErrorBody(
            "REFRESH_ERROR", "boom", new RefreshErrorDetails("x/y.cs")));

        var json = JsonSerializer.Serialize(dto, CodeContextJsonContext.Default.RefreshErrorResponse);

        Assert.Equal(
            "{\"error\":{\"code\":\"REFRESH_ERROR\",\"message\":\"boom\",\"details\":{\"path\":\"x/y.cs\"}}}",
            json);
    }

    [Fact]
    public void RestRefreshError_NullPath_OmitsPath()
    {
        var dto = new RefreshErrorResponse(new RefreshErrorBody(
            "REFRESH_ERROR", "boom", new RefreshErrorDetails(null)));

        var json = JsonSerializer.Serialize(dto, CodeContextJsonContext.Default.RefreshErrorResponse);

        Assert.Equal(
            "{\"error\":{\"code\":\"REFRESH_ERROR\",\"message\":\"boom\",\"details\":{}}}",
            json);
    }
}
