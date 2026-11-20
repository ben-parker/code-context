
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using CodeContext.Api;
using CodeContext.Api.Commands;
using CodeContext.Core.Services;

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

        Option<bool> noCacheOption = new("--no-cache")
        {
            Description = "Neither load nor write the saved index under <path>/.codecontext/cache (every start scans everything, as before 0.11). " +
                "Applies only to a new start: an instance already running keeps its setting.",
            DefaultValueFactory = parseResult => false,
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
            noCacheOption,
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
                    parseResult.GetValue(instanceIdOption),
                    parseResult.GetValue(noCacheOption)),
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
            "Queries one identifier. Uses the running instance, or starts one and waits for indexing (or answers at once " +
            "from the saved index, labelled cache, while it is re-checked). With --snapshot and no instance running, " +
            "answers in this process from the saved index when nothing it records changed since (labelled cache snapshot).");
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
        Option<int?> queryLimitOption = CreateLimitOption();
        Option<bool> queryExternalOption = new("--external")
        {
            Description = "Includes library symbols, which are left out of name matches and uses by default.",
        };
        Option<bool> queryStartOption = new("--start")
        {
            Description = "With no instance running, start one (the default unless CodeContext:QueryFromSnapshot is true).",
        };
        Option<bool> querySnapshotOption = new("--snapshot")
        {
            Description = "With no instance running, answer in this process from the saved index when, by file times, " +
                "nothing it was made from changed since (the files a scan walks and what each worker reported it read), " +
                "leaving nothing running; otherwise start one. CodeContext:QueryFromSnapshot=true " +
                "(environment CodeContext__QueryFromSnapshot=true) makes this the default.",
        };
        Option<string?> queryWithinOption = new("--within")
        {
            Description = "query hubs: only symbols under this repository-relative directory or file.",
        };
        Option<string?> queryTypeOption = new("--type")
        {
            Description = "query hubs: only symbols of this type (Class, Method, Interface, ...).",
        };
        foreach (var option in new Option[]
                 {
                     queryWithinOption,
                     queryTypeOption,
                     queryPathOption,
                     queryDepthOption,
                     queryTestsOption,
                     queryRelationOption,
                     queryExactOption,
                     queryJsonOption,
                     queryHumanOption,
                     queryLimitOption,
                     queryExternalOption,
                     queryStartOption,
                     querySnapshotOption,
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
        queryCommand.Options.Add(queryLimitOption);
        queryCommand.Options.Add(queryExternalOption);
        queryCommand.Options.Add(queryStartOption);
        queryCommand.Options.Add(querySnapshotOption);
        queryCommand.Options.Add(queryWithinOption);
        queryCommand.Options.Add(queryTypeOption);
        queryCommand.Options.Add(queryHelpOption);

        // The options given on the command line, not defaulted: a query refuses one it does not read.
        IReadOnlyCollection<string> Given(ParseResult parseResult)
            => new Option[] { queryDepthOption, queryExactOption, queryLimitOption, queryExternalOption, queryWithinOption, queryTypeOption }
                .Where(option => parseResult.GetResult(option) is { Implicit: false })
                .Select(option => option.Name)
                .ToList();

        QuerySettings Settings(ParseResult parseResult, IReadOnlyList<string> identifiers, bool multi, GraphQuerySettings? graph)
            => new(
                identifiers,
                parseResult.GetValue(queryPathOption)!,
                parseResult.GetValue(queryDepthOption),
                parseResult.GetValue(queryTestsOption),
                parseResult.GetValue(queryRelationOption),
                parseResult.GetValue(queryExactOption),
                parseResult.GetValue(queryJsonOption),
                multi,
                parseResult.GetValue(queryHumanOption),
                parseResult.GetValue(queryLimitOption),
                parseResult.GetValue(queryExternalOption),
                parseResult.GetValue(queryStartOption),
                parseResult.GetValue(querySnapshotOption),
                graph,
                Given(parseResult));

        GraphQuerySettings Graph(ParseResult parseResult, string query)
            => new(
                query,
                parseResult.GetResult(queryDepthOption) is { Implicit: false } ? parseResult.GetValue(queryDepthOption) : null,
                parseResult.GetValue(queryWithinOption),
                parseResult.GetValue(queryTypeOption));
        queryCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            QueryCommandHandler.ExecuteAsync(
                Settings(parseResult, [parseResult.GetValue(queryIdentifierArgument)!], multi: false, graph: null), ct));

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
                Settings(parseResult, parseResult.GetValue(queryIdentifiersArgument)!, multi: true, graph: null), ct));
        queryCommand.Subcommands.Add(multiQueryCommand);

        // Whole-graph queries. A symbol named like one of these subcommands is queried after `--`: `query -- path`.
        var pathQueryCommand = new Command(
            "path",
            "Shortest chain of recorded relationships from one symbol to another, through dispatch to implementations. " +
            $"--depth is the most hops searched (1-{GraphQueryLimits.MaxDepth}, default {GraphQueryLimits.DefaultPathDepth}); " +
            "--tests lets the path pass through test files; --relation limits the kinds followed.");
        Argument<string> pathFromArgument = new("from") { Description = "Where the path starts: identifier, exact or qualified name, or file path." };
        Argument<string> pathToArgument = new("to") { Description = "Where the path ends." };
        pathQueryCommand.Arguments.Add(pathFromArgument);
        pathQueryCommand.Arguments.Add(pathToArgument);
        pathQueryCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            QueryCommandHandler.ExecuteAsync(
                Settings(parseResult, [parseResult.GetValue(pathFromArgument)!, parseResult.GetValue(pathToArgument)!],
                    multi: false, Graph(parseResult, GraphQueryParameters.PathQuery)), ct));

        var affectedQueryCommand = new Command(
            "affected",
            "What depends on a symbol or file, transitively, nearest first: what could break if it changes. " +
            $"--depth is the most hops followed (1-{GraphQueryLimits.MaxDepth}, default {GraphQueryLimits.DefaultAffectedDepth}); " +
            $"--limit the entries listed (default {GraphQueryLimits.DefaultLimit}); --tests lists test-file dependents.");
        Argument<string> affectedIdentifierArgument = new("identifier") { Description = "The symbol or file that would change." };
        affectedQueryCommand.Arguments.Add(affectedIdentifierArgument);
        affectedQueryCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            QueryCommandHandler.ExecuteAsync(
                Settings(parseResult, [parseResult.GetValue(affectedIdentifierArgument)!],
                    multi: false, Graph(parseResult, GraphQueryParameters.AffectedQuery)), ct));

        var hubsQueryCommand = new Command(
            "hubs",
            "The symbols with the most distinct direct dependents. --within DIR, --type TYPE, " +
            $"--limit N (default {GraphQueryLimits.DefaultLimit}), --tests and --external widen or narrow what is ranked.");
        hubsQueryCommand.SetAction((ParseResult parseResult, CancellationToken ct) =>
            QueryCommandHandler.ExecuteAsync(
                Settings(parseResult, [], multi: false, Graph(parseResult, GraphQueryParameters.HubsQuery)), ct));

        foreach (var command in new[] { pathQueryCommand, affectedQueryCommand, hubsQueryCommand })
        {
            var help = new HelpOption();
            help.Action = new QueryHelpAction(queryIdentifierArgument);
            command.Options.Add(help);
            queryCommand.Subcommands.Add(command);
        }

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
            // A value that is not a number is the parser's error to report; reading it here would throw.
            if (TokenInt(result) is < 0)
                result.AddError("Depth must be non-negative.");
        });
        return option;
    }

    private static Option<int?> CreateLimitOption()
    {
        Option<int?> option = new("--limit")
        {
            Description = "Maximum entries per relationship list "
                + $"(1-{QueryCommandHandler.MaxRelationshipLimit}, default 10).",
        };
        option.Validators.Add(result =>
        {
            if (TokenInt(result) is < 1 or > QueryCommandHandler.MaxRelationshipLimit)
                result.AddError($"Limit must be between 1 and {QueryCommandHandler.MaxRelationshipLimit}.");
        });
        return option;
    }

    /// <summary>The option's value as given, when it is an integer; null when absent or not a number.</summary>
    private static int? TokenInt(System.CommandLine.Parsing.OptionResult result)
        => result.Tokens.Count > 0 && int.TryParse(result.Tokens[^1].Value, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

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
        singleIdentifier.Hidden = command.Name != "query";
        // List only the options this query reads, described as it reads them; the options are shared, so put them back.
        var mode = command.Name is "query" or "multi" ? "context" : command.Name;
        var queryCommand = command.Name == "query" ? command : command.Parents.OfType<Command>().First();
        var restore = new List<Action>();
        foreach (var option in queryCommand.Options)
        {
            if (!QueryCommandHandler.Reads(mode, option.Name))
            {
                var hidden = option.Hidden;
                option.Hidden = true;
                restore.Add(() => option.Hidden = hidden);
            }
            else if (QueryCommandHandler.GraphOptionDescription(mode, option.Name) is { } description)
            {
                var previous = option.Description;
                option.Description = description;
                restore.Add(() => option.Description = previous);
                if (option is Option<int> { DefaultValueFactory: { } factory } withDefault)
                {
                    // The graph queries' defaults are their own, given in the description.
                    withDefault.DefaultValueFactory = null;
                    restore.Add(() => withDefault.DefaultValueFactory = factory);
                }
            }
        }
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
            var usage = command.Name switch
            {
                "multi" => "codecontext query multi <identifier>... [options]",
                "path" => "codecontext query path <from> <to> [options]",
                "affected" => "codecontext query affected <identifier> [options]",
                "hubs" => "codecontext query hubs [options]",
                _ => "codecontext query <identifier> [options]   (a symbol named path, affected, hubs or multi: query -- <name>)",
            };
            output.Write(ReplaceUsage(rendered.ToString(), usage));
            return 0;
        }
        finally
        {
            singleIdentifier.Hidden = previousHidden;
            foreach (var undo in restore) undo();
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
