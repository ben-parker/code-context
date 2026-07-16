using Microsoft.Extensions.DependencyInjection;

namespace CodeContext.Mcp;

public static class ProgramHelpers
{
    public static void AddMcpServer(IServiceCollection services)
    {
        services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<CodeContextTools>();
    }
}
