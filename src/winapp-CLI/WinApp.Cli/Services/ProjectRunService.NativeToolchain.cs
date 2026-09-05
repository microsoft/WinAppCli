// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class ProjectRunService
{
    internal sealed record NativeAotToolchainSetup(
        bool Ready,
        string? Error,
        string HostArchitecture,
        string TargetArchitecture,
        string? VsWherePath,
        string StandardVsWherePath,
        string? VisualStudioPath,
        string? MsvcVersion,
        string? LinkerPath,
        string? WindowsSdkVersion,
        string? WindowsSdkRoot,
        bool AddedToPath,
        IReadOnlyDictionary<string, string>? EnvironmentOverrides)
    {
        internal NativeAotToolchainInfo ToInfo() =>
            new(
                Ready,
                HostArchitecture,
                TargetArchitecture,
                VsWherePath,
                VisualStudioPath,
                MsvcVersion,
                LinkerPath,
                WindowsSdkVersion,
                WindowsSdkRoot,
                Error);
    }

    internal static NativeAotToolchainSetup ResolveNativeAotToolchainSetup(
        string targetArchitecture,
        string? inheritedPath = null,
        string? installedVsWherePath = null,
        Architecture? hostArchitecture = null,
        IReadOnlyList<string>? visualStudioInstallations = null,
        string? windowsKitsRoot = null)
    {
        var target = RunArchHelper.NormalizeArchitecture(targetArchitecture);
        if (target is not ("x64" or "arm64"))
        {
            return FailedToolchain(
                targetArchitecture,
                HostArchitectureName(hostArchitecture ?? RuntimeInformation.ProcessArchitecture),
                installedVsWherePath,
                $"Windows Native AOT supports x64 and arm64 targets; '{targetArchitecture}' is not supported.");
        }

        var host = hostArchitecture ?? RuntimeInformation.ProcessArchitecture;
        var hostName = HostArchitectureName(host);
        var hostFolder = host switch
        {
            Architecture.X64 => "Hostx64",
            Architecture.Arm64 => "Hostarm64",
            _ => null,
        };
        if (hostFolder is null)
        {
            return FailedToolchain(
                target,
                hostName,
                installedVsWherePath,
                $"Windows Native AOT toolchain discovery supports x64 and ARM64 hosts; the current host is '{hostName}'.");
        }

        inheritedPath ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var installerDirectory = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer");
        installedVsWherePath ??= Path.Join(installerDirectory, "vswhere.exe");
        var vsWherePath = FindExecutableOnPath("vswhere.exe", inheritedPath);
        var addedToPath = false;
        IReadOnlyDictionary<string, string>? environmentOverrides = null;
        if (vsWherePath is null && File.Exists(installedVsWherePath))
        {
            vsWherePath = Path.GetFullPath(installedVsWherePath);
            installerDirectory = Path.GetDirectoryName(vsWherePath)!;
            var updatedPath = string.IsNullOrWhiteSpace(inheritedPath)
                ? installerDirectory
                : $"{installerDirectory}{Path.PathSeparator}{inheritedPath}";
            addedToPath = true;
            environmentOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = updatedPath,
            };
        }

        var installations = visualStudioInstallations ??
            (vsWherePath is null ? [] : QueryVisualStudioInstallations(vsWherePath));
        var linker = FindMsvcLinker(installations, hostFolder, target);
        if (linker is null)
        {
            var vsWhereDetail = vsWherePath is null
                ? $"vswhere.exe was not found on PATH or at '{installedVsWherePath}'."
                : $"Visual Studio installations reported by '{vsWherePath}' do not contain the required linker.";
            return new NativeAotToolchainSetup(
                Ready: false,
                Error:
                    $"Native AOT target win-{target} on a {hostName} host requires the MSVC linker '{hostFolder}\\{target}\\link.exe'. " +
                    $"{vsWhereDetail} Install Visual Studio or Visual Studio Build Tools with Desktop development with C++ and the {target} build tools.",
                hostName,
                target,
                vsWherePath,
                installedVsWherePath,
                VisualStudioPath: null,
                MsvcVersion: null,
                LinkerPath: null,
                WindowsSdkVersion: null,
                WindowsSdkRoot: null,
                addedToPath,
                environmentOverrides);
        }

        windowsKitsRoot ??= FindWindowsKitsRoot();
        var sdk = FindWindowsSdk(windowsKitsRoot, target);
        if (sdk is null)
        {
            return new NativeAotToolchainSetup(
                Ready: false,
                Error:
                    $"Native AOT target win-{target} on a {hostName} host found MSVC {linker.Value.Version}, but no Windows SDK containing " +
                    $"Lib\\<version>\\ucrt\\{target}\\ucrt.lib and Lib\\<version>\\um\\{target}\\kernel32.lib was found. " +
                    "Install a Windows 10 or Windows 11 SDK through Visual Studio Installer.",
                hostName,
                target,
                vsWherePath,
                installedVsWherePath,
                linker.Value.InstallationPath,
                linker.Value.Version,
                linker.Value.LinkerPath,
                WindowsSdkVersion: null,
                windowsKitsRoot,
                addedToPath,
                environmentOverrides);
        }

        return new NativeAotToolchainSetup(
            Ready: true,
            Error: null,
            hostName,
            target,
            vsWherePath,
            installedVsWherePath,
            linker.Value.InstallationPath,
            linker.Value.Version,
            linker.Value.LinkerPath,
            sdk.Value.Version,
            sdk.Value.Root,
            addedToPath,
            environmentOverrides);
    }

    private static NativeAotToolchainSetup FailedToolchain(
        string target,
        string host,
        string? installedVsWherePath,
        string error)
    {
        installedVsWherePath ??= Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");
        return new NativeAotToolchainSetup(
            Ready: false,
            error,
            host,
            target,
            VsWherePath: null,
            installedVsWherePath,
            VisualStudioPath: null,
            MsvcVersion: null,
            LinkerPath: null,
            WindowsSdkVersion: null,
            WindowsSdkRoot: null,
            AddedToPath: false,
            EnvironmentOverrides: null);
    }

    private static string HostArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    private static string[] QueryVisualStudioInstallations(string vsWherePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = vsWherePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-all", "-products", "*", "-property", "installationPath", "-utf8" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return [];
            }

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            Win32Exception)
        {
            return [];
        }
    }

    private static (string InstallationPath, string Version, string LinkerPath)? FindMsvcLinker(
        IReadOnlyList<string> installations,
        string hostFolder,
        string target)
    {
        foreach (var installation in installations)
        {
            var toolsRoot = Path.Join(installation, "VC", "Tools", "MSVC");
            if (!Directory.Exists(toolsRoot))
            {
                continue;
            }

            foreach (var versionDirectory in new DirectoryInfo(toolsRoot)
                         .EnumerateDirectories()
                         .OrderByDescending(directory => ParseVersion(directory.Name))
                         .ThenByDescending(directory => directory.Name, StringComparer.OrdinalIgnoreCase))
            {
                var linkerPath = Path.Join(
                    versionDirectory.FullName,
                    "bin",
                    hostFolder,
                    target,
                    "link.exe");
                var runtimeLibrary = Path.Join(
                    versionDirectory.FullName,
                    "lib",
                    target,
                    "libcmt.lib");
                if (File.Exists(linkerPath) && File.Exists(runtimeLibrary))
                {
                    return (Path.GetFullPath(installation), versionDirectory.Name, linkerPath);
                }
            }
        }

        return null;
    }

    private static (string Root, string Version)? FindWindowsSdk(string? root, string target)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var libRoot = Path.Join(root, "Lib");
        if (!Directory.Exists(libRoot))
        {
            return null;
        }

        foreach (var versionDirectory in new DirectoryInfo(libRoot)
                     .EnumerateDirectories()
                     .OrderByDescending(directory => ParseVersion(directory.Name))
                     .ThenByDescending(directory => directory.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ucrt = Path.Join(versionDirectory.FullName, "ucrt", target, "ucrt.lib");
            var kernel32 = Path.Join(versionDirectory.FullName, "um", target, "kernel32.lib");
            if (File.Exists(ucrt) && File.Exists(kernel32))
            {
                return (Path.GetFullPath(root), versionDirectory.Name);
            }
        }

        return null;
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private static string? FindWindowsKitsRoot()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                if (key?.GetValue("KitsRoot10") is string root && Directory.Exists(root))
                {
                    return Path.GetFullPath(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Try the other registry view, then fall back to the conventional location.
            }
        }

        var conventional = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits",
            "10");
        return Directory.Exists(conventional) ? conventional : null;
    }

    private static string? FindExecutableOnPath(string executableName, string path)
    {
        if (string.IsNullOrWhiteSpace(executableName) ||
            Path.IsPathRooted(executableName) ||
            !string.Equals(Path.GetFileName(executableName), executableName, StringComparison.Ordinal))
        {
            return null;
        }

        return path
            .Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'))
            .Where(Path.IsPathRooted)
            .Select(directory => Path.Join(directory, executableName))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    internal static string? ResolveDotnetHostPath(
        string? dotnetRoot = null,
        string? inheritedPath = null)
    {
        dotnetRoot ??= Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot) && Path.IsPathRooted(dotnetRoot))
        {
            var rootedHost = Path.Join(dotnetRoot, "dotnet.exe");
            if (File.Exists(rootedHost))
            {
                return Path.GetFullPath(rootedHost);
            }
        }

        inheritedPath ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return FindExecutableOnPath("dotnet.exe", inheritedPath);
    }
}
