// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake MSIX service that returns predictable identity results without performing real operations.
/// </summary>
internal class FakeMsixService : IMsixService
{
    public MsixIdentityResult FakeIdentityResult { get; set; } = new("TestPackage", "CN=TestPublisher", "TestApp");
    public List<string> AddLooseLayoutCalls { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    public Task<MsixIdentityResult> AddLooseLayoutIdentityAsync(
        FileInfo appxManifestPath,
        DirectoryInfo inputDirectory,
        DirectoryInfo outputAppXDirectory,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        AddLooseLayoutCalls.Add(appxManifestPath.FullName);
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(FakeIdentityResult);
    }

    public Task<MsixIdentityResult> AddSparseIdentityAsync(
        string? entryPointPath,
        FileInfo appxManifestPath,
        bool noInstall,
        bool keepIdentity,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(FakeIdentityResult);
    }

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
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new CreateMsixPackageResult(new FileInfo("fake.msix"), false));
    }
}
