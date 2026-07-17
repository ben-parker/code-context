
using System.CommandLine;
using CodeContext.Api;
using CodeContext.Api.Commands;

namespace CodeContext.Api;

public class Program
{
    static async Task<int> Main(string[] args)
    {
        Option<string> pathOption = new("--path", "-p")
        {
            Description = "The path to monitor.",
            DefaultValueFactory = parseResult => Directory.GetCurrentDirectory(),
        };

        Option<int?> portOption = new("--port")
        {
            Description = "The port for the web API. Defaults to the first free port from 7890.",
            DefaultValueFactory = parseResult => null,
        };

        Option<bool> mcpOption = new("--mcp")
        {
            Description = "Run as MCP server (stdio transport).",
            DefaultValueFactory = parseResult => false,
        };

        Option<bool> detachOption = new("--detach")
        {
            Description = "Start the service in the background and print its connection info as JSON.",
            DefaultValueFactory = parseResult => false,
        };

        Option<int> idleTimeoutOption = new("--idle-timeout")
        {
            Description = "Minutes without API activity before the instance shuts itself down (0 = never).",
            DefaultValueFactory = parseResult => 120,
        };

        Option<string?> logFileOption = new("--log-file")
        {
            Description = "Redirect output to this log file (used internally by --detach).",
            DefaultValueFactory = parseResult => null,
            Hidden = true,
        };

        Option<string?> instanceIdOption = new("--instance-id")
        {
            Description = "Instance identifier to register under (used internally by --detach).",
            DefaultValueFactory = parseResult => null,
            Hidden = true,
        };

        Option<bool> allOption = new("--all")
        {
            Description = "Stop all running instances.",
            DefaultValueFactory = parseResult => false,
        };

        Option<bool> jsonOption = new("--json")
        {
            Description = "Output machine-readable JSON.",
            DefaultValueFactory = parseResult => false,
        };

        Option<string?> instancePathOption = new("--path", "-p")
        {
            Description = "A path inside the target instance's watched tree. Defaults to the current directory.",
            DefaultValueFactory = parseResult => null,
        };

        var rootCommand = new RootCommand("CodeContext: A local service for code analysis.");

        var startCommand = new Command("start", "Starts the CodeContext service.")
        {
            pathOption,
            portOption,
            mcpOption,
            detachOption,
            idleTimeoutOption,
            logFileOption,
            instanceIdOption,
        };
        startCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            StartCommandHandler.ExecuteAsync(
                new StartSettings(
                    parseResult.GetValue(pathOption)!,
                    parseResult.GetValue(portOption),
                    parseResult.GetValue(mcpOption),
                    parseResult.GetValue(detachOption),
                    parseResult.GetValue(idleTimeoutOption),
                    parseResult.GetValue(logFileOption),
                    parseResult.GetValue(instanceIdOption)),
                ct));

        var stopCommand = new Command("stop", "Stops the instance watching the given (or current) directory.")
        {
            instancePathOption,
            allOption,
        };
        stopCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            StopCommandHandler.ExecuteAsync(
                parseResult.GetValue(instancePathOption),
                parseResult.GetValue(allOption),
                ct));

        var listCommand = new Command("list", "Lists running CodeContext instances.")
        {
            jsonOption,
        };
        listCommand.SetAction((ParseResult parseResult) =>
            ListCommandHandler.Execute(parseResult.GetValue(jsonOption)));

        var statusCommand = new Command("status", "Shows /api/status for the instance watching the given (or current) directory.")
        {
            instancePathOption,
        };
        statusCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            StatusCommandHandler.ExecuteAsync(parseResult.GetValue(instancePathOption), ct));

        rootCommand.Subcommands.Add(startCommand);
        rootCommand.Subcommands.Add(stopCommand);
        rootCommand.Subcommands.Add(listCommand);
        rootCommand.Subcommands.Add(statusCommand);

        return await rootCommand.Parse(args).InvokeAsync();
    }
}
