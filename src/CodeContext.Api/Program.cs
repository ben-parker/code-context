
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using CodeContext.Api;
using CodeContext.Api.Commands;

namespace CodeContext.Api;

public class Program
{
    public static Task<int> Main(string[] args)
        => CreateRootCommand().Parse(args).InvokeAsync();

    public static RootCommand CreateRootCommand()
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

        Option<bool> initWaitOption = new("--wait")
        {
            Description = "Wait (up to 5 minutes) for the initial scan to reach ready before returning.",
            DefaultValueFactory = parseResult => false,
        };

        var initCommand = new Command(
            "init",
            "Pre-warms the index: starts the background service and scan before you need it. " +
            "Exit codes: 0 running, 1 argument/path or instance-validation error, " +
            "3 --wait timeout, 4 startup failure.")
        {
            pathOption,
            portOption,
            idleTimeoutOption,
            initWaitOption,
            jsonOption,
        };
        initCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            InitCommandHandler.ExecuteAsync(
                new InitSettings(
                    parseResult.GetValue(pathOption)!,
                    parseResult.GetValue(portOption),
                    parseResult.GetValue(idleTimeoutOption),
                    parseResult.GetValue(initWaitOption),
                    parseResult.GetValue(jsonOption)),
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

        var queryCommand = new Command(
            "query",
            "Discovers or starts an instance, waits for indexing, and queries one identifier.");
        Argument<string> queryIdentifierArgument = new("identifier")
        {
            Description = "A symbol name, canonical identifier, or source file path.",
        };
        Option<string> queryPathOption = CreateQueryPathOption();
        Option<int> queryDepthOption = CreateDepthOption();
        Option<bool> queryTestsOption = CreateTestsOption();
        Option<string?> queryRelationOption = CreateRelationOption();
        Option<bool> queryExactOption = CreateExactOption();
        Option<bool> queryJsonOption = CreateQueryJsonOption();
        Option<bool> queryHumanOption = CreateQueryHumanOption();
        foreach (var option in new Option[]
                 {
                     queryPathOption,
                     queryDepthOption,
                     queryTestsOption,
                     queryRelationOption,
                     queryExactOption,
                     queryJsonOption,
                     queryHumanOption,
                 })
        {
            option.Recursive = true;
        }

        var queryHelpOption = new HelpOption();
        queryHelpOption.Action = new QueryHelpAction(queryIdentifierArgument);
        queryCommand.Arguments.Add(queryIdentifierArgument);
        queryCommand.Options.Add(queryPathOption);
        queryCommand.Options.Add(queryDepthOption);
        queryCommand.Options.Add(queryTestsOption);
        queryCommand.Options.Add(queryRelationOption);
        queryCommand.Options.Add(queryExactOption);
        queryCommand.Options.Add(queryJsonOption);
        queryCommand.Options.Add(queryHumanOption);
        queryCommand.Options.Add(queryHelpOption);
        queryCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            QueryCommandHandler.ExecuteAsync(
                new QuerySettings(
                    [parseResult.GetValue(queryIdentifierArgument)!],
                    parseResult.GetValue(queryPathOption)!,
                    parseResult.GetValue(queryDepthOption),
                    parseResult.GetValue(queryTestsOption),
                    parseResult.GetValue(queryRelationOption),
                    parseResult.GetValue(queryExactOption),
                    parseResult.GetValue(queryJsonOption),
                    Multi: false,
                    parseResult.GetValue(queryHumanOption)),
                ct));

        var multiQueryCommand = new Command(
            "multi",
            "Queries multiple identifiers in one API round trip, preserving order and duplicates.");
        Argument<string[]> queryIdentifiersArgument = new("identifier")
        {
            Description = "One or more symbol names, canonical identifiers, or source file paths.",
            Arity = ArgumentArity.OneOrMore,
        };
        multiQueryCommand.Arguments.Add(queryIdentifiersArgument);
        var multiHelpOption = new HelpOption();
        multiHelpOption.Action = new QueryHelpAction(queryIdentifierArgument);
        multiQueryCommand.Options.Add(multiHelpOption);
        multiQueryCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            QueryCommandHandler.ExecuteAsync(
                new QuerySettings(
                    parseResult.GetValue(queryIdentifiersArgument)!,
                    parseResult.GetValue(queryPathOption)!,
                    parseResult.GetValue(queryDepthOption),
                    parseResult.GetValue(queryTestsOption),
                    parseResult.GetValue(queryRelationOption),
                    parseResult.GetValue(queryExactOption),
                    parseResult.GetValue(queryJsonOption),
                    Multi: true,
                    parseResult.GetValue(queryHumanOption)),
                ct));
        queryCommand.Subcommands.Add(multiQueryCommand);

        rootCommand.Subcommands.Add(startCommand);
        rootCommand.Subcommands.Add(initCommand);
        rootCommand.Subcommands.Add(stopCommand);
        rootCommand.Subcommands.Add(listCommand);
        rootCommand.Subcommands.Add(statusCommand);
        rootCommand.Subcommands.Add(queryCommand);

        return rootCommand;
    }

    private static Option<string> CreateQueryPathOption()
        => new("--path", "-p")
        {
            Description = "A path inside the repository to query. Defaults to the current directory.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };

    private static Option<int> CreateDepthOption()
    {
        Option<int> option = new("--depth")
        {
            Description = "Relationship traversal depth (non-negative).",
            DefaultValueFactory = _ => 1,
        };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
                result.AddError("Depth must be non-negative.");
        });
        return option;
    }

    private static Option<bool> CreateTestsOption()
        => new("--tests")
        {
            Description = "Include static test evidence (omitted by default).",
            DefaultValueFactory = _ => false,
        };

    private static Option<string?> CreateRelationOption()
        => new("--relation")
        {
            Description = "Comma-separated relationship kinds for uses/used-by filtering.",
            DefaultValueFactory = _ => null,
        };

    private static Option<bool> CreateExactOption()
        => new("--exact")
        {
            Description = "Require an exact identifier match.",
            DefaultValueFactory = _ => false,
        };

    private static Option<bool> CreateQueryJsonOption()
        => new("--json")
        {
            Description = "Write the exact compact API response to stdout.",
            DefaultValueFactory = _ => false,
        };

    private static Option<bool> CreateQueryHumanOption()
        => new("--human")
        {
            Description = "Write expanded human-readable output instead of compact agent text.",
            DefaultValueFactory = _ => false,
        };
}

internal sealed class QueryHelpAction(Argument<string> singleIdentifier) : SynchronousCommandLineAction
{
    public override bool Terminating => true;
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        var previousHidden = singleIdentifier.Hidden;
        singleIdentifier.Hidden = command.Name == "multi";
        try
        {
            using var rendered = new StringWriter();
            var invocation = parseResult.InvocationConfiguration;
            var output = invocation.Output;
            invocation.Output = rendered;
            try
            {
                new HelpAction().Invoke(parseResult);
            }
            finally
            {
                invocation.Output = output;
            }
            var usage = command.Name == "multi"
                ? "codecontext query multi <identifier>... [options]"
                : "codecontext query <identifier> [options]";
            output.Write(ReplaceUsage(rendered.ToString(), usage));
            return 0;
        }
        finally
        {
            singleIdentifier.Hidden = previousHidden;
        }
    }

    private static string ReplaceUsage(string help, string usage)
    {
        var newline = help.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var usageStart = help.IndexOf($"Usage:{newline}", StringComparison.Ordinal);
        if (usageStart < 0) return help;
        var usageEnd = help.IndexOf(newline + newline, usageStart, StringComparison.Ordinal);
        if (usageEnd < 0) return help;
        return help[..usageStart]
            + $"Usage:{newline}  {usage}{newline}{newline}"
            + help[(usageEnd + (newline.Length * 2))..];
    }
}
