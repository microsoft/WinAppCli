// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class NativeAotVerifier(IPackageRegistrationService packageRegistrationService) : INativeAotVerifier
{
    private static readonly string[] RuntimePayloadFiles =
    [
        "coreclr.dll",
        "clrjit.dll",
        "hostfxr.dll",
        "hostpolicy.dll",
        "System.Private.CoreLib.dll",
    ];

    private static readonly HashSet<string> RuntimeModuleNames = new(
        RuntimePayloadFiles,
        StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan InitialStartupDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartupReadinessWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(100);

    public NativeAotStaticVerification VerifyPayload(
        DirectoryInfo publishDirectory,
        FileInfo sourceExecutable,
        DirectoryInfo? excludedStagingDirectory = null)
    {
        if (!publishDirectory.Exists)
        {
            return new NativeAotStaticVerification(
                false,
                [],
                $"The publish directory does not exist: {publishDirectory.FullName}");
        }

        if (!sourceExecutable.Exists)
        {
            return new NativeAotStaticVerification(
                false,
                [],
                $"The published executable does not exist: {sourceExecutable.FullName}");
        }

        var appName = Path.GetFileNameWithoutExtension(sourceExecutable.Name);
        var forbiddenNames = new HashSet<string>(RuntimePayloadFiles, StringComparer.OrdinalIgnoreCase)
        {
            $"{appName}.dll",
            $"{appName}.runtimeconfig.json",
        };

        try
        {
            var bundleHeaderOffset = PeHelper.GetDotNetSingleFileBundleHeaderOffset(
                sourceExecutable.FullName);
            if (bundleHeaderOffset is not null)
            {
                return new NativeAotStaticVerification(
                    false,
                    [sourceExecutable.FullName],
                    $"The published executable is a .NET single-file bundle (bundle header offset {bundleHeaderOffset.Value}). " +
                    "A self-contained single-file JIT app can omit side-by-side CoreCLR files, so it cannot be certified as Native AOT. " +
                    "Publish with PublishAot=true and PublishSingleFile disabled, then retry.",
                    SingleFileBundle: true);
            }

            var forbidden = publishDirectory
                .EnumerateFiles("*", SearchOption.AllDirectories)
               .Where(file => excludedStagingDirectory is null ||
                   !IsPathInsideDirectory(file.FullName, excludedStagingDirectory.FullName))
                .Where(file => forbiddenNames.Contains(file.Name))
                .Select(file => file.FullName)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new NativeAotStaticVerification(forbidden.Length == 0, forbidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NativeAotStaticVerification(
                false,
                [],
                $"The Native AOT payload could not be inspected: {ex.Message}");
        }
    }

    public async Task<NativeAotRuntimeVerification> VerifyRuntimeAsync(
        NativeAotRuntimeVerificationRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(InitialStartupDelay, cancellationToken);

        if (request.ProcessId == 0 || request.ProcessId > int.MaxValue)
        {
            return Failure($"The launched process ID '{request.ProcessId}' cannot be inspected.");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(unchecked((int)request.ProcessId));
        }
        catch (ArgumentException)
        {
            return ExitFailure(request);
        }

        using (process)
        {
           var deadline = DateTime.UtcNow + StartupReadinessWindow;
           while (true)
            {
               nint mainWindowHandle;
                try
                {
                   process.Refresh();
                   if (process.HasExited)
                   {
                       return ExitFailure(request, process);
                   }
                   mainWindowHandle = process.MainWindowHandle;
                }
               catch (InvalidOperationException)
               {
                   return ExitFailure(request, process);
               }

               // A GUI app may create its top-level window after the process itself is ready. Poll for
               // that observable signal during the verification-only readiness window, but do not require
               // it: console and background apps are valid Native AOT processes too.
               if (mainWindowHandle != 0 || DateTime.UtcNow >= deadline)
               {
                   break;
               }

               await Task.Delay(StartupPollInterval, cancellationToken);
            }

            string? processPath;
            IReadOnlyList<string> modules;
            try
            {
                processPath = process.MainModule?.FileName;
                modules = process.Modules
                    .Cast<ProcessModule>()
                    .Select(module => module.ModuleName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                return Failure($"The running process could not be inspected: {ex.Message}");
            }

            var runtimeModules = !modules.Any(IsManagedRuntimeModule);
            var processProvenance = PathsEqual(processPath, request.ExpectedProcessPath);
            string? provenanceError = processProvenance
                ? null
                : $"The launched process originated from '{processPath ?? "(unknown)"}', not the expected artifact '{request.ExpectedProcessPath}'.";

            bool? packageRegistration = null;
            if (request.Packaging == ProjectPackaging.Packaged)
            {
                (packageRegistration, provenanceError) = VerifyPackageRegistration(request, provenanceError);
                processProvenance &= packageRegistration == true;

                if (processProvenance && !FilesHaveSameContent(request.SourceExecutable, request.ExpectedProcessPath))
                {
                    processProvenance = false;
                    provenanceError =
                        $"The staged executable '{request.ExpectedProcessPath}' does not match the published artifact '{request.SourceExecutable}'.";
                }
            }

            var error = !runtimeModules
                ? $"The running process loaded managed runtime modules: {string.Join(", ", modules.Where(IsManagedRuntimeModule))}."
                : provenanceError;

            return new NativeAotRuntimeVerification(
                runtimeModules && processProvenance,
                Alive: true,
                RuntimeModules: runtimeModules,
                ProcessProvenance: processProvenance,
                PackageRegistration: packageRegistration,
                ProcessPath: processPath,
                LoadedModules: modules,
                MainWindowHandle: process.MainWindowHandle.ToInt64(),
                MainWindowTitle: process.MainWindowTitle ?? string.Empty,
                Error: error,
                ExitCode: null);
        }
    }

    private (bool Succeeded, string? Error) VerifyPackageRegistration(
        NativeAotRuntimeVerificationRequest request,
        string? priorError)
    {
        if (string.IsNullOrWhiteSpace(request.PackageIdentity) ||
            string.IsNullOrWhiteSpace(request.StagingDirectory))
        {
            return (false, priorError ?? "Packaged-process provenance is missing package identity or staging information.");
        }

        IReadOnlyList<DevPackageInfo> registrations;
        try
        {
            registrations = packageRegistrationService.FindDevPackages(request.PackageIdentity);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return (false, priorError ?? $"The package registration could not be inspected: {ex.Message}");
        }

        var matching = registrations.FirstOrDefault(registration =>
            PathsEqual(registration.InstallLocation, request.StagingDirectory));

        if (matching is null)
        {
            return (false, priorError ??
                $"No package registration points at the staging directory '{request.StagingDirectory}'.");
        }

        if (!matching.IsDevelopmentMode)
        {
            return (false, priorError ??
                $"The package at '{request.StagingDirectory}' is not registered in development mode.");
        }

        return (true, priorError);
    }

    private static bool IsManagedRuntimeModule(string name)
    {
        if (RuntimeModuleNames.Contains(name))
        {
            return true;
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(name);
        return withoutExtension.Equals("coreclr", StringComparison.OrdinalIgnoreCase) ||
               withoutExtension.Equals("clrjit", StringComparison.OrdinalIgnoreCase) ||
               withoutExtension.Equals("hostfxr", StringComparison.OrdinalIgnoreCase) ||
               withoutExtension.Equals("hostpolicy", StringComparison.OrdinalIgnoreCase) ||
               withoutExtension.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                NormalizePath(left),
                NormalizePath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsPathInsideDirectory(string path, string directory)
    {
       var normalizedPath = NormalizePath(path);
       var normalizedDirectory = NormalizePath(directory) + Path.DirectorySeparatorChar;
       return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FilesHaveSameContent(string left, string right)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using var leftStream = leftInfo.OpenRead();
            using var rightStream = rightInfo.OpenRead();
            return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static NativeAotRuntimeVerification ExitFailure(
        NativeAotRuntimeVerificationRequest request,
        Process? process = null)
    {
        var exitCode = TryReadExitCode(process) ?? TryReadExitCode(request.ExitCodeProvider);
        var exitDetail = exitCode is null ? string.Empty : $" with exit code {exitCode.Value}";
        return Failure(
            $"The app exited{exitDetail} before Native AOT verification completed. " +
            "Re-run without --verify-native-aot or --detach and add --debug-output; add --symbols for native crash details.",
            exitCode);
    }

    private static int? TryReadExitCode(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? TryReadExitCode(Func<int?>? provider)
    {
        if (provider is null)
        {
            return null;
        }

        try
        {
            return provider();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static NativeAotRuntimeVerification Failure(string error, int? exitCode = null) =>
        new(
            Succeeded: false,
            Alive: false,
            RuntimeModules: false,
            ProcessProvenance: false,
            PackageRegistration: null,
            ProcessPath: null,
            LoadedModules: [],
            MainWindowHandle: 0,
            MainWindowTitle: string.Empty,
            Error: error,
            ExitCode: exitCode);
}
