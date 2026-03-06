// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class MsixService(
    IWinappDirectoryService winappDirectoryService,
    IConfigService configService,
    IBuildToolsService buildToolsService,
    IPowerShellService powerShellService,
    ICertificateService certificateService,
    IWorkspaceSetupService workspaceSetupService,
    IDevModeService devModeService,
    IDotNetService dotNetService,
    INugetService nugetService,
    IWinmdService winmdService,
    ILogger<MsixService> logger,
    ICurrentDirectoryProvider currentDirectoryProvider) : IMsixService
{
    [GeneratedRegex(@"^Microsoft\.WindowsAppRuntime\.\d+\.\d+.*\.msix$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex WindowsAppRuntimeMsixRegex();
    [GeneratedRegex(@"<Identity[^>]*>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex IdentityElementRegex();
    [GeneratedRegex(@"Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageNameRegex();
    [GeneratedRegex(@"Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackagePublisherRegex();
    [GeneratedRegex(@"<Application[^>]*\sId\s*=\s*[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxApplicationIdRegex();
    [GeneratedRegex(@"<Identity[^>]*Name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageIdentityNameRegex();
    [GeneratedRegex(@"<Identity[^>]*Publisher\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageIdentityPublisherRegex();
    [GeneratedRegex(@"<Identity[^>]*Version\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageIdentityVersionRegex();
    [GeneratedRegex(@"<Application[^>]*Executable\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationExecutableRegex();
    [GeneratedRegex(@"(<Identity[^>]*Name\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageIdentityNameAssignmentRegex();
    [GeneratedRegex(@"(<Application[^>]*\sId\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxApplicationIdAssignmentRegex();
    [GeneratedRegex(@"(<Application[^>]*Executable\s*=\s*)[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationExecutableAssignmentRegex();
    [GeneratedRegex(@"(<Package[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageElementOpenTagRegex();
    [GeneratedRegex(@"(<Package[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageOpenTagRegex();
    [GeneratedRegex(@"(\s*</Properties>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackagePropertiesCloseTagRegex();
    [GeneratedRegex(@"(<Application[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxApplicationOpenTagRegex();
    [GeneratedRegex(@"\s*EntryPoint\s*=\s*[""'][^""']*[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageEntryPointRegex();
    [GeneratedRegex(@"(<uap:VisualElements[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageVisualElementsOpenTagRegex();
    [GeneratedRegex(@"(\s*<rescap:Capability Name=""runFullTrust"" />)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageRunFullTrustCapabilityRegex();
    [GeneratedRegex(@"(\s*<Applications>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageApplicationsTagRegex();
    [GeneratedRegex(@"(\s*</Dependencies>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxPackageDependenciesCloseTagRegex();
    [GeneratedRegex(@"<assemblyIdentity[^>]*name\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AssemblyIdentityNameRegex();

    // DLL dedup regexes — extract registered file/path names for HashSet-based dedup
    [GeneratedRegex(@"<asmv3:file\s+name='([^']+)'", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex SxsFileNameRegex();
    [GeneratedRegex(@"<Path>([^<]+)</Path>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AppxManifestPathElementRegex();

    // build:Metadata regexes
    [GeneratedRegex(@"(<Package\b[^>]*)(>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BuildMetadataPackageOpenTagRegex();
    [GeneratedRegex(@"IgnorableNamespaces\s*=\s*""[^""]*\bbuild\b", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BuildMetadataIgnorableNamespacesCheckRegex();
    [GeneratedRegex(@"(IgnorableNamespaces\s*=\s*""[^""]*)("")", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BuildMetadataIgnorableNamespacesAssignRegex();
    [GeneratedRegex(@"<build:Item\s[^>]*Name\s*=\s*""Microsoft\.WinAppCli""", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BuildMetadataWinAppCliItemCheckRegex();
    [GeneratedRegex(@"<build:Item\s[^>]*Name\s*=\s*""Microsoft\.WinAppCli""[^/]*/\s*>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BuildMetadataWinAppCliItemReplaceRegex();
    [GeneratedRegex(@"([ \t]*)(</build:Metadata>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BuildMetadataCloseTagRegex();
    [GeneratedRegex(@"([ \t]*)(</Package>)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BuildMetadataPackageCloseTagRegex();

    // Language (en, en-US, pt-BR, zh-Hans, etc.) – bare token
    [GeneratedRegex(@"^[a-zA-Z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageQualifierRegex();

    [GeneratedRegex(@"^scale-(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    // scale-100, scale-200, etc.
    private static partial Regex ScaleQualifierRegex();

    // theme-dark, theme-light
    [GeneratedRegex(@"^theme-(light|dark)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ThemeQualifierRegex();

    // contrast-standard, contrast-high
    [GeneratedRegex(@"^contrast-(standard|high)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ContrastQualifierRegex();

    // dxfeaturelevel-9 / 10 / 11
    [GeneratedRegex(@"^dxfeaturelevel-(9|10|11)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DxFeatureLevelQualifierRegex();

    // device-family-desktop / xbox / team / iot / mobile
    [GeneratedRegex(@"^device-family-(desktop|mobile|team|xbox|iot)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceFamilyQualifierRegex();

    // homeregion-US, homeregion-JP, ...
    [GeneratedRegex(@"^homeregion-[A-Za-z]{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex HomeRegionQualifierRegex();

    // configuration-debug, configuration-retail, etc.
    [GeneratedRegex(@"^configuration-[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationQualifierRegex();

    // targetsize-16, targetsize-24, targetsize-256, ...
    [GeneratedRegex(@"^targetsize-(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex TargetSizeQualifierRegex();

    // altform-unplated, altform-lightunplated, etc.
    [GeneratedRegex(@"^altform-[A-Za-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AltFormQualifierRegex();

    /// <summary>
    /// Sets up Windows App SDK for self-contained deployment by extracting MSIX content
    /// and preparing the necessary files for embedding in applications.
    /// </summary>
    public async Task SetupSelfContainedAsync(DirectoryInfo winappDir, string architecture, TaskContext taskContext, DotNetPackageListJson? dotNetPackageList = null, CancellationToken cancellationToken = default)
    {
        await taskContext.AddSubTaskAsync("Setting up Self Contained", async (taskContext, cancellationToken) =>
        {
            // Look for the Runtime package which contains the MSIX files
            var selfContainedDir = winappDir.CreateSubdirectory("self-contained");
            var archSelfContainedDir = selfContainedDir.CreateSubdirectory(architecture);

            var msixDir = await GetRuntimeMsixDirAsync(dotNetPackageList, taskContext, cancellationToken) ?? throw new DirectoryNotFoundException("Windows App SDK Runtime MSIX directory not found. Ensure Windows App SDK is installed.");

            // Look for the MSIX file in the tools/MSIX folder
            var msixToolsDir = new DirectoryInfo(Path.Combine(msixDir.FullName, $"win10-{architecture}"));
            if (!msixToolsDir.Exists)
            {
                throw new DirectoryNotFoundException($"MSIX tools directory not found: {msixToolsDir}");
            }

            // Try to use inventory first for accurate file selection
            FileInfo? msixPath = null;
            try
            {
                var packageEntries = await WorkspaceSetupService.ParseMsixInventoryAsync(taskContext, msixDir, cancellationToken);
                if (packageEntries != null)
                {
                    // Look for the base Windows App Runtime package (not Framework, DDLM, or Singleton packages)
                    var mainRuntimeEntry = packageEntries.FirstOrDefault(entry =>
                        entry.PackageIdentity.StartsWith("Microsoft.WindowsAppRuntime.") &&
                        !entry.PackageIdentity.Contains("Framework") &&
                        !entry.FileName.Contains("DDLM", StringComparison.OrdinalIgnoreCase) &&
                        !entry.FileName.Contains("Singleton", StringComparison.OrdinalIgnoreCase));

                    if (mainRuntimeEntry != null)
                    {
                        msixPath = new FileInfo(Path.Combine(msixToolsDir.FullName, mainRuntimeEntry.FileName));
                        taskContext.AddDebugMessage($"{UiSymbols.Package} Found main runtime package from inventory: {mainRuntimeEntry.FileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Could not parse inventory, falling back to file search: {ex.Message}");
            }

            // Fallback: search for files directly with pattern matching
            if (msixPath == null || !msixPath.Exists)
            {
                var msixFiles = msixToolsDir.GetFiles("Microsoft.WindowsAppRuntime.*.msix");
                if (msixFiles.Length == 0)
                {
                    throw new FileNotFoundException($"No MSIX files found in {msixToolsDir}");
                }

                // Look for the base runtime package (format: Microsoft.WindowsAppRuntime.{version}.msix)
                // Exclude files with additional suffixes like DDLM, Singleton, Framework, etc.
                msixPath = msixFiles.FirstOrDefault(f =>
                {
                    var fileName = f.Name;
                    return !fileName.Contains("DDLM", StringComparison.OrdinalIgnoreCase) &&
                           !fileName.Contains("Singleton", StringComparison.OrdinalIgnoreCase) &&
                           !fileName.Contains("Framework", StringComparison.OrdinalIgnoreCase) &&
                           WindowsAppRuntimeMsixRegex().IsMatch(fileName);
                }) ?? msixFiles[0];
            }

            taskContext.AddDebugMessage($"{UiSymbols.Package} Extracting MSIX: {msixPath.FullName}");

            // Extract MSIX content
            var extractedDir = new DirectoryInfo(Path.Combine(archSelfContainedDir.FullName, "extracted"));
            if (extractedDir.Exists)
            {
                extractedDir.Delete(recursive: true);
            }
            extractedDir.Refresh();
            extractedDir.Create();

            using (var archive = await ZipFile.OpenReadAsync(msixPath.FullName, cancellationToken))
            {
                await archive.ExtractToDirectoryAsync(extractedDir.FullName, cancellationToken);
            }

            // Copy relevant files to deployment directory
            var deploymentDir = archSelfContainedDir.CreateSubdirectory("deployment");

            // Copy DLLs, WinMD files, and other runtime assets
            await CopyRuntimeFilesAsync(extractedDir, deploymentDir, taskContext, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Self-contained files prepared in: {archSelfContainedDir.FullName}");

            return 0;
        }, cancellationToken);
    }

    /// <summary>
    /// Parses an AppX manifest file and extracts the package identity information
    /// </summary>
    /// <param name="appxManifestPath">Path to the appxmanifest.xml file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MsixIdentityResult containing package name, publisher, and application ID</returns>
    /// <exception cref="FileNotFoundException">Thrown when the manifest file is not found</exception>
    /// <exception cref="InvalidOperationException">Thrown when the manifest is invalid or missing required elements</exception>
    public static async Task<MsixIdentityResult> ParseAppxManifestFromPathAsync(FileInfo appxManifestPath, CancellationToken cancellationToken = default)
    {
        if (!appxManifestPath.Exists)
        {
            throw new FileNotFoundException($"AppX manifest not found at: {appxManifestPath}");
        }

        // Read and extract package identity from appxmanifest.xml
        var appxManifestContent = await File.ReadAllTextAsync(appxManifestPath.FullName, Encoding.UTF8, cancellationToken);

        return ParseAppxManifestAsync(appxManifestContent);
    }

    /// <summary>
    /// Parses an AppX manifest content and extracts the package identity information
    /// </summary>
    /// <param name="appxManifestContent">The content of the appxmanifest.xml file</param>
    /// <returns>MsixIdentityResult containing package name, publisher, and application ID</returns>
    /// <exception cref="InvalidOperationException">Thrown when the manifest is invalid or missing required elements</exception>
    public static MsixIdentityResult ParseAppxManifestAsync(string appxManifestContent)
    {
        // Extract Package Identity information
        var identityMatch = IdentityElementRegex().Match(appxManifestContent);
        if (!identityMatch.Success)
        {
            throw new InvalidOperationException("No Identity element found in AppX manifest");
        }

        var identityElement = identityMatch.Value;

        // Extract attributes from Identity element
        var nameMatch = AppxPackageNameRegex().Match(identityElement);
        var publisherMatch = AppxPackagePublisherRegex().Match(identityElement);

        if (!nameMatch.Success || !publisherMatch.Success)
        {
            throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");
        }

        var packageName = nameMatch.Groups[1].Value;
        var publisher = publisherMatch.Groups[1].Value;

        // Extract Application ID from Applications/Application element
        var applicationMatch = AppxApplicationIdRegex().Match(appxManifestContent);
        if (!applicationMatch.Success)
        {
            throw new InvalidOperationException("No Application element with Id attribute found in AppX manifest");
        }

        var applicationId = applicationMatch.Groups[1].Value;

        return new MsixIdentityResult(packageName, publisher, applicationId);
    }

    public async Task<MsixIdentityResult> AddMsixIdentityAsync(string? entryPointPath, FileInfo appxManifestPath, bool noInstall, bool keepIdentity, TaskContext taskContext, CancellationToken cancellationToken = default)
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

            // Resolve placeholders in memory only to extract the executable path
            if (PlaceholderHelper.ContainsPlaceholders(manifestContent))
            {
                // Without an explicit entrypoint, we can't resolve $targetnametoken$
                var executableMatch = AppxPackageApplicationExecutableRegex().Match(manifestContent);
                if (executableMatch.Success && PlaceholderHelper.ContainsPlaceholders(executableMatch.Groups[1].Value))
                {
                    throw new InvalidOperationException(
                        "The manifest contains a placeholder for the executable. " +
                        "Provide the entrypoint argument to specify the executable path.");
                }

                // Resolve built-in tokens (e.g. $targetentrypoint$) in memory to extract executable
                manifestContent = PlaceholderHelper.ReplacePlaceholders(manifestContent);
            }

            var execMatch = AppxPackageApplicationExecutableRegex().Match(manifestContent);
            if (execMatch.Success)
            {
                entryPointPath = execMatch.Groups[1].Value;
            }
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

    /// <summary>
    /// Creates a PRI configuration file for the given package directory
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="taskContext">Task context for logging and progress reporting</param>
    /// <param name="language">Default language qualifier (default: 'en-US')</param>
    /// <param name="platformVersion">Platform version (default: '10.0.0')</param>
    /// <param name="precomputedPriResourceCandidates">Pre-computed list of manifest-referenced resource file paths (relative to the package directory) to include in the PRI. Must be provided by the caller via <see cref="GetExpandedManifestReferencedFilesAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the created configuration file</returns>
    public async Task<FileInfo> CreatePriConfigAsync(
        DirectoryInfo packageDir,
        TaskContext taskContext,
        string language = "en-US",
        string platformVersion = "10.0.0",
        IEnumerable<string> precomputedPriResourceCandidates = null!,
        CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        ArgumentNullException.ThrowIfNull(precomputedPriResourceCandidates);

        var resfilesPath = Path.Combine(packageDir.FullName, "pri.resfiles");
        var priResourceCandidates = precomputedPriResourceCandidates.ToList();

        priResourceCandidates = [.. priResourceCandidates
            .Where(path => PriIncludedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

        taskContext.AddDebugMessage($"PRI resource candidates discovered: {priResourceCandidates.Count}");

        using (var writer = new StreamWriter(resfilesPath))
        {
            foreach (var priFile in priResourceCandidates)
            {
                await writer.WriteLineAsync(priFile);
            }
        }

        var configPath = new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var arguments = $@"createconfig /cf ""{configPath}"" /dq {language} /pv {platformVersion} /o";

        taskContext.AddDebugMessage("Creating PRI configuration file...");

        try
        {
            await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            taskContext.AddDebugMessage($"PRI configuration created: {configPath}");

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath.FullName);
            var resourcesNode = xmlDoc.SelectSingleNode("/resources");
            if (resourcesNode != null)
            {
                var indexNode = resourcesNode.SelectSingleNode("index");
                if (indexNode != null)
                {
                    if (indexNode.Attributes?["startIndexAt"]?.Value != null)
                    {
                        // set to relative path
                        indexNode.Attributes["startIndexAt"]!.Value = ".\\pri.resfiles";
                    }

                    var resfilesIndexerNode = xmlDoc.CreateElement("indexer-config");
                    var typeAttr = xmlDoc.CreateAttribute("type");
                    typeAttr.Value = "resfiles";
                    resfilesIndexerNode.Attributes.Append(typeAttr);

                    var delimiterAttr = xmlDoc.CreateAttribute("qualifierDelimiter");
                    delimiterAttr.Value = ".";
                    resfilesIndexerNode.Attributes.Append(delimiterAttr);

                    indexNode.AppendChild(resfilesIndexerNode);

                    // Ensure folder-based indexer is configured to parse qualifiers from
                    // both folder names and file names (e.g. targetsize-48_altform-unplated).
                    var folderIndexerNode = indexNode
                        .SelectNodes("indexer-config")
                        ?.OfType<XmlNode>()
                        .FirstOrDefault(node =>
                            node.Attributes?["type"]?.Value?.Equals("folder", StringComparison.OrdinalIgnoreCase) == true);

                    if (folderIndexerNode?.Attributes != null)
                    {
                        var folderAttributes = folderIndexerNode.Attributes;

                        var folderNameAsQualifierAttr = folderAttributes["foldernameAsQualifier"];
                        if (folderNameAsQualifierAttr == null)
                        {
                            folderNameAsQualifierAttr = xmlDoc.CreateAttribute("foldernameAsQualifier");
                            folderAttributes.Append(folderNameAsQualifierAttr);
                        }
                        folderNameAsQualifierAttr.Value = "true";

                        var fileNameAsQualifierAttr = folderAttributes["filenameAsQualifier"];
                        if (fileNameAsQualifierAttr == null)
                        {
                            fileNameAsQualifierAttr = xmlDoc.CreateAttribute("filenameAsQualifier");
                            folderAttributes.Append(fileNameAsQualifierAttr);
                        }
                        fileNameAsQualifierAttr.Value = "true";
                    }

                    xmlDoc.Save(configPath.FullName);
                }
            }

            return configPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create PRI configuration: {ex.Message}", ex);
        }
    }

    private static readonly HashSet<string> PriIncludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".ico",
        ".svg"
    };

    private static List<(FileInfo SourceFile, string RelativePath)> ExpandManifestReferencedFiles(
        DirectoryInfo manifestDir,
        IEnumerable<string> referencedFiles,
        TaskContext? taskContext,
        Func<FileInfo, bool>? includeFile = null)
    {
        var expandedFilesByRelativePath = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativeFilePath in referencedFiles)
        {
            var logicalSourceFile = new FileInfo(Path.Combine(manifestDir.FullName, relativeFilePath));
            var sourceDir = logicalSourceFile.Directory;

            if (sourceDir is null || !sourceDir.Exists)
            {
                taskContext?.AddDebugMessage($"{UiSymbols.Warning} Source directory not found for referenced file: {relativeFilePath}");
                continue;
            }

            var logicalBaseName = Path.GetFileNameWithoutExtension(logicalSourceFile.Name);
            var variantBaseName = GetMrtVariantBaseName(logicalBaseName);
            var extension = logicalSourceFile.Extension;

            var searchPattern = variantBaseName + "*" + extension;
            var candidates = sourceDir.EnumerateFiles(searchPattern);
            var anyIncludedForLogical = false;

            foreach (var candidateFile in candidates)
            {
                if (includeFile != null && !includeFile(candidateFile))
                {
                    continue;
                }

                var candidateNameWithoutExtension = Path.GetFileNameWithoutExtension(candidateFile.Name);
                if (!IsMrtVariantName(variantBaseName, candidateNameWithoutExtension))
                {
                    continue;
                }

                var relativeDir = Path.GetDirectoryName(relativeFilePath);
                var candidateRelativePath = string.IsNullOrEmpty(relativeDir)
                    ? candidateFile.Name
                    : Path.Combine(relativeDir, candidateFile.Name);

                expandedFilesByRelativePath[candidateRelativePath] = candidateFile;
                anyIncludedForLogical = true;
            }

            if (!anyIncludedForLogical && logicalSourceFile.Exists && (includeFile == null || includeFile(logicalSourceFile)))
            {
                expandedFilesByRelativePath[relativeFilePath] = logicalSourceFile;
            }
            else if (!anyIncludedForLogical && !logicalSourceFile.Exists)
            {
                taskContext?.AddDebugMessage($"{UiSymbols.Warning} Referenced file not found (no MRT variants): {logicalSourceFile}");
            }
        }

        return [.. expandedFilesByRelativePath
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Value, pair.Key))];
    }

    private static List<(FileInfo SourceFile, string RelativePath)> GetExpandedManifestReferencedFiles(
        FileInfo manifestPath,
        TaskContext taskContext)
    {
        var manifestDir = manifestPath.Directory;
        if (manifestDir == null)
        {
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Manifest directory not found for: {manifestPath}");
            return [];
        }

        taskContext.AddDebugMessage($"{UiSymbols.Note} Reading manifest: {manifestPath}");

        var assetReferences = ManifestService.ExtractAssetReferencesFromManifest(manifestPath, taskContext);
        var referencedFiles = assetReferences.Select(a => a.RelativePath);
        return ExpandManifestReferencedFiles(manifestDir, referencedFiles, taskContext);
    }

    /// <summary>
    /// Generates a PRI file from the configuration
    /// </summary>
    /// <param name="packageDir">Path to the package directory</param>
    /// <param name="configPath">Path to PRI config file (default: packageDir/priconfig.xml)</param>
    /// <param name="outputPath">Output path for PRI file (default: packageDir/resources.pri)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of resource files that were processed</returns>
    public async Task<List<FileInfo>> GeneratePriFileAsync(DirectoryInfo packageDir, TaskContext taskContext, FileInfo? configPath = null, FileInfo? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        var priConfigPath = configPath ?? new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var priOutputPath = outputPath ?? new FileInfo(Path.Combine(packageDir.FullName, "resources.pri"));

        if (!priConfigPath.Exists)
        {
            throw new FileNotFoundException($"PRI configuration file not found: {priConfigPath}");
        }

        var arguments = $@"new /pr ""{Path.TrimEndingDirectorySeparator(packageDir.FullName)}"" /cf ""{priConfigPath.FullName}"" /of ""{priOutputPath.FullName}"" /o";

        taskContext.AddDebugMessage("Generating PRI file...");

        try
        {
            var (stdout, stderr) = await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            // Parse the output to extract resource files
            var resourceFiles = new List<FileInfo>();
            var lines = stdout.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Look for lines that match the pattern "Resource File: *"
                const string resourceFileStr = "Resource File: ";
                if (line.StartsWith(resourceFileStr, StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = line[resourceFileStr.Length..].Trim();
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        resourceFiles.Add(new FileInfo(Path.Combine(packageDir.FullName, fileName)));
                    }
                }
            }

            taskContext.AddDebugMessage($"PRI file generated: {priOutputPath}");
            if (resourceFiles.Count > 0)
            {
                taskContext.AddDebugMessage($"Processed {resourceFiles.Count} resource files");
            }

            return resourceFiles;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate PRI file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Resolves $placeholder$ tokens in manifest content. Handles $targetnametoken$ and $targetentrypoint$.
    /// If the Executable attribute contains a placeholder and no --executable is provided,
    /// attempts to infer by searching for .exe files in the input folder.
    /// </summary>
    private static string ResolveManifestPlaceholders(string manifestContent, string? executable, DirectoryInfo inputFolder, TaskContext taskContext)
    {
        // Check if manifest contains any placeholders at all
        if (!PlaceholderHelper.ContainsPlaceholders(manifestContent))
        {
            return manifestContent;
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Determine the executable name for $targetnametoken$
        if (!string.IsNullOrWhiteSpace(executable))
        {
            // --executable was provided explicitly
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(executable);
            replacements[PlaceholderHelper.TargetNameToken] = nameWithoutExtension;

            // Also replace the Executable attribute value if it contains a placeholder
            var execMatch = AppxPackageApplicationExecutableRegex().Match(manifestContent);
            if (execMatch.Success && PlaceholderHelper.ContainsPlaceholders(execMatch.Groups[1].Value))
            {
                manifestContent = AppxPackageApplicationExecutableAssignmentRegex().Replace(
                    manifestContent, $"${{1}}\"{executable}\"");
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified executable: {executable}");
        }
        else
        {
            // Check if the Executable attribute in the manifest has a placeholder
            var execMatch = AppxPackageApplicationExecutableRegex().Match(manifestContent);
            if (execMatch.Success && PlaceholderHelper.ContainsPlaceholders(execMatch.Groups[1].Value))
            {
                // Try to auto-infer by finding .exe files in the input folder root
                var exeFiles = inputFolder.Exists
                    ? inputFolder.GetFiles("*.exe", SearchOption.TopDirectoryOnly)
                        .Where(f => !string.Equals(f.Name, "createdump.exe", StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                    : [];

                if (exeFiles.Length == 1)
                {
                    var inferredExe = exeFiles[0].Name;
                    var nameWithoutExtension = Path.GetFileNameWithoutExtension(inferredExe);
                    replacements[PlaceholderHelper.TargetNameToken] = nameWithoutExtension;

                    manifestContent = AppxPackageApplicationExecutableAssignmentRegex().Replace(
                        manifestContent, $"${{1}}\"{inferredExe}\"");

                    taskContext.AddDebugMessage($"{UiSymbols.Note} Auto-inferred executable: {inferredExe}");
                }
                else
                {
                    var count = exeFiles.Length == 0 ? "no" : "multiple";
                    throw new InvalidOperationException(
                        $"The manifest contains a placeholder for the executable but {count} .exe files were found in the input folder. " +
                        "Edit the manifest to specify the executable or use --executable to specify the relative path to the exe.");
                }
            }
        }

        // Apply all placeholder replacements
        manifestContent = PlaceholderHelper.ReplacePlaceholders(manifestContent, replacements);

        // Sanity check: ensure no unresolved placeholders remain
        PlaceholderHelper.ThrowIfUnresolvedPlaceholders(manifestContent);

        return manifestContent;
    }

    /// <summary>
    /// Creates an MSIX package from a prepared package directory
    /// </summary>
    /// <param name="inputFolder">Path to the folder containing the package contents</param>
    /// <param name="outputPath">Path to the file or folder for the output MSIX</param>
    /// <param name="packageName">Name for the output MSIX file (default: derived from manifest)</param>
    /// <param name="skipPri">Skip PRI generation</param>
    /// <param name="autoSign">Automatically sign the package</param>
    /// <param name="certificatePath">Path to signing certificate (required if autoSign is true)</param>
    /// <param name="certificatePassword">Certificate password</param>
    /// <param name="generateDevCert">Generate a new development certificate if none provided</param>
    /// <param name="installDevCert">Install certificate to machine</param>
    /// <param name="publisher">Publisher name for certificate generation (default: extracted from manifest)</param>
    /// <param name="manifestPath">Path to the manifest file (optional)</param>
    /// <param name="selfContained">Enable self-contained deployment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the MSIX path and signing status</returns>
    public async Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        DirectoryInfo inputFolder,
        FileSystemInfo? outputPath,
        TaskContext taskContext,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        FileInfo? manifestPath = null,
        bool selfContained = false,
        string? executable = null,
        CancellationToken cancellationToken = default)
    {
        // Validate input folder and manifest
        if (!inputFolder.Exists)
        {
            throw new DirectoryNotFoundException($"Input folder not found: {inputFolder}");
        }

        // Warn if the input folder contains .pfx certificate files, which are likely
        // development certificates that should not be included in the package payload.
        var pfxFiles = inputFolder.EnumerateFiles("*.pfx", SearchOption.AllDirectories).ToList();
        if (pfxFiles.Count > 0)
        {
            foreach (var pfxFile in pfxFiles)
            {
                var relativePath = Path.GetRelativePath(inputFolder.FullName, pfxFile.FullName);
                taskContext.AddStatusMessage($"{UiSymbols.Warning} PFX certificate file found in input folder: {relativePath}. Consider removing it before packaging.");
            }
        }

        // Determine manifest path based on priority:
        // 1. Use provided manifestPath parameter
        // 2. Check for appxmanifest.xml in input folder
        // 3. Check for appxmanifest.xml in current directory
        FileInfo resolvedManifestPath;
        if (manifestPath != null)
        {
            resolvedManifestPath = manifestPath;
            taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified manifest: {resolvedManifestPath}");
        }
        else
        {
            var inputFolderManifest = new FileInfo(Path.Combine(inputFolder.FullName, "appxmanifest.xml"));
            if (inputFolderManifest.Exists)
            {
                resolvedManifestPath = inputFolderManifest;
                taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest from input folder: {inputFolderManifest}");
            }
            else
            {
                var currentDirManifest = new FileInfo(Path.Combine(currentDirectoryProvider.GetCurrentDirectory(), "appxmanifest.xml"));
                if (currentDirManifest.Exists)
                {
                    resolvedManifestPath = currentDirManifest;
                    taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest from current directory: {currentDirManifest}");
                }
                else
                {
                    throw new FileNotFoundException($"Manifest file not found. Searched in: input folder ({inputFolderManifest}), current directory ({currentDirManifest})");
                }
            }
        }

        if (!resolvedManifestPath.Exists)
        {
            throw new FileNotFoundException($"Manifest file not found: {resolvedManifestPath}");
        }

        // Determine package name and publisher
        var finalPackageName = packageName;
        var extractedPublisher = publisher;
        string? extractedVersion = null;

        var manifestContent = await File.ReadAllTextAsync(resolvedManifestPath.FullName, Encoding.UTF8, cancellationToken);

        // Resolve $placeholder$ tokens in the manifest
        manifestContent = ResolveManifestPlaceholders(manifestContent, executable, inputFolder, taskContext);

        // Update manifest content to ensure it's either referencing Windows App SDK or is self-contained
        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

        manifestContent = await UpdateAppxManifestContentAsync(manifestContent, null, null, sparse: false, selfContained: selfContained, dotNetPackageList, taskContext, cancellationToken);

        try
        {
            if (string.IsNullOrWhiteSpace(finalPackageName))
            {
                var nameMatch = AppxPackageIdentityNameRegex().Match(manifestContent);
                finalPackageName = nameMatch.Success ? nameMatch.Groups[1].Value : "Package";
            }

            if (string.IsNullOrWhiteSpace(extractedPublisher))
            {
                var publisherMatch = AppxPackageIdentityPublisherRegex().Match(manifestContent);
                extractedPublisher = publisherMatch.Success ? publisherMatch.Groups[1].Value : null;
            }

            if (string.IsNullOrWhiteSpace(extractedVersion))
            {
                var versionMatch = AppxPackageIdentityVersionRegex().Match(manifestContent);
                extractedVersion = versionMatch.Success ? versionMatch.Groups[1].Value : null;
            }
        }
        catch
        {
            finalPackageName ??= "Package";
        }

        var executableMatch = AppxPackageApplicationExecutableRegex().Match(manifestContent);

        // Clean the resolved package name to ensure it meets MSIX schema requirements
        finalPackageName = ManifestService.CleanPackageName(finalPackageName);

        var defaultMsixFileName = !string.IsNullOrWhiteSpace(extractedVersion)
            ? $"{finalPackageName}_{extractedVersion}.msix"
            : $"{finalPackageName}.msix";

        FileInfo outputMsixPath;
        DirectoryInfo outputFolder;
        if (outputPath == null)
        {
            outputFolder = currentDirectoryProvider.GetCurrentDirectoryInfo();
            outputMsixPath = new FileInfo(Path.Combine(outputFolder.FullName, defaultMsixFileName));
        }
        else
        {
            if (Path.HasExtension(outputPath.Name) && string.Equals(Path.GetExtension(outputPath.Name), ".msix", StringComparison.OrdinalIgnoreCase))
            {
                outputMsixPath = new FileInfo(outputPath.FullName);
                outputFolder = outputMsixPath.Directory!;
            }
            else
            {
                outputFolder = new DirectoryInfo(outputPath.FullName);
                outputMsixPath = new FileInfo(Path.Combine(outputPath.FullName, defaultMsixFileName));
            }
        }

        // Ensure output folder exists
        if (!outputFolder.Exists)
        {
            outputFolder.Create();
        }

        // Create a temporary staging directory so we never modify the original input folder.
        // All packaging operations (manifest updates, asset copies, PRI generation, self-contained
        // runtime bundling) happen in this staging copy. The original target folder stays untouched.
        var stagingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"winapp-package-{Guid.NewGuid():N}"));
        stagingDir.Create();

        taskContext.AddDebugMessage($"{UiSymbols.Note} Created staging directory: {stagingDir.FullName}");

        try
        {
            // Copy input folder contents to staging directory
            CopyDirectoryRecursive(inputFolder, stagingDir);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Copied input folder to staging directory");

            // Write the updated manifest into the staging directory
            var updatedManifestPath = Path.Combine(stagingDir.FullName, "appxmanifest.xml");
            await File.WriteAllTextAsync(updatedManifestPath, manifestContent, Encoding.UTF8, cancellationToken);

            // Resolve executable path relative to the staging directory
            FileInfo? executablePath = executableMatch.Success ? new FileInfo(Path.Combine(stagingDir.FullName, executableMatch.Groups[1].Value)) : null;

            // Pre-compute expanded manifest resources from the original manifest
            var manifestIsOutsideInputFolder = !inputFolder.FullName.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(resolvedManifestPath.Directory!.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

            List<(FileInfo SourceFile, string RelativePath)>? expandedFiles = null;
            if (manifestIsOutsideInputFolder || !skipPri)
            {
                // Pre-compute the expanded list of manifest-referenced files here.
                expandedFiles = GetExpandedManifestReferencedFiles(resolvedManifestPath, taskContext);
            }

            // If manifest is outside input folder, copy its referenced assets into the staging directory
            if (manifestIsOutsideInputFolder)
            {
                CopyAllAssets(expandedFiles!, stagingDir, taskContext);
            }

            taskContext.AddDebugMessage($"Creating MSIX package from staging: {stagingDir.FullName}");
            taskContext.AddDebugMessage($"Output: {outputMsixPath.FullName}");

            // Generate PRI files if not skipped
            if (!skipPri)
            {
                taskContext.AddDebugMessage("Generating PRI configuration and files...");

                var priResourceCandidates = expandedFiles!.Select(file => file.RelativePath);
                await CreatePriConfigAsync(
                    stagingDir,
                    taskContext,
                    precomputedPriResourceCandidates: priResourceCandidates,
                    cancellationToken: cancellationToken);
                var resourceFiles = await GeneratePriFileAsync(stagingDir, taskContext, cancellationToken: cancellationToken);
                if (resourceFiles.Count > 0 && logger.IsEnabled(LogLevel.Debug))
                {
                    taskContext.AddDebugMessage($"Resource files included in PRI:");
                    await taskContext.AddSubTaskAsync("Pri Resources", async (taskContext, cancellationToken) =>
                    {
                        foreach (var resourceFile in resourceFiles)
                        {
                            taskContext.AddDebugMessage(resourceFile.ToString());
                        }
                        return Task.FromResult(0);
                    }, cancellationToken);
                }
            }

            // Handle self-contained deployment if requested
            if (selfContained && executablePath != null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Package} Preparing self-contained Windows App SDK runtime...");

                var winAppSDKDeploymentDir = await PrepareRuntimeForPackagingAsync(stagingDir, dotNetPackageList, taskContext, cancellationToken);

                // Add WindowsAppSDK.manifest to existing manifest
                var resolvedDeploymentDir = Path.Combine(winAppSDKDeploymentDir.FullName, "..", "extracted");
                var windowsAppSDKManifestPath = new FileInfo(Path.Combine(resolvedDeploymentDir, "AppxManifest.xml"));
                await EmbedActivationManifestToExeAsync(executablePath, winAppSDKDeploymentDir, windowsAppSDKManifestPath, dotNetPackageList, taskContext, cancellationToken);
            }

            await CreateMsixPackageFromFolderAsync(stagingDir, outputMsixPath, taskContext, cancellationToken);

            // Handle certificate generation and signing
            if (autoSign)
            {
                await SignMsixPackageAsync(outputFolder, certificatePassword, generateDevCert, installDevCert, finalPackageName, extractedPublisher, outputMsixPath, certificatePath, resolvedManifestPath, taskContext, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create MSIX package: {ex.Message}", ex);
        }
        finally
        {
            // Clean up the staging directory
            try
            {
                if (stagingDir.Exists)
                {
                    stagingDir.Delete(recursive: true);
                    taskContext.AddDebugMessage($"{UiSymbols.Note} Cleaned up staging directory");
                }
            }
            catch
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not clean up staging directory: {stagingDir.FullName}");
            }
        }

        taskContext.AddDebugMessage($"MSIX package created successfully: {outputMsixPath}");
        if (autoSign)
        {
            taskContext.AddDebugMessage("Package has been signed");
        }

        return new CreateMsixPackageResult(outputMsixPath, autoSign);
    }

    private async Task EmbedActivationManifestToExeAsync(FileInfo exePath, DirectoryInfo winAppSDKDeploymentDir, FileInfo windowsAppSDKAppXManifestPath, DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Use applicationLocation for DLL content (where runtime files were copied by PrepareRuntimeForPackagingAsync)
        var exeDir = exePath.Directory!;

        taskContext.AddDebugMessage($"{UiSymbols.Note} Generating activation manifest from: {windowsAppSDKAppXManifestPath}");
        taskContext.AddDebugMessage($"{UiSymbols.Package} Using DLL content from: {winAppSDKDeploymentDir}");

        // Create a temporary manifest file
        var tempManifestPath = new FileInfo(Path.Combine(exeDir.FullName, "WindowsAppSDK_temp.manifest"));

        try
        {
            // Build the entire manifest in memory, then write to disk once
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version='1.0' encoding='utf-8' standalone='yes'?>");
            sb.AppendLine("<assembly manifestVersion='1.0'");
            sb.AppendLine("    xmlns:asmv3='urn:schemas-microsoft-com:asm.v3'");
            sb.AppendLine("    xmlns:winrtv1='urn:schemas-microsoft-com:winrt.v1'");
            sb.AppendLine("    xmlns='urn:schemas-microsoft-com:asm.v1'>");

            // Collect all AppX manifests (main package + component fragments) and their DLLs
            (var packageDependencies, _) = await GetWinAppSDKPackageDependenciesAsync(dotNetPackageList, taskContext, cancellationToken);
            if (packageDependencies == null || packageDependencies.Count == 0)
            {
                throw new InvalidOperationException("No Windows SDK packages found. Please install the Windows SDK or Windows App SDK.");
            }

            var architecture = WorkspaceSetupService.GetSystemArchitecture();
            IEnumerable<FileInfo> appxFragments = GetComponents(packageDependencies);

            // Combine all manifests: main AppxManifest.xml (Package root) + fragments (Fragment root)
            var allManifests = new List<FileInfo> { windowsAppSDKAppXManifestPath };
            allManifests.AddRange(appxFragments);

            // Combine all DLL file names from deployment dir and fragment native dirs
            var allDllFiles = new List<string>(winAppSDKDeploymentDir.EnumerateFiles("*.dll").Select(di => di.Name));
            allDllFiles.AddRange(appxFragments
                .Select(fragment => Path.Combine(fragment.DirectoryName!, $"win-{architecture}\\native"))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.dll"))
                .Select(Path.GetFileName)!);

            // Single pass: process all AppX manifests (auto-detects Package vs Fragment root)
            AppendAppManifestFromAppx(
                sb,
                redirectDlls: false,
                inDllFiles: allDllFiles,
                inAppxManifests: allManifests);

            // Phase 3: Discover and register third-party WinRT components (e.g., Win2D, WebView2)
            // These packages ship .winmd files + native DLLs but no package.appxfragment
            await AppendThirdPartyWinRTManifestEntriesAsync(
                sb, architecture, dotNetPackageList, taskContext, cancellationToken);

            sb.AppendLine("</assembly>");

            // Single write to disk
            await File.WriteAllTextAsync(
                tempManifestPath.FullName,
                sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, taskContext, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempManifestPath);
        }
    }

    private IEnumerable<FileInfo> GetComponents(Dictionary<string, string> packageDependencies)
    {
        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();

        // Find appx fragments in the NuGet global cache (lowercase-id/version/ layout)
        var appxFragments = packageDependencies
            .Select(package => new FileInfo(Path.Combine(nugetCacheDir.FullName, package.Key.ToLowerInvariant(), package.Value, "runtimes-framework", "package.appxfragment")))
            .Where(f => f.Exists);
        return appxFragments;
    }

    /// <summary>
    /// Collects all user NuGet packages from winapp.yaml or .csproj.
    /// Returns the full package dictionary (name → version) for WinRT component scanning.
    /// </summary>
    private async Task<Dictionary<string, string>> GetAllUserPackagesAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Path 1: Try winapp.yaml
        if (configService.Exists())
        {
            var config = configService.Load();
            foreach (var pkg in config.Packages)
            {
                packages.TryAdd(pkg.Name, pkg.Version);
            }
        }
        else
        {
            // Path 2: Try .csproj via `dotnet list package --format json` (cached)
            try
            {
                var allPackages = dotNetPackageList?.Projects?
                    .SelectMany(p => p.Frameworks ?? [])
                    .SelectMany(f => (f.TopLevelPackages ?? []).Concat(f.TransitivePackages ?? []));

                if (allPackages != null)
                {
                    foreach (var pkg in allPackages)
                    {
                        if (!string.IsNullOrEmpty(pkg.Id) && !string.IsNullOrEmpty(pkg.ResolvedVersion))
                        {
                            packages.TryAdd(pkg.Id, pkg.ResolvedVersion);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not retrieve package list from .csproj: {ex.Message}");
            }
        }

        return packages;
    }

    private async Task<DotNetPackageListJson?> FetchDotNetPackageListAsync(CancellationToken cancellationToken)
    {
        var cwd = new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
        var csprojFiles = dotNetService.FindCsproj(cwd);
        var csproj = csprojFiles.Count > 0 ? csprojFiles[0] : null;
        if (csproj == null)
        {
            return null;
        }

        return await dotNetService.GetPackageListAsync(csproj, cancellationToken);
    }

    /// <summary>
    /// Discovers third-party WinRT components and appends their activatable class
    /// entries to the in-memory SxS manifest (for self-contained deployment).
    /// </summary>
    private async Task AppendThirdPartyWinRTManifestEntriesAsync(
        StringBuilder sb,
        string architecture,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var allPackages = await GetAllUserPackagesAsync(dotNetPackageList, taskContext, cancellationToken);
        if (allPackages.Count == 0)
        {
            return;
        }

        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();

        // DiscoverWinRTComponents filters out packages that have a package.appxfragment
        // (WinAppSDK sub-packages), and only returns packages with both a .winmd and a matching DLL.
        // We do NOT exclude the full WinAppSDK dependency tree because packages like WebView2
        // are transitive WinAppSDK deps but need their own InProcessServer entries.
        var components = winmdService.DiscoverWinRTComponents(nugetCacheDir, allPackages, architecture);
        if (components.Count == 0)
        {
            return;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Package} Found {components.Count} third-party WinRT component(s) to register");

        // Build a set of DLL names already registered in the manifest (from WinAppSDK fragments)
        // so we can do exact-name dedup instead of substring matching.
        var registeredDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SxsFileNameRegex().Matches(sb.ToString()))
        {
            registeredDlls.Add(match.Groups[1].Value);
        }

        foreach (var component in components)
        {
            var classes = winmdService.GetActivatableClasses(component.WinmdPath);
            if (classes.Count == 0)
            {
                continue;
            }

            // Skip components whose DLL is already in the manifest (from WinAppSDK fragments
            // or a previous iteration) to avoid duplicate activatableClass entries.
            if (!registeredDlls.Add(component.ImplementationDll))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Skipping {component.ImplementationDll} — already in manifest");
                continue;
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} Registering {classes.Count} activatable class(es) from {component.ImplementationDll}");

            sb.AppendLine($"    <asmv3:file name='{component.ImplementationDll}'>");
            foreach (var className in classes)
            {
                sb.AppendLine($"        <winrtv1:activatableClass name='{className}' threadingModel='both'/>");
            }
            sb.AppendLine("    </asmv3:file>");
        }
    }

    /// <summary>
    /// Discovers third-party WinRT components and generates InProcessServer
    /// extension entries for AppxManifest.xml (for packaged apps).
    /// </summary>
    private async Task<string> AddThirdPartyWinRTExtensionsToAppxManifestAsync(
        string manifestContent,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var allPackages = await GetAllUserPackagesAsync(dotNetPackageList, taskContext, cancellationToken);
        if (allPackages.Count == 0)
        {
            return manifestContent;
        }

        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
        var architecture = WorkspaceSetupService.GetSystemArchitecture();

        // DiscoverWinRTComponents filters out packages that have a package.appxfragment
        // (WinAppSDK sub-packages), and only returns packages with both a .winmd and a matching DLL.
        // We do NOT exclude the full WinAppSDK dependency tree because packages like WebView2
        // are transitive WinAppSDK deps but need their own InProcessServer entries.
        var components = winmdService.DiscoverWinRTComponents(nugetCacheDir, allPackages, architecture);
        if (components.Count == 0)
        {
            return manifestContent;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Package} Adding InProcessServer entries for {components.Count} third-party WinRT component(s)");

        // Build a set of DLL names already registered in the manifest
        // so we can do exact-name dedup instead of substring matching.
        var registeredDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AppxManifestPathElementRegex().Matches(manifestContent))
        {
            registeredDlls.Add(match.Groups[1].Value);
        }

        var extensionsSb = new StringBuilder();
        foreach (var component in components)
        {
            var classes = winmdService.GetActivatableClasses(component.WinmdPath);
            if (classes.Count == 0)
            {
                continue;
            }

            // Skip components whose DLL is already in the manifest or in entries we've already generated
            if (!registeredDlls.Add(component.ImplementationDll))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Skipping {component.ImplementationDll} — already in manifest");
                continue;
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} Adding {classes.Count} activatable class(es) for {component.ImplementationDll}");

            extensionsSb.AppendLine(@"    <Extension Category=""windows.activatableClass.inProcessServer"">");
            extensionsSb.AppendLine(@"      <InProcessServer>");
            extensionsSb.AppendLine($@"        <Path>{component.ImplementationDll}</Path>");
            foreach (var className in classes)
            {
                extensionsSb.AppendLine($@"        <ActivatableClass ActivatableClassId=""{className}"" ThreadingModel=""both""/>");
            }
            extensionsSb.AppendLine(@"      </InProcessServer>");
            extensionsSb.AppendLine(@"    </Extension>");
        }

        if (extensionsSb.Length == 0)
        {
            return manifestContent;
        }

        return InsertPackageLevelExtensions(manifestContent, extensionsSb.ToString());
    }

    /// <summary>
    /// Inserts Package-level extension entries (e.g. InProcessServer) into a manifest string.
    /// Correctly distinguishes Package-level &lt;Extensions&gt; from Application-level ones.
    /// </summary>
    internal static string InsertPackageLevelExtensions(string manifestContent, string extensionEntries)
    {
        // IMPORTANT: These are Package-level extensions (e.g. windows.activatableClass.inProcessServer),
        // NOT Application-level extensions. We must find a Package-level <Extensions> block
        // (after </Applications>), not an Application-level one (inside <Application>).
        var extensionsCloseTag = "</Extensions>";
        var applicationsCloseTag = "</Applications>";
        var applicationsCloseIndex = manifestContent.IndexOf(applicationsCloseTag, StringComparison.OrdinalIgnoreCase);

        // Look for </Extensions> AFTER </Applications> — that's the Package-level one
        var extensionsCloseIndex = applicationsCloseIndex >= 0
            ? manifestContent.IndexOf(extensionsCloseTag, applicationsCloseIndex, StringComparison.OrdinalIgnoreCase)
            : -1;

        if (extensionsCloseIndex >= 0)
        {
            // Insert before the Package-level </Extensions>
            return manifestContent.Insert(extensionsCloseIndex, extensionEntries);
        }

        // No Package-level <Extensions> block exists — create one before </Package>
        var packageCloseTag = "</Package>";
        var packageCloseIndex = manifestContent.LastIndexOf(packageCloseTag, StringComparison.OrdinalIgnoreCase);
        if (packageCloseIndex >= 0)
        {
            var extensionsBlock = $"  <Extensions>\n{extensionEntries}  </Extensions>\n";
            return manifestContent.Insert(packageCloseIndex, extensionsBlock);
        }

        return manifestContent;
    }

    /// <summary>
    /// Generates Win32 SxS manifest entries from AppX manifests (Package or Fragment format).
    /// Auto-detects the root element name (Package vs Fragment) per document.
    /// </summary>
    /// <param name="sb">StringBuilder to append manifest entries to</param>
    /// <param name="redirectDlls">Whether to redirect DLLs to %MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%</param>
    /// <param name="inDllFiles">List of DLL file names to track</param>
    /// <param name="inAppxManifests">List of paths to the input AppX manifest files or fragments</param>
    internal static void AppendAppManifestFromAppx(
        StringBuilder sb,
        bool redirectDlls,
        IEnumerable<string> inDllFiles,
        IEnumerable<FileInfo> inAppxManifests)
    {
        var dllFileFormat = redirectDlls ?
            @"    <asmv3:file name='{0}' loadFrom='%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%{0}'>" :
            @"    <asmv3:file name='{0}'>";

        var dllFiles = inDllFiles.ToList();
        var hasPackageManifest = false;

        foreach (var inAppxManifest in inAppxManifests)
        {
            XmlDocument doc = new();
            doc.Load(inAppxManifest.FullName);

            // Auto-detect root element name (Package or Fragment)
            var prefix = doc.DocumentElement?.LocalName ?? "Package";
            var isPackage = prefix == "Package";
            if (isPackage)
            {
                hasPackageManifest = true;
            }

            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            // Add InProcessServer elements to the generated appxmanifest
            var xQuery = $"./m:{prefix}/m:Extensions/m:Extension/m:InProcessServer";
            XmlNodeList? inProcessServers = doc.SelectNodes(xQuery, nsmgr);
            if (inProcessServers != null)
            {
                foreach (XmlNode winRTFactory in inProcessServers)
                {
                    var dllFileNode = winRTFactory.SelectSingleNode("./m:Path", nsmgr);
                    if (dllFileNode == null)
                    {
                        continue;
                    }

                    var dllFile = dllFileNode.InnerText;
                    var typesNames = winRTFactory.SelectNodes("./m:ActivatableClass", nsmgr)?.OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    if (typesNames != null)
                    {
                        foreach (var typeNode in typesNames)
                        {
                            var attribs = typeNode.Attributes?.OfType<XmlAttribute>().ToArray();
                            var typeName = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "ActivatableClassId")
                                ?.InnerText;
                            var xmlEntryFormat =
        @"        <winrtv1:activatableClass name='{0}' threadingModel='both'/>";
                            sb.AppendFormat(xmlEntryFormat, typeName);
                            sb.AppendLine();
                            dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
            }

            // Only for Package manifests with redirect
            if (isPackage && redirectDlls)
            {
                foreach (var dllFile in dllFiles)
                {
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine(@"</asmv3:file>");
                }
            }
            // Add ProxyStub elements to the generated appxmanifest
            dllFiles = [.. inDllFiles];

            xQuery = $"./m:{prefix}/m:Extensions/m:Extension/m:ProxyStub";
            var inProcessProxystubs = doc.SelectNodes(xQuery, nsmgr);
            if (inProcessProxystubs != null)
            {
                foreach (XmlNode proxystub in inProcessProxystubs)
                {
                    var classIDAdded = false;

                    var dllFileNode = proxystub.SelectSingleNode("./m:Path", nsmgr);
                    var dllFile = dllFileNode?.InnerText;
                    // exclude PushNotificationsLongRunningTask, which requires the Singleton (which is unavailable for self-contained apps)
                    // exclude Widgets entries unless/until they have been tested and verified by the Widgets team
                    if (dllFile == null || dllFile == "PushNotificationsLongRunningTask.ProxyStub.dll" || dllFile == "Microsoft.Windows.Widgets.dll")
                    {
                        continue;
                    }
                    var typesNamesForProxy = proxystub.SelectNodes("./m:Interface", nsmgr)?.OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    if (typesNamesForProxy != null)
                    {
                        foreach (var typeNode in typesNamesForProxy)
                        {
                            if (!classIDAdded)
                            {
                                var classIdAttribute = proxystub.Attributes?.OfType<XmlAttribute>().ToArray();
                                var classID = classIdAttribute
                                    ?.OfType<XmlAttribute>()
                                    ?.SingleOrDefault(x => x.Name == "ClassId")
                                    ?.InnerText;

                                if (classID != null)
                                {
                                    var xmlEntryFormat = @"        <asmv3:comClass clsid='{{{0}}}'/>"; 
                                    sb.AppendFormat(xmlEntryFormat, classID);
                                    classIDAdded = true;
                                }
                            }
                            var attribs = typeNode.Attributes?.OfType<XmlAttribute>().ToArray();
                            var typeID = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "InterfaceId")
                                ?.InnerText;
                            var typeNames = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "Name")
                                ?.InnerText;
                            var xmlEntryFormatForStubs = @"        <asmv3:comInterfaceProxyStub name='{0}' iid='{{{1}}}'/>"; 
                            if (typeNames != null && typeID != null)
                            {
                                sb.AppendFormat(xmlEntryFormatForStubs, typeNames, typeID);
                                sb.AppendLine();
                                dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                            }
                        }
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
            }
        }

        if (hasPackageManifest && redirectDlls)
        {
            foreach (var dllFile in dllFiles)
            {
                sb.AppendFormat(dllFileFormat, dllFile);
                sb.AppendLine(@"</asmv3:file>");
            }
        }
    }

    private async Task SignMsixPackageAsync(DirectoryInfo outputFolder, string certificatePassword, bool generateDevCert, bool installDevCert, string finalPackageName, string? extractedPublisher, FileInfo outputMsixPath, FileInfo? certPath, FileInfo resolvedManifestPath, TaskContext taskContext, CancellationToken cancellationToken)
    {
        if (certPath == null && generateDevCert)
        {
            if (string.IsNullOrWhiteSpace(extractedPublisher))
            {
                throw new InvalidOperationException("Publisher name required for certificate generation. Provide publisher option or ensure it exists in manifest.");
            }

            taskContext.AddDebugMessage($"{UiSymbols.Package} Generating certificate for publisher: {extractedPublisher}");

            certPath = new FileInfo(Path.Combine(outputFolder.FullName, $"{finalPackageName}_cert.pfx"));
            await certificateService.GenerateDevCertificateAsync(extractedPublisher, certPath, taskContext, certificatePassword, cancellationToken: cancellationToken);
        }

        if (certPath == null)
        {
            throw new InvalidOperationException("Certificate path required for signing. Provide certificatePath or set generateDevCert to true.");
        }

        // Validate that the certificate publisher matches the manifest publisher
        taskContext.AddDebugMessage($"{UiSymbols.Note} Validating certificate and manifest publishers match...");

        try
        {
            await CertificateService.ValidatePublisherMatchAsync(certPath, certificatePassword, resolvedManifestPath, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Certificate and manifest publishers match");
        }
        catch (InvalidOperationException ex)
        {
            // Re-throw with the specific error message format requested
            throw new InvalidOperationException(ex.Message, ex);
        }

        // Install certificate if requested
        if (installDevCert)
        {
            certificateService.InstallCertificate(certPath, certificatePassword, false, taskContext);
        }

        // Sign the package
        await certificateService.SignFileAsync(outputMsixPath, certPath, taskContext, certificatePassword, cancellationToken: cancellationToken);
    }

    private async Task CreateMsixPackageFromFolderAsync(DirectoryInfo inputFolder, FileInfo outputMsixPath, TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Create MSIX package
        var makeappxArguments = $@"pack /o /d ""{Path.TrimEndingDirectorySeparator(inputFolder.FullName)}"" /nv /p ""{outputMsixPath.FullName}""";

        taskContext.AddDebugMessage("Creating MSIX package...");

        await buildToolsService.RunBuildToolAsync(new MakeAppxTool(), makeappxArguments, taskContext, cancellationToken: cancellationToken);
    }

    private async Task RunMtToolAsync(string arguments, bool printErrors, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        // Use BuildToolsService to run mt.exe
        await buildToolsService.RunBuildToolAsync(new GenericTool("mt.exe"), arguments, taskContext, printErrors, cancellationToken: cancellationToken);
    }

    private static void TryDeleteFile(FileInfo path)
    {
        try
        {
            path.Refresh();
            if (path.Exists)
            {
                path.Delete();
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    /// <summary>
    /// Recursively copies all files and subdirectories from source to destination.
    /// </summary>
    private static void CopyDirectoryRecursive(DirectoryInfo source, DirectoryInfo destination)
    {
        destination.Create();

        foreach (var file in source.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destination.FullName, file.Name), overwrite: true);
        }

        foreach (var subDir in source.EnumerateDirectories())
        {
            var destSubDir = new DirectoryInfo(Path.Combine(destination.FullName, subDir.Name));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }

    /// <summary>
    /// Searches for appxmanifest.xml in the project by looking for .winapp directory in parent directories
    /// </summary>
    /// <param name="startDirectory">The directory to start searching from. If null, uses current directory.</param>
    /// <returns>Path to the project's appxmanifest.xml file, or null if not found</returns>
    public static FileInfo? FindProjectManifest(ICurrentDirectoryProvider currentDirectoryProvider, DirectoryInfo? startDirectory = null)
    {
        var directory = startDirectory ?? currentDirectoryProvider.GetCurrentDirectoryInfo();

        while (directory != null)
        {
            var manifestPath = new FileInfo(Path.Combine(directory.FullName, "appxmanifest.xml"));
            if (manifestPath.Exists)
            {
                return manifestPath;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Generates a sparse package structure for debug purposes
    /// </summary>
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
            var executableAttrMatch = AppxPackageApplicationExecutableRegex().Match(originalManifestContent);
            if (executableAttrMatch.Success && PlaceholderHelper.ContainsPlaceholders(executableAttrMatch.Groups[1].Value))
            {
                var exeName = Path.GetFileName(entryPointPath);
                originalManifestContent = AppxPackageApplicationExecutableAssignmentRegex().Replace(
                    originalManifestContent, $"${{1}}\"{exeName}\"");
            }

            originalManifestContent = PlaceholderHelper.ReplacePlaceholders(originalManifestContent, replacements);
            PlaceholderHelper.ThrowIfUnresolvedPlaceholders(originalManifestContent);

            taskContext.AddDebugMessage($"{UiSymbols.Note} Resolved manifest placeholders for debug identity");
        }

        var originalIdentity = ParseAppxManifestAsync(originalManifestContent);

        // Step 3: Create debug identity (optionally with ".debug" suffix)
        var debugIdentity = keepIdentity ? originalIdentity : CreateDebugIdentity(originalIdentity);

        // Step 4: Modify manifest for sparse packaging and debug identity
        var debugManifestContent = await UpdateAppxManifestContentAsync(
            originalManifestContent,
            debugIdentity,
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
                var expandedFiles = GetExpandedManifestReferencedFiles(originalManifestPath, taskContext);
                CopyAllAssets(expandedFiles, entryPointDirInfo, taskContext);
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Manifest directory and target directory are the same, skipping assets copy");
            }
        }

        return (debugManifestPath, debugIdentity);
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
    /// Updates the manifest identity, application ID, and executable path for sparse packaging
    /// </summary>
    private async Task<string> UpdateAppxManifestContentAsync(
        string originalAppxManifestContent,
        MsixIdentityResult? identity,
        string? entryPointPath,
        bool sparse,
        bool selfContained,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var modifiedContent = originalAppxManifestContent;

        if (identity != null)
        {
            // Replace package identity attributes
            modifiedContent = AppxPackageIdentityNameAssignmentRegex().Replace(modifiedContent, $@"$1""{identity.PackageName}""");

            // Replace application ID
            modifiedContent = AppxApplicationIdAssignmentRegex().Replace(modifiedContent, $@"$1""{identity.ApplicationId}""");
        }

        if (entryPointPath != null)
        {
            // Replace executable path with relative path from package root
            var entryPointDir = Path.GetDirectoryName(entryPointPath);
            var workingDir = string.IsNullOrEmpty(entryPointDir) ? currentDirectoryProvider.GetCurrentDirectory() : entryPointDir;
            string relativeExecutablePath;

            try
            {
                // Calculate relative path from the working directory (package root) to the executable
                relativeExecutablePath = Path.GetRelativePath(workingDir, entryPointPath);

                // Ensure we use forward slashes for consistency in manifest
                relativeExecutablePath = relativeExecutablePath.Replace('\\', '/');
            }
            catch
            {
                // Fallback to just the filename if relative path calculation fails
                relativeExecutablePath = Path.GetFileName(entryPointPath);
            }

            modifiedContent = AppxPackageApplicationExecutableAssignmentRegex().Replace(modifiedContent, $@"$1""{relativeExecutablePath}""");
        }

        bool isExe = Path.HasExtension(entryPointPath) && string.Equals(Path.GetExtension(entryPointPath), ".exe", StringComparison.OrdinalIgnoreCase);

        // Only apply sparse packaging modifications if sparse is true
        if (sparse)
        {
            // Add required namespaces for sparse packaging
            if (!modifiedContent.Contains("xmlns:uap10"))
            {
                modifiedContent = AppxPackageElementOpenTagRegex().Replace(modifiedContent, @"$1 xmlns:uap10=""http://schemas.microsoft.com/appx/manifest/uap/windows10/10""$2");
            }

            if (!modifiedContent.Contains("xmlns:desktop6"))
            {
                modifiedContent = AppxPackageOpenTagRegex().Replace(modifiedContent, @"$1 xmlns:desktop6=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/6""$2");
            }

            // Add sparse package properties
            if (!modifiedContent.Contains("<uap10:AllowExternalContent>"))
            {
                modifiedContent = AppxPackagePropertiesCloseTagRegex().Replace(modifiedContent, @"    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
$1");
            }

            // Ensure Application has sparse packaging attributes
            if (!modifiedContent.Contains("uap10:TrustLevel") && isExe)
            {
                modifiedContent = AppxApplicationOpenTagRegex().Replace(modifiedContent, @"$1 uap10:TrustLevel=""mediumIL"" uap10:RuntimeBehavior=""packagedClassicApp""$2");
            }

            // Remove EntryPoint if present (not needed for sparse packages)
            modifiedContent = AppxPackageEntryPointRegex().Replace(modifiedContent, "");

            // Add AppListEntry="none" to VisualElements if not present
            if (!modifiedContent.Contains("AppListEntry"))
            {
                modifiedContent = AppxPackageVisualElementsOpenTagRegex().Replace(modifiedContent, @"$1 AppListEntry=""none""$2");
            }

            // Add sparse-specific capabilities if not present
            if (!modifiedContent.Contains("unvirtualizedResources"))
            {
                modifiedContent = AppxPackageRunFullTrustCapabilityRegex().Replace(modifiedContent, @"$1
    <rescap:Capability Name=""unvirtualizedResources""/>
    <rescap:Capability Name=""allowElevation"" />");
            }
        }

        // Update or insert Windows App SDK dependency (skip for self-contained packages)
        if (!selfContained && (entryPointPath == null || isExe))
        {
            modifiedContent = await UpdateWindowsAppSdkDependencyAsync(modifiedContent, dotNetPackageList, taskContext, cancellationToken);
        }

        // Add InProcessServer entries for third-party WinRT components (e.g., Win2D, WebView2)
        // In self-contained mode, activation entries go in the SxS manifest embedded in the exe,
        // so we skip them here to avoid duplication.
        if (!selfContained)
        {
            modifiedContent = await AddThirdPartyWinRTExtensionsToAppxManifestAsync(modifiedContent, dotNetPackageList, taskContext, cancellationToken);
        }

        // Stamp build metadata with CLI version
        modifiedContent = AddBuildMetadata(modifiedContent);

        return modifiedContent;
    }

    /// <summary>
    /// Adds or updates build:Metadata in the manifest with the CLI tool name and version.
    /// Inserts the build namespace and IgnorableNamespaces entry if not already present.
    /// </summary>
    internal static string AddBuildMetadata(string manifestContent)
    {
        var version = VersionHelper.GetVersionString();

        // Add xmlns:build namespace to <Package> if not present
        if (!manifestContent.Contains("xmlns:build"))
        {
            manifestContent = BuildMetadataPackageOpenTagRegex().Replace(manifestContent,
                @"$1 xmlns:build=""http://schemas.microsoft.com/developer/appx/2015/build""$2");
        }

        // Add 'build' to IgnorableNamespaces if not already listed
        if (!BuildMetadataIgnorableNamespacesCheckRegex().IsMatch(manifestContent))
        {
            if (manifestContent.Contains("IgnorableNamespaces"))
            {
                // Append 'build' to the existing IgnorableNamespaces value
                manifestContent = BuildMetadataIgnorableNamespacesAssignRegex().Replace(manifestContent,
                    @"$1 build""");
            }
            else
            {
                // No IgnorableNamespaces attribute exists — add one to <Package>
                manifestContent = BuildMetadataPackageOpenTagRegex().Replace(manifestContent,
                    @"$1 IgnorableNamespaces=""build""$2");
            }
        }

        var buildItemEntry = $@"<build:Item Name=""Microsoft.WinAppCli"" Version=""{version}"" />";

        if (manifestContent.Contains("<build:Metadata"))
        {
            // build:Metadata section already exists
            if (BuildMetadataWinAppCliItemCheckRegex().IsMatch(manifestContent))
            {
                // Update existing WinAppCli entry with current version
                manifestContent = BuildMetadataWinAppCliItemReplaceRegex().Replace(manifestContent,
                    buildItemEntry);
            }
            else
            {
                // Append new entry inside existing build:Metadata
                manifestContent = BuildMetadataCloseTagRegex().Replace(manifestContent,
                    $"$1  {buildItemEntry}\n$1$2");
            }
        }
        else
        {
            // Create new build:Metadata section before </Package>
            manifestContent = BuildMetadataPackageCloseTagRegex().Replace(manifestContent,
                $"\n$1<build:Metadata>\n$1  {buildItemEntry}\n$1</build:Metadata>\n$2");
        }

        return manifestContent;
    }

    /// <summary>
    /// Updates or inserts the Windows App SDK dependency in the manifest
    /// </summary>
    /// <param name="manifestContent">The manifest content to modify</param>
    /// <returns>The modified manifest content</returns>
    private async Task<string> UpdateWindowsAppSdkDependencyAsync(string manifestContent, DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Get the Windows App SDK version from the locked winapp.yaml config
        var winAppSdkInfo = await GetWindowsAppSdkDependencyInfoAsync(dotNetPackageList, taskContext, cancellationToken);

        if (winAppSdkInfo == null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not determine Windows App SDK version, skipping dependency update");
            return manifestContent;
        }

        // Check if Dependencies section exists
        if (!manifestContent.Contains("<Dependencies>"))
        {
            // Add Dependencies section before Applications
            manifestContent = AppxPackageApplicationsTagRegex().Replace(manifestContent, $@"  <Dependencies>
    <PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
$1");

            taskContext.AddDebugMessage($"{UiSymbols.Package} Added Windows App SDK dependency {winAppSdkInfo.RuntimeName} (v{winAppSdkInfo.MinVersion})");
        }
        else
        {
            // Check if Windows App SDK dependency already exists
            var existingDependencyPattern = @"<PackageDependency[^>]*Name\s*=\s*[""']Microsoft\.WindowsAppRuntime\.[^""']*[""'][^>]*>";
            var existingMatch = Regex.Match(manifestContent, existingDependencyPattern, RegexOptions.IgnoreCase);

            if (existingMatch.Success)
            {
                // Update existing dependency
                var newDependency = $@"<PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />";
                manifestContent = Regex.Replace(
                    manifestContent,
                    existingDependencyPattern,
                    newDependency,
                    RegexOptions.IgnoreCase);

                taskContext.AddDebugMessage($"{UiSymbols.Sync} Updated Windows App SDK dependency to {winAppSdkInfo.RuntimeName} v{winAppSdkInfo.MinVersion}");
            }
            else
            {
                // Add new dependency to existing Dependencies section
                manifestContent = AppxPackageDependenciesCloseTagRegex().Replace(manifestContent, $@"
    <PackageDependency Name=""{winAppSdkInfo.RuntimeName}"" MinVersion=""{winAppSdkInfo.MinVersion}"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />$1");

                taskContext.AddDebugMessage($"{UiSymbols.Add} Added Windows App SDK dependency {winAppSdkInfo.RuntimeName} to existing Dependencies section (v{winAppSdkInfo.MinVersion})");
            }
        }

        return manifestContent;
    }

    /// <summary>
    /// Gets the Windows App SDK dependency information from the locked winapp.yaml config and package source
    /// </summary>
    /// <returns>The dependency information, or null if not found</returns>
    private async Task<WindowsAppRuntimePackageInfo?> GetWindowsAppSdkDependencyInfoAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        try
        {
            var msixDir = await GetRuntimeMsixDirAsync(dotNetPackageList, taskContext, cancellationToken);
            if (msixDir == null)
            {
                return null;
            }

            // Get the runtime package information from the MSIX inventory
            var runtimeInfo = GetWindowsAppRuntimePackageInfo(taskContext, msixDir, cancellationToken);
            if (runtimeInfo == null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not parse Windows App Runtime package information from MSIX inventory");
                return null;
            }

            return runtimeInfo;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Error getting Windows App SDK dependency info: {ex.Message}");
            return null;
        }
    }

    private async Task<DirectoryInfo?> GetRuntimeMsixDirAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        (var packageDependencies, var mainVersion) = await GetWinAppSDKPackageDependenciesAsync(dotNetPackageList, taskContext, cancellationToken);
        if (packageDependencies == null || mainVersion == null)
        {
            return null;
        }

        // Look for the runtime package in the package dependencies
        var runtimePackage = packageDependencies.FirstOrDefault(kvp =>
            kvp.Key.StartsWith(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, StringComparison.OrdinalIgnoreCase));

        // Create a dictionary with versions for FindWindowsAppSdkMsixDirectory
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = mainVersion
        };

        if (runtimePackage.Key != null)
        {
            // For Windows App SDK 1.8+, there's a separate runtime package
            var runtimeVersion = runtimePackage.Value;
            usedVersions[runtimePackage.Key] = runtimeVersion;

            taskContext.AddDebugMessage($"{UiSymbols.Package} Found runtime package: {runtimePackage.Key} v{runtimeVersion}");
        }
        else
        {
            // For Windows App SDK 1.7 and earlier, runtime is included in the main package
            taskContext.AddDebugMessage($"{UiSymbols.Note} No separate runtime package found - using main package (Windows App SDK 1.7 or earlier)");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available package dependencies: {string.Join(", ", packageDependencies.Keys)}");
        }

        // Find the MSIX directory with the runtime package
        var msixDir = workspaceSetupService.FindWindowsAppSdkMsixDirectory(usedVersions);
        if (msixDir == null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Windows App SDK MSIX directory not found for dependent runtime package");
            return null;
        }

        return msixDir;
    }

    private async Task<(Dictionary<string, string>? CachedPackages, string? MainVersion)> GetWinAppSDKPackageDependenciesAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        string? mainVersion = null;
        // Path 1: Try winapp.yaml (C++ / native projects)
        if (configService.Exists())
        {
            var config = configService.Load();
            mainVersion = config.GetVersion(BuildToolsService.WINAPP_SDK_PACKAGE);
        }
        else
        {
            // Path 2: Try .csproj via `dotnet list package --format json`
            taskContext.AddDebugMessage($"{UiSymbols.Package} Querying NuGet package list...");

            var allPackages = dotNetPackageList?.Projects?
                .SelectMany(p => p.Frameworks ?? [])
                .SelectMany(f => (f.TopLevelPackages ?? []).Concat(f.TransitivePackages ?? []));

            var winAppSdkPkg = allPackages?
                .FirstOrDefault(p => string.Equals(p.Id, BuildToolsService.WINAPP_SDK_PACKAGE, StringComparison.OrdinalIgnoreCase));

            if (winAppSdkPkg != null && !string.IsNullOrEmpty(winAppSdkPkg.ResolvedVersion))
            {
                mainVersion = winAppSdkPkg.ResolvedVersion;
            }
        }

        if (string.IsNullOrEmpty(mainVersion))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} No {BuildToolsService.WINAPP_SDK_PACKAGE} package found in winapp.yaml");
            return (null, null);
        }
        taskContext.AddDebugMessage($"{UiSymbols.Package} Found Windows App SDK main package: v{mainVersion}");
        try
        {
            // Query NuGet API for the dependency tree of this package
            var deps = await nugetService.GetPackageDependenciesAsync(BuildToolsService.WINAPP_SDK_PACKAGE, mainVersion, cancellationToken);

            // Include the main package itself in the result
            deps.TryAdd(BuildToolsService.WINAPP_SDK_PACKAGE, mainVersion);

            return (deps, mainVersion);
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} {BuildToolsService.WINAPP_SDK_PACKAGE} v{mainVersion} not found in package source: {ex.Message}");
        }

        return (null, null);
    }

    /// <summary>
    /// Parses the MSIX inventory file to extract Windows App Runtime package information
    /// </summary>
    /// <param name="msixDir">The MSIX directory containing the inventory file</param>
    /// <returns>Package information, or null if not found</returns>
    private static WindowsAppRuntimePackageInfo? GetWindowsAppRuntimePackageInfo(TaskContext taskContext, DirectoryInfo msixDir, CancellationToken cancellationToken)
    {
        try
        {
            // Use the shared inventory parsing logic (synchronous version)
            var packageEntries = WorkspaceSetupService.ParseMsixInventoryAsync(taskContext, msixDir, cancellationToken).GetAwaiter().GetResult();

            if (packageEntries == null || packageEntries.Count == 0)
            {
                return null;
            }

            // Look for the Windows App Runtime main package (not Framework packages)
            var mainRuntimeEntry = packageEntries
                .FirstOrDefault(entry => entry.PackageIdentity.StartsWith("Microsoft.WindowsAppRuntime.") &&
                                       !entry.PackageIdentity.Contains("Framework"));

            if (mainRuntimeEntry != null)
            {
                // Parse the PackageIdentity (format: Name_Version_Architecture_PublisherId)
                var identityParts = mainRuntimeEntry.PackageIdentity.Split('_');
                if (identityParts.Length >= 2)
                {
                    var runtimeName = identityParts[0];
                    var version = identityParts[1];

                    taskContext.AddDebugMessage($"{UiSymbols.Package} Found Windows App Runtime: {runtimeName} v{version}");

                    return new WindowsAppRuntimePackageInfo
                    {
                        RuntimeName = runtimeName,
                        MinVersion = version
                    };
                }
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} No Windows App Runtime main package found in inventory");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available packages: {string.Join(", ", packageEntries.Select(e => e.PackageIdentity))}");

            return null;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Error parsing MSIX inventory: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copies files referenced in the manifest to the target directory.
    /// </summary>
    private static void CopyAllAssets(List<(FileInfo SourceFile, string RelativePath)> expandedFiles, DirectoryInfo targetDir, TaskContext taskContext)
    {
        var filesCopied = 0;

        foreach (var (sourceFile, relativePath) in expandedFiles)
        {
            var targetFile = new FileInfo(Path.Combine(targetDir.FullName, relativePath));

            targetFile.Directory?.Create();
            sourceFile.CopyTo(targetFile.FullName, overwrite: true);
            filesCopied++;

            taskContext.AddDebugMessage($"{UiSymbols.Files} Copied manifest resource: {relativePath}");
        }

        taskContext.AddDebugMessage($"{UiSymbols.Note} Copied {filesCopied} files to target directory");
    }

    // ltr / rtl
    private static bool IsLayoutDirectionQualifier(string token)
    {
        return token.Equals("ltr", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("rtl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSingleQualifierToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        return LanguageQualifierRegex().IsMatch(token)
            || ScaleQualifierRegex().IsMatch(token)
            || ThemeQualifierRegex().IsMatch(token)
            || ContrastQualifierRegex().IsMatch(token)
            || DxFeatureLevelQualifierRegex().IsMatch(token)
            || DeviceFamilyQualifierRegex().IsMatch(token)
            || HomeRegionQualifierRegex().IsMatch(token)
            || ConfigurationQualifierRegex().IsMatch(token)
            || TargetSizeQualifierRegex().IsMatch(token)
            || AltFormQualifierRegex().IsMatch(token)
            || IsLayoutDirectionQualifier(token);
    }

    private static bool IsQualifierToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var parts = token.Split('_');

        foreach (var part in parts)
        {
            if (!IsSingleQualifierToken(part))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="candidateNameWithoutExtension"/> is a valid MRT
    /// variant of the logical base name (dots allowed in base name).
    /// </summary>
    private static bool IsMrtVariantName(string logicalBaseName, string candidateNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(logicalBaseName) || string.IsNullOrWhiteSpace(candidateNameWithoutExtension))
        {
            return false;
        }

        // Split by '.'; "Logo.scale-200.theme-dark" -> ["Logo", "scale-200", "theme-dark"]
        var parts = candidateNameWithoutExtension.Split('.');

        if (parts.Length == 0)
        {
            return false;
        }

        // First token must match logical base name (case-insensitive)
        if (!parts[0].Equals(logicalBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // No qualifiers -> exact logical name, valid
        if (parts.Length == 1)
        {
            return true;
        }

        // All remaining tokens must be valid MRT qualifiers
        for (int i = 1; i < parts.Length; i++)
        {
            if (!IsQualifierToken(parts[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// For a qualified logical name like "Logo.scale-100" or "Logo.targetsize-24_altform-unplated",
    /// returns the unqualified asset family base (e.g. "Logo").
    /// If the name has no trailing qualifier tokens, returns the original name unchanged.
    /// </summary>
    private static string GetMrtVariantBaseName(string logicalBaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalBaseName);

        var parts = logicalBaseName.Split('.');
        if (parts.Length <= 1)
        {
            return logicalBaseName;
        }

        // Find the earliest segment where every remaining segment is a valid qualifier token.
        for (int i = 1; i < parts.Length; i++)
        {
            var allRemainingAreQualifiers = true;
            for (int j = i; j < parts.Length; j++)
            {
                if (!IsQualifierToken(parts[j]))
                {
                    allRemainingAreQualifiers = false;
                    break;
                }
            }

            if (allRemainingAreQualifiers)
            {
                return string.Join('.', parts[..i]);
            }
        }

        return logicalBaseName;
    }

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

    private static readonly string[] patterns = new[] { "*.dll", "workloads*.json", "restartAgent.exe", "map.html", "*.mui", "*.png", "*.winmd", "*.xaml", "*.xbf", "*.pri" };

    private static async Task CopyRuntimeFilesAsync(DirectoryInfo extractedDir, DirectoryInfo deploymentDir, TaskContext taskContext, CancellationToken cancellationToken)
    {
        await taskContext.AddSubTaskAsync("Copying Runtime Files", (taskContext, cancellationToken) =>
        {
            foreach (var pattern in patterns)
            {
                var files = extractedDir.GetFiles(pattern, SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(extractedDir.FullName, file.FullName);
                    var destPath = Path.Combine(deploymentDir.FullName, relativePath);

                    // Create destination directory if needed
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    file.CopyTo(destPath, overwrite: true);

                    taskContext.AddDebugMessage($"{UiSymbols.Files} {relativePath}");
                }
            }

            return Task.FromResult(0);
        }, cancellationToken);
    }

    /// <summary>
    /// Prepares Windows App SDK runtime files for packaging into an MSIX by extracting them to the input folder
    /// </summary>
    /// <param name="inputFolder">The folder where runtime files should be copied</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The path to the self-contained deployment directory</returns>
    private async Task<DirectoryInfo> PrepareRuntimeForPackagingAsync(DirectoryInfo inputFolder, DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();

        var winappDir = winappDirectoryService.GetLocalWinappDirectory();

        // Extract runtime files using the existing method
        await SetupSelfContainedAsync(winappDir, arch, taskContext, dotNetPackageList, cancellationToken);

        // Copy runtime files from .winapp/self-contained to input folder
        var runtimeSourceDir = new DirectoryInfo(Path.Combine(winappDir.FullName, "self-contained", arch, "deployment"));

        if (runtimeSourceDir.Exists)
        {
            // Copy files recursively to maintain directory structure
            foreach (var file in runtimeSourceDir.GetFiles("*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(runtimeSourceDir.FullName, file.FullName);
                var destFile = Path.Combine(inputFolder.FullName, relativePath);

                // Create destination directory if needed
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                file.CopyTo(destFile, overwrite: true);

                taskContext.AddDebugMessage($"{UiSymbols.Folder} Bundled runtime: {relativePath}");
            }

            taskContext.AddDebugMessage($"{UiSymbols.Check} Windows App SDK runtime bundled into package");
        }
        else
        {
            throw new DirectoryNotFoundException($"Runtime files not found at {runtimeSourceDir}");
        }

        return runtimeSourceDir;
    }
}
