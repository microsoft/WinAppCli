// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Runtime.InteropServices;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Parameters for workspace setup operations
/// </summary>
internal class WorkspaceSetupOptions
{
    public required DirectoryInfo BaseDirectory { get; set; }
    public required DirectoryInfo ConfigDir { get; set; }
    public SdkInstallMode? SdkInstallMode { get; set; }
    public bool IgnoreConfig { get; set; }
    public bool NoGitignore { get; set; }
    public bool UseDefaults { get; set; }
    public bool RequireExistingConfig { get; set; }
    public bool ForceLatestBuildTools { get; set; }
    public bool ConfigOnly { get; set; }
}

/// <summary>
/// Shared service for setting up winapp workspaces
/// </summary>
internal class WorkspaceSetupService(
    IConfigService configService,
    IWinappDirectoryService winappDirectoryService,
    IPackageInstallationService packageInstallationService,
    IBuildToolsService buildToolsService,
    ICppWinrtService cppWinrtService,
    IPackageLayoutService packageLayoutService,
    IPowerShellService powerShellService,
    INugetService nugetService,
    IManifestService manifestService,
    IDevModeService devModeService,
    IGitignoreService gitignoreService,
    IDirectoryPackagesService directoryPackagesService,
    IDotNetService dotNetService,
    IStatusService statusService,
    ICurrentDirectoryProvider currentDirectoryProvider,
    IAnsiConsole ansiConsole,
    ILogger<WorkspaceSetupService> logger) : IWorkspaceSetupService
{
    public async Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
    {
        configService.ConfigPath = new FileInfo(Path.Combine(options.ConfigDir.FullName, "winapp.yaml"));

        // Detect .NET project (.csproj) in the base directory
        FileInfo? csprojFile = null;
        bool isDotNetProject = false;

        if (!options.RequireExistingConfig)
        {
            var csprojFiles = dotNetService.FindCsproj(options.BaseDirectory);
            if (csprojFiles.Count > 0)
            {
                isDotNetProject = true;
                logger.LogDebug("Detected {Count} .NET project(s) in {BaseDirectory}", csprojFiles.Count, options.BaseDirectory);
                csprojFile = await SelectCsprojFileAsync(csprojFiles, cancellationToken);
                logger.LogDebug(".NET project setup for {CsprojFile}", csprojFile.FullName);
            }
        }
        else if (dotNetService.FindCsproj(options.BaseDirectory).Count > 0 && !configService.Exists())
        {
            // Restore on a .NET project that was initialized with winapp init (no winapp.yaml)
            logger.LogError(".NET project detected, but no winapp.yaml configuration file was found. The 'winapp restore' command is not supported for .NET projects without a winapp.yaml. Please run 'dotnet restore' to restore NuGet packages for this project.");
            return 1;
        }

        // Configuration / prompting phase
        bool hadExistingConfig;
        WinappConfig? config;
        bool shouldGenerateManifest;
        ManifestGenerationInfo? manifestGenerationInfo;
        bool shouldEnableDeveloperMode;
        string? recommendedTfm;

        (var initializationResult, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm) = await InitializeConfigurationAsync(options, isDotNetProject, csprojFile, cancellationToken);
        if (initializationResult != 0)
        {
            return initializationResult;
        }

        // Handle config-only mode: just create/validate config file and exit (only for non-.NET path)
        if (!isDotNetProject && options.ConfigOnly)
        {
            if (hadExistingConfig && config != null)
            {
                logger.LogInformation("{UISymbol} Existing configuration file found and validated → {ConfigPath}", UiSymbols.Check, configService.ConfigPath);
                logger.LogInformation("{UISymbol} Configuration contains {PackageCount} packages", UiSymbols.Package, config.Packages.Count);

                if (config.Packages.Count > 0)
                {
                    logger.LogInformation("Configured packages:");
                    foreach (var pkg in config.Packages)
                    {
                        logger.LogInformation("{UISymbol} {PackageName} = {PackageVersion}", UiSymbols.Bullet, pkg.Name, pkg.Version);
                    }
                }
            }
            else if (options.SdkInstallMode != SdkInstallMode.None)
            {
                logger.LogInformation("Creating configuration file");

                // Get latest package versions (respecting prerelease option)
                var defaultVersions = new Dictionary<string, string>();
                foreach (var packageName in NugetService.SDK_PACKAGES)
                {
                    try
                    {
                        var version = await nugetService.GetLatestVersionAsync(
                            packageName,
                            options.SdkInstallMode ?? SdkInstallMode.Stable,
                            cancellationToken: cancellationToken);
                        defaultVersions[packageName] = version;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("{UISymbol} Could not get version for {PackageName}: {ErrorMessage}", UiSymbols.Note, packageName, ex.Message);
                    }
                }

                var finalConfig = new WinappConfig();
                foreach (var kvp in defaultVersions)
                {
                    finalConfig.SetVersion(kvp.Key, kvp.Value);
                }

                configService.Save(finalConfig);

                logger.LogDebug("{UISymbol} Configuration file created → {ConfigPath}", UiSymbols.Save, configService.ConfigPath);
                logger.LogDebug("{UISymbol} Added {PackageCount} default SDK packages", UiSymbols.Package, finalConfig.Packages.Count);

                logger.LogDebug("Generated packages");
                foreach (var pkg in finalConfig.Packages)
                {
                    logger.LogDebug("{UISymbol} {PackageName} = {PackageVersion}", UiSymbols.Bullet, pkg.Name, pkg.Version);
                }

                if (options.SdkInstallMode == SdkInstallMode.Experimental)
                {
                    logger.LogDebug("{UISymbol} Prerelease packages were included", UiSymbols.Wrench);
                }
                else if (options.SdkInstallMode == SdkInstallMode.Preview)
                {
                    logger.LogDebug("{UISymbol} Preview packages were included", UiSymbols.Wrench);
                }
            }
            // else: SdkInstallMode == None and no existing config - nothing to do

            logger.LogInformation("Configuration-only operation completed");
            return 0;
        }

        // Initialize workspace directories (native/C++ projects only)
        DirectoryInfo? globalWinappDir = null;
        DirectoryInfo? localWinappDir = null;

        if (!isDotNetProject)
        {
            if (options.SdkInstallMode == SdkInstallMode.None)
            {
                logger.LogDebug("{UISymbol} SDK installation skipped by user choice", UiSymbols.Skip);
                logger.LogInformation("Configuration processed (SDK installation skipped)");
            }
            else
            {
                // Step 3: Initialize workspace
                globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
                localWinappDir = winappDirectoryService.GetLocalWinappDirectory(options.BaseDirectory);

                // Setup-specific startup messages
                if (!options.RequireExistingConfig)
                {
                    logger.LogDebug("{UISymbol} using config → {ConfigPath}", UiSymbols.Rocket, configService.ConfigPath);
                    logger.LogDebug("{UISymbol} winapp init starting in {BaseDirectory}", UiSymbols.Rocket, options.BaseDirectory);
                    logger.LogDebug("{UISymbol} Global packages → {GlobalWinappDir}", UiSymbols.Folder, globalWinappDir);
                    logger.LogDebug("{UISymbol} Global workspace → {GlobalWinappDir}", UiSymbols.Folder, globalWinappDir);
                    logger.LogDebug("{UISymbol} Local workspace → {LocalWinappDir}", UiSymbols.Folder, localWinappDir);

                    if (options.SdkInstallMode == SdkInstallMode.Experimental)
                    {
                        logger.LogDebug("{UISymbol} Experimental/prerelease packages will be included", UiSymbols.Wrench);
                    }
                }
                else
                {
                    logger.LogDebug("{UISymbol} Global packages → {GlobalWinappDir}", UiSymbols.Folder, globalWinappDir);
                    logger.LogDebug("{UISymbol} Local workspace → {LocalWinappDir}", UiSymbols.Folder, localWinappDir);
                }

                // First ensure basic workspace (for global packages)
                logger.LogDebug("{UISymbol} Initializing workspace at {LocalWinappDir}", UiSymbols.Sync, localWinappDir);
                packageInstallationService.InitializeWorkspace(globalWinappDir);
            }
        }
        else if (options.SdkInstallMode == SdkInstallMode.None)
        {
            logger.LogInformation("{UISymbol} SDK installation skipped by user choice", UiSymbols.Skip);
        }

        var statusLabel = isDotNetProject ? "Setting up .NET project" : "Setting up workspace";
        return await statusService.ExecuteWithStatusAsync(statusLabel, async (taskContext, cancellationToken) =>
        {
            try
            {
                // Config-only mode completes here - skip all other setup steps
                if (options.ConfigOnly)
                {
                    return (0, "Configuration-only operation completed");
                }

                // Enable Developer Mode (for setup only)
                if (!options.RequireExistingConfig)
                {
                    await taskContext.AddSubTaskAsync("Configuring developer mode", async (taskContext, cancellationToken) =>
                    {
                        if (!shouldEnableDeveloperMode)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Skip} Developer Mode setup skipped");
                            return (0, "Developer Mode setup skipped");
                        }
                        try
                        {
                            if (devModeService.IsEnabled())
                            {
                                taskContext.AddDebugMessage("Developer Mode already enabled.");
                                return (0, "Developer mode: already enabled");
                            }
                            taskContext.UpdateSubStatus("Checking Developer Mode");
                            var devModeResult = await devModeService.EnsureWin11DevModeAsync(taskContext, cancellationToken);

                            if (devModeResult == -1)
                            {
                                return (-1, "Developer mode: [red]not enabled[/]");
                            }

                            if (devModeResult != 0 && devModeResult != 3010)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Developer Mode setup returned exit code {devModeResult}");
                            }

                            return (devModeResult, "Developer mode: enabled");
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Developer Mode setup failed: {ex.Message}");
                            return (1, "Developer Mode setup failed");
                        }
                    }, cancellationToken);
                }

                Dictionary<string, string>? usedVersions = null;
                DirectoryInfo? nugetCacheDir = null;
                (int, string) partialResult;
                var sdkInstallMode = options.SdkInstallMode ?? SdkInstallMode.Stable;

                // .NET-specific: Update TargetFramework if needed (independent of SDK install mode)
                if (isDotNetProject && csprojFile != null && recommendedTfm != null)
                {
                    dotNetService.SetTargetFramework(csprojFile, recommendedTfm);
                    taskContext.AddStatusMessage($"{UiSymbols.Check} Updated TargetFramework to {recommendedTfm}");
                }

                // .NET-specific: Add NuGet package references
                if (isDotNetProject && options.SdkInstallMode != SdkInstallMode.None && csprojFile != null)
                {
                    partialResult = await taskContext.AddSubTaskAsync("Adding NuGet packages to project", async (taskContext, cancellationToken) =>
                    {
                        usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var packages = new (string Name, bool Required)[]
                        {
                            (BuildToolsService.BUILD_TOOLS_PACKAGE, true),
                            (DotNetService.WINAPP_SDK_NUGET_PACKAGE, true)
                        };

                        foreach (var (packageName, required) in packages)
                        {
                            taskContext.UpdateSubStatus($"Querying latest {packageName} version");
                            try
                            {
                                var version = await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode, cancellationToken: cancellationToken);
                                taskContext.AddDebugMessage($"{UiSymbols.Package} {packageName} → {version}");
                                usedVersions[packageName] = version;

                                await dotNetService.AddOrUpdatePackageReferenceAsync(csprojFile, packageName, version, cancellationToken);
                                taskContext.AddStatusMessage($"{UiSymbols.Check} Added {packageName} {version}");
                            }
                            catch (Exception ex)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Could not add {packageName}: {ex.Message}");
                                if (required)
                                {
                                    return (1, $"Failed to add {packageName} package reference");
                                }
                            }
                        }

                        return (0, $"NuGet packages added to [underline]{csprojFile.Name}[/]");
                    }, cancellationToken);

                    if (partialResult.Item1 != 0)
                    {
                        return partialResult;
                    }
                }

                // Native/C++ specific: Install SDK packages, headers, and build tools
                if (!isDotNetProject && options.SdkInstallMode != SdkInstallMode.None)
                {
                    // Ensure directories are initialized before use
                    if (globalWinappDir == null || localWinappDir == null)
                    {
                        return (1, "Workspace directories were not initialized.");
                    }

                    // Create all standard workspace directories for full setup/restore
                    nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
                    var includeOut = localWinappDir.CreateSubdirectory("include");
                    var libRoot = localWinappDir.CreateSubdirectory("lib");
                    var binRoot = localWinappDir.CreateSubdirectory("bin");

                    // Step 4: Install packages
                    partialResult = await taskContext.AddSubTaskAsync("Installing SDK packages", async (taskContext, cancellationToken) =>
                    {
                        if (options.RequireExistingConfig && hadExistingConfig && config != null && config.Packages.Count > 0)
                        {
                            // Restore: use packages from existing config
                            var packageNames = config.Packages.Select(p => p.Name).ToArray();
                            usedVersions = await packageInstallationService.InstallPackagesAsync(
                                globalWinappDir,
                                packageNames,
                                taskContext,
                                sdkInstallMode: sdkInstallMode,
                                ignoreConfig: false, // Use config versions for restore
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            // Setup: install standard SDK packages
                            usedVersions = await packageInstallationService.InstallPackagesAsync(
                                globalWinappDir,
                                NugetService.SDK_PACKAGES,
                                taskContext,
                                sdkInstallMode: sdkInstallMode,
                                ignoreConfig: options.IgnoreConfig,
                                cancellationToken: cancellationToken);
                        }

                        if (usedVersions == null)
                        {
                            return (1, "Error installing packages.");
                        }

                        // Step 5: Run cppwinrt and set up projections
                        var cppWinrtExe = cppWinrtService.FindCppWinrtExe(nugetCacheDir, usedVersions);
                        if (cppWinrtExe is null)
                        {
                            return (1, "cppwinrt.exe not found in installed packages.");
                        }

                        taskContext.AddDebugMessage($"{UiSymbols.Tools} Using cppwinrt tool → {cppWinrtExe}");

                        // Copy headers, libs, runtimes
                        taskContext.UpdateSubStatus("Copying headers");
                        packageLayoutService.CopyIncludesFromPackages(nugetCacheDir, includeOut, usedVersions);
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Headers ready → {includeOut}");

                        taskContext.UpdateSubStatus("Copying import libraries");
                        packageLayoutService.CopyLibsAllArch(nugetCacheDir, libRoot, usedVersions);
                        var libArchs = libRoot.Exists ? string.Join(", ", libRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
                        taskContext.AddDebugMessage($"{UiSymbols.Books} Import libs ready for archs: {libArchs}");

                        taskContext.UpdateSubStatus("Copying runtime binaries");
                        packageLayoutService.CopyRuntimesAllArch(nugetCacheDir, binRoot, usedVersions);
                        var binArchs = binRoot.Exists ? string.Join(", ", binRoot.EnumerateDirectories().Select(d => d.Name)) : "(none)";
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Runtime binaries ready for archs: {binArchs}");

                        // Copy Windows App SDK license
                        try
                        {
                            if (usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_PACKAGE, out var wasdkVersion))
                            {
                                var pkgDir = nugetService.GetNuGetPackageDir(BuildToolsService.WINAPP_SDK_PACKAGE, wasdkVersion);
                                var licenseSrc = Path.Combine(pkgDir.FullName, "license.txt");
                                if (File.Exists(licenseSrc))
                                {
                                    var shareDir = Path.Combine(localWinappDir.FullName, "share", BuildToolsService.WINAPP_SDK_PACKAGE);
                                    Directory.CreateDirectory(shareDir);
                                    var licenseDst = Path.Combine(shareDir, "copyright");
                                    File.Copy(licenseSrc, licenseDst, overwrite: true);
                                    taskContext.AddDebugMessage($"{UiSymbols.Check} License copied → {licenseDst}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to copy license: {ex.Message}");
                        }

                        // Collect winmd inputs and run cppwinrt
                        taskContext.UpdateSubStatus("Searching for .winmd metadata");
                        var winmds = packageLayoutService.FindWinmds(nugetCacheDir, usedVersions).ToList();
                        taskContext.AddDebugMessage($"{UiSymbols.Search} Found {winmds.Count} .winmd");
                        if (winmds.Count == 0)
                        {
                            return (2, "No .winmd files found for C++/WinRT projection.");
                        }

                        // Run cppwinrt
                        taskContext.UpdateSubStatus("Generating C++/WinRT projections");
                        await cppWinrtService.RunWithRspAsync(cppWinrtExe, winmds, includeOut, localWinappDir, taskContext, cancellationToken: cancellationToken);
                        taskContext.AddDebugMessage($"{UiSymbols.Check} C++/WinRT headers generated → {includeOut}");

                        partialResult = await taskContext.AddSubTaskAsync("Setting up tools", async (taskContext, cancellationToken) =>
                        {
                            // Step 6: Handle BuildTools
                            var buildToolsPinned = config?.GetVersion(BuildToolsService.BUILD_TOOLS_PACKAGE);
                            var forceLatestBuildTools = options.ForceLatestBuildTools || string.IsNullOrWhiteSpace(buildToolsPinned);

                            if (forceLatestBuildTools && options.RequireExistingConfig)
                            {
                                taskContext.UpdateSubStatus("Installing BuildTools");
                            }
                            else if (!string.IsNullOrWhiteSpace(buildToolsPinned))
                            {
                                taskContext.UpdateSubStatus($"Installing BuildTools {buildToolsPinned}");
                            }

                            var buildToolsPath = await buildToolsService.EnsureBuildToolsAsync(
                                taskContext,
                                forceLatest: forceLatestBuildTools,
                                cancellationToken: cancellationToken);

                            if (buildToolsPath != null)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Check} BuildTools ready → {buildToolsPath}");
                            }

                            return (0, "Tools setup complete");
                        }, cancellationToken);

                        if (partialResult.Item1 != 0)
                        {
                            return partialResult;
                        }

                        return (0, "SDK and Windows App SDK packages downloaded and C++ headers generated in [underline].winapp[/]");
                    }, cancellationToken);

                    if (partialResult.Item1 != 0)
                    {
                        return partialResult;
                    }

                    if (usedVersions == null)
                    {
                        return (1, "Error determining installed package versions.");
                    }
                }

                // Install Windows App SDK Runtime (shared: both .NET and native paths)
                if (options.SdkInstallMode != SdkInstallMode.None)
                {
                    await taskContext.AddSubTaskAsync("Installing Windows App SDK Runtime", async (taskContext, cancellationToken) =>
                    {
                        try
                        {
                            var msixDir = FindWindowsAppSdkMsixDirectory(usedVersions);

                            if (msixDir != null)
                            {
                                // Install Windows App SDK runtime packages
                                (int installedCount, int errorCount) = await InstallWindowsAppRuntimeAsync(msixDir, taskContext, cancellationToken);

                                string? version = null;
                                if (usedVersions != null)
                                {
                                    usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, out version);
                                }

                                if (errorCount > 0)
                                {
                                    return (1, "Some Windows App Runtime packages failed to install.");
                                }
                                else if (installedCount == 0)
                                {
                                    return (0, version != null
                                        ? $"Windows App SDK Runtime ([underline]{version}[/]) already installed"
                                        : "Windows App SDK Runtime already installed");
                                }

                                return (0, version != null
                                    ? $"Windows App SDK Runtime installed: [underline]{version}[/]"
                                    : "Windows App SDK Runtime installed");
                            }
                            else
                            {
                                taskContext.AddStatusMessage($"{UiSymbols.Note} MSIX directory not found, skipping Windows App Runtime installation");
                                return (1, "Error locating Windows App SDK MSIX packages.");
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to install Windows App Runtime: {ex.Message}");
                            return (1, "Windows App Runtime installation failed.");
                        }
                    }, cancellationToken);
                }

                // Generate AppxManifest.xml (for setup only)
                if (!options.RequireExistingConfig)
                {
                    await SetupManifestSubTaskAsync(options, shouldGenerateManifest, manifestGenerationInfo, taskContext, cancellationToken);
                }

                // Save configuration (native/C++ projects only — .NET uses .csproj PackageReferences)
                if (!isDotNetProject && !options.RequireExistingConfig && options.SdkInstallMode != SdkInstallMode.None && usedVersions != null)
                {
                    await taskContext.AddSubTaskAsync("Saving configuration", (taskContext, cancellationToken) =>
                    {
                        // Setup: Save winapp.yaml with used versions
                        var finalConfig = new WinappConfig();
                        // only from SDK_PACKAGES
                        var versionsToSave = usedVersions
                            .Where(kvp => NugetService.SDK_PACKAGES.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        foreach (var kvp in versionsToSave)
                        {
                            finalConfig.SetVersion(kvp.Key, kvp.Value);
                        }
                        configService.Save(finalConfig);
                        taskContext.AddDebugMessage($"{UiSymbols.Save} Wrote config → {configService.ConfigPath}");
                        return Task.FromResult((0, "Configuration file created: [underline]winapp.yaml[/]"));
                    }, cancellationToken);
                }

                if (!options.RequireExistingConfig && options.SdkInstallMode != SdkInstallMode.None && !options.NoGitignore && localWinappDir?.Parent != null)
                {
                    var gitignorePath = Path.Combine(localWinappDir.Parent.FullName, ".gitignore");

                    if (File.Exists(gitignorePath))
                    {
                        await taskContext.AddSubTaskAsync("Updating .gitignore", async (taskContext, cancellationToken) =>
                        {
                            // Update .gitignore to exclude .winapp folder (unless --no-gitignore is specified)
                            var addedWinAppToGitIgnore = await gitignoreService.AddWinAppFolderToGitIgnoreAsync(localWinappDir.Parent, taskContext, cancellationToken);

                            if (addedWinAppToGitIgnore)
                            {
                                return (0, "Added .winapp to [underline].gitignore[/]");
                            }

                            return (0, "[underline].gitignore[/] is up to date");
                        }, cancellationToken);
                    }
                }

                // Update Directory.Packages.props versions to match winapp.yaml if needed (only with SDK installation)
                if (options.SdkInstallMode != SdkInstallMode.None && config != null && directoryPackagesService.Exists(options.ConfigDir))
                {
                    await taskContext.AddSubTaskAsync("Updating Directory.Packages.props", (taskContext, cancellationToken) =>
                    {
                        try
                        {
                            var packageVersions = config.Packages.ToDictionary(
                                p => p.Name,
                                p => p.Version,
                                StringComparer.OrdinalIgnoreCase);

                            var wasUpdated = directoryPackagesService.UpdatePackageVersions(options.ConfigDir, packageVersions, taskContext);
                            return Task.FromResult((0, message: wasUpdated
                                ? "Directory.Packages.props updated"
                                : "Directory.Packages.props is up to date"));
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to update Directory.Packages.props: {ex.Message}");
                            // Don't fail the restore if Directory.Packages.props update fails
                            return Task.FromResult((0, "Directory.Packages.props update failed"));
                        }
                    }, cancellationToken);
                }

                // We're done
                string successMessage;
                if (isDotNetProject)
                {
                    successMessage = ".NET project setup completed successfully";
                }
                else
                {
                    successMessage = options.RequireExistingConfig ? "Restore completed successfully" : "Setup completed successfully";
                }
                if (options.SdkInstallMode == SdkInstallMode.None)
                {
                    successMessage += " (SDK installation skipped)";
                }
                return (0, successMessage);
            }
            catch (OperationCanceledException)
            {
                return (1, "Operation cancelled");
            }
            catch (Exception ex)
            {
                var operation = isDotNetProject ? ".NET Init" : (options.RequireExistingConfig ? "Restore" : "Init");
                taskContext.StatusError($"{operation} failed: {ex.Message}" + Environment.NewLine +
                                        $"{ex.StackTrace}");
                return (1, "Error!");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Selects the .csproj file to configure when multiple are found.
    /// </summary>
    private async Task<FileInfo> SelectCsprojFileAsync(IReadOnlyList<FileInfo> csprojFiles, CancellationToken cancellationToken)
    {
        if (csprojFiles.Count == 1)
        {
            return csprojFiles[0];
        }

        // Multiple .csproj files found — ask the user which one to use
        var choices = csprojFiles.Select(f => f.Name).ToArray();
        var selected = await ansiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title("Multiple .csproj files found. Which project should be configured?")
                .AddChoices(choices),
            cancellationToken);
        return csprojFiles.First(f => f.Name == selected);
    }

    private async Task SetupManifestSubTaskAsync(WorkspaceSetupOptions options, bool shouldGenerateManifest, ManifestGenerationInfo? manifestGenerationInfo, TaskContext taskContext, CancellationToken cancellationToken)
    {
        await taskContext.AddSubTaskAsync("Generating Manifest and Assets", async (taskContext, cancellationToken) =>
        {
            if (!shouldGenerateManifest || manifestGenerationInfo == null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Skip} AppxManifest.xml generation skipped");
                return (0, "Manifest generation skipped");
            }

            try
            {
                await manifestService.GenerateManifestAsync(
                    directory: options.BaseDirectory,
                    manifestGenerationInfo: manifestGenerationInfo,
                    manifestTemplate: ManifestTemplates.Packaged,
                    logoPath: null,
                    executable: null,
                    taskContext,
                    cancellationToken: cancellationToken);

                return (0, "Manifest and Assets created: [underline]appxmanifest.xml[/]");
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Failed to generate manifest: {ex.Message}");
                return (0, "Manifest generation failed, but continuing setup");
            }
        }, cancellationToken);
    }

    private async Task<(int ReturnCode, WinappConfig? Config, bool HadExistingConfig, bool ShouldGenerateManifest, ManifestGenerationInfo? ManifestGenerationInfo, bool ShouldEnableDeveloperMode, string? RecommendedTfm)> InitializeConfigurationAsync(WorkspaceSetupOptions options, bool isDotNetProject, FileInfo? csprojFile, CancellationToken cancellationToken)
    {
        if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null && options.UseDefaults)
        {
            // Default to Stable when --use-defaults
            options.SdkInstallMode = SdkInstallMode.Stable;
        }

        var hadExistingConfig = configService.Exists();
        bool shouldGenerateManifest = true;
        bool shouldEnableDeveloperMode = false;
        string? recommendedTfm = null;
        ManifestGenerationInfo? manifestGenerationInfo = null;
        WinappConfig? config = null;

        // Step 1: Handle configuration requirements
        if (options.RequireExistingConfig && !configService.Exists())
        {
            logger.LogInformation("winapp.yaml not found in {ConfigDir}", options.ConfigDir);
            logger.LogInformation("Run 'winapp init' to initialize a new workspace or navigate to a directory with winapp.yaml");
            return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
        }

        // Step 2: Load or prepare configuration
        if (hadExistingConfig)
        {
            config = configService.Load();

            if (config.Packages.Count == 0 && options.RequireExistingConfig)
            {
                logger.LogInformation("{UISymbol} winapp.yaml found but contains no packages. Nothing to restore.", UiSymbols.Note);
                shouldEnableDeveloperMode = await AskShouldEnableDeveloperModeAsync(options, cancellationToken);
                return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }

            var operation = options.RequireExistingConfig ? "Found" : "Found existing";
            logger.LogDebug("{UISymbol} {Operation} winapp.yaml with {PackageCount} packages", UiSymbols.Package, operation, config.Packages.Count);

            if (!options.RequireExistingConfig && config.Packages.Count > 0)
            {
                logger.LogDebug("{UISymbol} Using pinned package versions from winapp.yaml unless overridden.", UiSymbols.Note);
            }

            // For setup command: ask about overwriting existing config (only if not skipping SDK installation and not config-only mode)
            if (!options.RequireExistingConfig && !options.IgnoreConfig && !options.ConfigOnly && options.SdkInstallMode != SdkInstallMode.None && config.Packages.Count > 0)
            {
                if (options.UseDefaults)
                {
                    options.IgnoreConfig = true;
                }
                else
                {
                    var overwriteConfig = await ansiConsole.PromptAsync(new ConfirmationPrompt("winapp.yaml exists with pinned versions. Overwrite?"), cancellationToken);
                    shouldGenerateManifest = await AskShouldGenerateManifestAsync(options, cancellationToken);
                    if (shouldGenerateManifest)
                    {
                        manifestGenerationInfo = await PromptForManifestInfoAsync(options, cancellationToken);
                    }
                    if (!overwriteConfig)
                    {
                        options.IgnoreConfig = true;
                    }
                    else
                    {
                        await AskSdkInstallModeAsync(options, isDotNetProject, cancellationToken);
                    }
                }
            }
        }
        else
        {
            shouldGenerateManifest = await AskShouldGenerateManifestAsync(options, cancellationToken);
            if (shouldGenerateManifest)
            {
                manifestGenerationInfo = await PromptForManifestInfoAsync(options, cancellationToken);
            }

            await AskSdkInstallModeAsync(options, isDotNetProject, cancellationToken);
            if (options.SdkInstallMode != SdkInstallMode.None)
            {
                config = new WinappConfig();
                logger.LogDebug("{UISymbol} No winapp.yaml found; will generate one after setup.", UiSymbols.New);
            }
        }

        // .NET: Validate TargetFramework (interactive)
        if (isDotNetProject && csprojFile != null)
        {
            if (dotNetService.IsMultiTargeted(csprojFile))
            {
                logger.LogError("The project '{CsprojFile}' uses multi-targeting (TargetFrameworks). winapp init does not support multi-targeted projects.", csprojFile.Name);
                return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
            }

            var currentTfm = dotNetService.GetTargetFramework(csprojFile);
            logger.LogDebug("Current TargetFramework: {Tfm}", currentTfm ?? "(not set)");

            if (currentTfm == null || !dotNetService.IsTargetFrameworkSupported(currentTfm))
            {
                recommendedTfm = dotNetService.GetRecommendedTargetFramework(currentTfm);

                if (!options.UseDefaults)
                {
                    var currentDisplay = currentTfm ?? "(not set)";

                    var promptSuffix = options.SdkInstallMode != SdkInstallMode.None
                        ? " (Required for Windows App SDK)"
                        : "";

                    var shouldUpdate = await ansiConsole.PromptAsync(
                        new ConfirmationPrompt($"Update TargetFramework to \"{recommendedTfm}\"{promptSuffix}?"),
                        cancellationToken);

                    if (!shouldUpdate)
                    {
                        if (options.SdkInstallMode != SdkInstallMode.None)
                        {
                            logger.LogError("TargetFramework '{Tfm}' is not supported for Windows App SDK. Cannot continue.", currentDisplay);
                            return (1, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
                        }

                        // Not installing SDKs, so TFM update is not required — skip it
                        recommendedTfm = null;
                    }
                }
                else
                {
                    var currentDisplay = currentTfm ?? "(not set)";
                    logger.LogWarning(
                        "TargetFramework '{CurrentTfm}' is not supported for Windows App SDK. Automatically updating to '{RecommendedTfm}' because --use-defaults was specified.",
                        currentDisplay,
                        recommendedTfm);
                    logger.LogInformation("Automatically updating TargetFramework from {CurrentTfm} to {RecommendedTfm} because --use-defaults was specified.", Markup.Escape(currentDisplay), recommendedTfm);
                }
            }
            else
            {
                logger.LogDebug("{UISymbol} TargetFramework '{Tfm}' is supported", UiSymbols.Check, currentTfm);
            }
        }

        shouldEnableDeveloperMode = await AskShouldEnableDeveloperModeAsync(options, cancellationToken);

        return (0, config, hadExistingConfig, shouldGenerateManifest, manifestGenerationInfo, shouldEnableDeveloperMode, recommendedTfm);
    }

    private async Task<ManifestGenerationInfo?> PromptForManifestInfoAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigOnly)
        {
            return null;
        }

        return await manifestService.PromptForManifestInfoAsync(options.BaseDirectory, null, null, "1.0.0.0", "Windows Application", null, options.UseDefaults, cancellationToken);
    }

    private async Task<bool> AskShouldEnableDeveloperModeAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigOnly || options.RequireExistingConfig)
        {
            return false;
        }

        if (devModeService.IsEnabled())
        {
            return false;
        }

        if (options.UseDefaults)
        {
            return false;
        }

        return await ansiConsole.PromptAsync(
            new ConfirmationPrompt("Enable Developer Mode (requires elevation and you will be prompted by User Account Control)"),
            cancellationToken);
    }

    private async Task<bool> AskShouldGenerateManifestAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken)
    {
        if (options.RequireExistingConfig)
        {
            return true;
        }

        // Check if manifest already exists, and if so, ask about overwriting
        var manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider, options.BaseDirectory);
        if ((manifestPath?.Exists) == true)
        {
            logger.LogDebug("{UISymbol} AppxManifest.xml already exists at {ManifestPath}", UiSymbols.Check, manifestPath.FullName);
            if (options.UseDefaults)
            {
                // With --use-defaults, skip overwriting existing manifest (non-destructive)
                return false;
            }
            else
            {
                return await ansiConsole.PromptAsync(new ConfirmationPrompt("AppxManifest.xml already exists. Overwrite?"), cancellationToken);
            }
        }

        return true;
    }

    private async Task AskSdkInstallModeAsync(WorkspaceSetupOptions options, bool isDotNetProject, CancellationToken cancellationToken)
    {
        // For init (not restore), prompt for SDK installation choice if not specified
        if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null)
        {
            // Determine which packages to show versions for
            var packages = isDotNetProject
                ? [BuildToolsService.WINAPP_SDK_PACKAGE]
                : new[] { BuildToolsService.CPP_SDK_PACKAGE, BuildToolsService.WINAPP_SDK_PACKAGE };

            // Fetch versions for all modes in parallel
            var modes = new[] { SdkInstallMode.Stable, SdkInstallMode.Preview, SdkInstallMode.Experimental };
            var versionTasks = modes
                .SelectMany(mode => packages.Select(pkg => (Mode: mode, Package: pkg, Task: nugetService.GetLatestVersionAsync(pkg, sdkInstallMode: mode, cancellationToken: cancellationToken))))
                .ToList();
            await Task.WhenAll(versionTasks.Select(v => v.Task));

            // Build a lookup: (mode) → version label
            var versionsByMode = modes.ToDictionary(
                mode => mode,
                mode =>
                {
                    var parts = versionTasks
                        .Where(v => v.Mode == mode)
                        .Select(v => $"{(v.Package == BuildToolsService.CPP_SDK_PACKAGE ? "Windows SDK" : "Windows App SDK")} [green]{v.Task.Result}[/]");
                    return string.Join(", ", parts);
                });

            var label = isDotNetProject ? "Windows App SDK" : "SDKs";
            string[] sdkChoices = [
                $"Setup Stable {label} ({versionsByMode[SdkInstallMode.Stable]})",
                $"Setup Preview {label} ({versionsByMode[SdkInstallMode.Preview]})",
                $"Setup Experimental {label} ({versionsByMode[SdkInstallMode.Experimental]})",
                $"Do not setup {label}"
            ];

            ansiConsole.WriteLine($"Select {label} setup option:");
            var sdkPrompt = new SelectionPrompt<string>()
                .AddChoices(sdkChoices);

            var sdkChoice = await ansiConsole.PromptAsync(sdkPrompt, cancellationToken);

            ansiConsole.Cursor.MoveUp();
            ansiConsole.Write("\x1b[2K"); // Clear line

            if (sdkChoice == sdkChoices[0])
            {
                options.SdkInstallMode = SdkInstallMode.Stable;
            }
            else if (sdkChoice == sdkChoices[1])
            {
                options.SdkInstallMode = SdkInstallMode.Preview;
            }
            else if (sdkChoice == sdkChoices[2])
            {
                options.SdkInstallMode = SdkInstallMode.Experimental;
            }
            else
            {
                options.SdkInstallMode = SdkInstallMode.None;
                logger.LogInformation("Setup {Label}: Do not setup {Label}", label, label);
                return;
            }

            ansiConsole.MarkupLine($"Setup {label}: [underline]{Markup.Remove(sdkChoice["Setup ".Length..])}[/]");
        }
    }

    /// <summary>
    /// Package entry information from MSIX inventory
    /// </summary>
    public class MsixPackageEntry
    {
        public required string FileName { get; set; }
        public required string PackageIdentity { get; set; }
    }

    /// <summary>
    /// Parses the MSIX inventory file and returns package entries (shared implementation)
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of package entries, or null if not found</returns>
    public static async Task<List<MsixPackageEntry>?> ParseMsixInventoryAsync(TaskContext taskContext, DirectoryInfo msixDir, CancellationToken cancellationToken)
    {
        var architecture = GetSystemArchitecture();

        taskContext.AddDebugMessage($"{UiSymbols.Note} Detected system architecture: {architecture}");

        // Look for MSIX packages for the current architecture
        var msixArchDir = Path.Combine(msixDir.FullName, $"win10-{architecture}");
        if (!Directory.Exists(msixArchDir))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No MSIX packages found for architecture {architecture}");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available directories: {string.Join(", ", msixDir.GetDirectories().Select(d => d.Name))}");
            return null;
        }

        // Read the MSIX inventory file
        var inventoryPath = Path.Combine(msixArchDir, "msix.inventory");
        if (!File.Exists(inventoryPath))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No msix.inventory file found in {msixArchDir}");
            return null;
        }

        var inventoryLines = await File.ReadAllLinesAsync(inventoryPath, cancellationToken);
        var packageEntries = inventoryLines
            .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new MsixPackageEntry { FileName = parts[0].Trim(), PackageIdentity = parts[1].Trim() })
            .ToList();

        if (packageEntries.Count == 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No valid package entries found in msix.inventory");
            return null;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Package} Found {packageEntries.Count} MSIX packages in inventory");

        return packageEntries;
    }

    /// <summary>
    /// Installs Windows App SDK runtime MSIX packages for the current system architecture
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<(int InstalledCount, int ErrorCount)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var architecture = GetSystemArchitecture();

        // Get package entries from MSIX inventory
        var packageEntries = await ParseMsixInventoryAsync(taskContext, msixDir, cancellationToken);
        if (packageEntries == null || packageEntries.Count == 0)
        {
            return (0, 0);
        }

        var msixArchDir = Path.Combine(msixDir.FullName, $"win10-{architecture}");

        // Build package data for PowerShell script
        var packageData = new List<string>();
        foreach (var entry in packageEntries)
        {
            var msixFilePath = Path.Combine(msixArchDir, entry.FileName);
            if (!File.Exists(msixFilePath))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} MSIX file not found: {msixFilePath}");
                continue;
            }

            // Parse the PackageIdentity (format: Name_Version_Architecture_PublisherId)
            var identityParts = entry.PackageIdentity.Split('_');
            var packageName = identityParts[0];
            var newVersionString = identityParts.Length >= 2 ? identityParts[1] : "";

            packageData.Add($"@{{Path='{msixFilePath}';Identity='{entry.PackageIdentity}';Name='{packageName}';Version='{newVersionString}';FileName='{entry.FileName}'}}");
        }

        if (packageData.Count == 0)
        {
            return (0, 0);
        }

        // Create compact PowerShell script with reusable function
        var script = $@"
function Test-PackageNeedsInstall($pkg) {{
    $exactMatch = Get-AppxPackage | Where-Object {{ $_.PackageFullName -eq $pkg.Identity }}
    if ($exactMatch) {{ return $false }}
    
    $existing = Get-AppxPackage -Name $pkg.Name -ErrorAction SilentlyContinue
    if (-not $existing) {{ return $true }}
    
    $shouldUpgrade = $false
    foreach ($p in $existing) {{ if ([version]$pkg.Version -gt [version]$p.Version) {{ $shouldUpgrade = $true; break }} }}
    return $shouldUpgrade
}}

$packages = @({string.Join(",", packageData)})
$toInstall = @()

foreach ($pkg in $packages) {{
    if (Test-PackageNeedsInstall $pkg) {{
        $toInstall += $pkg.Path
        Write-Output ""INSTALL|$($pkg.FileName)|Will install""
    }} else {{
        Write-Output ""SKIP|$($pkg.FileName)|Already installed or newer version exists""
    }}
}}

if ($toInstall.Count -gt 0) {{
    Write-Output ""INSTALLING|$($toInstall.Count) packages will be installed""
    foreach ($path in $toInstall) {{
        try {{
            Add-AppxPackage -Path $path -ForceApplicationShutdown -ErrorAction Stop
            Write-Output ""SUCCESS|$(Split-Path $path -Leaf)|Installation successful""
        }} catch {{
            Write-Output ""ERROR|$(Split-Path $path -Leaf)|$($_.Exception.Message)""
        }}
    }}
}} else {{
    Write-Output ""COMPLETE|No packages need to be installed""
}}";

        taskContext.AddDebugMessage($"{UiSymbols.Info} Checking and installing {packageEntries.Count} MSIX packages");

        // Execute the batch script
        var (exitCode, output) = await powerShellService.RunCommandAsync(script, taskContext, cancellationToken: cancellationToken);

        // Parse the output to provide user feedback
        var outputLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line));

        var installedCount = 0;
        var errorCount = 0;

        foreach (var line in outputLines)
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 2)
            {
                continue;
            }

            var action = parts[0];
            var fileName = parts[1];
            var message = parts.Length > 2 ? parts[2] : "";

            switch (action)
            {
                case "SKIP":
                    taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: {message}");
                    break;

                case "INSTALL":
                    taskContext.AddDebugMessage($"{UiSymbols.Info} {fileName}: {message}");
                    break;

                case "INSTALLING":
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Info} {message}");
                    }
                    break;

                case "SUCCESS":
                    installedCount++;
                    taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: {message}");
                    break;

                case "ERROR":
                    errorCount++;
                    taskContext.AddDebugMessage($"{UiSymbols.Note} {fileName}: {message}");
                    break;

                case "COMPLETE":
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} {message}");
                    }
                    break;
            }
        }

        // Provide summary feedback
        if (installedCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Check} Installed {installedCount} MSIX packages");
        }
        if (errorCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} {errorCount} packages failed to install");
        }

        return (installedCount, errorCount);
    }

    /// <summary>
    /// Gets the current system architecture string for package selection
    /// </summary>
    /// <returns>Architecture string (x64, arm64, x86)</returns>
    public static string GetSystemArchitecture()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        return arch switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64" // Default fallback
        };
    }

    /// <summary>
    /// Finds the MSIX directory for Windows App SDK runtime packages
    /// </summary>
    /// <param name="usedVersions">Optional dictionary of package versions to look for specific installed packages</param>
    /// <returns>The path to the MSIX directory, or null if not found</returns>
    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null)
    {
        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
        return FindMsixDirectoryInNuGetCache(nugetCacheDir, usedVersions);
    }

    /// <summary>
    /// Searches the NuGet global packages cache (lowercase id/version folder convention).
    /// </summary>
    private static DirectoryInfo? FindMsixDirectoryInNuGetCache(DirectoryInfo nugetCacheDir, Dictionary<string, string>? usedVersions)
    {
        if (usedVersions != null)
        {
            // Try runtime package first (Windows App SDK 1.8+)
            if (usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, out var runtimeVersion))
            {
                var msixDir = TryGetMsixDirectoryFromNuGetCache(nugetCacheDir, BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, runtimeVersion);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }

            // Fallback to main package
            if (usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_PACKAGE, out var mainVersion))
            {
                var msixDir = TryGetMsixDirectoryFromNuGetCache(nugetCacheDir, BuildToolsService.WINAPP_SDK_PACKAGE, mainVersion);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        // General scan: look for any runtime package directories
        var runtimeDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE.ToLowerInvariant()));
        if (runtimeDir.Exists)
        {
            foreach (var versionDir in runtimeDir.GetDirectories().OrderByDescending(d => d.Name, new VersionStringComparer()))
            {
                var msixDir = TryGetMsixDirectoryFromPath(versionDir);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        // Fallback: main package
        var mainDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, BuildToolsService.WINAPP_SDK_PACKAGE.ToLowerInvariant()));
        if (mainDir.Exists)
        {
            foreach (var versionDir in mainDir.GetDirectories().OrderByDescending(d => d.Name, new VersionStringComparer()))
            {
                var msixDir = TryGetMsixDirectoryFromPath(versionDir);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the NuGet cache for a specific package/version (lowercase ID/version layout).
    /// </summary>
    private static DirectoryInfo? TryGetMsixDirectoryFromNuGetCache(DirectoryInfo nugetCacheDir, string packageId, string version)
    {
        // NuGet global cache uses lowercase package IDs
        var pkgVersionDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, packageId.ToLowerInvariant(), version));
        return TryGetMsixDirectoryFromPath(pkgVersionDir);
    }

    /// <summary>
    /// Helper method to check if an MSIX directory exists for a given package path
    /// </summary>
    /// <param name="packagePath">The full path to the package directory</param>
    /// <returns>The MSIX directory path if it exists, null otherwise</returns>
    private static DirectoryInfo? TryGetMsixDirectoryFromPath(DirectoryInfo packagePath)
    {
        var msixDir = new DirectoryInfo(Path.Combine(packagePath.FullName, "tools", "MSIX"));
        return msixDir.Exists ? msixDir : null;
    }

    /// <summary>
    /// Comparer for sorting version strings, including prerelease support
    /// </summary>
    private class VersionStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null && y == null)
            {
                return 0;
            }
            if (x == null)
            {
                return -1;
            }
            if (y == null)
            {
                return 1;
            }

            // Use the same comparison logic as NugetService.CompareVersions
            return NugetService.CompareVersions(x, y);
        }
    }
}
