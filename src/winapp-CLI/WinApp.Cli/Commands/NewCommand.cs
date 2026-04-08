// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class NewCommand : Command, IShortDescription
{
    public string ShortDescription => "Create a new WinUI project or add an item from a template";

    public static Argument<string> TemplateArgument { get; }
    public static Option<string> NameOption { get; }
    public static Option<DirectoryInfo> OutputOption { get; }
    public static Option<FileInfo> ProjectOption { get; }

    static NewCommand()
    {
        TemplateArgument = new Argument<string>("template")
        {
            Description = "Template short name (e.g., 'winui', 'winui-navview', 'winui-page'). If omitted, an interactive selection is shown.",
            Arity = ArgumentArity.ZeroOrOne
        };
        NameOption = new Option<string>("--name", "-n")
        {
            Description = "Name for the created project or item"
        };
        OutputOption = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "Output directory for the created project"
        };
        ProjectOption = new Option<FileInfo>("--project")
        {
            Description = "Target .csproj file (for item templates). Auto-detected if omitted."
        };
        ProjectOption.AcceptExistingOnly();
    }

    public NewCommand() : base("new", "Create a new WinUI 3 project or add an item to an existing project. " +
        "Uses the latest Microsoft.WindowsAppSDK.WinUI.CSharp.Templates (automatically installed/updated). " +
        "When run inside a .csproj directory, shows item templates (pages, windows, controls). " +
        "Otherwise, shows project templates. Pass additional dotnet new arguments after --.")
    {
        Arguments.Add(TemplateArgument);
        Options.Add(NameOption);
        Options.Add(OutputOption);
        Options.Add(ProjectOption);

        // Allow pass-through of additional args to dotnet new
        TreatUnmatchedTokensAsErrors = false;
    }

    public class Handler(
        ITemplateService templateService,
        IDotNetService dotNetService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IStatusService statusService,
        IAnsiConsole ansiConsole) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var templateName = parseResult.GetValue(TemplateArgument);
            var name = parseResult.GetValue(NameOption);
            var outputDir = parseResult.GetValue(OutputOption);
            var projectFile = parseResult.GetValue(ProjectOption);
            var extraArgs = parseResult.UnmatchedTokens.ToList();

            var isInteractive = Environment.UserInteractive && !Console.IsOutputRedirected;

            // Detect context: are we in a project directory?
            var cwd = currentDirectoryProvider.GetCurrentDirectoryInfo();
            var csprojFiles = dotNetService.FindCsproj(cwd);
            var isInProjectDir = csprojFiles.Count > 0;
            if (projectFile is not null)
            {
                isInProjectDir = true;
            }

            // Phase 1: Load templates (simple spinner, no residual output)
            IReadOnlyList<TemplateInfo>? allTemplates = null;
            try
            {
                var spinnerTask = Task.Run(async () =>
                {
                    await templateService.EnsureAllProvidersAsync(cancellationToken);
                    return await templateService.GetAvailableTemplatesAsync(cancellationToken);
                }, cancellationToken);

                if (isInteractive)
                {
                    var spinnerChars = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
                    int i = 0;
                    while (!spinnerTask.IsCompleted)
                    {
                        Console.Write($"\r\x1b[33m{spinnerChars[i++ % spinnerChars.Length]}\x1b[0m Loading templates...");
                        await Task.WhenAny(spinnerTask, Task.Delay(100, cancellationToken));
                    }
                    Console.Write("\r\x1b[2K");
                }

                allTemplates = await spinnerTask;
            }
            catch (Exception ex)
            {
                ansiConsole.MarkupLine($"{UiSymbols.Error} Failed to load templates: {Markup.Escape(ex.Message)}");
                return 1;
            }

            if (allTemplates is null || allTemplates.Count == 0)
            {
                ansiConsole.MarkupLine($"{UiSymbols.Error} No templates found. Check your internet connection and try again.");
                return 1;
            }

            // Filter templates based on context
            var availableTemplates = isInProjectDir
                ? allTemplates.Where(t => t.Type == TemplateType.Item).ToList()
                : allTemplates.Where(t => t.Type == TemplateType.Project).ToList();

            // Phase 2: Interactive prompts (direct console, no Live context)
            TemplateInfo? selectedTemplate = null;

            if (!string.IsNullOrEmpty(templateName))
            {
                // User specified a template — look across ALL templates
                selectedTemplate = allTemplates.FirstOrDefault(t =>
                    string.Equals(t.ShortName, templateName, StringComparison.OrdinalIgnoreCase));

                if (selectedTemplate is null)
                {
                    var available = string.Join(", ", allTemplates.Select(t => Markup.Escape(t.ShortName)));
                    ansiConsole.MarkupLine($"{UiSymbols.Error} Template [bold]'{Markup.Escape(templateName)}'[/] not found.");
                    ansiConsole.MarkupLine($"Available: {available}");
                    return 1;
                }
            }
            else if (isInteractive && availableTemplates.Count > 0)
            {
                selectedTemplate = PromptForTemplate(availableTemplates);
                if (selectedTemplate is null)
                {
                    return 1;
                }

                // Show what was selected (match winapp init's style: clear prompt, show result)
                ansiConsole.MarkupLine($"Template: [underline]{Markup.Escape(selectedTemplate.Name)}[/]");
            }
            else
            {
                ansiConsole.MarkupLine($"{UiSymbols.Error} Template argument is required.");
                ansiConsole.MarkupLine($"Available: {string.Join(", ", availableTemplates.Select(t => Markup.Escape(t.ShortName)))}");
                return 1;
            }

            var isItemTemplate = selectedTemplate.Type == TemplateType.Item;

            // Resolve name — prompt only if fully interactive (no template argument provided)
            if (string.IsNullOrEmpty(name))
            {
                var baseName = isItemTemplate ? "NewItem" : "winui-app";
                var defaultName = GetUniqueDefaultName(baseName, cwd, isItemTemplate);

                if (isInteractive && string.IsNullOrEmpty(templateName))
                {
                    // Fully interactive — user didn't specify template, so ask for name too
                    name = ansiConsole.Prompt(
                        new TextPrompt<string>(isItemTemplate ? "Item name:" : "Project name:")
                            .DefaultValue(defaultName));
                }
                else
                {
                    // Template was specified via argument — use default name silently
                    name = defaultName;
                }
            }

            // Resolve project file for item templates
            if (isItemTemplate && projectFile is null)
            {
                if (csprojFiles.Count == 1)
                {
                    projectFile = csprojFiles[0];
                }
                else if (csprojFiles.Count > 1 && isInteractive)
                {
                    projectFile = ansiConsole.Prompt(
                        new SelectionPrompt<FileInfo>()
                            .Title("Select target project:")
                            .AddChoices(csprojFiles)
                            .UseConverter(f => f.Name));
                }
                else if (csprojFiles.Count > 1)
                {
                    ansiConsole.MarkupLine($"{UiSymbols.Error} Multiple .csproj files found. Use --project to specify which one.");
                    return 1;
                }
            }

            // Resolve output directory for project templates
            if (!isItemTemplate && outputDir is null && !string.IsNullOrEmpty(name))
            {
                outputDir = new DirectoryInfo(Path.Combine(
                    currentDirectoryProvider.GetCurrentDirectory(), name));
            }

            // Phase 3: Create from template + auto-init (status-tracked)
            var result = await statusService.ExecuteWithStatusAsync(
                $"Creating {selectedTemplate.ShortName} '{name}'...",
                async (taskContext, ct) =>
                {
                    var createResult = await taskContext.AddSubTaskAsync<(int ExitCode, string Output, string Error)>(
                        $"Running dotnet new {selectedTemplate.ShortName}",
                        async (ctx, ct2) =>
                        {
                            return await templateService.CreateFromTemplateAsync(
                                selectedTemplate.ShortName,
                                name!,
                                isItemTemplate ? null : outputDir,
                                isItemTemplate ? projectFile : null,
                                null, // Use template defaults
                                extraArgs.Count > 0 ? extraArgs : null,
                                ct2);
                        }, ct);

                    if (createResult is not { ExitCode: 0 })
                    {
                        return (1, $"Template creation failed: {createResult.Error}");
                    }

                    // For project templates, set up the project with our NuGet package and platform defaults
                    if (!isItemTemplate && outputDir is not null)
                    {
                        // Refresh DirectoryInfo since dotnet new just created it
                        outputDir.Refresh();

                        // Find the csproj in the created project directory
                        var createdCsprojFiles = dotNetService.FindCsproj(outputDir);
                        if (createdCsprojFiles.Count > 0)
                        {
                            var csprojFile = createdCsprojFiles[0];

                            // Add our build tools package and configure project properties
                            await taskContext.AddSubTaskAsync<bool>("Configuring project", async (ctx, ct2) =>
                            {
                                // Ensure RuntimeIdentifier defaults to current arch
                                await dotNetService.EnsureRuntimeIdentifierAsync(csprojFile, ct2);

                                // Add publish profile condition
                                await dotNetService.UpdatePublishProfileAsync(csprojFile, ct2);

                                // Add our build tools WinApp package (optional, best-effort)
                                try
                                {
                                    await dotNetService.AddOrUpdatePackageReferenceAsync(
                                        csprojFile, DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE, null, ct2);
                                }
                                catch
                                {
                                    // Non-critical — project works without it
                                }

                                return true;
                            }, ct);
                        }
                    }

                    var message = isItemTemplate
                        ? $"Added {selectedTemplate.Name} '{name}'"
                        : $"Created {selectedTemplate.Name} '{name}' in {outputDir?.FullName}";

                    return (0, message);
                }, cancellationToken);

            // Print next steps after successful project creation
            if (result == 0 && !isItemTemplate && outputDir is not null)
            {
                ansiConsole.WriteLine();
                ansiConsole.MarkupLine("[bold]Next steps:[/]");
                ansiConsole.WriteLine($"  cd {name}");
                ansiConsole.WriteLine("  dotnet run");
            }

            return result;
        }

        /// <summary>
        /// Shows an interactive prompt for template selection.
        /// Templates are already filtered by context (project vs item).
        /// </summary>
        private TemplateInfo? PromptForTemplate(List<TemplateInfo> templates)
        {
            if (templates.Count == 0)
            {
                return null;
            }

            // Group by language only if there are multiple languages
            var languages = templates.Select(t => t.Language).Distinct().ToList();

            // Print title manually to avoid Spectre's extra blank line between title and choices
            ansiConsole.MarkupLine("Select a template:");

            var prompt = new SelectionPrompt<(string Display, TemplateInfo? Template)>()
                .HighlightStyle(new Style(Color.Cyan1))
                .UseConverter(c => c.Display)
                .PageSize(15);

            if (languages.Count > 1)
            {
                foreach (var language in languages)
                {
                    var langTemplates = templates.Where(t => t.Language == language).ToList();
                    prompt.AddChoiceGroup(
                        ($"[bold yellow]{Markup.Escape(language)}[/]", (TemplateInfo?)null),
                        langTemplates.Select(t => ($"{Markup.Escape(t.Name)} [dim]({Markup.Escape(t.ShortName)})[/]", (TemplateInfo?)t)).ToArray());
                }
            }
            else
            {
                foreach (var t in templates)
                {
                    prompt.AddChoice(($"{Markup.Escape(t.Name)} [dim]({Markup.Escape(t.ShortName)})[/]", (TemplateInfo?)t));
                }
            }

            var selected = ansiConsole.Prompt(prompt);
            return selected.Template;
        }
        /// <summary>
        /// Generates a unique default name by appending a number if the base name already exists.
        /// For project templates, checks for existing directories. For item templates, checks for existing files.
        /// </summary>
        private static string GetUniqueDefaultName(string baseName, DirectoryInfo directory, bool isItem)
        {
            if (isItem)
            {
                // For items, check for existing .xaml files with that name
                if (!File.Exists(Path.Combine(directory.FullName, $"{baseName}.xaml")))
                {
                    return baseName;
                }

                for (int i = 2; i < 100; i++)
                {
                    var candidate = $"{baseName}{i}";
                    if (!File.Exists(Path.Combine(directory.FullName, $"{candidate}.xaml")))
                    {
                        return candidate;
                    }
                }
            }
            else
            {
                // For projects, check for existing directories
                if (!Directory.Exists(Path.Combine(directory.FullName, baseName)))
                {
                    return baseName;
                }

                for (int i = 2; i < 100; i++)
                {
                    var candidate = $"{baseName}{i}";
                    if (!Directory.Exists(Path.Combine(directory.FullName, candidate)))
                    {
                        return candidate;
                    }
                }
            }

            return baseName;
        }
    }
}
