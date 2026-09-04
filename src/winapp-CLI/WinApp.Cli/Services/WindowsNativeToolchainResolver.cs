// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class WindowsNativeToolchainResolver(IProcessRunner processRunner) : IWindowsNativeToolchainResolver
{
    private const string X64Component = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
    private const string Arm64Component = "Microsoft.VisualStudio.Component.VC.Tools.ARM64";

    internal Func<string> VswherePathProvider { get; set; } = static () =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");

    internal Func<string> WindowsKitsRootProvider { get; set; } = ResolveWindowsKitsRoot;

    public async Task<WindowsNativeToolchainResolution> ResolveAsync(
        WindowsNativeToolchainRequirements requirements,
        CancellationToken cancellationToken)
    {
        var (targetName, component) = requirements.TargetArchitecture switch
        {
            Architecture.X64 => ("x64", X64Component),
            Architecture.Arm64 => ("arm64", Arm64Component),
            _ => (null, null),
        };

        if (targetName is null || component is null)
        {
            return Failure(
                "UnsupportedArchitecture",
                $"Windows Native AOT publishing supports win-x64 and win-arm64. Target architecture '{requirements.TargetArchitecture}' is not supported.");
        }

        var vswherePath = VswherePathProvider();
        if (!File.Exists(vswherePath))
        {
            return Failure(
                "VswhereNotFound",
                $"Visual Studio Installer discovery tool was not found at '{vswherePath}'. Install Visual Studio Build Tools with Desktop development with C++ and retry.",
                component);
        }

        ProcessRunResult installationPathResult;
        try
        {
            installationPathResult = await RunVswhereAsync(
                vswherePath,
                component,
                "installationPath",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or
            InvalidOperationException or
            UnauthorizedAccessException)
        {
            return Failure(
                "VswhereFailed",
                $"Visual Studio discovery could not run '{vswherePath}': {ex.Message}",
                component);
        }
        if (installationPathResult.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(installationPathResult.StandardOutput))
        {
            return Failure(
                "VisualStudioComponentMissing",
                $"No Visual Studio or Visual Studio Build Tools instance contains the required '{component}' component. Install Desktop development with C++ in Visual Studio Installer and retry.",
                component);
        }

        var installationPath = FirstOutputLine(installationPathResult.StandardOutput);
        if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
        {
            return Failure(
                "VisualStudioInstallInvalid",
                $"Visual Studio Installer returned an invalid installation path: '{installationPath}'. Repair the Visual Studio Build Tools installation and retry.",
                component);
        }

        var visualStudioVersion = "unknown";
        try
        {
            var installationVersionResult = await RunVswhereAsync(
                vswherePath,
                component,
                "installationVersion",
                cancellationToken);
            visualStudioVersion =
                FirstOutputLine(installationVersionResult.StandardOutput) ?? "unknown";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or
            InvalidOperationException or
            UnauthorizedAccessException)
        {
            // The install path and required component were already validated. Version metadata is
            // diagnostic-only, so keep the usable toolchain and report an unknown display version.
        }

        var vcToolsRoot = ResolveChildPath(installationPath, "VC", "Tools", "MSVC");
        string? vcToolsVersion;
        try
        {
            vcToolsVersion = ResolveVcToolsVersion(installationPath, vcToolsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(
                "MsvcToolsUnreadable",
                $"The MSVC tools under '{vcToolsRoot}' could not be inspected: {ex.Message}",
                component);
        }
        if (vcToolsVersion is null)
        {
            return Failure(
                "MsvcToolsMissing",
                $"The MSVC tools directory was not found under '{vcToolsRoot}'. Install or repair the '{component}' component in Visual Studio Installer.",
                component);
        }
        if (!IsSafePathSegment(vcToolsVersion))
        {
            return Failure(
                "MsvcToolsMissing",
                $"Visual Studio returned an invalid MSVC tools version '{vcToolsVersion}'.",
                component);
        }

        var vcToolsDirectory = ResolveChildPath(vcToolsRoot, vcToolsVersion);
        var toolDirectory = ResolveNativeToolDirectory(vcToolsDirectory, targetName);
        var linkerPath = toolDirectory is null
            ? null
            : ResolveChildPath(toolDirectory, "link.exe");
        if (requirements.RequireLinker && (linkerPath is null || !File.Exists(linkerPath)))
        {
            return Failure(
                "LinkerNotFound",
                $"The {targetName} Microsoft C++ linker was not found. Native AOT publishing on Windows requires Visual Studio or Visual Studio Build Tools with Desktop development with C++. Install component '{component}' and retry.",
                component);
        }

        var compilerPath = toolDirectory is null
            ? null
            : ResolveChildPath(toolDirectory, "cl.exe");
        if (requirements.RequireCompiler && (compilerPath is null || !File.Exists(compilerPath)))
        {
            return Failure(
                "CompilerNotFound",
                $"The {targetName} Microsoft C++ compiler was not found. Install component '{component}' in Visual Studio Installer and retry.",
                component);
        }

        WindowsSdkSelection? sdk;
        try
        {
            sdk = ResolveWindowsSdk(WindowsKitsRootProvider(), targetName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(
                "WindowsSdkUnreadable",
                $"The Windows SDK installation could not be inspected: {ex.Message}");
        }
        if (requirements.RequireWindowsSdk && sdk is null)
        {
            return Failure(
                "WindowsSdkNotFound",
                $"A compatible Windows 10/11 SDK with {targetName} libraries and tools was not found. Add a Windows SDK through Visual Studio Installer and retry.");
        }

        var sdkVersion = sdk?.Version ?? string.Empty;
        var environment = BuildEnvironment(
            vswherePath,
            installationPath,
            vcToolsDirectory,
            vcToolsVersion,
            toolDirectory,
            sdk,
            targetName);

        return new WindowsNativeToolchainResolution(
            new WindowsNativeToolchain(
                installationPath,
                visualStudioVersion,
                vcToolsVersion,
                requirements.RequireCompiler ? compilerPath : compilerPath is not null && File.Exists(compilerPath) ? compilerPath : null,
                linkerPath ?? string.Empty,
                sdkVersion,
                environment));
    }

    private async Task<ProcessRunResult> RunVswhereAsync(
        string vswherePath,
        string component,
        string property,
        CancellationToken cancellationToken)
    {
        var request = new ProcessRunRequest(
            vswherePath,
            [
                "-latest",
                "-products",
                "*",
                "-requires",
                component,
                "-property",
                property,
                "-utf8",
            ]);

        return await processRunner.RunAsync(request, cancellationToken: cancellationToken);
    }

    private static string? ResolveVcToolsVersion(string installationPath, string vcToolsRoot)
    {
        var defaultVersionFile = ResolveChildPath(
            installationPath,
            "VC",
            "Auxiliary",
            "Build",
            "Microsoft.VCToolsVersion.default.txt");

        if (File.Exists(defaultVersionFile))
        {
            var configured = File.ReadAllText(defaultVersionFile).Trim();
            if (IsSafePathSegment(configured) &&
                Directory.Exists(ResolveChildPath(vcToolsRoot, configured)))
            {
                return configured;
            }
        }

        if (!Directory.Exists(vcToolsRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(vcToolsRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null && IsSafePathSegment(name))
            .OrderByDescending(name => ParseVersion(name!), VersionComparer.Instance)
            .FirstOrDefault();
    }

    private static string? ResolveNativeToolDirectory(string vcToolsDirectory, string targetName)
    {
        if (!IsSafePathSegment(targetName))
        {
            return null;
        }

        var hostNames = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new[] { "Hostarm64", "Hostx64" }
            : new[] { "Hostx64", "Hostarm64" };

        return hostNames
            .Select(host => ResolveChildPath(vcToolsDirectory, "bin", host, targetName))
            .FirstOrDefault(directory =>
                File.Exists(ResolveChildPath(directory, "link.exe")));
    }

    private static WindowsSdkSelection? ResolveWindowsSdk(string kitsRoot, string targetName)
    {
        if (!Path.IsPathFullyQualified(kitsRoot) ||
            !IsSafePathSegment(targetName))
        {
            return null;
        }

        var libRoot = ResolveChildPath(kitsRoot, "Lib");
        if (!Directory.Exists(libRoot))
        {
            return null;
        }

        foreach (var versionDirectory in Directory.EnumerateDirectories(libRoot)
                     .OrderByDescending(path => ParseVersion(Path.GetFileName(path)), VersionComparer.Instance))
        {
            var version = Path.GetFileName(versionDirectory);
            if (!IsSafePathSegment(version))
            {
                continue;
            }

            var umLib = ResolveChildPath(
                versionDirectory,
                "um",
                targetName,
                "kernel32.lib");
            var ucrtLib = ResolveChildPath(
                versionDirectory,
                "ucrt",
                targetName,
                "ucrt.lib");
            if (!File.Exists(umLib) || !File.Exists(ucrtLib))
            {
                continue;
            }

            var hostName = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var versionedBin = ResolveChildPath(
                kitsRoot,
                "bin",
                version,
                hostName);
            var unversionedBin = ResolveChildPath(
                kitsRoot,
                "bin",
                hostName);
            var binDirectory = HasSdkTool(versionedBin)
                ? versionedBin
                : HasSdkTool(unversionedBin)
                    ? unversionedBin
                    : null;

            if (binDirectory is not null)
            {
                return new WindowsSdkSelection(version, kitsRoot, binDirectory, versionDirectory);
            }
        }

        return null;
    }

    private static bool HasSdkTool(string directory) =>
        File.Exists(ResolveChildPath(directory, "mt.exe")) ||
        File.Exists(ResolveChildPath(directory, "rc.exe"));

    private static string ResolveWindowsKitsRoot()
    {
        const string installedRoots = @"SOFTWARE\Microsoft\Windows Kits\Installed Roots";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
           try
           {
               using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
               using var key = baseKey.OpenSubKey(installedRoots);
               if (key?.GetValue("KitsRoot10") is string root &&
                   !string.IsNullOrWhiteSpace(root))
               {
                   return root.Trim();
               }
           }
           catch (Exception ex) when (
               ex is UnauthorizedAccessException or
               System.Security.SecurityException or
               PlatformNotSupportedException)
           {
               // Fall back to the conventional location below.
           }
        }

        return ResolveChildPath(
           Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
           "Windows Kits",
           "10");
    }

    private static Dictionary<string, string> BuildEnvironment(
        string vswherePath,
        string installationPath,
        string vcToolsDirectory,
        string vcToolsVersion,
        string? toolDirectory,
        WindowsSdkSelection? sdk,
        string targetName)
    {
        if (!IsSafePathSegment(targetName))
        {
            throw new ArgumentException(
                $"'{targetName}' is not a valid target architecture segment.",
                nameof(targetName));
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VSINSTALLDIR"] = EnsureTrailingSeparator(installationPath),
            ["VCToolsInstallDir"] = EnsureTrailingSeparator(vcToolsDirectory),
            ["VCToolsVersion"] = vcToolsVersion,
            ["VSCMD_ARG_TGT_ARCH"] = targetName,
        };

        var pathEntries = new List<string>
        {
            Path.GetDirectoryName(vswherePath)!,
        };
        if (toolDirectory is not null)
        {
            pathEntries.Add(toolDirectory);
        }

        if (sdk is not null)
        {
            environment["WindowsSdkDir"] = EnsureTrailingSeparator(sdk.Root);
            environment["WindowsSDKVersion"] = EnsureTrailingSeparator(sdk.Version);
            pathEntries.Add(sdk.BinDirectory);

            var libEntries = new[]
            {
                ResolveChildPath(sdk.LibVersionDirectory, "um", targetName),
                ResolveChildPath(sdk.LibVersionDirectory, "ucrt", targetName),
                ResolveChildPath(vcToolsDirectory, "lib", targetName),
            };
            environment["LIB"] = JoinEnvironmentPath(libEntries, Environment.GetEnvironmentVariable("LIB"));

            var includeRoot = ResolveChildPath(sdk.Root, "Include", sdk.Version);
            var includeEntries = new[]
            {
                ResolveChildPath(vcToolsDirectory, "include"),
                ResolveChildPath(includeRoot, "ucrt"),
                ResolveChildPath(includeRoot, "shared"),
                ResolveChildPath(includeRoot, "um"),
                ResolveChildPath(includeRoot, "winrt"),
            };
            environment["INCLUDE"] = JoinEnvironmentPath(includeEntries, Environment.GetEnvironmentVariable("INCLUDE"));
        }

        environment["PATH"] = JoinEnvironmentPath(
            pathEntries,
            Environment.GetEnvironmentVariable("PATH"));
        return environment;
    }

    private static string JoinEnvironmentPath(IEnumerable<string> entries, string? inherited)
    {
        var all = entries.Where(entry => !string.IsNullOrWhiteSpace(entry)).ToList();
        if (!string.IsNullOrWhiteSpace(inherited))
        {
            all.Add(inherited);
        }
        return string.Join(Path.PathSeparator, all);
    }

    private static string ResolveChildPath(string root, params string[] segments)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException(
                $"Path root must be fully qualified: '{root}'.",
                nameof(root));
        }
        if (segments.Any(segment => !IsSafePathSegment(segment)))
        {
            throw new ArgumentException(
                $"Path contains an invalid child segment: '{string.Join(", ", segments)}'.",
                nameof(segments));
        }

        return Path.GetFullPath(
            string.Join(Path.DirectorySeparatorChar, segments),
            root);
    }

    private static bool IsSafePathSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) &&
        value is not "." and not ".." &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string? FirstOutputLine(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static Version ParseVersion(string? value) =>
        Version.TryParse(value, out var parsed) ? parsed : new Version();

    private static WindowsNativeToolchainResolution Failure(
        string errorCode,
        string error,
        string? component = null) =>
        new(null, errorCode, error, component);

    private sealed record WindowsSdkSelection(
        string Version,
        string Root,
        string BinDirectory,
        string LibVersionDirectory);

    private sealed class VersionComparer : IComparer<Version>
    {
        internal static readonly VersionComparer Instance = new();
        public int Compare(Version? x, Version? y) => Comparer<Version>.Default.Compare(x, y);
    }
}
