// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Models;

internal sealed record WindowsNativeToolchainRequirements(
    Architecture TargetArchitecture,
    bool RequireCompiler,
    bool RequireLinker,
    bool RequireWindowsSdk);

internal sealed record WindowsNativeToolchain(
    string VisualStudioInstallPath,
    string VisualStudioVersion,
    string VcToolsVersion,
    string? CompilerPath,
    string LinkerPath,
    string WindowsSdkVersion,
    IReadOnlyDictionary<string, string> Environment);

internal sealed record WindowsNativeToolchainResolution(
    WindowsNativeToolchain? Toolchain,
    string? ErrorCode = null,
    string? Error = null,
    string? RequiredComponent = null)
{
    public bool Succeeded => Toolchain is not null;
}

internal sealed record NativeAotStaticVerification(
    bool Succeeded,
    IReadOnlyList<string> ForbiddenFiles,
    string? Error = null,
    bool SingleFileBundle = false);

internal sealed record NativeAotRuntimeVerificationRequest(
    uint ProcessId,
    string SourceExecutable,
    string ExpectedProcessPath,
    ProjectPackaging Packaging,
    string? StagingDirectory = null,
    string? PackageIdentity = null,
    Func<int?>? ExitCodeProvider = null);

internal sealed record NativeAotRuntimeVerification(
    bool Succeeded,
    bool Alive,
    bool RuntimeModules,
    bool ProcessProvenance,
    bool? PackageRegistration,
    string? ProcessPath,
    IReadOnlyList<string> LoadedModules,
    long MainWindowHandle,
    string MainWindowTitle,
    string? Error = null,
    int? ExitCode = null);

internal sealed class RunVerificationResult
{
    public bool StaticPayload { get; set; }
    public bool RuntimeModules { get; set; }
    public bool ProcessProvenance { get; set; }
    public bool? PackageRegistration { get; set; }
}
