// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class NewCommand : Command
{
    public static Argument<string> TemplateArgument { get; }
    public static Option<string> NameOption { get; }
    public static Option<bool> UseMvvmOption { get; }
    public static Option<DirectoryInfo> OutputOption { get; }

    static NewCommand()
    {
        TemplateArgument = new Argument<string>("template")
        {
            Description = "Template short name (winui, winui-blazor, winuilib, winui-page, winui-window, winui-usercontrol)",
            Arity = ArgumentArity.ZeroOrOne
        };
        NameOption = new Option<string>("--name", "-n")
        {
            Description = "Name for the new project or item"
        };
        UseMvvmOption = new Option<bool>("--use-mvvm", "-mvvm")
        {
            Description = "Use the MVVM pattern (skip interactive prompt)"
        };
        OutputOption = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "Parent directory for project output (defaults to current directory)"
        };
    }

    public NewCommand() : base("new", "Create a new WinUI 3 project or add a component to an existing project. Scaffolds from templates with interactive prompts for template type, name, and MVVM support. Auto-installs required templates if needed.")
    {
        Arguments.Add(TemplateArgument);
        Options.Add(NameOption);
        Options.Add(UseMvvmOption);
        Options.Add(OutputOption);
    }

    public class Handler(
        IDotnetService dotnetService,
        IStatusService statusService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        ILogger<NewCommand> logger) : AsynchronousCommandLineAction
    {
        private static readonly Dictionary<string, string> ProjectTemplates = new()
        {
            ["WinUI 3 App"] = "winui",
            ["WinUI 3 Blazor App"] = "winui-blazor",
            ["WinUI 3 Class Library"] = "winuilib"
        };

        private static readonly Dictionary<string, string> ItemTemplates = new()
        {
            ["Page"] = "winui-page",
            ["Window"] = "winui-window",
            ["UserControl"] = "winui-usercontrol"
        };

        private static readonly HashSet<string> ProjectTemplateShortNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "winui", "winui-blazor", "winuilib"
        };

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var template = parseResult.GetValue(TemplateArgument);
            var name = parseResult.GetValue(NameOption);
            var useMvvm = parseResult.GetValue(UseMvvmOption);
            var outputDir = parseResult.GetValue(OutputOption);

            // 1. Check dotnet installed
            if (!await dotnetService.IsDotnetInstalledAsync(cancellationToken))
            {
                logger.LogError(".NET SDK is not installed. Install it with: winget install Microsoft.DotNet.SDK.10");
                return 1;
            }

            // 2. Check and install WinUI templates
            if (!await dotnetService.IsWinUITemplatesInstalledAsync(cancellationToken))
            {
                var installResult = await statusService.ExecuteWithStatusAsync(
                    "Installing WinUI 3 templates...",
                    async (taskContext, ct) =>
                    {
                        var (exitCode, output) = await dotnetService.InstallWinUITemplatesAsync(taskContext, ct);
                        return (exitCode, output);
                    },
                    cancellationToken);

                if (installResult != 0)
                {
                    logger.LogError("Failed to install WinUI 3 templates. Please install manually: dotnet new install VijayAnand.WinUITemplates");
                    return 1;
                }
            }

            // 3. Resolve template
            bool templateWasInteractive = false;
            string templateShortName;

            if (!string.IsNullOrEmpty(template))
            {
                // Template provided via argument — use directly
                templateShortName = template;
            }
            else
            {
                // No template argument — detect .csproj and show interactive prompt
                templateWasInteractive = true;
                var currentDir = currentDirectoryProvider.GetCurrentDirectory();
                var csprojFiles = Directory.GetFiles(currentDir, "*.csproj");
                var isItemMode = csprojFiles.Length > 0;

                if (isItemMode)
                {
                    var templatePrompt = new SelectionPrompt<string>()
                        .Title("Select a component template:")
                        .AddChoices(ItemTemplates.Keys);
                    var selectedTemplate = await ansiConsole.PromptAsync(templatePrompt, cancellationToken);
                    ansiConsole.MarkupLine($"Template: [green]{selectedTemplate}[/]");
                    templateShortName = ItemTemplates[selectedTemplate];
                }
                else
                {
                    var templatePrompt = new SelectionPrompt<string>()
                        .Title("Select a project template:")
                        .AddChoices(ProjectTemplates.Keys);
                    var selectedTemplate = await ansiConsole.PromptAsync(templatePrompt, cancellationToken);
                    ansiConsole.MarkupLine($"Template: [green]{selectedTemplate}[/]");
                    templateShortName = ProjectTemplates[selectedTemplate];
                }
            }

            bool isProjectTemplate = ProjectTemplateShortNames.Contains(templateShortName);

            // 4. Resolve name
            if (string.IsNullOrEmpty(name))
            {
                var promptLabel = isProjectTemplate ? "Project name:" : "Component name:";
                var namePrompt = new TextPrompt<string>(promptLabel);
                name = await ansiConsole.PromptAsync(namePrompt, cancellationToken);
            }

            // 5. Resolve MVVM (only for winui/winui-blazor)
            var parameters = new Dictionary<string, string>();
            if (templateShortName is "winui" or "winui-blazor")
            {
                if (useMvvm)
                {
                    parameters["use-mvvm"] = "true";
                }
                else if (templateWasInteractive)
                {
                    // Only prompt for MVVM when template was chosen interactively
                    var mvvmPrompt = new ConfirmationPrompt("Use MVVM?") { DefaultValue = false };
                    var wantsMvvm = await ansiConsole.PromptAsync(mvvmPrompt, cancellationToken);
                    if (wantsMvvm)
                    {
                        parameters["use-mvvm"] = "true";
                    }
                }
                // else: template was explicit and --use-mvvm not set → default off, no prompt
            }

            // 6. Compute output path
            string? effectiveOutputDir = null;
            if (isProjectTemplate)
            {
                var currentDir = currentDirectoryProvider.GetCurrentDirectory();
                var parentDir = outputDir?.FullName ?? currentDir;
                effectiveOutputDir = Path.Combine(parentDir, name);
            }

            // 7. Item templates: run dotnet restore first
            if (!isProjectTemplate)
            {
                var currentDir = currentDirectoryProvider.GetCurrentDirectory();
                var restoreResult = await statusService.ExecuteWithStatusAsync(
                    "Restoring project packages...",
                    async (taskContext, ct) =>
                    {
                        var (exitCode, output) = await dotnetService.RunDotnetRestoreAsync(currentDir, taskContext, ct);
                        return (exitCode, output);
                    },
                    cancellationToken);

                if (restoreResult != 0)
                {
                    logger.LogError("Failed to restore project packages.");
                    return 1;
                }
            }

            // 8. Run dotnet new
            var templateDisplayName = isProjectTemplate ? "project" : "component";
            return await statusService.ExecuteWithStatusAsync(
                $"Creating {templateDisplayName} '{name}'...",
                async (taskContext, ct) =>
                {
                    var (exitCode, output) = await dotnetService.RunDotnetNewAsync(
                        templateShortName, name, effectiveOutputDir, parameters, taskContext, ct);
                    var message = exitCode == 0
                        ? isProjectTemplate
                            ? $"Project '{name}' created at {effectiveOutputDir}"
                            : $"Component '{name}' created successfully."
                        : $"Failed to create {templateDisplayName}: {output}";
                    return (exitCode, message);
                },
                cancellationToken);
        }
    }
}
