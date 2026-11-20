using CodeContext.Core.Memory;
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
            .WithCallToolHandler(async (context, cancellationToken) =>
            {
                // A tool call is work: the quiet-time memory trim waits for it to end.
                using var activity = context.Services?.GetService<QuietTimeTrim>()?.BeginActivity();
                return await McpToolCatalog.CallAsync(context, cancellationToken);
            });
    }
}
