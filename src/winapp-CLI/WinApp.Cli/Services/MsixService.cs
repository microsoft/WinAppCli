// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class MsixService(
    IWinappDirectoryService winappDirectoryService,
    IConfigService configService,
    IBuildToolsService buildToolsService,
    ICertificateService certificateService,
    IWorkspaceSetupService workspaceSetupService,
    IDevModeService devModeService,
    INugetService nugetService,
    IWinmdService winmdService,
    IPriService priService,
    IPackageRegistrationService packageRegistrationService,
    ILogger<MsixService> logger,
    ICurrentDirectoryProvider currentDirectoryProvider,
    IDotNetService dotNetService) : IMsixService
{
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
        var doc = AppxManifestDocument.Parse(appxManifestContent);

        var identity = doc.GetIdentityElement()
            ?? throw new InvalidOperationException("No Identity element found in AppX manifest");

        var packageName = identity.Attribute("Name")?.Value
            ?? throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");

        var publisher = identity.Attribute("Publisher")?.Value
            ?? throw new InvalidOperationException("AppX manifest Identity element missing required Name or Publisher attributes");

        var applicationId = doc.ApplicationId
            ?? throw new InvalidOperationException("No Application element with Id attribute found in AppX manifest");

        return new MsixIdentityResult(packageName, publisher, applicationId);
    }

    /// <summary>
    /// Extracts execution alias names from an AppX manifest content.
    /// Looks for uap5:ExecutionAlias or desktop:ExecutionAlias elements.
    /// </summary>
    /// <param name="manifestContent">The content of the appxmanifest.xml file</param>
    /// <returns>List of alias names (e.g. "myapp.exe")</returns>
    public static List<string> ExtractExecutionAliases(string manifestContent)
    {
        var aliases = new List<string>();
        var matches = ExecutionAliasRegex().Matches(manifestContent);
        foreach (Match match in matches)
        {
            aliases.Add(match.Groups[1].Value);
        }
        return aliases;
    }

    [GeneratedRegex(@"<(?:uap5|desktop):ExecutionAlias\s+Alias\s*=\s*[""']([^""']*)[""']\s*/>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ExecutionAliasRegex();

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

        // If there is a csproj, warn the user that they should use `dotnet run` instead of `winapp run`
        var csprojFiles = dotNetService.FindCsproj(inputDirectory);
        var csproj = csprojFiles.Count > 0 ? csprojFiles[0] : null;
        if (csproj != null)
        {
            throw new InvalidOperationException(
                $"A .csproj file was found in the input directory: {csproj.FullName}. " +
                $"Please use 'dotnet run' to run your application instead of 'winapp run'.");
        }

        if (!outputAppXDirectory.Exists)
        {
            outputAppXDirectory.Create();
        }

        // Recursive copy all files to output directory, but exclude the outputAppXFolder itself if it's inside the input directory
        if (inputDirectory != null && !string.Equals(inputDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar),
            outputAppXDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            var outputFullPath = outputAppXDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var file in inputDirectory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                // Skip files that are inside the output folder (if output is nested inside input)
                if (file.FullName.StartsWith(outputFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(inputDirectory.FullName, file.FullName);
                var destFile = new FileInfo(Path.Combine(outputAppXDirectory.FullName, relativePath));

                destFile.Directory?.Create();
                file.CopyTo(destFile.FullName, overwrite: true);

                taskContext.AddDebugMessage($"{UiSymbols.Files} Copied: {relativePath}");
            }

            taskContext.AddDebugMessage($"{UiSymbols.Check} Copied files to output directory: {outputAppXDirectory.FullName}");
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

        manifestContent = manifestContent.Replace("$targetnametoken$", Path.GetFileNameWithoutExtension(executableMatch.Name), StringComparison.OrdinalIgnoreCase);
        manifestContent = manifestContent.Replace("$targetentrypoint$", "Windows.FullTrustApplication", StringComparison.OrdinalIgnoreCase);
        manifestContent = manifestContent.Replace("x-generate", "EN-US");

        // If there is a pri file named after the executable, rename it to resources.pri
        var priFilePath = Path.Combine(outputAppXDirectory.FullName, Path.GetFileNameWithoutExtension(executableMatch.Name) + ".pri");
        if (File.Exists(priFilePath))
        {
            var resourcesPriPath = Path.Combine(outputAppXDirectory.FullName, "resources.pri");
            File.Move(priFilePath, resourcesPriPath, overwrite: true);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Renamed {Path.GetFileName(priFilePath)} to resources.pri");
        }

        await File.WriteAllTextAsync(copiedAppxManifestPath.FullName, manifestContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // Copy all assets
        var originalManifestDir = appxManifestPath.DirectoryName;

        if (!string.Equals(originalManifestDir, outputAppXDirectory.FullName, StringComparison.OrdinalIgnoreCase))
        {
            var expandedFiles = GetExpandedManifestReferencedFiles(appxManifestPath, taskContext);
            CopyAllAssets(expandedFiles, outputAppXDirectory, taskContext);
        }
        else
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Manifest directory and target directory are the same, skipping assets copy");
        }

        var identity = ParseAppxManifestAsync(manifestContent);

        // Update manifest content to ensure it's either referencing Windows App SDK or is self-contained
        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

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
    /// Extracts execution alias names from an AppX manifest content.
    /// Looks for uap5:ExecutionAlias or desktop:ExecutionAlias elements.
    /// </summary>
    /// <param name="manifestContent">The content of the appxmanifest.xml file</param>
    /// <returns>List of alias names (e.g. "myapp.exe")</returns>
    public static List<string> ExtractExecutionAliases(string manifestContent)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        var aliases = new List<string>();
        var root = doc.Document.Root;
        if (root == null)
        {
            return aliases;
        }

        foreach (var element in root.Descendants()
            .Where(e => e.Name.LocalName == "ExecutionAlias"
                && (e.Name.Namespace == AppxManifestDocument.Uap5Ns || e.Name.Namespace == AppxManifestDocument.DesktopNs)))
        {
            var alias = element.Attribute("Alias")?.Value;
            if (alias != null)
            {
                aliases.Add(alias);
            }
        }

        return aliases;
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
            var doc = AppxManifestDocument.Parse(manifestContent);
            if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
            {
                doc.ApplicationExecutable = executable;
                manifestContent = doc.ToXml();
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified executable: {executable}");
        }
        else
        {
            // Check if the Executable attribute in the manifest has a placeholder
            var doc = AppxManifestDocument.Parse(manifestContent);
            if (doc.ApplicationExecutable != null && PlaceholderHelper.ContainsPlaceholders(doc.ApplicationExecutable))
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

                    doc.ApplicationExecutable = inferredExe;
                    manifestContent = doc.ToXml();

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
    /// Resolves <c>&lt;Resource Language="x-generate"/&gt;</c> in the manifest by replacing it
    /// with concrete language tags. Languages are extracted from the existing <c>resources.pri</c>
    /// in the input folder; falls back to <c>en-US</c> when no PRI or no language qualifiers are found.
    /// </summary>
    private async Task<string> ResolveResourceLanguageXGenerateAsync(
        string manifestContent,
        DirectoryInfo inputFolder,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        if (!ContainsXGenerateLanguage(manifestContent))
        {
            return manifestContent;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Note} Detected <Resource Language=\"x-generate\"/> — resolving to concrete language(s)");

        var languages = new List<string>();

        // Try to extract languages from existing resources.pri
        var priFile = new FileInfo(Path.Combine(inputFolder.FullName, "resources.pri"));
        if (priFile.Exists)
        {
            languages = await priService.ExtractLanguagesFromPriAsync(priFile, taskContext, cancellationToken);
        }

        if (languages.Count == 0)
        {
            languages.Add("en-US");
            taskContext.AddDebugMessage($"{UiSymbols.Note} No language qualifiers found in PRI — defaulting to en-US");
        }
        else
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Resolved resource languages from PRI: {string.Join(", ", languages)}");
        }

        return ReplaceXGenerateLanguage(manifestContent, languages);
    }

    /// <summary>
    /// Returns true if the manifest contains a <c>&lt;Resource Language="x-generate"/&gt;</c> element.
    /// </summary>
    internal static bool ContainsXGenerateLanguage(string manifestContent)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        return doc.GetResourceLanguages()
            .Any(lang => string.Equals(lang, "x-generate", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Replaces <c>&lt;Resource Language="x-generate"/&gt;</c> with concrete
    /// <c>&lt;Resource Language="..."/&gt;</c> entries for each specified language.
    /// </summary>
    internal static string ReplaceXGenerateLanguage(string manifestContent, IList<string> languages)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        doc.SetResourceLanguages(languages);
        return doc.ToXml();
    }

    /// <summary>
    /// Creates an MSIX package from a prepared package directory
    /// </summary>
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
        // 2. Check for appxmanifest.xml or package.appxmanifest in input folder
        // 3. Check for appxmanifest.xml or package.appxmanifest in current directory
        FileInfo resolvedManifestPath;
        if (manifestPath != null)
        {
            resolvedManifestPath = manifestPath;
            taskContext.AddDebugMessage($"{UiSymbols.Note} Using specified manifest: {resolvedManifestPath}");
        }
        else
        {
            var resolvedFromSearch = FindManifestInDirectory(new DirectoryInfo(inputFolder.FullName))
                ?? FindManifestInDirectory(new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory()));

            if (resolvedFromSearch != null)
            {
                resolvedManifestPath = resolvedFromSearch;
                taskContext.AddDebugMessage($"{UiSymbols.Note} Using manifest: {resolvedManifestPath}");
            }
            else
            {
                throw new FileNotFoundException($"Manifest file not found. Searched for appxmanifest.xml and package.appxmanifest in: input folder ({inputFolder.FullName}), current directory ({currentDirectoryProvider.GetCurrentDirectory()})");
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

        // Resolve <Resource Language="x-generate"/> with concrete language(s) from PRI
        manifestContent = await ResolveResourceLanguageXGenerateAsync(manifestContent, inputFolder, taskContext, cancellationToken);

        // Update manifest content to ensure it's either referencing Windows App SDK or is self-contained
        // Fetch dotnet package list once for all downstream operations
        var dotNetPackageList = await FetchDotNetPackageListAsync(cancellationToken);

        // Determine executable path for ProcessorArchitecture auto-detection
        string? resolvedExePath = null;
        {
            var tempDoc = AppxManifestDocument.Parse(manifestContent);
            var appExe = tempDoc.ApplicationExecutable;
            if (appExe != null)
            {
                resolvedExePath = Path.Combine(inputFolder.FullName, appExe);
            }
        }

        (manifestContent, var packageArch) = await UpdateAppxManifestContentAsync(manifestContent, null, null, resolvedExePath, sparse: false, selfContained: selfContained, dotNetPackageList, taskContext, cancellationToken);

        // Parse the manifest to extract identity, executable, and architecture info
        var manifestDoc = AppxManifestDocument.Parse(manifestContent);

        try
        {
            if (string.IsNullOrWhiteSpace(finalPackageName))
            {
                finalPackageName = manifestDoc.IdentityName ?? "Package";
            }

            if (string.IsNullOrWhiteSpace(extractedPublisher))
            {
                extractedPublisher = manifestDoc.IdentityPublisher;
            }

            if (string.IsNullOrWhiteSpace(extractedVersion))
            {
                extractedVersion = manifestDoc.IdentityVersion;
            }
        }
        catch
        {
            finalPackageName ??= "Package";
        }

        // Clean the resolved package name to ensure it meets MSIX schema requirements
        finalPackageName = ManifestService.CleanPackageName(finalPackageName);

        var defaultMsixFileName = (packageArch, extractedVersion) switch
        {
            (not null, not null) when !string.IsNullOrWhiteSpace(extractedVersion) => $"{finalPackageName}_{extractedVersion}_{packageArch}.msix",
            (null, not null) when !string.IsNullOrWhiteSpace(extractedVersion) => $"{finalPackageName}_{extractedVersion}.msix",
            (not null, _) => $"{finalPackageName}_{packageArch}.msix",
            _ => $"{finalPackageName}.msix"
        };

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
            // Check if the manifest was generated by MSBuild and a .build.appxrecipe is available.
            // When present, the recipe lists exactly which files belong in the package and their
            // correct PackagePaths, producing a cleaner MSIX without build artifacts.
            var isMSBuildGenerated = manifestDoc.Document.Root?
                .Element(AppxManifestDocument.BuildNs + "Metadata")?
                .Elements(AppxManifestDocument.BuildNs + "Item")
                .Any(e => string.Equals(e.Attribute("Name")?.Value, "makepri.exe", StringComparison.OrdinalIgnoreCase)) == true;

            FileInfo? recipeFile = null;
            if (isMSBuildGenerated)
            {
                recipeFile = inputFolder.EnumerateFiles("*.build.appxrecipe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }

            if (recipeFile != null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} MSBuild-generated manifest detected");
                taskContext.AddDebugMessage($"{UiSymbols.Files} Using appxrecipe for staging: {recipeFile.Name}");
                await CopyFilesFromRecipeAsync(recipeFile, stagingDir, taskContext, cancellationToken);
            }
            else
            {
                // No recipe available — copy the entire input folder to staging
                CopyDirectoryRecursive(inputFolder, stagingDir);
                taskContext.AddDebugMessage($"{UiSymbols.Files} Copied input folder to staging directory");
            }

            // Write the updated manifest into the staging directory
            var updatedManifestPath = Path.Combine(stagingDir.FullName, "appxmanifest.xml");
            await File.WriteAllTextAsync(updatedManifestPath, manifestContent, Encoding.UTF8, cancellationToken);

            // Resolve executable path relative to the staging directory
            var applicationExecutable = manifestDoc.ApplicationExecutable;
            FileInfo? executablePath = applicationExecutable != null ? new FileInfo(Path.Combine(stagingDir.FullName, applicationExecutable)) : null;

            // Pre-compute expanded manifest resources from the original manifest
            var manifestIsOutsideInputFolder = !inputFolder.FullName.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(resolvedManifestPath.Directory!.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

            // If manifest is outside input folder, copy its referenced assets into the staging directory
            if (manifestIsOutsideInputFolder)
            {
                var externalAssets = MrtAssetHelper.GetExpandedManifestReferencedFiles(resolvedManifestPath, taskContext);
                MrtAssetHelper.CopyAllAssets(externalAssets, stagingDir, taskContext);
            }

            taskContext.AddDebugMessage($"Creating MSIX package from staging: {stagingDir.FullName}");
            taskContext.AddDebugMessage($"Output: {outputMsixPath.FullName}");

            // Generate PRI files if not skipped and no existing PRI from the build output
            var existingPri = new FileInfo(Path.Combine(stagingDir.FullName, "resources.pri"));
            if (!skipPri && !existingPri.Exists)
            {
                taskContext.AddDebugMessage("Generating PRI configuration and files...");

                // Expand manifest-referenced files from the staging manifest so that
                // assets from both the input folder and external manifest are discovered.
                var stagingManifest = new FileInfo(Path.Combine(stagingDir.FullName, "appxmanifest.xml"));
                var priExpandedFiles = MrtAssetHelper.GetExpandedManifestReferencedFiles(stagingManifest, taskContext);
                var priResourceCandidates = priExpandedFiles.Select(file => file.RelativePath);
                await priService.CreatePriConfigAsync(
                    stagingDir,
                    taskContext,
                    precomputedPriResourceCandidates: priResourceCandidates,
                    cancellationToken: cancellationToken);
                var resourceFiles = await priService.GeneratePriFileAsync(stagingDir, taskContext, cancellationToken: cancellationToken);
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
            else if (!skipPri && existingPri.Exists)
            {
                taskContext.AddDebugMessage("Skipping PRI generation — existing resources.pri found in input folder");
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

    private async Task<DotNetPackageListJson?> FetchDotNetPackageListAsync(CancellationToken cancellationToken)
    {
        var cwd = new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
        var csprojFiles = dotNetService.FindCsproj(cwd);
        var csproj = csprojFiles.Count > 0 ? csprojFiles[0] : null;
        if (csproj == null)
        {
            return null;
        }

        return await dotNetService.GetPackageListAsync(csproj, cancellationToken: cancellationToken);
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
            var found = FindManifestInDirectory(directory);
            if (found != null)
            {
                return found;
            }
            
            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Checks a single directory for a manifest file (appxmanifest.xml or package.appxmanifest).
    /// </summary>
    internal static FileInfo? FindManifestInDirectory(DirectoryInfo directory)
    {
        var appxManifest = new FileInfo(Path.Combine(directory.FullName, "appxmanifest.xml"));
        if (appxManifest.Exists)
        {
            return appxManifest;
        }

        var packageManifest = new FileInfo(Path.Combine(directory.FullName, "package.appxmanifest"));
        if (packageManifest.Exists)
        {
            return packageManifest;
        }

        return null;
    }

    /// <summary>
    /// Updates the manifest identity, application ID, and executable path for sparse packaging
    /// </summary>
    private async Task<(string Content, string? DetectedArchitecture)> UpdateAppxManifestContentAsync(
        string originalAppxManifestContent,
        MsixIdentityResult? identity,
        string? entryPointPath,
        string? exePath,
        bool sparse,
        bool selfContained,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var doc = AppxManifestDocument.Parse(originalAppxManifestContent);

        if (identity != null)
        {
            doc.IdentityName = identity.PackageName;
            doc.ApplicationId = identity.ApplicationId;
        }

        if (entryPointPath != null)
        {
            var entryPointDir = Path.GetDirectoryName(entryPointPath);
            var workingDir = string.IsNullOrEmpty(entryPointDir) ? currentDirectoryProvider.GetCurrentDirectory() : entryPointDir;
            string relativeExecutablePath;

            try
            {
                relativeExecutablePath = Path.GetRelativePath(workingDir, entryPointPath);
                relativeExecutablePath = relativeExecutablePath.Replace('\\', '/');
            }
            catch
            {
                relativeExecutablePath = Path.GetFileName(entryPointPath);
            }

            doc.ApplicationExecutable = relativeExecutablePath;
        }

        bool isExe = Path.HasExtension(entryPointPath) && string.Equals(Path.GetExtension(entryPointPath), ".exe", StringComparison.OrdinalIgnoreCase);

        if (sparse)
        {
            // Add required namespaces for sparse packaging
            doc.EnsureNamespace("uap10", AppxManifestDocument.Uap10Ns);
            doc.EnsureNamespace("desktop6", AppxManifestDocument.Desktop6Ns);

            // Add sparse package properties
            var properties = doc.Document.Root?.Element(AppxManifestDocument.DefaultNs + "Properties");
            if (properties != null && properties.Element(AppxManifestDocument.Uap10Ns + "AllowExternalContent") == null)
            {
                properties.Add(new XElement(AppxManifestDocument.Uap10Ns + "AllowExternalContent", "true"));
                properties.Add(new XElement(AppxManifestDocument.Desktop6Ns + "RegistryWriteVirtualization", "disabled"));
            }

            // Ensure Application has sparse packaging attributes
            var app = doc.GetFirstApplicationElement();
            if (app != null && isExe && app.Attribute(AppxManifestDocument.Uap10Ns + "TrustLevel") == null)
            {
                app.SetAttributeValue(AppxManifestDocument.Uap10Ns + "TrustLevel", "mediumIL");
                app.SetAttributeValue(AppxManifestDocument.Uap10Ns + "RuntimeBehavior", "packagedClassicApp");
            }

            // Remove EntryPoint if present (not needed for sparse packages)
            doc.ApplicationEntryPoint = null;

            // Add AppListEntry="none" to VisualElements if not present
            var ve = doc.GetVisualElements();
            if (ve != null && ve.Attribute("AppListEntry") == null)
            {
                ve.SetAttributeValue("AppListEntry", "none");
            }

            // Add sparse-specific capabilities if not present
            var capsElement = doc.GetCapabilitiesElement();
            bool hasUnvirtualizedResources = capsElement?.Elements()
                .Any(e => string.Equals(e.Attribute("Name")?.Value, "unvirtualizedResources", StringComparison.OrdinalIgnoreCase)) == true;
            if (!hasUnvirtualizedResources)
            {
                doc.EnsureCapability("unvirtualizedResources", AppxManifestDocument.RescapNs);
                doc.EnsureCapability("allowElevation", AppxManifestDocument.RescapNs);
            }
        }

        // Convert to string for remaining string-based operations
        var modifiedContent = doc.ToXml();

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

        // Auto-detect ProcessorArchitecture from the executable PE header if not already set.
        // Without this, ARM64 Windows resolves framework dependencies to ARM64 DLLs even for x64 apps.
        string? detectedArch = null;
        if (exePath != null)
        {
            (modifiedContent, detectedArch) = AutoDetectProcessorArchitecture(modifiedContent, exePath, taskContext);
        }

        return (modifiedContent, detectedArch);
    }

    /// <summary>
    /// Adds or updates build:Metadata in the manifest with the CLI tool name and version.
    /// Inserts the build namespace and IgnorableNamespaces entry if not already present.
    /// </summary>
    internal static string AddBuildMetadata(string manifestContent)
    {
        var version = VersionHelper.GetVersionString();

        var doc = AppxManifestDocument.Parse(manifestContent);

        doc.EnsureNamespace("build", AppxManifestDocument.BuildNs);
        doc.AddIgnorableNamespace("build");
        doc.SetBuildMetadata("Microsoft.WinAppCli", version);

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
