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
            Description = "Directory to read/store configuration (default: the selected project directory, or current directory if no project is detected)"
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
            Description = "Do not prompt; requires an explicit project directory (e.g., winapp init . --use-defaults)"
        };
        ConfigOnlyOption = new Option<bool>("--config-only")
        {
            Description = "Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps."
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

            // Detect non-interactive environments (piped stdin, CI, etc.) and fall back
            // to --use-defaults behavior to avoid InvalidOperationException from prompts.
            if (!useDefaults && !ansiConsole.Profile.Capabilities.Interactive)
            {
                logger.LogWarning("{Warning}  Non-interactive environment detected. Using default values.", UiSymbols.Warning);
                useDefaults = true;
            }

            DirectoryInfo? selectedDirectory;

            if (baseDirectoryExplicit)
            {
                // User specified a directory: skip search, use the directory directly
                selectedDirectory = await InitDirectlyAsync(baseDirectory, useDefaults, cancellationToken);
            }
            else if (useDefaults)
            {
                // --use-defaults without explicit directory: use cwd, but warn if no project detected
                var detected = projectDetectionService.DetectProjectAt(baseDirectory);
                if (detected == null)
                {
                    logger.LogWarning("{Warning}  No known project type detected in current directory. Initializing with winapp.yaml",
                        UiSymbols.Warning);
                }
                selectedDirectory = baseDirectory;
            }
            else
            {
                // No directory specified: search for compatible projects
                selectedDirectory = await DetectAndSelectProjectAsync(
                    baseDirectory, cancellationToken);
            }

            if (selectedDirectory == null)
            {
                // User declined to init in a directory with no compatible projects
                return 1;
            }

            // If --config-dir was not explicitly set, use the selected/init directory
            // so winapp.yaml is co-located with the project
            if (!configDirExplicit)
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

            var result = await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);

            // If init ran in a nested directory, remind the user to cd there for further commands
            if (result == 0 && selectedDirectory.FullName != baseDirectory.FullName)
            {
                var relativePath = Path.GetRelativePath(baseDirectory.FullName, selectedDirectory.FullName);
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Run [blue]cd \"{relativePath}\"[/] to use further winapp commands in your project directory.");
            }

            return result;
        }

        /// <summary>
        /// Detects compatible projects in the directory tree and prompts the user to select one.
        /// Only called when no directory argument was provided and --use-defaults is not set.
        /// Returns the selected directory, or null if the user declines.
        /// </summary>
        private async Task<DirectoryInfo?> DetectAndSelectProjectAsync(
            DirectoryInfo searchRoot,
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
                    .StartAsync("Searching for known project types...", async ctx =>
                    {
                        return await projectDetectionService.DetectProjectsAsync(
                            searchRoot, maxProjects, progress: null, cancellationToken);
                    });
            }
            else
            {
                results = await projectDetectionService.DetectProjectsAsync(
                    searchRoot, maxProjects, progress: null, cancellationToken);
            }

            // Handle results based on count
            if (results.Count == 0)
            {
                return await HandleNoProjectsFoundAsync(searchRoot, cancellationToken);
            }

            // If the only result is at the search root, use it directly
            if (results.Count == 1 && results[0].DisplayPath == ".")
            {
                logger.LogInformation("{Check} {TypeLabel} project detected ({FilePath})",
                    UiSymbols.Check, results[0].TypeLabel, results[0].DisplayFilePath);
                return results[0].Directory;
            }

            if (results.Count == 1)
            {
                return await HandleSingleProjectAsync(results[0], cancellationToken);
            }

            return await HandleMultipleProjectsAsync(results, searchRoot, results.Count >= maxProjects, cancellationToken);
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
            var detected = projectDetectionService.DetectProjectAt(targetDirectory);

            if (detected != null)
            {
                logger.LogInformation("{Check} {TypeLabel} project detected ({FilePath})",
                    UiSymbols.Check, detected.TypeLabel, detected.DisplayFilePath);
                return targetDirectory;
            }

            // No project detected at the specified path
            if (useDefaults)
            {
                logger.LogWarning("{Warning} No known project type detected at {Path}. Initializing with winapp.yaml.",
                    UiSymbols.Warning, targetDirectory.FullName);
                return targetDirectory;
            }

            var proceed = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"[yellow]No known project type was detected at this path.[/] Initialize with winapp here anyway?")
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
            var proceed = await ansiConsole.PromptAsync(
                new ConfirmationPrompt(
                    $"[yellow]No known projects type were found.[/] Initialize with winapp.yaml here?")
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
                    $"Found [green]{Markup.Escape(project.TypeLabel)}[/] project at [blue]{Markup.Escape(project.DisplayFilePath)}[/]. Initialize with winapp?"),
                cancellationToken);

            if (!confirm)
            {
                logger.LogInformation("Init cancelled by user.");
                return null;
            }

            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Selected [green]{project.TypeLabel} project ({project.DisplayFilePath})[/]");
            return project.Directory;
        }

        private async Task<DirectoryInfo?> HandleMultipleProjectsAsync(
            IReadOnlyList<DetectedProject> projects,
            DirectoryInfo searchRoot,
            bool searchLimitReached,
            CancellationToken cancellationToken)
        {
            var choices = projects.Select(p =>
                $"{Markup.Escape(p.TypeLabel)} project ({Markup.Escape(p.DisplayFilePath)})").ToList();

            // Always offer the current directory as a fallback option
            var currentDirIsListed = projects.Any(p => p.DisplayPath == ".");
            if (!currentDirIsListed)
            {
                choices.Add("Current directory (./) — no project detected");
            }

            var prompt = new SelectionPrompt<string>()
                .Title("Which project would you like to initialize with winapp?")
                .AddChoices(choices);

            if (searchLimitReached)
            {
                prompt.Title("Which project would you like to initialize with winapp? [dim](If your project wasn't found, run: winapp init <path-to-project>)[/]");
            }

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);

            var selectedIndex = choices.IndexOf(selected);

            // If the user picked the appended current-directory fallback
            if (!currentDirIsListed && selectedIndex == projects.Count)
            {
                ansiConsole.MarkupLine("Which project would you like to initialize with winapp? [underline]Current directory (./)[/]");
                return searchRoot;
            }

            var selectedProject = projects[selectedIndex];
            ansiConsole.MarkupLineInterpolated($"Which project would you like to initialize with winapp? [underline]{selectedProject.TypeLabel} project ({selectedProject.DisplayFilePath})[/]");
            return selectedProject.Directory;
        }
    }
}
