// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Completions;

namespace WinApp.Cli.Commands;

/// <summary>
/// Hidden command that provides shell tab-completion candidates.
/// Designed for use with shell-specific argument completers (PowerShell, bash, zsh).
/// 
/// Usage from a shell completer:
///   winapp complete --commandline "winapp cert " --position 12
///
/// To print shell registration scripts:
///   winapp complete --setup powershell
///   winapp complete --setup bash
///   winapp complete --setup zsh
/// </summary>
internal class CompleteCommand : Command, IShortDescription
{
    public string ShortDescription => "Provide shell tab-completion candidates";

    internal static readonly Option<string> CommandLineOption = new("--commandline")
    {
        Description = "The full command line text to complete"
    };

    internal static readonly Option<int> PositionOption = new("--position")
    {
        Description = "The cursor position within the command line"
    };

    internal static readonly Option<string?> SetupOption = new("--setup")
    {
        Description = "Print the shell registration script for the specified shell",
        HelpName = "powershell|pwsh|bash|zsh"
    };

    public CompleteCommand() : base("complete", "Provide shell tab-completion candidates. Used by shell argument completers to suggest commands, options, and values.")
    {
        Hidden = true;
        Options.Add(CommandLineOption);
        Options.Add(PositionOption);
        Options.Add(SetupOption);

        SetAction(Invoke);
    }

    private static int Invoke(ParseResult parseResult)
    {
        var setup = parseResult.GetValue(SetupOption);
        if (!string.IsNullOrEmpty(setup))
        {
            return PrintSetupScript(setup, parseResult.InvocationConfiguration.Output);
        }

        var commandLine = parseResult.GetValue(CommandLineOption);
        var position = parseResult.GetValue(PositionOption);

        if (string.IsNullOrEmpty(commandLine))
        {
            return 0;
        }

        // Clamp position to valid range
        if (position < 0)
        {
            position = 0;
        }

        // When the cursor is past the end of the text (e.g., PowerShell sends
        // position=12 for "winapp cert" which is 11 chars), the user has a trailing
        // space after the last token. Treat as exactly one trailing space.
        if (position > commandLine.Length)
        {
            position = commandLine.Length + 1;
            commandLine += " ";
        }

        // Strip the leading command name (e.g., "winapp" or "winapp-dev") from the
        // command line before parsing. System.CommandLine's GetCompletions() works
        // on the arguments only, and including the exe name can cause position
        // mismatches when the first token matches the root command name.
        var textToComplete = commandLine[..position];
        var firstSpaceIndex = textToComplete.IndexOf(' ');
        string argsText;
        int argsPosition;
        if (firstSpaceIndex >= 0)
        {
            argsText = textToComplete[(firstSpaceIndex + 1)..];
            argsPosition = argsText.Length;
        }
        else
        {
            // Just the command name, no args yet — complete subcommands
            argsText = "";
            argsPosition = 0;
        }

        // Re-parse the arguments using the root command,
        // then ask System.CommandLine for completions at that position.
        var rootCommand = parseResult.RootCommandResult.Command;

        // If "-- " (end-of-options followed by space) appears before the cursor,
        // everything after it is positional — don't offer option completions.
        var endOfOptionsMarker = argsText.IndexOf("-- ", StringComparison.Ordinal);
        if (endOfOptionsMarker >= 0 && endOfOptionsMarker + 3 <= argsPosition)
        {
            return 0;
        }

        var completionParseResult = rootCommand.Parse(argsText);
        var completions = completionParseResult.GetCompletions(argsPosition);

        // Build a set of alias names to exclude from completions.
        // System.CommandLine returns both primary names and aliases — we only want primary names.
        var currentCommand = completionParseResult.CommandResult.Command;
        var aliasNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in currentCommand.Subcommands)
        {
            // The first alias is the primary name (same as sub.Name); skip it, collect the rest
            foreach (var alias in sub.Aliases.Where(a => a != sub.Name))
            {
                aliasNames.Add(alias);
            }
        }
        foreach (var opt in currentCommand.Options)
        {
            foreach (var alias in opt.Aliases.Where(a => a != opt.Name))
            {
                aliasNames.Add(alias);
            }
        }
        // Also exclude short-form help aliases like -h, -?, /h, /?
        aliasNames.Add("-?");
        aliasNames.Add("-h");
        aliasNames.Add("/?");
        aliasNames.Add("/h");

        // Determine the partial word being typed (text after last space up to cursor)
        var lastSpaceIndex = argsText.LastIndexOf(' ');
        var currentWord = lastSpaceIndex >= 0 ? argsText[(lastSpaceIndex + 1)..] : argsText;

        // If the user is typing a path (starts with . or / or \), return nothing
        // and let the shell's built-in file completion handle it.
        if (currentWord.StartsWith('.') || currentWord.StartsWith('\\'))
        {
            return 0;
        }

        // System.CommandLine uses substring matching by default. Apply prefix matching
        // for a more intuitive shell experience (e.g., "i" should match "init", not "sign").
        // Also filter out aliases — only show the primary command/option name.
        var filteredCompletions = completions
            .Where(c => c.Label.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase))
            .Where(c => !aliasNames.Contains(c.Label));

        // When the user hasn't typed a dash prefix, prefer showing only commands/arguments.
        // But if hiding flags would leave zero results (e.g., "winapp init " has no
        // subcommands), show everything so the user discovers available options.
        if (!currentWord.StartsWith('-') && !currentWord.StartsWith('/'))
        {
            var commandsOnly = filteredCompletions
                .Where(c => !c.Label.StartsWith('-') && !c.Label.StartsWith('/'))
                .ToList();
            if (commandsOnly.Count > 0)
            {
                filteredCompletions = commandsOnly;
            }
        }

        var output = parseResult.InvocationConfiguration.Output;
        foreach (var item in filteredCompletions)
        {
            // Output "label\tdescription" so shell scripts can show rich completions.
            // If no description, just output the label.
            if (!string.IsNullOrEmpty(item.Detail))
            {
                output.Write(item.Label);
                output.Write('\t');
                output.WriteLine(item.Detail);
            }
            else
            {
                output.WriteLine(item.Label);
            }
        }

        return 0;
    }

    private static int PrintSetupScript(string shell, TextWriter output)
    {
        var script = shell.ToLowerInvariant() switch
        {
            "powershell" or "pwsh" => GetPowerShellScript(),
            "bash" => GetBashScript(),
            "zsh" => GetZshScript(),
            _ => null
        };

        if (script is null)
        {
            Console.Error.WriteLine($"Unknown shell: {shell}. Supported shells: powershell, bash, zsh");
            return 1;
        }

        output.Write(script);
        return 0;
    }

    private static string GetPowerShellScript() =>
        """
        Register-ArgumentCompleter -Native -CommandName winapp -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            if ($wordToComplete -like '.*' -or $wordToComplete -like '\*' -or $wordToComplete -like '[A-Z]:\*') { return }
            if ($wordToComplete -eq '--') {
                [System.Management.Automation.CompletionResult]::new('-- ', '-- ', 'Text', 'End of options')
                return
            }
            winapp complete "--commandline=$commandAst" "--position=$cursorPosition" 2>$null | ForEach-Object {
                $parts = $_ -split "`t", 2
                $label = $parts[0]
                $tooltip = if ($parts.Count -gt 1) { $parts[1] } else { $label }
                [System.Management.Automation.CompletionResult]::new($label, $label, 'ParameterValue', $tooltip)
            }
        }
        """;

    private static string GetBashScript() =>
        """
        _winapp_completions() {
            local IFS=$'\n'
            local completions
            completions=$(winapp complete "--commandline=${COMP_LINE}" "--position=${COMP_POINT}" 2>/dev/null)
            COMPREPLY=()
            while IFS= read -r line; do
                COMPREPLY+=("${line%%	*}")
            done <<< "$completions"
        }
        complete -o default -F _winapp_completions winapp
        """;

    private static string GetZshScript() =>
        """
        _winapp() {
            local completions
            completions=("${(@f)$(winapp complete "--commandline=${words[*]}" "--position=$CURSOR" 2>/dev/null)}")
            local labels=()
            for c in $completions; do
                labels+=("${c%%	*}")
            done
            compadd -a labels
        }
        compdef _winapp winapp
        """;
}
