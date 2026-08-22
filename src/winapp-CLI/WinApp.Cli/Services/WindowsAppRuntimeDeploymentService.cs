// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Prepares an exact Windows App SDK runtime for an unpackaged desktop app. The framework-dependent
/// path stages the bootstrap payload and verifies (or installs) the matching runtime packages; the
/// self-contained path stages the complete runtime and embeds the registration-free activation manifest.
/// </summary>
internal sealed class WindowsAppRuntimeDeploymentService(
    INugetService nugetService,
    IWindowsAppRuntimeService windowsAppRuntimeService) : IWindowsAppRuntimeDeploymentService
{
    private const string BootstrapDllName = "Microsoft.WindowsAppRuntime.Bootstrap.dll";

    public async Task<WindowsAppRuntimePrepareResult> PrepareAsync(
        string version,
        string architecture,
        DirectoryInfo outputDirectory,
        bool install,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var arch = RunArchHelper.NormalizeArchitecture(architecture)
            ?? throw new ArgumentException(
                $"Unsupported architecture '{architecture}'. Supported values: {string.Join(", ", RunArchHelper.SupportedArchitectures)}.",
                nameof(architecture));

        ValidateExactVersion(version);

        outputDirectory.Create();

        taskContext.AddDebugMessage($"{UiSymbols.Package} Resolving {BuildToolsService.WINAPP_SDK_PACKAGE} v{version}");
        var packages = await nugetService.InstallPackageAsync(
            BuildToolsService.WINAPP_SDK_PACKAGE,
            version,
            taskContext,
            cancellationToken);

        var bootstrapSource = FindBootstrapDll(version, arch, packages);
        var bootstrapPath = new FileInfo(Path.Combine(outputDirectory.FullName, BootstrapDllName));
        ReplaceBootstrapDll(bootstrapSource, bootstrapPath);
        bootstrapPath.Refresh();

        var runtimeVersion = packages
            .Where(p => p.Key.Equals(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value)
            .FirstOrDefault() ?? version;

        var msixDirectory = windowsAppRuntimeService.FindWindowsAppSdkMsixDirectory(packages, requireExactVersion: true)
            ?? throw new DirectoryNotFoundException(
                $"Windows App Runtime MSIX payloads for {BuildToolsService.WINAPP_SDK_PACKAGE} v{version} were not found after restore.");

        var runtimePackages = (await windowsAppRuntimeService.GetWindowsAppRuntimePackagesAsync(
                msixDirectory,
                taskContext,
                cancellationToken,
                arch))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .Select(p => new WindowsAppRuntimePackageIdentity(p.Name, p.Version))
            .ToList();

        var hasFramework = runtimePackages.Any(p => WindowsAppRuntimeService.IsFrameworkGatePackageName(p.Name));
        var hasDdlm = runtimePackages.Any(p => WindowsAppRuntimeService.IsDdlmGatePackageName(p.Name));
        if (!hasFramework || !hasDdlm)
        {
            var missingIdentities = string.Join(
                " and ",
                new[]
                {
                    hasFramework ? null : "Framework",
                    hasDdlm ? null : "DDLM",
                }.Where(name => name is not null));
            throw new InvalidDataException(
                $"The exact runtime payload for {BuildToolsService.WINAPP_SDK_PACKAGE} v{version} ({arch}) " +
                $"does not contain a required {missingIdentities} identity.");
        }

        var expectedPackages = runtimePackages.Select(p => (p.Name, p.Version)).ToList();
        var runtimeRegistered = windowsAppRuntimeService.IsWindowsAppRuntimeRegistered(arch, expectedPackages);
        var installedCount = 0;
        var installed = false;

        if (!runtimeRegistered && install)
        {
            var installResult = await windowsAppRuntimeService.InstallWindowsAppRuntimeAsync(
                msixDirectory,
                taskContext,
                cancellationToken,
                arch);
            installedCount = installResult.InstalledCount;
            installed = installedCount > 0;

            if (installResult.ErrorCount > 0)
            {
                throw new InvalidOperationException(
                    $"{installResult.ErrorCount} Windows App Runtime package(s) failed to install.");
            }

            runtimeRegistered = windowsAppRuntimeService.IsWindowsAppRuntimeRegistered(arch, expectedPackages);
        }

        var guidance = runtimeRegistered
            ? null
            : $"Install the matching runtime with: winapp runtime prepare --version {version} --arch {arch} --output \"{outputDirectory.FullName}\" --install";

        return new WindowsAppRuntimePrepareResult
        {
            DeploymentMode = "framework-dependent",
            Version = version,
            RuntimeVersion = runtimeVersion,
            Architecture = arch,
            OutputPath = outputDirectory.FullName,
            BootstrapDllPath = bootstrapPath.FullName,
            Ready = runtimeRegistered,
            RuntimeRegistered = runtimeRegistered,
            Installed = installed,
            InstalledPackageCount = installedCount,
            RuntimePackages = runtimePackages,
            Guidance = guidance,
        };
    }

    private static void ValidateExactVersion(string version)
    {
        if (!NuGetVersionHelper.IsPlausibleVersion(version)
            || version.IndexOfAny(['*', '[', ']', '(', ')', ',']) >= 0)
        {
            throw new ArgumentException(
                $"'{version}' is not an exact Windows App SDK package version. Pass a concrete NuGet version such as 1.8.250907003 or 2.2.0.",
                nameof(version));
        }
    }

    private FileInfo FindBootstrapDll(
        string version,
        string architecture,
        IReadOnlyDictionary<string, string> packages)
    {
        foreach (var (package, packageVersion) in packages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var packageDirectory = nugetService.GetNuGetPackageDir(package, packageVersion);
            foreach (var runtimeIdentifier in new[] { $"win-{architecture}", $"win10-{architecture}" })
            {
                var candidate = new FileInfo(Path.Combine(
                    packageDirectory.FullName,
                    "runtimes",
                    runtimeIdentifier,
                    "native",
                    BootstrapDllName));
                if (candidate.Exists)
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"{BootstrapDllName} was not found for {BuildToolsService.WINAPP_SDK_PACKAGE} v{version} ({architecture}). " +
            "The selected package version may not contain a runtime for this architecture.");
    }

    private static void ReplaceBootstrapDll(FileInfo source, FileInfo destination)
    {
        var tempPath = Path.Combine(
            destination.DirectoryName!,
            $".{BootstrapDllName}.{Guid.NewGuid():N}.tmp");

        try
        {
            source.CopyTo(tempPath, overwrite: false);
            destination.Refresh();
            if (destination.Exists)
            {
                File.Replace(tempPath, destination.FullName, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, destination.FullName);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
