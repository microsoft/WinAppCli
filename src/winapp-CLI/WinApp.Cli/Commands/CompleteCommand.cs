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
        HelpName = "powershell|bash|zsh"
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

        // When the cursor is past the end of the text (e.g., PowerShell sends
        // position=12 for "winapp cert" which is 11 chars), the user has a trailing
        // space after the last token. Append a space so System.CommandLine knows to
        // complete the NEXT token rather than the current one.
        if (position > commandLine.Length)
        {
            commandLine += " ";
        }

        // Re-parse the user's command line using the root command,
        // then ask System.CommandLine for completions at that position.
        var rootCommand = parseResult.RootCommandResult.Command;
        var textToComplete = commandLine[..position];

        var completionParseResult = rootCommand.Parse(textToComplete);
        var completions = completionParseResult.GetCompletions(position);

        // Determine the partial word being typed (text after last space up to cursor)
        var lastSpaceIndex = textToComplete.LastIndexOf(' ');
        var currentWord = lastSpaceIndex >= 0 ? textToComplete[(lastSpaceIndex + 1)..] : textToComplete;

        // System.CommandLine uses substring matching by default. Apply prefix matching
        // for a more intuitive shell experience (e.g., "i" should match "init", not "sign").
        var filteredCompletions = completions
            .Where(c => c.Label.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase));

        // Only include options (starting with - or /) when the user has started typing
        // a prefix character, otherwise just show commands/arguments for cleaner completions.
        if (!currentWord.StartsWith('-') && !currentWord.StartsWith('/'))
        {
            filteredCompletions = filteredCompletions
                .Where(c => !c.Label.StartsWith('-') && !c.Label.StartsWith('/'));
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
            $completions = @(winapp complete --commandline "$commandAst" --position $cursorPosition 2>$null)
            if ($completions.Count -gt 0) {
                $completions | ForEach-Object {
                    $parts = $_ -split "`t", 2
                    $label = $parts[0]
                    $tooltip = if ($parts.Count -gt 1) { $parts[1] } else { $label }
                    [System.Management.Automation.CompletionResult]::new($label, $label, 'ParameterValue', $tooltip)
                }
            }
        }
        """;

    private static string GetBashScript() =>
        """
        _winapp_completions() {
            local IFS=$'\n'
            local completions
            completions=$(winapp complete --commandline "${COMP_LINE}" --position "${COMP_POINT}" 2>/dev/null)
            COMPREPLY=()
            while IFS= read -r line; do
                COMPREPLY+=("$line")
            done <<< "$completions"
        }
        complete -o default -F _winapp_completions winapp
        """;

    private static string GetZshScript() =>
        """
        _winapp() {
            local completions
            completions=("${(@f)$(winapp complete --commandline "${words[*]}" --position $CURSOR 2>/dev/null)}")
            compadd -a completions
        }
        compdef _winapp winapp
        """;
}
