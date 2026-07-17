using Microsoft.Extensions.DependencyInjection;

namespace CodeContext.Mcp;

public static class ProgramHelpers
{
    public static void AddMcpServer(IServiceCollection services)
    {
        // Manual, reflection-free tool registration (AOT-safe): explicit list/call handlers
        // over McpToolCatalog instead of the attribute-discovery .WithTools<CodeContextTools>().
        services.AddMcpServer()
            .WithStdioServerTransport()
            .WithListToolsHandler((_, _) =>
                ValueTask.FromResult(McpToolCatalog.ListTools()))
            .WithCallToolHandler((context, cancellationToken) =>
                McpToolCatalog.CallAsync(context, cancellationToken));
    }
}
