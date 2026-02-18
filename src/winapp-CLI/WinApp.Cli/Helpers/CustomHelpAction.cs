// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Spectre.Console;
using Spectre.Console.Rendering;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Custom help action that renders the root command help screen with
/// categorized command tables and short descriptions, styled with Spectre.Console.
/// For non-root commands, delegates to the default System.CommandLine help rendering.
/// </summary>
internal sealed class CustomHelpAction : SynchronousCommandLineAction
{
    private readonly (string Category, string[] CommandNames)[] _categories;
    private readonly Command _targetCommand;

    public override bool Terminating => true;

    /// <summary>
    /// Gets the flat set of all command names covered by the help categories.
    /// </summary>
    internal IReadOnlyCollection<string> CategorizedCommandNames =>
        _categories.SelectMany(c => c.CommandNames).ToArray();

    /// <summary>
    /// Creates a new custom help action with the specified command categories.
    /// </summary>
    /// <param name="targetCommand">The command this custom help applies to (e.g. root command).</param>
    /// <param name="categories">Ordered list of (category name, command names) tuples.</param>
    public CustomHelpAction(Command targetCommand, params (string Category, string[] CommandNames)[] categories)
    {
        _targetCommand = targetCommand;
        _categories = categories;
    }

    public override int Invoke(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;

        // Only use custom rendering for the target command; fall back to default for others
        if (command != _targetCommand)
        {
            var defaultHelp = new HelpAction();
            return defaultHelp.Invoke(parseResult);
        }

        var output = parseResult.InvocationConfiguration.Output;
        var useColor = BannerHelper.UseEmoji;
        var commandPath = GetCommandPath(command);

        // Create a Spectre console that writes to the invocation output writer
        var ansiSupport = Console.IsOutputRedirected ? AnsiSupport.No : AnsiSupport.Detect;
        var terminalWidth = Math.Max(40, AnsiConsole.Profile.Width);
        var spectreConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = ansiSupport
        });
        spectreConsole.Profile.Width = terminalWidth;

        // Description
        if (!string.IsNullOrEmpty(command.Description))
        {
            spectreConsole.WriteLine();
            WriteIndented(spectreConsole, new Markup($"[dim]{Markup.Escape(command.Description)}[/]"));
        }

        // Usage
        spectreConsole.WriteLine();
        spectreConsole.MarkupLine($" Usage: [white]{commandPath} <command>[/] [dim][[options]][/]");
        spectreConsole.MarkupLine($"        [white]{commandPath} --help[/]");
        spectreConsole.WriteLine();
        spectreConsole.MarkupLine($" Use '[white]{commandPath} <command> --help[/]' to get detailed help for any command.");

        // Build a lookup from command name -> Command object
        var subcommandLookup = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in command.Subcommands)
        {
            subcommandLookup[sub.Name] = sub;
        }

        // Render each category
        foreach (var (category, commandNames) in _categories)
        {
            spectreConsole.WriteLine();

            var grid = new Grid();
            grid.AddColumn(new GridColumn().PadLeft(1).PadRight(2).NoWrap());
            grid.AddColumn(new GridColumn());

            foreach (var name in commandNames)
            {
                if (subcommandLookup.TryGetValue(name, out var sub))
                {
                    var desc = sub is IShortDescription sd ? sd.ShortDescription : sub.Description ?? "";
                    grid.AddRow(
                        $"[white]{Markup.Escape(sub.Name)}[/]",
                        Markup.Escape(desc)
                    );
                }
            }

            var panel = new Panel(grid);
            panel.Border = BoxBorder.Rounded;
            panel.BorderStyle = new Style(Color.Grey35);
            panel.Header = new PanelHeader(
                useColor ? $"[rgb(99,141,255)]{Markup.Escape(category)}[/]" : $"[bold]{Markup.Escape(category)}[/]");
            panel.Padding = new Padding(1, 0, 1, 0);

            WriteIndented(spectreConsole, panel);
        }

        // Global options
        spectreConsole.WriteLine();

        var optGrid = new Grid();
        optGrid.AddColumn(new GridColumn().PadLeft(1).PadRight(2).NoWrap());
        optGrid.AddColumn(new GridColumn());

        foreach (var option in command.Options)
        {
            if (option.Hidden)
            {
                continue;
            }

            // Build alias string: include the canonical name first, then additional aliases
            var allAliases = new List<string> { option.Name };
            foreach (var alias in option.Aliases)
            {
                if (alias != option.Name)
                {
                    allAliases.Add(alias);
                }
            }
            var aliasText = string.Join(", ", allAliases);
            var desc = option.Description ?? "";

            optGrid.AddRow(
                $"[white]{Markup.Escape(aliasText)}[/]",
                Markup.Escape(desc)
            );
        }

        var optPanel = new Panel(optGrid);
        optPanel.Border = BoxBorder.Rounded;
        optPanel.BorderStyle = new Style(Color.Grey35);
        optPanel.Header = new PanelHeader(
            useColor ? "[rgb(99,141,255)]Options[/]" : "[bold]Options[/]");
        optPanel.Padding = new Padding(1, 0, 1, 0);

        WriteIndented(spectreConsole, optPanel);
        spectreConsole.WriteLine();

        return 0;
    }

    /// <summary>
    /// Renders a Spectre renderable with a 1-space left indent, without trailing blank lines.
    /// </summary>
    private static void WriteIndented(IAnsiConsole targetConsole, IRenderable renderable)
    {
        // Account for the 1-space indent so content fits within the terminal width
        var width = Math.Max(40, targetConsole.Profile.Width - 1);
        using var writer = new StringWriter();
        var renderConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = targetConsole.Profile.Capabilities.Ansi ? AnsiSupport.Yes : AnsiSupport.No
        });
        renderConsole.Profile.Width = width;
        renderConsole.Write(renderable);

        var lines = writer.ToString().TrimEnd('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            targetConsole.Write(new Text(" "));
            targetConsole.Profile.Out.Writer.WriteLine(line.TrimEnd('\r'));
        }
    }

    private static string GetCommandPath(Command command)
    {
        // For root command, use "winapp"; for subcommands, build the path
        var parts = new List<string>();
        var current = command;
        while (current != null)
        {
            if (current is RootCommand)
            {
                parts.Add("winapp");
            }
            else
            {
                parts.Add(current.Name);
            }
            current = current.Parents.OfType<Command>().FirstOrDefault();
        }

        parts.Reverse();
        return string.Join(" ", parts);
    }
}
