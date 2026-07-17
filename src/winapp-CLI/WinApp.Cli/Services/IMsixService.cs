// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IMsixService
{
    public Task<CreateMsixPackageResult> CreateMsixPackageAsync(
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
        CancellationToken cancellationToken = default);

    public Task<CreateMsixBundleResult> CreateMsixBundleAsync(
        DirectoryInfo[] inputFolders,
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
        CancellationToken cancellationToken = default);

    public Task<MsixIdentityResult> AddSparseIdentityAsync(
        string? entryPointPath,
        FileInfo appxManifestPath,
        bool noInstall,
        bool keepIdentity,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);

    public Task<MsixIdentityResult> AddLooseLayoutIdentityAsync(
        FileInfo appxManifestPath,
        DirectoryInfo inputDirectory,
        DirectoryInfo outputAppXDirectory,
        TaskContext taskContext,
        bool clean = false,
        string? executable = null,
        string? runtimeArch = null,
        FileInfo? projectFile = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the Windows App Runtime framework packages (Framework / DDLM / Singleton / Main) are
    /// installed for a project-mode <b>unpackaged</b> app before it is launched. The DDLM this lays down
    /// is exactly what an unpackaged WinUI app's bootstrapper resolves at startup. Reuses the same install
    /// path as the packaged flow; callers must gate on <c>WindowsAppSDKSelfContained</c> (skip when true).
    /// </summary>
    /// <param name="projectFile">The project whose package list drives runtime version resolution; <c>null</c> falls back to a cwd glob.</param>
    /// <param name="architecture">The app's resolved architecture (<c>x64</c> / <c>arm64</c> / <c>x86</c>), so the correct-arch Framework/DDLM is installed.</param>
    /// <param name="taskContext">Status/debug sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task EnsureWindowsAppRuntimeInstalledAsync(
        FileInfo? projectFile,
        string? architecture,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);
}
