// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class InitCommand : Command, IShortDescription
{
    public string ShortDescription => "Initialize existing project with manifest and/or SDK packages";

    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }
    public static Option<SdkInstallMode?> SetupSdksOption { get; }
    public static Option<bool> IgnoreConfigOption { get; }
    public static Option<bool> NoGitignoreOption { get; }
    public static Option<bool> UseDefaults { get; }
    public static Option<bool> ConfigOnlyOption { get; }
    public static Option<bool> SearchAllOption { get; }

    static InitCommand()
    {
        BaseDirectoryArgument = new Argument<DirectoryInfo>("base-directory")
        {
            Description = "Base/root directory for the winapp workspace, for consumption or installation.",
            Arity = ArgumentArity.ZeroOrOne
        };
        BaseDirectoryArgument.AcceptExistingOnly();
        ConfigDirOption = new Option<DirectoryInfo>("--config-dir")
        {
            Description = "Directory to read/store configuration (default: current directory)"
        };
        ConfigDirOption.AcceptExistingOnly();
        SetupSdksOption = new Option<SdkInstallMode?>("--setup-sdks")
        {
            Description = "SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)",
            HelpName = "stable|preview|experimental|none"
        };
        IgnoreConfigOption = new Option<bool>("--ignore-config", "--no-config")
        {
            Description = "Don't use configuration file for version management"
        };
        NoGitignoreOption = new Option<bool>("--no-gitignore")
        {
            Description = "Don't update .gitignore file"
        };
        UseDefaults = new Option<bool>("--use-defaults", "--no-prompt")
        {
            Description = "Do not prompt, and use default of all prompts"
        };
        ConfigOnlyOption = new Option<bool>("--config-only")
        {
            Description = "Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps."
        };
        SearchAllOption = new Option<bool>("--search-all")
        {
            Description = "Search all directories, including commonly ignored ones like node_modules, bin, obj, etc."
        };
    }

    public InitCommand() : base("init", "Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates Package.appxmanifest with default assets, downloads Windows SDK and Windows App SDK packages, and generates projections. When SDK packages are managed (--setup-sdks stable/preview/experimental), also creates winapp.yaml to pin versions for 'restore'/'update'; with --setup-sdks none (e.g., for Rust/Tauri projects that bring their own SDK bindings), no winapp.yaml is created. Interactive by default (use --use-defaults to skip prompts). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(SetupSdksOption);
        Options.Add(IgnoreConfigOption);
        Options.Add(NoGitignoreOption);
        Options.Add(UseDefaults);
        Options.Add(ConfigOnlyOption);
        Options.Add(SearchAllOption);
    }

    public class Handler(
        IWorkspaceSetupService workspaceSetupService,
        IProjectDetectionService projectDetectionService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        ILogger<Handler> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectoryExplicit = parseResult.GetResult(BaseDirectoryArgument)?.Implicit == false;
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var configDirExplicit = parseResult.GetResult(ConfigDirOption)?.Implicit == false;
            var configDir = parseResult.GetValue(ConfigDirOption) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var setupSdks = parseResult.GetValue(SetupSdksOption);
            var ignoreConfig = parseResult.GetValue(IgnoreConfigOption);
            var noGitignore = parseResult.GetValue(NoGitignoreOption);
            var useDefaults = parseResult.GetValue(UseDefaults);
            var configOnly = parseResult.GetValue(ConfigOnlyOption);
            var searchAll = parseResult.GetValue(SearchAllOption);

            DirectoryInfo? selectedDirectory;

            if (baseDirectoryExplicit || useDefaults)
            {
                // User specified a directory or --use-defaults: skip search, use the directory directly
                if (searchAll)
                {
                    logger.LogDebug("--search-all has no effect when a directory is specified or --use-defaults is set");
                }

                selectedDirectory = await InitDirectlyAsync(baseDirectory, useDefaults, cancellationToken);
            }
            else
            {
                // No directory specified: search for compatible projects
                selectedDirectory = await DetectAndSelectProjectAsync(
                    baseDirectory, searchAll, cancellationToken);
            }

            if (selectedDirectory == null)
            {
                // User declined to init in a directory with no compatible projects
                return 1;
            }

            // If a nested project was selected and --config-dir was not explicitly set,
            // move config-dir to the selected project directory so winapp.yaml is co-located
            if (!configDirExplicit && selectedDirectory.FullName != baseDirectory.FullName)
            {
                configDir = selectedDirectory;
            }

            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = selectedDirectory,
                ConfigDir = configDir,
                SdkInstallMode = setupSdks,
                IgnoreConfig = ignoreConfig,
                NoGitignore = noGitignore,
                UseDefaults = useDefaults,
                RequireExistingConfig = false,
                ForceLatestBuildTools = true,
                ConfigOnly = configOnly
            };

            return await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);
        }

        /// <summary>
        /// Detects compatible projects in the directory tree and prompts the user to select one.
        /// Only called when no directory argument was provided and --use-defaults is not set.
        /// Returns the selected directory, or null if the user declines.
        /// </summary>
        private async Task<DirectoryInfo?> DetectAndSelectProjectAsync(
            DirectoryInfo searchRoot,
            bool searchAll,
            CancellationToken cancellationToken)
        {
            const int maxProjects = 10;
            var useLiveSpinner = ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);

            IReadOnlyList<DetectedProject> results;

            if (useLiveSpinner)
            {
                // Animated mode: run detection inside a Spectre Status spinner
                results = await ansiConsole.Status()
                    .AutoRefresh(true)
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("blue"))
                    .StartAsync("Searching for compatible projects...", async ctx =>
                    {
                        return await projectDetectionService.DetectProjectsAsync(
                            searchRoot, maxProjects, searchAll, progress: null, cancellationToken);
                    });
            }
            else
            {
                results = await projectDetectionService.DetectProjectsAsync(
                    searchRoot, maxProjects, searchAll, progress: null, cancellationToken);
            }

            // Handle results based on count
            if (results.Count >= maxProjects)
            {
                logger.LogWarning("{Warning} Search stopped at {Max} projects. If your project wasn't found, provide a directory argument: winapp init <path-to-project>",
                    UiSymbols.Warning, maxProjects);
            }

            if (results.Count == 0)
            {
                return await HandleNoProjectsFoundAsync(searchRoot, cancellationToken);
            }

            // If the only result is at the search root, use it directly
            if (results.Count == 1 && results[0].DisplayPath == ".")
            {
                logger.LogInformation("{Check} {TypeLabel} project detected in current directory",
                    UiSymbols.Check, results[0].TypeLabel);
                return results[0].Directory;
            }

            if (results.Count == 1)
            {
                return await HandleSingleProjectAsync(results[0], cancellationToken);
            }

            return await HandleMultipleProjectsAsync(results, searchRoot, cancellationToken);
        }

        /// <summary>
        /// Handles direct initialization when a directory was explicitly specified or --use-defaults is set.
        /// Checks for a project at the target path; warns if none found.
        /// </summary>
        private async Task<DirectoryInfo?> InitDirectlyAsync(
            DirectoryInfo targetDirectory,
            bool useDefaults,
            CancellationToken cancellationToken)
        {
            var detected = ProjectDetectionService.DetectProject(targetDirectory, targetDirectory);

            if (detected != null)
            {
                logger.LogInformation("{Check} {TypeLabel} project detected at {Path}",
                    UiSymbols.Check, detected.TypeLabel, targetDirectory.FullName);
                return targetDirectory;
            }

            // No project detected at the specified path
            logger.LogWarning("{Warning} No compatible project detected at {Path}",
                UiSymbols.Warning, targetDirectory.FullName);

            if (useDefaults)
            {
                logger.LogWarning("{Warning} Proceeding anyway (--use-defaults). The CLI might not function as expected.",
                    UiSymbols.Warning);
                return targetDirectory;
            }

            var proceed = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"[yellow]No compatible project was detected at this path.[/] Initialize winapp here anyway? " +
                    $"([dim]The CLI might not function as expected[/])")
                {
                    DefaultValue = false
                },
                cancellationToken);

            if (!proceed)
            {
                logger.LogInformation("Init cancelled by user.");
                return null;
            }

            return targetDirectory;
        }

        private async Task<DirectoryInfo?> HandleNoProjectsFoundAsync(
            DirectoryInfo searchRoot,
            CancellationToken cancellationToken)
        {
            logger.LogWarning("{Warning} No compatible projects found in {Path}",
                UiSymbols.Warning, searchRoot.FullName);

            var proceed = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"[yellow]No compatible projects were found.[/] Initialize winapp here anyway? " +
                    $"([dim]The CLI might not function as expected[/])")
                {
                    DefaultValue = false
                },
                cancellationToken);

            if (!proceed)
            {
                logger.LogInformation("Init cancelled by user.");
                return null;
            }

            return searchRoot;
        }

        private async Task<DirectoryInfo?> HandleSingleProjectAsync(
            DetectedProject project,
            CancellationToken cancellationToken)
        {
            var confirm = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"Found [green]{Markup.Escape(project.TypeLabel)}[/] project at [blue]{Markup.Escape(project.DisplayPath)}[/]. Initialize with winapp?"),
                cancellationToken);

            if (!confirm)
            {
                logger.LogInformation("Init cancelled by user.");
                return null;
            }

            logger.LogInformation("{Check} Selected {TypeLabel} project at {Path}",
                UiSymbols.Check, project.TypeLabel, project.DisplayPath);
            return project.Directory;
        }

        private async Task<DirectoryInfo?> HandleMultipleProjectsAsync(
            IReadOnlyList<DetectedProject> projects,
            DirectoryInfo searchRoot,
            CancellationToken cancellationToken)
        {
            var choices = projects.Select(p =>
                $"{p.TypeLabel} project at {p.DisplayPath}").ToList();

            // Always offer the current directory as a fallback option
            var currentDirIsListed = projects.Any(p => p.DisplayPath == ".");
            if (!currentDirIsListed)
            {
                choices.Add(". (current directory — no project detected)");
            }

            var selected = await ansiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("Which project would you like to initialize with winapp?")
                    .AddChoices(choices),
                cancellationToken);

            var selectedIndex = choices.IndexOf(selected);

            // If the user picked the appended current-directory fallback
            if (!currentDirIsListed && selectedIndex == projects.Count)
            {
                logger.LogWarning("{Warning} No compatible project was detected in the current directory. The CLI might not function as expected.",
                    UiSymbols.Warning);
                return searchRoot;
            }

            var selectedProject = projects[selectedIndex];
            logger.LogInformation("{Check} Selected {TypeLabel} project at {Path}",
                UiSymbols.Check, selectedProject.TypeLabel, selectedProject.DisplayPath);
            return selectedProject.Directory;
        }
    }
}
