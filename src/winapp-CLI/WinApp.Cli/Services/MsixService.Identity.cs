// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class MsixService
{
    /// <summary>
    /// Namespace of the SxS assembly manifest root element.
    /// </summary>
    private static readonly XNamespace AsmV1Ns = "urn:schemas-microsoft-com:asm.v1";

    /// <summary>
    /// Namespace of the &lt;msix&gt; package-identity element embedded in a fusion manifest.
    /// </summary>
    private static readonly XNamespace MsixV1Ns = "urn:schemas-microsoft-com:msix.v1";

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

            // Unregister any existing package first (preserving app data by default)
            await UnregisterExistingPackageAsync(debugIdentity.PackageName, taskContext, cancellationToken: cancellationToken);

            // Register the new debug manifest with external location
            await RegisterSparsePackageAsync(debugManifestPath, externalLocation, taskContext, cancellationToken);
        }

        return new MsixIdentityResult(debugIdentity.PackageName, debugIdentity.Publisher, debugIdentity.ApplicationId);
    }

    public async Task<MsixIdentityResult> AddLooseLayoutIdentityAsync(FileInfo appxManifestPath, DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory, TaskContext taskContext, bool clean = false, string? executable = null, CancellationToken cancellationToken = default)
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

        var manifestContent = await File.ReadAllTextAsync(appxManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Detect whether this manifest was generated by MSBuild (dotnet build).
        // MSBuild-generated manifests have build:Metadata with a makepri.exe entry.
        // When MSBuild-generated, the build output includes a .appxrecipe file that
        // lists all files and their correct source paths for the AppX layout.
        var doc = AppxManifestDocument.Parse(manifestContent);
        var isMSBuildGenerated = doc.Document.Root?
            .Element(AppxManifestDocument.BuildNs + "Metadata")?
            .Elements(AppxManifestDocument.BuildNs + "Item")
            .Any(e => string.Equals(e.Attribute("Name")?.Value, "makepri.exe", StringComparison.OrdinalIgnoreCase)) == true;

        if (isMSBuildGenerated)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} MSBuild-generated manifest detected");

            // Snapshot the previous registered manifest BEFORE the copy/sync overwrites it (issue #537).
            var previousManifestBytes = TryReadExistingLayoutManifestBytes(outputAppXDirectory);

            // Look for a .build.appxrecipe file in the input directory
            var recipeFile = inputDirectory.EnumerateFiles("*.build.appxrecipe", SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (recipeFile != null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Files} Using appxrecipe for layout: {recipeFile.Name}");
                await CopyFilesFromRecipeAsync(recipeFile, outputAppXDirectory, taskContext, cancellationToken);
            }
            else
            {
                // No recipe — fall back to incremental copy from input directory
                taskContext.AddDebugMessage($"{UiSymbols.Warning} No .appxrecipe found, falling back to file copy");
                SyncFilesToOutputDirectory(inputDirectory, outputAppXDirectory, appxManifestPath, taskContext);
            }

            var identity = ParseAppxManifestAsync(manifestContent);

            // Install the Windows App Runtime framework packages if not already present
            var msbuildPackageList = await FetchDotNetPackageListAsync(cancellationToken);
            await EnsureWindowsAppRuntimeInstalledAsync(msbuildPackageList, taskContext, cancellationToken);

            // Resolve the manifest that would be registered (issue #537 / TrySkipRegistration).
            // ManifestHelper.FindManifest already probes both canonical filenames; if it
            // returns a non-existent FileInfo, downstream RegisterLooseLayoutPackageAsync
            // will surface the missing-manifest error.
            var registrationManifest = ManifestHelper.FindManifest(outputAppXDirectory.FullName);

            var skipResult = TrySkipRegistration(
                identity.PackageName, identity.Publisher, identity.ApplicationId,
                previousManifestBytes, registrationManifest, outputAppXDirectory,
                clean, taskContext, cancellationToken);
            if (skipResult is not null)
            {
                return skipResult;
            }

            // Unregister any existing package first (preserving app data by default)
            await UnregisterExistingPackageAsync(identity.PackageName, taskContext, preserveAppData: !clean, cancellationToken);

            // Register from the AppX layout directory
            await RegisterLooseLayoutPackageAsync(registrationManifest, taskContext, cancellationToken);

            return new MsixIdentityResult(identity.PackageName, identity.Publisher, identity.ApplicationId);
        }

        // --- Non-MSBuild manifest path (raw Package.appxmanifest with unresolved placeholders) ---

        if (!outputAppXDirectory.Exists)
        {
            outputAppXDirectory.Create();
        }

        // Snapshot the previously-registered manifest BEFORE Sync overwrites it (issue #537).
        var previousRawManifestBytes = TryReadExistingLayoutManifestBytes(outputAppXDirectory);

        SyncFilesToOutputDirectory(inputDirectory, outputAppXDirectory, appxManifestPath, taskContext);

        // SyncFilesToOutputDirectory renames Package.appxmanifest → appxmanifest.xml
        var copiedManifestName = string.Equals(appxManifestPath.Name, "Package.appxmanifest", StringComparison.OrdinalIgnoreCase)
            ? "appxmanifest.xml"
            : appxManifestPath.Name;
        var copiedAppxManifestPath = new FileInfo(Path.Combine(outputAppXDirectory.FullName, copiedManifestName));
        manifestContent = await File.ReadAllTextAsync(copiedAppxManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Resolve $targetnametoken$ and other placeholders using the same logic as
        // winapp package — uses --executable if provided, otherwise searches the AppX
        // root for a single non-runtime exe.
        manifestContent = ResolveManifestPlaceholders(manifestContent, executable, outputAppXDirectory, taskContext);

        // Determine the resolved executable for downstream operations (PRI rename, arch detection).
        // ResolveManifestPlaceholders guarantees the Executable attribute is non-placeholder
        // on success, so we expect a concrete file name here.
        var resolvedDoc = AppxManifestDocument.Parse(manifestContent);
        var resolvedExeName = resolvedDoc.ApplicationExecutable
            ?? throw new InvalidOperationException(
                "Manifest has no Application/Executable attribute. Cannot determine the application executable.");
        var executableMatch = new FileInfo(Path.Combine(outputAppXDirectory.FullName, resolvedExeName));
        if (!executableMatch.Exists)
        {
            throw new FileNotFoundException(
                $"Executable '{resolvedExeName}' (from manifest) was not found in the output directory '{outputAppXDirectory.FullName}'. " +
                "Ensure the build output contains the exe, or pass --executable with the correct relative path.");
        }

        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

        // If there is a pri file named after the executable, rename it to resources.pri
        var priFilePath = Path.Combine(outputAppXDirectory.FullName, Path.GetFileNameWithoutExtension(executableMatch.Name) + ".pri");
        if (File.Exists(priFilePath))
        {
            var resourcesPriPath = Path.Combine(outputAppXDirectory.FullName, "resources.pri");
            File.Move(priFilePath, resourcesPriPath, overwrite: true);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Renamed {Path.GetFileName(priFilePath)} to resources.pri");
        }

        // Generate resources.pri if not present (matches winapp package behavior)
        var existingPri = new FileInfo(Path.Combine(outputAppXDirectory.FullName, "resources.pri"));
        if (!existingPri.Exists)
        {
            try
            {
                var stagingManifest = new FileInfo(Path.Combine(outputAppXDirectory.FullName, "appxmanifest.xml"));
                var priExpandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(stagingManifest, taskContext);
                var priResourceCandidates = priExpandedFiles.Select(file => file.RelativePath);
                await priService.CreatePriConfigAsync(
                    outputAppXDirectory,
                    taskContext,
                    precomputedPriResourceCandidates: priResourceCandidates,
                    cancellationToken: cancellationToken);
                await priService.GeneratePriFileAsync(outputAppXDirectory, taskContext, cancellationToken: cancellationToken);
                taskContext.AddDebugMessage($"{UiSymbols.Files} Generated resources.pri");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{UISymbol} Failed to generate resources.pri: {Message}. The app may not launch correctly. Re-run with --verbose for details.", UiSymbols.Warning, ex.Message);
                taskContext.AddDebugMessage($"{UiSymbols.Warning} PRI generation error details: {ex}");
            }
        }

        // Resolve <Resource Language="x-generate"/> — falls back to "en-US" if no PRI found
        manifestContent = manifestContent.Replace("x-generate", "EN-US");

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

        {
            var identity = ParseAppxManifestAsync(manifestContent);

            // Install the Windows App Runtime framework packages if not already present
            await EnsureWindowsAppRuntimeInstalledAsync(dotNetPackageList, taskContext, cancellationToken);

            // See MSBuild branch above for the rationale (issue #537).
            var skipResult = TrySkipRegistration(
                identity.PackageName, identity.Publisher, identity.ApplicationId,
                previousRawManifestBytes, copiedAppxManifestPath, outputAppXDirectory,
                clean, taskContext, cancellationToken);
            if (skipResult is not null)
            {
                return skipResult;
            }

            // Unregister any existing package first (preserving app data by default)
            await UnregisterExistingPackageAsync(identity.PackageName, taskContext, preserveAppData: !clean, cancellationToken);

            // Register the new debug manifest with external location
            await RegisterLooseLayoutPackageAsync(copiedAppxManifestPath, taskContext, cancellationToken);

            return new MsixIdentityResult(identity.PackageName, identity.Publisher, identity.ApplicationId);
        }
    }

    /// <summary>
    /// Copies files to the AppX layout directory using the .build.appxrecipe file.
    /// The recipe is generated by MSBuild and lists all files with their correct source
    /// paths and target PackagePaths. This preserves file metadata that .NET's CopyTo may lose.
    /// </summary>
    private static async Task CopyFilesFromRecipeAsync(FileInfo recipeFile, DirectoryInfo outputDir, TaskContext taskContext, CancellationToken cancellationToken)
    {
        if (!outputDir.Exists)
        {
            outputDir.Create();
        }

        var recipeContent = await File.ReadAllTextAsync(recipeFile.FullName, Encoding.UTF8, cancellationToken);
        var recipeDoc = System.Xml.Linq.XDocument.Parse(recipeContent);
        System.Xml.Linq.XNamespace msbuildNs = "http://schemas.microsoft.com/developer/msbuild/2003";

        int copied = 0, skipped = 0;

        // Copy the AppxManifest
        var manifestEntries = recipeDoc.Descendants(msbuildNs + "AppXManifest");
        foreach (var entry in manifestEntries)
        {
            var sourcePath = entry.Attribute("Include")?.Value;
            var packagePath = entry.Element(msbuildNs + "PackagePath")?.Value;
            if (sourcePath != null && packagePath != null && File.Exists(sourcePath))
            {
                var destPath = Path.Combine(outputDir.FullName, packagePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(sourcePath, destPath, overwrite: true);
                copied++;
            }
        }

        // Copy all AppxPackagedFile entries
        var fileEntries = recipeDoc.Descendants(msbuildNs + "AppxPackagedFile");
        foreach (var entry in fileEntries)
        {
            var sourcePath = entry.Attribute("Include")?.Value;
            var packagePath = entry.Element(msbuildNs + "PackagePath")?.Value;
            if (sourcePath == null || packagePath == null || !File.Exists(sourcePath))
            {
                continue;
            }

            var destPath = Path.Combine(outputDir.FullName, packagePath);
            var destFile = new FileInfo(destPath);

            // Skip unchanged files (same size and timestamp)
            if (destFile.Exists)
            {
                var srcFile = new FileInfo(sourcePath);
                if (destFile.Length == srcFile.Length && destFile.LastWriteTimeUtc == srcFile.LastWriteTimeUtc)
                {
                    skipped++;
                    continue;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(sourcePath, destPath, overwrite: true);
            copied++;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Check} AppX layout from recipe: {copied} copied, {skipped} unchanged");
    }

    /// <summary>
    /// Syncs files from the input directory to the output AppX directory using IncrementalCopyHelper.
    /// Also handles manifest copy and rename.
    /// </summary>
    private static void SyncFilesToOutputDirectory(DirectoryInfo inputDirectory, DirectoryInfo outputAppXDirectory, FileInfo appxManifestPath, TaskContext taskContext)
    {
        if (!outputAppXDirectory.Exists)
        {
            outputAppXDirectory.Create();
        }

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

        // Copy the appxmanifest to the output directory
        appxManifestPath.CopyTo(Path.Combine(outputAppXDirectory.FullName, appxManifestPath.Name), overwrite: true);

        // If its Package.appxmanifest, rename to appxmanifest.xml
        if (string.Equals(appxManifestPath.Name, "Package.appxmanifest", StringComparison.OrdinalIgnoreCase))
        {
            var renamedPath = Path.Combine(outputAppXDirectory.FullName, "appxmanifest.xml");
            var originalPath = Path.Combine(outputAppXDirectory.FullName, appxManifestPath.Name);
            File.Move(originalPath, renamedPath, true);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Renamed Package.appxmanifest to appxmanifest.xml");
        }
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
        string assemblyIdentity = $@"<assemblyIdentity version=""1.0.0.0"" name=""{SecurityElement.Escape(identityInfo.PackageName)}"" type=""win32""/>";
        var existingManifestPath = CreateTempManifestFile("extracted");

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, existingManifestPath, taskContext, cancellationToken);
            if (hasExistingManifest)
            {
                taskContext.AddDebugMessage("Existing manifest found in executable, checking for a top-level AssemblyIdentity...");
                var existingManifestContent = await File.ReadAllTextAsync(existingManifestPath.FullName, Encoding.UTF8, cancellationToken);
                if (HasTopLevelAssemblyIdentity(existingManifestContent))
                {
                    taskContext.AddDebugMessage("Top-level AssemblyIdentity already present in manifest, will not add a new one.");
                    assemblyIdentity = string.Empty;
                }
                else
                {
                    // A manifest with only a nested/dependency <assemblyIdentity> (e.g.
                    // Microsoft.Windows.Common-Controls) does not give the assembly its own
                    // identity, so we still add a top-level one alongside the <msix> element.
                    taskContext.AddDebugMessage("No top-level AssemblyIdentity found, adding one so identity can be granted.");
                }
            }
            else
            {
                // A bare exe (Rust / C++ / trimmed .NET) has no embedded manifest; mt.exe will
                // create one from ours, so it must carry the top-level <assemblyIdentity>.
                taskContext.AddDebugMessage("No embedded manifest in executable, adding a top-level AssemblyIdentity.");
            }
        }
        finally
        {
            TryDeleteFile(existingManifestPath);
        }

        var manifestContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
    {assemblyIdentity}
  <msix xmlns=""urn:schemas-microsoft-com:msix.v1""
            publisher=""{SecurityElement.Escape(identityInfo.Publisher)}""
            packageName=""{SecurityElement.Escape(identityInfo.PackageName)}""
            applicationId=""{SecurityElement.Escape(identityInfo.ApplicationId)}""
        />
</assembly>";

        // Create a temporary manifest file
        var tempManifestPath = CreateTempManifestFile("identity");

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
    /// Returns true if the side-by-side manifest XML has a top-level (root-child)
    /// asm.v1 &lt;assemblyIdentity&gt; element. Unlike a whole-file scan, this ignores nested
    /// &lt;assemblyIdentity&gt; elements (e.g. a &lt;dependency&gt; on
    /// Microsoft.Windows.Common-Controls) and any identically named element from a different
    /// namespace (e.g. asm.v3), which do not grant the assembly its own SxS identity.
    /// Returns false if the content cannot be parsed as XML.
    /// </summary>
    private static bool HasTopLevelAssemblyIdentity(string manifestContent)
    {
        try
        {
            var root = XDocument.Parse(manifestContent).Root;
            return root is not null && root.Elements(AsmV1Ns + "assemblyIdentity").Any();
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a uniquely named temp manifest file path under the system temp directory. Callers own
    /// deletion. Using the temp directory rather than the executable's own directory avoids silently
    /// clobbering any same-named files a user keeps next to their exe.
    /// </summary>
    private static FileInfo CreateTempManifestFile(string label)
        => new(Path.Join(Path.GetTempPath(), $"winapp_{label}_{Guid.NewGuid():N}.manifest"));

    /// <summary>
    /// Removes any &lt;msix&gt; identity element(s) from a side-by-side manifest file on disk so a
    /// subsequent mt.exe merge can insert a fresh identity without colliding with a stale one.
    /// No-ops if the file cannot be parsed as XML.
    /// </summary>
    private static void RemoveMsixElements(FileInfo manifestPath, TaskContext taskContext)
    {
        try
        {
            var xdoc = XDocument.Load(manifestPath.FullName);
            var msixElements = xdoc.Descendants(MsixV1Ns + "msix").ToList();
            if (msixElements.Count == 0)
            {
                return;
            }

            foreach (var element in msixElements)
            {
                element.Remove();
            }

            xdoc.Save(manifestPath.FullName);
            taskContext.AddDebugMessage("Removed existing <msix> identity from embedded manifest before merge.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.Xml.XmlException)
        {
            taskContext.AddDebugMessage($"Could not strip existing <msix> identity (continuing): {ex.Message}");
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

        var tempManifestPath = CreateTempManifestFile("extracted");
        var mergedManifestPath = CreateTempManifestFile("merged");

        try
        {
            bool hasExistingManifest = await TryExtractManifestFromExeAsync(exePath, tempManifestPath, taskContext, cancellationToken);

            if (hasExistingManifest)
            {
                // Drop any <msix> identity already embedded in the exe. Otherwise mt.exe refuses to
                // merge when the new identity differs from the old one (error c1010001: "Values of
                // attribute ... not equal in different manifest snippets"), which would make
                // re-branding an exe a hard failure instead of an idempotent update.
                RemoveMsixElements(tempManifestPath, taskContext);

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

        // Step 6: Copy all assets and generate resources.pri
        var entryPointDir = Path.GetDirectoryName(entryPointPath);
        if (!string.IsNullOrEmpty(entryPointDir))
        {
            var entryPointDirInfo = new DirectoryInfo(entryPointDir);
            var originalManifestDir = originalManifestPath.DirectoryName;
            var expandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(originalManifestPath, taskContext);

            if (!string.Equals(originalManifestDir, entryPointDirInfo.FullName, StringComparison.OrdinalIgnoreCase))
            {
                MrtAssetHelper.CopyAllAssets(expandedFiles, entryPointDirInfo, taskContext);
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Manifest directory and target directory are the same, skipping assets copy");
            }

            // Generate resources.pri in a temporary staging directory, then copy only the
            // final resources.pri into the ExternalLocation (entry point directory). This avoids
            // leaving intermediate files such as priconfig.xml and pri.resfiles alongside app output.
            // Sparse packages look for resources.pri in the ExternalLocation, not alongside the manifest.
            if (expandedFiles.Count > 0)
            {
                string? priStagingDir = null;

                try
                {
                    taskContext.AddDebugMessage($"{UiSymbols.Note} Generating PRI for asset resource resolution...");
                    var priResourceCandidates = expandedFiles.Select(file => file.RelativePath).ToArray();

                    priStagingDir = Path.Combine(
                        Path.GetTempPath(),
                        "WinAppCli-Pri-" + Guid.NewGuid().ToString("N"));

                    var priStagingDirInfo = Directory.CreateDirectory(priStagingDir);
                    MrtAssetHelper.CopyAllAssets(expandedFiles, priStagingDirInfo, taskContext);

                    await priService.CreatePriConfigAsync(
                        priStagingDirInfo,
                        taskContext,
                        precomputedPriResourceCandidates: priResourceCandidates,
                        cancellationToken: cancellationToken);
                    await priService.GeneratePriFileAsync(priStagingDirInfo, taskContext, cancellationToken: cancellationToken);

                    var stagedPriPath = Path.Combine(priStagingDirInfo.FullName, "resources.pri");
                    var targetPriPath = Path.Combine(entryPointDirInfo.FullName, "resources.pri");

                    if (!File.Exists(stagedPriPath))
                    {
                        throw new FileNotFoundException("Generated resources.pri was not found in the staging directory.", stagedPriPath);
                    }

                    if (File.Exists(targetPriPath))
                    {
                        File.Delete(targetPriPath);
                    }

                    File.Copy(stagedPriPath, targetPriPath);
                    taskContext.AddDebugMessage($"{UiSymbols.Check} Generated resources.pri in entry point directory");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{UISymbol} Failed to generate resources.pri: {Message}. The app may not launch correctly. Re-run with --verbose for details.", UiSymbols.Warning, ex.Message);
                    taskContext.AddDebugMessage($"{UiSymbols.Warning} PRI generation error details: {ex}");
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(priStagingDir) && Directory.Exists(priStagingDir))
                    {
                        try
                        {
                            Directory.Delete(priStagingDir, recursive: true);
                        }
                        catch (Exception cleanupEx)
                        {
                            taskContext.AddDebugMessage($"{UiSymbols.Warning} Failed to clean up PRI staging directory '{priStagingDir}': {cleanupEx.Message}");
                        }
                    }
                }
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
    /// <param name="taskContext">Task context for debug output</param>
    /// <param name="preserveAppData">When true, preserves the package's application data during removal</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if package was found and unregistered, false if no package was found</returns>
    public async Task<bool> UnregisterExistingPackageAsync(string packageName, TaskContext taskContext, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        taskContext.AddDebugMessage($"{UiSymbols.Trash} Checking for existing package...");

        try
        {
            // Inspect installed packages first so we can make a safe, per-package decision
            // about non-dev-mode installations (where PreserveApplicationData is rejected
            // and a blind removal would wipe user data).
            // NOTE: despite its name, IPackageRegistrationService.FindDevPackages returns
            // *all* same-name packages (dev-mode AND non-dev-mode); the IsDevelopmentMode
            // flag on each entry is what we classify on below.
            var installed = packageRegistrationService.FindDevPackages(packageName);

            if (installed.Count == 0)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} No existing package found");
                return false;
            }

            var cwd = Path.GetFullPath(currentDirectoryProvider.GetCurrentDirectory());

            // First pass: classify packages and refuse the whole operation if any
            // out-of-tree non-dev install is present. Doing this up front (instead of
            // mid-loop) prevents the first removal from racing ahead and wiping data
            // that the safety check on a *later* package was meant to protect.
            foreach (var pkg in installed)
            {
                if (pkg.IsDevelopmentMode)
                {
                    continue;
                }

                if (!IsPathInsideDirectory(pkg.InstallLocation, cwd))
                {
                    var locationDescription = string.IsNullOrEmpty(pkg.InstallLocation)
                        ? "."
                        : $" at '{Path.GetFullPath(pkg.InstallLocation)}'.";
                    throw new InvalidOperationException(
                        $"A package with the same identity ('{pkg.FullName}') is already installed " +
                        $"as a non-development-mode package" + locationDescription + Environment.NewLine +
                        "Remove it manually if you intended to replace it:" + Environment.NewLine +
                        $"  Get-AppxPackage {packageName} | Remove-AppxPackage");
                }
            }

            // Second pass: safe to remove each package individually using its full name.
            // Using the per-FullName API ensures one iteration cannot wipe packages the
            // first pass approved or rejected separately.
            var anyRemoved = false;
            foreach (var pkg in installed)
            {
                if (pkg.IsDevelopmentMode)
                {
                    await packageRegistrationService.UnregisterByFullNameAsync(pkg.FullName, preserveAppData, cancellationToken);
                }
                else
                {
                    // Verified in-tree above: safe to remove (app data deleted, since
                    // PreserveApplicationData isn't valid for non-dev-mode packages).
                    taskContext.AddDebugMessage(
                        $"{UiSymbols.Warning} Existing non-dev-mode package {pkg.FullName} is rooted in the current " +
                        $"project tree ({pkg.InstallLocation}); removing it (application data will be deleted).");
                    await packageRegistrationService.UnregisterByFullNameAsync(pkg.FullName, preserveAppData: false, cancellationToken);
                }
                anyRemoved = true;
            }

            if (anyRemoved)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Check} Existing package unregistered successfully{(preserveAppData ? " (app data preserved where possible)" : "")}");
            }

            return anyRemoved;
        }
        catch (InvalidOperationException)
        {
            // Surface actionable conflicts (non-dev-mode package outside project tree) to the caller.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate so callers (StatusService etc.) can treat it
            // distinctly from a normal "no package removed" outcome.
            throw;
        }
        catch (Exception ex)
        {
            // Other failures (e.g., transient deployment errors during inspection or
            // removal) shouldn't block the caller's overall flow — log and continue,
            // matching prior behavior.
            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not unregister existing package: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Segment-aware containment check: returns true iff <paramref name="candidatePath"/>
    /// lives inside <paramref name="containerPath"/>. Uses <see cref="Path.GetRelativePath"/>
    /// so that sibling directories sharing a string prefix (e.g. <c>C:\proj</c> vs
    /// <c>C:\project2</c>) are correctly treated as outside.
    /// </summary>
    internal static bool IsPathInsideDirectory(string? candidatePath, string containerPath)
    {
        if (string.IsNullOrEmpty(candidatePath))
        {
            return false;
        }

        string fullCandidate;
        string fullContainer;
        try
        {
            fullCandidate = Path.GetFullPath(candidatePath);
            fullContainer = Path.GetFullPath(containerPath);
        }
        catch
        {
            return false;
        }

        var relative = Path.GetRelativePath(fullContainer, fullCandidate);
        if (Path.IsPathRooted(relative))
        {
            // Different volume / UNC root — definitely not contained.
            return false;
        }

        // Reject any traversal out of the container ("..", "..\foo", etc.).
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                             || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
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

        try
        {
            await packageRegistrationService.RegisterSparseAsync(
                manifestPath.FullName, externalLocation.FullName, cancellationToken);

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

        try
        {
            await packageRegistrationService.RegisterLooseLayoutAsync(
                manifestPath.FullName, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Package registered successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register package: {ex.Message}", ex);
        }
    }
}
