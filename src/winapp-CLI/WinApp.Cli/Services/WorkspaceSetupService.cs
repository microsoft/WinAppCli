// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Runtime.InteropServices;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Shared service for setting up winapp workspaces. Split into partials:
// - this file: orchestration (SetupWorkspaceAsync, init/restore flow, JS bindings step glue)
// - WorkspaceSetupService.Options.cs: option DTO (WorkspaceSetupOptions)
// - WorkspaceSetupService.Prompts.cs: Spectre.Console prompts (SDK choice, manifest, dev mode, .csproj picker, bindings kind)
// - WorkspaceSetupService.Msix.cs: Windows App SDK runtime MSIX install / NuGet-cache discovery
internal partial class WorkspaceSetupService(
    IConfigService configService,
    IWinappDirectoryService winappDirectoryService,
    IPackageInstallationService packageInstallationService,
    IBuildToolsService buildToolsService,
    ICppWinrtService cppWinrtService,
    IJsBindingsWorkspaceService jsBindingsWorkspaceService,
    IPackageLayoutService packageLayoutService,
    IWinmdsLockfileService winmdsLockfileService,
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

                // Persist the prompt's freshly-injected jsBindings (and any
                // cppProjections override) even under --config-only.
                if (options.AddJsBindings && config.JsBindings is not null)
                {
                    if (options.SkipCppProjections)
                    {
                        // SaveJsBindingsOnly only splices jsBindings; full-save
                        // is the simplest way to round-trip cppProjections too
                        // (loses comments — acceptable trade-off for a niche
                        // --config-only + JS-only path).
                        configService.Save(config);
                    }
                    else
                    {
                        configService.SaveJsBindingsOnly(config);
                    }
                    logger.LogDebug("{UISymbol} Persisted updated configuration with jsBindings → {ConfigPath}", UiSymbols.Save, configService.ConfigPath);
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

                var finalConfig = new WinappConfig
                {
                    // Preserve JsBindings + CppProjections across re-init.
                    JsBindings = config?.JsBindings,
                    CppProjections = config?.CppProjections ?? true,
                };
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

            // --config-only skips the bindings step entirely, so the M13
            // "defer pkg.json mutation until codegen succeeds" rule has
            // nothing to defer past. Update package.json here so the
            // npm-caller path still gets @microsoft/dynwinrt wired up.
            if (options.AddJsBindings && config?.JsBindings is not null)
            {
                jsBindingsWorkspaceService.EnsureRuntimeDependencyAndPrintHint(options.BaseDirectory);
            }

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
                        var failedPackages = new List<string>();

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
                                failedPackages.Add(packageName);
                            }
                        }

                        if (failedPackages.Count > 0)
                        {
                            var failedList = string.Join(", ", failedPackages);
                            if (usedVersions.Count > 0)
                            {
                                return (0, $"NuGet packages added to [underline]{csprojFile.Name}[/], but failed to add: {failedList}");
                            }

                            // Only optional package failures reach this point. Required package failures
                            // already return non-zero in the catch block above, so do not abort init here.
                            return (0, $"Failed to add optional NuGet packages: {failedList}");
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

                        // Step 5: Run cppwinrt and set up projections.
                        // Gated by config.CppProjections (default true): JS-only workspaces
                        // (config.CppProjections == false) skip cppwinrt + headers/libs/runtimes
                        // but still need .winmd discovery + lockfile for the JS bindings step.
                        var generateCppProjections = config?.CppProjections != false;
                        FileInfo? cppWinrtExe = null;
                        if (generateCppProjections)
                        {
                            cppWinrtExe = cppWinrtService.FindCppWinrtExe(nugetCacheDir, usedVersions);
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
                        }
                        else
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Skip} cppProjections: false → skipping cppwinrt + headers/libs/runtimes (JS-only workspace).");
                        }

                        // Collect winmd inputs (unconditional: JS bindings need the lockfile too).
                        taskContext.UpdateSubStatus("Searching for .winmd metadata");
                        var winmds = packageLayoutService.FindWinmds(nugetCacheDir, usedVersions).ToList();
                        taskContext.AddDebugMessage($"{UiSymbols.Search} Found {winmds.Count} .winmd");
                        if (winmds.Count == 0)
                        {
                            return (2, "No .winmd files found in installed SDK packages.");
                        }

                        // Persist the lockfile so the JS bindings step can skip re-globbing
                        // / re-fetching nuspecs. Hash source must match what lands in
                        // winapp.yaml (SDK_PACKAGES-filtered for fresh init, config.Packages for restore).
                        var yamlHash = (options.RequireExistingConfig && config?.Packages.Count > 0)
                            ? YamlPackagesHasher.Compute(config.Packages)
                            : YamlPackagesHasher.ComputeFromVersions(usedVersions
                                .Where(kvp => NugetService.SDK_PACKAGES.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase)));
                        await winmdsLockfileService.WriteAsync(
                            localWinappDir, usedVersions, winmds, nugetCacheDir, yamlHash, cancellationToken);

                        if (generateCppProjections)
                        {
                            // Run cppwinrt
                            taskContext.UpdateSubStatus("Generating C++/WinRT projections");
                            await cppWinrtService.RunWithRspAsync(cppWinrtExe!, winmds, includeOut, localWinappDir, taskContext, cancellationToken: cancellationToken);
                            taskContext.AddDebugMessage($"{UiSymbols.Check} C++/WinRT headers generated → {includeOut}");
                        }

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

                        return (0, generateCppProjections
                            ? "SDK and Windows App SDK packages downloaded and C++ headers generated in [underline].winapp[/]"
                            : "SDK and Windows App SDK packages downloaded in [underline].winapp[/] (cppProjections: false → C++ headers skipped)");
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

                // Step 5.5: Generate JS/TS bindings (opt-in via jsBindings: in winapp.yaml)
                var jsBindingsStep = await MaybeRunJsBindingsStepAsync(
                    config, usedVersions, nugetCacheDir, localWinappDir,
                    options, taskContext, cancellationToken);
                if (jsBindingsStep is { } failed)
                {
                    return failed;
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

                // Add generated assets as Content items so MSIX tooling includes them in the package layout
                if (isDotNetProject && csprojFile != null && shouldGenerateManifest)
                {
                    if (await dotNetService.EnsureAssetContentItemsAsync(csprojFile, cancellationToken))
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} Added asset Content items to .csproj");
                    }
                }

                // Save configuration (native/C++ projects only — .NET uses .csproj PackageReferences)
                if (!isDotNetProject && !options.RequireExistingConfig && options.SdkInstallMode != SdkInstallMode.None && usedVersions != null)
                {
                    await taskContext.AddSubTaskAsync("Saving configuration", (taskContext, cancellationToken) =>
                    {
                        // Setup: Save winapp.yaml with used versions
                        var finalConfig = new WinappConfig
                        {
                            // Preserve JsBindings + CppProjections so the persisted yaml round-trips.
                            JsBindings = config?.JsBindings,
                            CppProjections = config?.CppProjections ?? true,
                        };
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

    // Runs the JS-bindings step when prerequisites are present.
    // Returns null on skip/success; non-zero tuple on failure (forwarded to caller).
    // Internal so unit tests can drive it with a fake IJsBindingsWorkspaceService.
    internal async Task<(int, string)?> MaybeRunJsBindingsStepAsync(
        WinappConfig? config,
        Dictionary<string, string>? usedVersions,
        DirectoryInfo? nugetCacheDir,
        DirectoryInfo? localWinappDir,
        WorkspaceSetupOptions options,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        if (config?.JsBindings is null
            || usedVersions is null
            || nugetCacheDir is null
            || localWinappDir is null)
        {
            return null;
        }

        var jsBindingsResult = await taskContext.AddSubTaskAsync("Generating JS bindings", async (taskContext, cancellationToken) =>
        {
            var orchResult = await jsBindingsWorkspaceService.RunAsync(
                new JsBindingsOrchestrationContext
                {
                    JsBindingsConfig = config.JsBindings,
                    WinappConfig = config,
                    WorkspaceDir = options.BaseDirectory,
                    LocalWinappDir = localWinappDir,
                    NugetCacheDir = nugetCacheDir,
                    UsedVersions = usedVersions,
                },
                taskContext,
                cancellationToken);
            return (orchResult.ExitCode, orchResult.Message);
        }, cancellationToken);

        // Propagate failure so init doesn't report success while shipping
        // a broken workspace.
        if (jsBindingsResult.Item1 != 0)
        {
            return jsBindingsResult;
        }
        return null;
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
}
