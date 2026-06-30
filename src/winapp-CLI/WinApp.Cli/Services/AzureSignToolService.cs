// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

/// <summary>
/// Acquires the Azure Trusted Signing client library and drives signtool to sign a file.
/// </summary>
internal class AzureSignToolService(
    IBuildToolsService buildToolsService,
    INugetService nugetService,
    IPackageInstallationService packageInstallationService,
    IWinappDirectoryService winappDirectoryService) : IAzureSignToolService
{
    internal const string ArtifactSigningClientPackage = "Microsoft.ArtifactSigning.Client";

    // Pin to a known-good version so the DLL loaded into signtool is reproducible and
    // not silently upgraded to whatever happens to be latest in the NuGet feed.
    internal const string ArtifactSigningClientVersion = "1.0.128";

    private const string TimestampUrl = "http://timestamp.acs.microsoft.com";

    public async Task SignAsync(
        FileInfo filePath,
        FileInfo metadataFilePath,
        string? tenantId,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        // Ensure the Trusted Signing dlib is available
        var dlibPath = await EnsureTrustedSigningDlibAsync(taskContext, cancellationToken);

        // The dlib only ships as x64/x86. We must use the matching signtool architecture.
        // Determine dlib architecture from its path (bin/x64/ or bin/x86/)
        var dlibArch = dlibPath.Directory?.Name; // "x64" or "x86"

        // Ensure signtool is installed, then find the matching architecture version
        var signtoolPath = await buildToolsService.EnsureBuildToolAvailableAsync("signtool.exe", taskContext, cancellationToken: cancellationToken);

        // If we're on ARM64 and the signtool resolved is arm64, swap to x64 to match the dlib
        if (dlibArch != null && !signtoolPath.FullName.Contains($"\\{dlibArch}\\", StringComparison.OrdinalIgnoreCase))
        {
            // Try to find the x64 signtool alongside the resolved one
            var signtoolDir = signtoolPath.Directory!;
            var parentDir = signtoolDir.Parent;
            if (parentDir != null)
            {
                var matchingArchSigntool = new FileInfo(Path.Combine(parentDir.FullName, dlibArch, "signtool.exe"));
                if (matchingArchSigntool.Exists)
                {
                    signtoolPath = matchingArchSigntool;
                }
            }
        }

        // Build signtool arguments for Azure Trusted Signing
        var arguments = $@"sign /v /debug /fd SHA256 /tr ""{TimestampUrl}"" /td SHA256 /dlib ""{dlibPath.FullName}"" /dmdf ""{metadataFilePath.FullName}"" ""{filePath.FullName}""";

        taskContext.AddDebugMessage($"Using signtool: {signtoolPath.FullName}");
        taskContext.AddDebugMessage($"Using dlib: {dlibPath.FullName}");

        // Pass tenant ID to signtool so the dlib's Azure.Identity authenticates against the correct tenant.
        IReadOnlyDictionary<string, string>? environment = !string.IsNullOrEmpty(tenantId)
            ? new Dictionary<string, string> { ["AZURE_TENANT_ID"] = tenantId }
            : null;

        // Reuse the shared build-tool runner so process spawning, concurrent stream draining,
        // cancellation/kill, and exit-code handling live in one place. We pass the resolved
        // (architecture-matched) signtool path as an override rather than re-resolving by name.
        await buildToolsService.RunBuildToolAsync(
            new GenericTool("signtool.exe"),
            arguments,
            taskContext,
            toolPathOverride: signtoolPath,
            environment: environment,
            cancellationToken: cancellationToken);

        taskContext.AddDebugMessage("File signed successfully");
    }

    internal async Task<FileInfo> EnsureTrustedSigningDlibAsync(TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Check if already available in NuGet cache
        var dlibPath = FindTrustedSigningDlib(ArtifactSigningClientVersion);
        if (dlibPath != null)
        {
            return dlibPath;
        }

        // Download the pinned version of the package
        await taskContext.AddSubTaskAsync($"Installing {ArtifactSigningClientPackage} {ArtifactSigningClientVersion}...", async (subContext, ct) =>
        {
            var globalWinappDir = winappDirectoryService.GetGlobalWinappDirectory();
            var success = await packageInstallationService.EnsurePackageAsync(
                globalWinappDir,
                ArtifactSigningClientPackage,
                subContext,
                version: ArtifactSigningClientVersion,
                cancellationToken: ct);

            if (!success)
            {
                return (1, $"Failed to install {ArtifactSigningClientPackage}.");
            }

            return (0, $"{ArtifactSigningClientPackage} installed successfully.");
        }, cancellationToken);

        dlibPath = FindTrustedSigningDlib(ArtifactSigningClientVersion);
        if (dlibPath == null)
        {
            throw new InvalidOperationException(
                $"Could not find the Trusted Signing client library after installing {ArtifactSigningClientPackage} {ArtifactSigningClientVersion}.\n" +
                "Ensure the package contains the expected DLL structure.");
        }

        return dlibPath;
    }

    private FileInfo? FindTrustedSigningDlib(string? version = null)
    {
        var nugetCache = nugetService.GetNuGetGlobalPackagesDir();
        var packageDir = new DirectoryInfo(Path.Combine(nugetCache.FullName, ArtifactSigningClientPackage.ToLowerInvariant()));

        if (!packageDir.Exists)
        {
            return null;
        }

        IEnumerable<DirectoryInfo> versionDirs;
        if (!string.IsNullOrEmpty(version))
        {
            // Prefer the exact pinned version so the loaded DLL is reproducible.
            var exactDir = new DirectoryInfo(Path.Combine(packageDir.FullName, version));
            if (!exactDir.Exists)
            {
                return null;
            }
            versionDirs = [exactDir];
        }
        else
        {
            // Fall back to the highest installed version using semantic (not lexicographic) ordering.
            versionDirs = packageDir.GetDirectories()
                .OrderByDescending(d => d.Name, Comparer<string>.Create(NugetService.CompareVersions));
        }

        foreach (var versionDir in versionDirs)
        {
            // The dlib is at: bin/x64/Azure.CodeSigning.Dlib.dll (x64 works on ARM64 via emulation)
            var dlibFile = new FileInfo(Path.Combine(versionDir.FullName, "bin", "x64", "Azure.CodeSigning.Dlib.dll"));
            if (dlibFile.Exists)
            {
                return dlibFile;
            }

            // Fallback: search recursively for the DLL
            var found = versionDir.GetFiles("Azure.CodeSigning.Dlib.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
