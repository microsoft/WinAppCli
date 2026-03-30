// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace WinApp.Cli.Commands;

internal class CompleteCommand : Command, IShortDescription
{
    public string ShortDescription => "Output shell completion suggestions for use with Register-ArgumentCompleter";

    public static readonly Option<string> WordOption = new("--word")
    {
        Description = "The word being completed",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static readonly Option<string> CommandlineOption = new("--commandline")
    {
        Description = "The full command line text",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static readonly Option<int> PositionOption = new("--position")
    {
        Description = "The cursor position within the command line",
        Arity = ArgumentArity.ZeroOrOne
    };

    public CompleteCommand() : base("complete", "Output shell completion suggestions for a partial command line. Used with Register-ArgumentCompleter in PowerShell to enable tab completion.")
    {
        Options.Add(WordOption);
        Options.Add(CommandlineOption);
        Options.Add(PositionOption);
    }

    public class Handler : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var commandLine = parseResult.GetValue(CommandlineOption) ?? string.Empty;
            var position = parseResult.GetValue(PositionOption);

            // Navigate up the parse tree to obtain the root command
            var commandResult = parseResult.CommandResult;
            while (commandResult.Parent is CommandResult parent)
            {
                commandResult = parent;
            }
            var rootCommand = (RootCommand)commandResult.Command;

            // Strip the leading program name (e.g., "winapp ") from the command line
            // so that the root command can parse the remaining tokens for completion
            var firstSpaceIdx = commandLine.IndexOf(' ');
            string partialLine;
            int adjustedPosition;
            if (firstSpaceIdx >= 0)
            {
                partialLine = commandLine[(firstSpaceIdx + 1)..];
                adjustedPosition = Math.Max(0, position - (firstSpaceIdx + 1));
            }
            else
            {
                // Only the program name typed — return top-level subcommand completions
                partialLine = string.Empty;
                adjustedPosition = 0;
            }

            var completionResult = rootCommand.Parse(partialLine);
            var completions = completionResult.GetCompletions(adjustedPosition);

            var output = parseResult.InvocationConfiguration.Output;
            foreach (var completion in completions)
            {
                output.WriteLine(completion.Label);
            }

            return Task.FromResult(0);
        }
    }
}
