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
    IPackageRegistrationService packageRegistrationService,
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
                // The "why we're skipping" message is emitted by AskSdkInstallModeAsync (interactive
                // choice, --setup-sdks none) or — for .NET — by the early-exit when the project
                // already references WinAppSDK. Don't repeat a generic / potentially-misleading
                // "by user choice" line here (#464).
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
            // For .NET projects: AskSdkInstallModeAsync already logged the actual reason we're
            // skipping (auto-skipped because WinAppSDK is already referenced, or the user picked
            // "Do not setup", or --setup-sdks=none was passed). Don't append a misleading
            // "by user choice" line on top of that (#464).
        }

        // Prompt to install the WinApp CLI package before entering the live display context
        // (Spectre.Console does not allow interactive prompts inside a live display)
        var installWinAppPackage = false;
        if (isDotNetProject && csprojFile != null)
        {
            var hasWinAppPackage = await dotNetService.HasPackageReferenceAsync(
                csprojFile,
                DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE,
                cancellationToken);

            if (hasWinAppPackage)
            {
                logger.LogDebug("{UISymbol} {Package} already referenced by project; skipping install prompt",
                    UiSymbols.Skip, DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE);
                installWinAppPackage = true;
            }
            else if (options.UseDefaults)
            {
                installWinAppPackage = true;
            }
            else
            {
                installWinAppPackage = await ShowConfirmationPromptAsync(
                    ansiConsole,
                    $"Add package {DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE}? (Enables running the app packaged via 'dotnet run')",
                    cancellationToken);
                if (!installWinAppPackage)
                {
                    logger.LogWarning("{UISymbol} Skipped {Package} — packaged app support via 'dotnet run' will not be available",
                        UiSymbols.Warning, DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE);
                }
            }
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

                // .NET-specific: Update TargetFramework (independent of SDK install mode)
                if (isDotNetProject && csprojFile != null && recommendedTfm != null)
                {
                    dotNetService.SetTargetFramework(csprojFile, recommendedTfm);
                    taskContext.AddStatusMessage($"{UiSymbols.Check} Updated TargetFramework to {recommendedTfm}");
                }

                // .NET-specific: Add NuGet package references and configure project
                if (isDotNetProject && csprojFile != null)
                {
                    if (await dotNetService.UpdatePublishProfileAsync(csprojFile, cancellationToken))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Updated PublishProfile with existence condition");
                    }

                    if (await dotNetService.EnsureRuntimeIdentifierAsync(csprojFile, cancellationToken))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Added default RuntimeIdentifier");
                    }

                    // Build dynamic package list:
                    // WinApp integration package is added only when the user opted in
                    var packages = new List<(string Name, bool Required)>();

                    if (installWinAppPackage)
                    {
                        // Non-required: a transient NuGet failure should not abort init
                        packages.Add((DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE, false));
                    }

                    if (options.SdkInstallMode != SdkInstallMode.None)
                    {
                        packages.Add((DotNetService.WINAPP_SDK_NUGET_PACKAGE, true));
                    }

                    partialResult = await taskContext.AddSubTaskAsync("Adding NuGet packages to project", async (taskContext, cancellationToken) =>
                    {
                        usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        // When SdkInstallMode is None, still use Stable versions for build tools packages
                        var versionQueryMode = sdkInstallMode == SdkInstallMode.None ? SdkInstallMode.Stable : sdkInstallMode;

                        // Query existing package versions so we can preserve them
                        // (except for the WinApp CLI package which should always be updated)
                        var existingVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var packageList = await dotNetService.GetPackageListAsync(csprojFile, includeTransitive: false, cancellationToken);
                            var project = packageList?.Projects?.FirstOrDefault();
                            if (project is not null)
                            {
                                foreach (var pkg in (project.Frameworks ?? [])
                                    .SelectMany(f => f.TopLevelPackages ?? []))
                                {
                                    existingVersions[pkg.Id] = pkg.ResolvedVersion;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not query existing packages: {ex.Message}");
                        }

                        foreach (var (packageName, required) in packages)
                        {
                            // Preserve existing package versions unless it's the WinApp CLI package
                            if (existingVersions.TryGetValue(packageName, out var existingVersion)
                                && !string.Equals(packageName, DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE, StringComparison.OrdinalIgnoreCase))
                            {
                                usedVersions[packageName] = existingVersion;
                                taskContext.AddStatusMessage($"{UiSymbols.Check} Keeping {packageName} {existingVersion}");
                                continue;
                            }

                            taskContext.UpdateSubStatus($"Querying latest {packageName} version");
                            string? version = null;
                            try
                            {
                                version = await nugetService.GetLatestVersionAsync(packageName, versionQueryMode, cancellationToken: cancellationToken);
                                if (version != null)
                                {
                                    taskContext.AddDebugMessage($"{UiSymbols.Package} {packageName} → {version}");
                                }
                            }
                            catch (Exception ex)
                            {
                                taskContext.AddDebugMessage($"{UiSymbols.Note} Could not get version for {packageName}: {ex.Message}");
                                if (required)
                                {
                                    return (1, $"Failed to get version for {packageName}");
                                }
                            }

                            try
                            {
                                version = await dotNetService.AddOrUpdatePackageReferenceAsync(csprojFile, packageName, version, cancellationToken);
                                usedVersions[packageName] = version;
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

                    // Apply MSIX csproj properties if the WindowsAppSDK package is in the project
                    // (whether we just added it or it was already there)
                    if (await dotNetService.HasPackageReferenceAsync(csprojFile, DotNetService.WINAPP_SDK_NUGET_PACKAGE, cancellationToken))
                    {
                        if (await dotNetService.EnsureEnableMsixToolingAsync(csprojFile, cancellationToken))
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Check} Enabled MSIX tooling");
                        }

                        if (await dotNetService.RemoveWindowsPackageTypeNoneAsync(csprojFile, cancellationToken))
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Check} Removed WindowsPackageType=None to enable packaged app mode");
                        }
                    }

                    // Add descriptive comments above package references in the csproj
                    var packageComments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE] = "WinApp CLI integration: enables 'dotnet run' support for packaged apps",
                        [DotNetService.WINAPP_SDK_NUGET_PACKAGE] = "Windows App SDK: provides WinUI 3, app lifecycle, windowing, and other modern Windows APIs"
                    };
                    await dotNetService.AnnotatePackageReferencesAsync(csprojFile, packageComments, cancellationToken);
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

                return (0, "Manifest and Assets created: [underline]Package.appxmanifest[/]");
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
                    var overwriteConfig = await ShowConfirmationPromptAsync(ansiConsole, "winapp.yaml exists with pinned versions. Overwrite?", cancellationToken);
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
                        await AskSdkInstallModeAsync(options, isDotNetProject, csprojFile, cancellationToken);
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

            await AskSdkInstallModeAsync(options, isDotNetProject, csprojFile, cancellationToken);
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

                    var shouldUpdate = await ShowConfirmationPromptAsync(ansiConsole, $"Update TargetFramework to \"{recommendedTfm}\"{promptSuffix}?", cancellationToken);

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

    private static async Task<bool> ShowConfirmationPromptAsync(IAnsiConsole ansiConsole, string prompt, CancellationToken cancellationToken)
    {
        var result = await ansiConsole.PromptAsync(new ConfirmationPrompt(prompt), cancellationToken);

        ansiConsole.Cursor.MoveUp();
        ansiConsole.Write("\x1b[2K"); // Clear line
        ansiConsole.MarkupLine($"{prompt}: [underline]{(result ? "Yes" : "No")}[/]");

        return result;
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

        return await ShowConfirmationPromptAsync(ansiConsole, "Enable Developer Mode (requires elevation and you will be prompted by User Account Control)", cancellationToken);
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
            logger.LogDebug("{UISymbol} {ManifestFileName} already exists at {ManifestPath}", UiSymbols.Check, manifestPath.Name, manifestPath.FullName);
            if (options.UseDefaults)
            {
                // With --use-defaults, skip overwriting existing manifest (non-destructive)
                return false;
            }
            else
            {
                return await ShowConfirmationPromptAsync(ansiConsole, $"{manifestPath.Name} already exists. Overwrite?", cancellationToken);
            }
        }

        return true;
    }

    private async Task AskSdkInstallModeAsync(WorkspaceSetupOptions options, bool isDotNetProject, FileInfo? csprojFile, CancellationToken cancellationToken)
    {
        // For init (not restore), prompt for SDK installation choice if not specified
        if (!options.RequireExistingConfig && !options.ConfigOnly && options.SdkInstallMode == null)
        {
            // If the .NET project already references WinAppSDK, skip the prompt and default to None.
            // This call may take a while on a fresh machine because `dotnet list package` triggers
            // an implicit restore — surface a spinner so the user knows we're doing something (#463).
            if (isDotNetProject && csprojFile != null)
            {
                var alreadyReferencesWinAppSdk = await RunWithStatusAsync(
                    "Detecting project SDK references...",
                    ct => dotNetService.HasPackageReferenceAsync(csprojFile, DotNetService.WINAPP_SDK_NUGET_PACKAGE, ct),
                    cancellationToken);
                if (alreadyReferencesWinAppSdk)
                {
                    options.SdkInstallMode = SdkInstallMode.None;
                    logger.LogInformation("{UISymbol} Project already references {PackageName}; skipping Windows App SDK setup.", UiSymbols.Check, DotNetService.WINAPP_SDK_NUGET_PACKAGE);
                    return;
                }
            }
            // Determine which packages to show versions for
            var packages = isDotNetProject
                ? [BuildToolsService.WINAPP_SDK_PACKAGE]
                : new[] { BuildToolsService.CPP_SDK_PACKAGE, BuildToolsService.WINAPP_SDK_PACKAGE };

            // Fetch versions for all modes in parallel (failures are non-fatal). On a fresh machine
            // these NuGet feed calls can take many seconds; show a spinner so the prompt doesn't
            // appear to hang (#463).
            var modes = new[] { SdkInstallMode.Stable, SdkInstallMode.Preview, SdkInstallMode.Experimental };
            var versionTasks = await RunWithStatusAsync(
                "Fetching latest SDK versions...",
                async ct =>
                {
                    var tasks = modes
                        .SelectMany(mode => packages.Select(pkg => (Mode: mode, Package: pkg, Task: SafeGetLatestVersionAsync(pkg, mode, ct))))
                        .ToList();
                    await Task.WhenAll(tasks.Select(v => v.Task));
                    return tasks;
                },
                cancellationToken);

            // Build a lookup: (mode) → version label
            var versionsByMode = modes.ToDictionary(
                mode => mode,
                mode =>
                {
                    var parts = versionTasks
                        .Where(v => v.Mode == mode && v.Task.Result != null)
                        .Select(v => $"{(v.Package == BuildToolsService.CPP_SDK_PACKAGE ? "Windows SDK" : "Windows App SDK")} [green]{v.Task.Result}[/]");
                    return string.Join(", ", parts);
                });

            var label = isDotNetProject ? "Windows App SDK" : "SDKs";
            string FormatChoice(string modeLabel, SdkInstallMode mode)
            {
                var versions = versionsByMode[mode];
                return string.IsNullOrEmpty(versions)
                    ? $"Setup {modeLabel} {label}"
                    : $"Setup {modeLabel} {label} ({versions})";
            }
            string[] sdkChoices = [
                FormatChoice("Stable", SdkInstallMode.Stable),
                FormatChoice("Preview", SdkInstallMode.Preview),
                FormatChoice("Experimental", SdkInstallMode.Experimental),
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

    private async Task<string?> SafeGetLatestVersionAsync(string packageName, SdkInstallMode mode, CancellationToken cancellationToken)
    {
        try
        {
            return await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode: mode, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to fetch latest version for {PackageName} ({Mode}): {ErrorMessage}", packageName, mode, ex.Message);
            return null;
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
    /// Reads the actual package Name and Version from the AppxManifest.xml inside an MSIX file.
    /// The MSIX inventory file can have incorrect package names (e.g., the DDLM), so we read
    /// the real identity directly from the package to ensure correct installation checks.
    /// </summary>
    private static (string? Name, string? Version) ReadMsixIdentity(string msixFilePath, TaskContext taskContext)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(msixFilePath);
            var manifestEntry = zip.GetEntry("AppxManifest.xml");
            if (manifestEntry == null)
            {
                return (null, null);
            }

            using var stream = manifestEntry.Open();
            var doc = System.Xml.Linq.XDocument.Load(stream);
            var identityElement = doc.Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Identity");

            var name = identityElement?.Attribute("Name")?.Value;
            var version = identityElement?.Attribute("Version")?.Value;
            return (name, version);
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not read identity from {Path.GetFileName(msixFilePath)}: {ex.Message}");
            return (null, null);
        }
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

        // Build list of packages to evaluate
        var packagesToCheck = new List<(string FilePath, string PackageName, string NewVersion, string FileName)>();
        foreach (var entry in packageEntries)
        {
            var msixFilePath = Path.Combine(msixArchDir, entry.FileName);
            if (!File.Exists(msixFilePath))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} MSIX file not found: {msixFilePath}");
                continue;
            }

            // Read the actual package identity from the MSIX's AppxManifest.xml.
            // The inventory file's PackageIdentity can differ from the real installed name.
            var (packageName, newVersionString) = ReadMsixIdentity(msixFilePath, taskContext);
            if (packageName == null)
            {
                // Fallback: parse from inventory identity string
                var identityParts = entry.PackageIdentity.Split('_');
                packageName = identityParts[0];
                newVersionString = identityParts.Length >= 2 ? identityParts[1] : "";
            }

            packagesToCheck.Add((msixFilePath, packageName, newVersionString ?? "", entry.FileName));
        }

        if (packagesToCheck.Count == 0)
        {
            return (0, 0);
        }

        taskContext.AddDebugMessage($"{UiSymbols.Info} Checking and installing {packagesToCheck.Count} MSIX packages");

        var installedCount = 0;
        var errorCount = 0;

        foreach (var (filePath, packageName, newVersion, fileName) in packagesToCheck)
        {
            // Check if already installed with same or newer version
            var installedVersion = packageRegistrationService.GetInstalledVersion(packageName);
            if (installedVersion != null)
            {
                if (Version.TryParse(installedVersion, out var existing) &&
                    Version.TryParse(newVersion, out var incoming) &&
                    existing >= incoming)
                {
                    taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: Already installed or newer version exists");
                    continue;
                }
            }

            taskContext.AddDebugMessage($"{UiSymbols.Info} {fileName}: Will install");

            try
            {
                await packageRegistrationService.InstallPackageAsync(filePath, cancellationToken);
                installedCount++;
                taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: Installation successful");
            }
            catch (Exception ex)
            {
                errorCount++;
                taskContext.AddDebugMessage($"{UiSymbols.Note} {fileName}: {ex.Message}");
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
    /// Runs <paramref name="work"/> while showing a Spectre.Console spinner with <paramref name="message"/>.
    /// In non-interactive contexts (redirected output, no Information logging), falls back to a single
    /// log line so the user still sees what's happening (#463).
    /// </summary>
    private async Task<T> RunWithStatusAsync<T>(string message, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (Environment.UserInteractive
            && !Console.IsOutputRedirected
            && logger.IsEnabled(LogLevel.Information)
            && ansiConsole.Profile.Capabilities.Interactive)
        {
            T result = default!;
            await ansiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(message, async _ =>
                {
                    result = await work(cancellationToken);
                });
            return result;
        }

        logger.LogInformation("{Message}", message);
        return await work(cancellationToken);
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
