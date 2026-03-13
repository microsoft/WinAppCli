// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security;
using System.Text;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class MsixService
{
    public async Task<MsixIdentityResult> AddSparseIdentityAsync(string? entryPointPath, FileInfo appxManifestPath, bool noInstall, bool keepIdentity, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (!appxManifestPath.Exists)
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}. You can generate one using 'winapp manifest generate'.");
        }

        if (!devModeService.IsEnabled() && noInstall == false)
        {
            throw new InvalidOperationException("Developer Mode is not enabled on this machine. Please enable Developer Mode and try again.");
        }

        if (entryPointPath == null)
        {
            var manifestContent = await File.ReadAllTextAsync(appxManifestPath.FullName, Encoding.UTF8, cancellationToken);

            // Parse once to extract the executable path
            var doc = AppxManifestDocument.Parse(manifestContent);

            if (PlaceholderHelper.ContainsPlaceholders(manifestContent))
            {
                // Without an explicit entrypoint, we can't resolve $targetnametoken$ in the executable
                if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
                {
                    throw new InvalidOperationException(
                        "The manifest contains a placeholder for the executable. " +
                        "Provide the entrypoint argument to specify the executable path.");
                }

                // Resolve built-in tokens (e.g. $targetentrypoint$) in memory — the executable
                // attribute itself has no placeholders, so its value from the initial parse is valid.
                manifestContent = PlaceholderHelper.ReplacePlaceholders(manifestContent);
            }

            entryPointPath = doc.ApplicationExecutable ?? entryPointPath;
        }

        // Validate inputs
        if (!File.Exists(entryPointPath))
        {
            throw new FileNotFoundException($"EntryPoint/Executable not found at: {entryPointPath}");
        }

        taskContext.AddDebugMessage($"Processing entryPoint/executable: {entryPointPath}");
        taskContext.AddDebugMessage($"Using AppX manifest: {appxManifestPath}");

        // Generate sparse package structure
        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

        var (debugManifestPath, debugIdentity) = await GenerateSparsePackageStructureAsync(
            appxManifestPath,
            entryPointPath,
            keepIdentity,
            dotNetPackageList,
            taskContext,
            cancellationToken);

        // Update executable with debug identity
        if (Path.HasExtension(entryPointPath) && string.Equals(Path.GetExtension(entryPointPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exePath = new FileInfo(entryPointPath);
            await EmbedMsixIdentityToExeAsync(exePath, debugIdentity, taskContext, cancellationToken);
        }

        if (noInstall)
        {
            taskContext.AddDebugMessage("Skipping package installation as per --no-install option.");
        }
        else
        {
            // Register the debug appxmanifest
            var entryPointDir = Path.GetDirectoryName(entryPointPath);
            var externalLocation = new DirectoryInfo(string.IsNullOrEmpty(entryPointDir) ? currentDirectoryProvider.GetCurrentDirectory() : entryPointDir);

            // Unregister any existing package first
            await UnregisterExistingPackageAsync(debugIdentity.PackageName, taskContext, cancellationToken);

            // Register the new debug manifest with external location
            await RegisterSparsePackageAsync(debugManifestPath, externalLocation, taskContext, cancellationToken);
        }

        return new MsixIdentityResult(debugIdentity.PackageName, debugIdentity.Publisher, debugIdentity.ApplicationId);
    }

    public async Task<MsixIdentityResult> AddLooseLayoutIdentityAsync(FileInfo appxManifestPath, DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (!appxManifestPath.Exists)
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}. You can generate one using 'winapp manifest generate'.");
        }

        if (!devModeService.IsEnabled())
        {
            throw new InvalidOperationException("Developer Mode is not enabled on this machine. Please enable Developer Mode and try again.");
        }

        taskContext.AddDebugMessage($"Using AppX manifest: {appxManifestPath}");

        if (!outputAppXDirectory.Exists)
        {
            outputAppXDirectory.Create();
        }

        // Incremental copy: only copy files that are new or changed (by size or timestamp).
        // Also remove stale files from the output directory that no longer exist in input.
        if (inputDirectory != null && !string.Equals(inputDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar),
            outputAppXDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "appxmanifest.xml",
                "Package.appxmanifest",
                "resources.pri"
            };

            var result = IncrementalCopyHelper.SyncDirectory(inputDirectory, outputAppXDirectory, protectedFiles);
            taskContext.AddDebugMessage($"{UiSymbols.Check} Sync to output directory: {result.Copied} copied, {result.Skipped} unchanged, {result.Deleted} deleted");
        }

        // Copy the appxmanifest to the output directory, if not already present
        appxManifestPath.CopyTo(Path.Combine(outputAppXDirectory.FullName, appxManifestPath.Name), overwrite: true);

        // If its Package.appxmanifest, rename to appxmanifest.xml
        if (string.Equals(appxManifestPath.Name, "Package.appxmanifest", StringComparison.OrdinalIgnoreCase))
        {
            var renamedPath = Path.Combine(outputAppXDirectory.FullName, "appxmanifest.xml");
            var originalPath = Path.Combine(outputAppXDirectory.FullName, appxManifestPath.Name);
            File.Move(originalPath, renamedPath, true);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Renamed Package.appxmanifest to appxmanifest.xml");
            appxManifestPath = new FileInfo(renamedPath);
        }

        var copiedAppxManifestPath = new FileInfo(Path.Combine(outputAppXDirectory.FullName, appxManifestPath.Name));
        var manifestContent = await File.ReadAllTextAsync(copiedAppxManifestPath.FullName, Encoding.UTF8, cancellationToken);
        var executableMatch = outputAppXDirectory.EnumerateFiles("*", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(f.Extension, ".exe", StringComparison.OrdinalIgnoreCase));

        if (executableMatch == null)
        {
            throw new FileNotFoundException("No executable (.exe) file found in the output directory for token replacement.");
        }

        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

        // Resolve $targetnametoken$ and $targetentrypoint$ placeholders
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PlaceholderHelper.TargetNameToken] = Path.GetFileNameWithoutExtension(executableMatch.Name)
        };
        manifestContent = PlaceholderHelper.ReplacePlaceholders(manifestContent, replacements);

        // Resolve <Resource Language="x-generate"/> — falls back to "en-US" if no PRI found
        manifestContent = manifestContent.Replace("x-generate", "EN-US");

        // If there is a pri file named after the executable, rename it to resources.pri
        var priFilePath = Path.Combine(outputAppXDirectory.FullName, Path.GetFileNameWithoutExtension(executableMatch.Name) + ".pri");
        if (File.Exists(priFilePath))
        {
            var resourcesPriPath = Path.Combine(outputAppXDirectory.FullName, "resources.pri");
            File.Move(priFilePath, resourcesPriPath, overwrite: true);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Renamed {Path.GetFileName(priFilePath)} to resources.pri");
        }

        // Unified manifest processing: WinAppSDK dependency, third-party WinRT components,
        // ProcessorArchitecture auto-detection, and build metadata
        (manifestContent, _) = await UpdateAppxManifestContentAsync(
            manifestContent, null, null, executableMatch.FullName,
            sparse: false, selfContained: false,
            dotNetPackageList, taskContext, cancellationToken);

        await File.WriteAllTextAsync(copiedAppxManifestPath.FullName, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // Copy all assets
        var originalManifestDir = appxManifestPath.DirectoryName;

        if (!string.Equals(originalManifestDir, outputAppXDirectory.FullName, StringComparison.OrdinalIgnoreCase))
        {
            var expandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(appxManifestPath, taskContext);
            MrtAssetHelper.CopyAllAssets(expandedFiles, outputAppXDirectory, taskContext);
        }
        else
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Manifest directory and target directory are the same, skipping assets copy");
        }

        var identity = ParseAppxManifestAsync(manifestContent);

        // Install the Windows App Runtime framework packages if not already present
        await EnsureWindowsAppRuntimeInstalledAsync(dotNetPackageList, taskContext, cancellationToken);

        // Unregister any existing package first
        await UnregisterExistingPackageAsync(identity.PackageName, taskContext, cancellationToken);

        // Register the new debug manifest with external location
        await RegisterLooseLayoutPackageAsync(copiedAppxManifestPath, taskContext, cancellationToken);

        return new MsixIdentityResult(identity.PackageName, identity.Publisher, identity.ApplicationId);
    }

    /// <summary>
    /// Ensures that the Windows App Runtime framework MSIX packages are installed on the machine.
    /// Locates the runtime MSIX directory from the NuGet package cache and installs any
    /// missing or outdated packages (Framework, DDLM, Singleton, Main) via Add-AppxPackage.
    /// </summary>
    private async Task EnsureWindowsAppRuntimeInstalledAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var msixDir = await GetRuntimeMsixDirAsync(dotNetPackageList, taskContext, cancellationToken);
        if (msixDir == null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not locate Windows App Runtime MSIX packages. The runtime may need to be installed manually.");
            return;
        }

        var (installedCount, errorCount) = await workspaceSetupService.InstallWindowsAppRuntimeAsync(msixDir, taskContext, cancellationToken);

        if (errorCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} {errorCount} runtime package(s) failed to install. The app may not launch correctly.");
        }
        else if (installedCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Check} Installed {installedCount} Windows App Runtime package(s)");
        }
    }

    private async Task EmbedMsixIdentityToExeAsync(FileInfo exePath, MsixIdentityResult identityInfo, TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Create the MSIX element for the win32 manifest
        string assemblyIdentity = $@"<assemblyIdentity version=""1.0.0.0"" name=""{SecurityElement.Escape(identityInfo.PackageName)}"" type=""win32""/>;";
        var existingManifestPath = new FileInfo(Path.Combine(exePath.DirectoryName!, "temp_extracted.manifest"));

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, existingManifestPath, taskContext, cancellationToken);
            if (!hasExistingManifest)
            {
                assemblyIdentity = string.Empty;
            }
            else
            {
                taskContext.AddDebugMessage("Existing manifest found in executable, checking for AssemblyIdentity...");
                var existingManifestContent = await File.ReadAllTextAsync(existingManifestPath.FullName, Encoding.UTF8, cancellationToken);
                var assemblyIdentityMatch = AssemblyIdentityNameRegex().Match(existingManifestContent);
                if (assemblyIdentityMatch.Success)
                {
                    taskContext.AddDebugMessage("Existing AssemblyIdentity found in manifest, will not add a new one.");
                    assemblyIdentity = string.Empty;
                }
            }
        }
        finally
        {
            TryDeleteFile(existingManifestPath);
        }

        var manifestContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  <msix xmlns=""urn:schemas-microsoft-com:msix.v1""
            publisher=""{SecurityElement.Escape(identityInfo.Publisher)}""
            packageName=""{SecurityElement.Escape(identityInfo.PackageName)}""
            applicationId=""{SecurityElement.Escape(identityInfo.ApplicationId)}""
        />
    {assemblyIdentity}
</assembly>";

        // Create a temporary manifest file
        var tempManifestPath = new FileInfo(Path.Combine(exePath.DirectoryName!, "msix_identity_temp.manifest"));

        try
        {
            await File.WriteAllTextAsync(tempManifestPath.FullName, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, taskContext, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempManifestPath);
        }
    }

    /// <summary>
    /// Embeds a manifest file into the Win32 manifest of an executable using mt.exe for proper merging.
    /// </summary>
    /// <param name="exePath">Path to the executable to modify</param>
    /// <param name="manifestPath">Path to the manifest file to embed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task EmbedManifestFileToExeAsync(
        FileInfo exePath,
        FileInfo manifestPath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (!exePath.Exists)
        {
            throw new FileNotFoundException($"Executable not found at: {exePath}");
        }

        if (!manifestPath.Exists)
        {
            throw new FileNotFoundException($"Manifest file not found at: {manifestPath}");
        }

        taskContext.AddDebugMessage($"Processing executable: {exePath}");
        taskContext.AddDebugMessage($"Embedding manifest: {manifestPath}");

        var exeDir = exePath.DirectoryName!;
        var tempManifestPath = new FileInfo(Path.Combine(exeDir, "temp_extracted.manifest"));
        var mergedManifestPath = new FileInfo(Path.Combine(exeDir, "merged.manifest"));

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, tempManifestPath, taskContext, cancellationToken);

            if (hasExistingManifest)
            {
                taskContext.AddDebugMessage("Merging with existing manifest using mt.exe...");

                // Use mt.exe to merge existing manifest with new manifest
                await RunMtToolAsync($@"-manifest ""{tempManifestPath}"" ""{manifestPath}"" -out:""{mergedManifestPath}""", true, taskContext, cancellationToken);
            }
            else
            {
                taskContext.AddDebugMessage("No existing manifest, using new manifest as-is");

                // No existing manifest, use the new manifest directly
                manifestPath.CopyTo(mergedManifestPath.FullName);
            }

            taskContext.AddDebugMessage("Embedding merged manifest into executable...");

            // Update the executable with merged manifest
            await RunMtToolAsync($@"-manifest ""{mergedManifestPath}"" -outputresource:""{exePath}"";#1", true, taskContext, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Successfully embedded manifest into: {exePath}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to embed manifest into executable: {ex.Message}", ex);
        }
        finally
        {
            // Clean up temporary files
            TryDeleteFile(tempManifestPath);
            TryDeleteFile(mergedManifestPath);
        }
    }

    private async Task<bool> TryExtractManifestFromExeAsync(FileInfo exePath, FileInfo tempManifestPath, TaskContext taskContext, CancellationToken cancellationToken)
    {
        taskContext.AddDebugMessage("Extracting current manifest from executable...");

        // Extract current manifest from the executable
        bool hasExistingManifest = false;
        try
        {
            await RunMtToolAsync($@"-inputresource:""{exePath}"";#1 -out:""{tempManifestPath}""", false, taskContext, cancellationToken);
            tempManifestPath.Refresh();
            hasExistingManifest = tempManifestPath.Exists;
        }
        catch
        {
            taskContext.AddDebugMessage("No existing manifest found in executable");
        }

        return hasExistingManifest;
    }

    private async Task RunMtToolAsync(string arguments, bool printErrors, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        // Use BuildToolsService to run mt.exe
        await buildToolsService.RunBuildToolAsync(new GenericTool("mt.exe"), arguments, taskContext, printErrors, cancellationToken: cancellationToken);
    }

    /// <param name="originalManifestPath">Path to the original appxmanifest.xml</param>
    /// <param name="entryPointPath">Path to the entryPoint/executable that the manifest should reference</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the debug manifest path and modified identity info</returns>
    public async Task<(FileInfo debugManifestPath, MsixIdentityResult debugIdentity)> GenerateSparsePackageStructureAsync(
        FileInfo originalManifestPath,
        string entryPointPath,
        bool keepIdentity,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        var winappDir = winappDirectoryService.GetLocalWinappDirectory();
        var debugDir = new DirectoryInfo(Path.Combine(winappDir.FullName, "debug"));

        taskContext.AddDebugMessage($"{UiSymbols.Note} Creating sparse package structure in: {debugDir.FullName}");

        // Step 1: Create debug directory, removing existing one if present
        if (debugDir.Exists)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Trash} Removing existing debug directory...");
            debugDir.Delete(recursive: true);
        }

        debugDir.Create();
        taskContext.AddDebugMessage($"{UiSymbols.Folder} Created debug directory");

        // Step 2: Parse original manifest to get identity and assets
        var originalManifestContent = await File.ReadAllTextAsync(originalManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Resolve placeholders in memory (never write back to the original manifest)
        if (PlaceholderHelper.ContainsPlaceholders(originalManifestContent))
        {
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(entryPointPath);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlaceholderHelper.TargetNameToken] = nameWithoutExtension
            };

            // Also replace the Executable attribute if it has a placeholder
            var doc = AppxManifestDocument.Parse(originalManifestContent);
            if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
            {
                var exeName = Path.GetFileName(entryPointPath);
                doc.ApplicationExecutable = exeName;
                originalManifestContent = doc.ToXml();
            }

            originalManifestContent = PlaceholderHelper.ReplacePlaceholders(originalManifestContent, replacements);
            PlaceholderHelper.ThrowIfUnresolvedPlaceholders(originalManifestContent);

            taskContext.AddDebugMessage($"{UiSymbols.Note} Resolved manifest placeholders for debug identity");
        }

        var originalIdentity = ParseAppxManifestAsync(originalManifestContent);

        // Step 3: Create debug identity (optionally with ".debug" suffix)
        var debugIdentity = keepIdentity ? originalIdentity : CreateDebugIdentity(originalIdentity);

        // Step 4: Modify manifest for sparse packaging and debug identity
        (var debugManifestContent, _) = await UpdateAppxManifestContentAsync(
            originalManifestContent,
            debugIdentity,
            entryPointPath,
            entryPointPath,
            sparse: true,
            selfContained: false,
            dotNetPackageList,
            taskContext,
            cancellationToken);

        taskContext.AddDebugMessage($"{UiSymbols.Note} Modified manifest for sparse packaging and debug identity");

        // Step 5: Write debug manifest
        var debugManifestPath = new FileInfo(Path.Combine(debugDir.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(debugManifestPath.FullName, debugManifestContent, Encoding.UTF8, cancellationToken);

        taskContext.AddDebugMessage($"{UiSymbols.Files} Created debug manifest: {debugManifestPath.FullName}");

        // Step 6: Copy all assets
        var entryPointDir = Path.GetDirectoryName(entryPointPath);
        if (!string.IsNullOrEmpty(entryPointDir))
        {
            var entryPointDirInfo = new DirectoryInfo(entryPointDir);
            var originalManifestDir = originalManifestPath.DirectoryName;

            if (!string.Equals(originalManifestDir, entryPointDirInfo.FullName, StringComparison.OrdinalIgnoreCase))
            {
                var expandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(originalManifestPath, taskContext);
                MrtAssetHelper.CopyAllAssets(expandedFiles, entryPointDirInfo, taskContext);
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Manifest directory and target directory are the same, skipping assets copy");
            }
        }

        return (debugManifestPath, debugIdentity);
    }

    /// <summary>
    /// Auto-detects ProcessorArchitecture from the executable PE header and sets it in the manifest
    /// if not already present. Mirrors the logic used by all three code paths (run, create-debug-identity, package).
    /// Without this, ARM64 Windows resolves framework dependencies to ARM64 DLLs even for x64 apps.
    /// </summary>
    /// <returns>The effective architecture (detected or existing), or null if unknown.</returns>
    internal static (string manifestContent, string? architecture) AutoDetectProcessorArchitecture(string manifestContent, string exePath, TaskContext taskContext)
    {
        var detectedArch = PeHelper.DetectPeArchitecture(exePath);
        if (detectedArch == null)
        {
            // Can't detect — return whatever the manifest already has
            var existingDoc = AppxManifestDocument.Parse(manifestContent);
            return (manifestContent, existingDoc.IdentityProcessorArchitecture);
        }

        var doc = AppxManifestDocument.Parse(manifestContent);
        var existingArch = doc.IdentityProcessorArchitecture;

        if (existingArch == null)
        {
            doc.IdentityProcessorArchitecture = detectedArch;
            taskContext.AddDebugMessage($"{UiSymbols.Note} Auto-detected ProcessorArchitecture: {detectedArch}");
            return (doc.ToXml(), detectedArch);
        }

        if (!string.Equals(existingArch, detectedArch, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(existingArch, "neutral", StringComparison.OrdinalIgnoreCase))
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Manifest ProcessorArchitecture is '{existingArch}' but the executable is {detectedArch}. This may cause runtime failures.");
        }

        return (manifestContent, existingArch);
    }

    /// <summary>
    /// Creates a debug version of the identity by appending ".debug" to package name and application ID
    /// </summary>
    private static MsixIdentityResult CreateDebugIdentity(MsixIdentityResult originalIdentity)
    {
        var debugPackageName = originalIdentity.PackageName.EndsWith(".debug")
            ? originalIdentity.PackageName
            : $"{originalIdentity.PackageName}.debug";

        var debugApplicationId = originalIdentity.ApplicationId.EndsWith(".debug")
            ? originalIdentity.ApplicationId
            : $"{originalIdentity.ApplicationId}.debug";

        return new MsixIdentityResult(debugPackageName, originalIdentity.Publisher, debugApplicationId);
    }

    /// <summary>
    /// Copies files referenced in the manifest to the target directory.
    /// </summary>
    /// <summary>
    /// Checks if a package with the given name exists and unregisters it if found
    /// </summary>
    /// <param name="packageName">The name of the package to check and unregister</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if package was found and unregistered, false if no package was found</returns>
    public async Task<bool> UnregisterExistingPackageAsync(string packageName, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"{UiSymbols.Trash} Checking for existing package...");

        try
        {
            // First check if package exists
            var checkCommand = $"Get-AppxPackage -Name '{packageName}'";
            var (_, checkResult, _) = await powerShellService.RunCommandAsync(checkCommand, taskContext, cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(checkResult))
            {
                // Package exists, remove it
                taskContext.AddDebugMessage($"{UiSymbols.Package} Found existing package '{packageName}', removing it...");

                var unregisterCommand = $"Get-AppxPackage -Name '{packageName}' | Remove-AppxPackage";
                await powerShellService.RunCommandAsync(unregisterCommand, taskContext, cancellationToken: cancellationToken);

                taskContext.AddDebugMessage($"{UiSymbols.Check} Existing package unregistered successfully");
                return true;
            }
            else
            {
                // No package found
                taskContext.AddDebugMessage($"{UiSymbols.Note} No existing package found");
                return false;
            }
        }
        catch (Exception ex)
        {
            // If check fails, package likely doesn't exist or we don't have permission
            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not check for existing package: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Registers a sparse package with external location using Add-AppxPackage
    /// </summary>
    /// <param name="manifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="externalLocation">External location path (typically the working directory)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RegisterSparsePackageAsync(FileInfo manifestPath, DirectoryInfo externalLocation, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"{UiSymbols.Clipboard} Registering sparse package with external location...");

        var registerCommand = $"Add-AppxPackage -Path '{manifestPath.FullName}' -ExternalLocation '{externalLocation.FullName}' -Register -ForceUpdateFromAnyVersion";

        try
        {
            var (exitCode, output, error) = await powerShellService.RunCommandAsync(registerCommand, taskContext, cancellationToken: cancellationToken);

            if (exitCode != 0)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    throw new InvalidOperationException($"PowerShell command failed with exit code {exitCode}");
                }

                throw new InvalidOperationException(error.Trim());
            }

            taskContext.AddDebugMessage($"{UiSymbols.Check} Sparse package registered successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register sparse package: {ex.Message}", ex);
        }
    }

    public async Task RegisterLooseLayoutPackageAsync(FileInfo manifestPath, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"{UiSymbols.Clipboard} Registering loose layout package...");

        var registerCommand = $"Add-AppxPackage -Register '{manifestPath.FullName}'";

        try
        {
            var (exitCode, output, _) = await powerShellService.RunCommandAsync(registerCommand, taskContext, cancellationToken: cancellationToken);

            if (exitCode != 0)
            {
                if (string.IsNullOrWhiteSpace(output))
                {
                    throw new InvalidOperationException($"PowerShell command failed with exit code {exitCode}");
                }

                throw new InvalidOperationException(output.Trim());
            }

            taskContext.AddDebugMessage($"{UiSymbols.Check} Package registered successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register package: {ex.Message}", ex);
        }
    }
}
