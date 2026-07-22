// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

[TestClass]
public class AzureSignToolServiceTests : BaseCommandTests
{
    public AzureSignToolServiceTests() : base(configPaths: false)
    {
    }

    private static readonly string PackageDirName = AzureSignToolService.ArtifactSigningClientPackage.ToLowerInvariant();
    private const string DlibDllName = "Azure.CodeSigning.Dlib.dll";

    private DirectoryInfo _nugetCacheDir = null!;
    private DirectoryInfo _globalWinappDir = null!;
    private FakeSignToolNugetService _nuget = null!;
    private FakeSignToolPackageInstallationService _installer = null!;
    private AzureSignToolService _service = null!;

    [TestInitialize]
    public void SetupService()
    {
        _nugetCacheDir = _tempDirectory.CreateSubdirectory("nuget-cache");
        _globalWinappDir = _tempDirectory.CreateSubdirectory("global-winapp");
        _nuget = new FakeSignToolNugetService(_nugetCacheDir);
        _installer = new FakeSignToolPackageInstallationService();

        _service = new AzureSignToolService(
            new FakeSignToolBuildToolsService(),
            _nuget,
            _installer,
            new FakeSignToolWinappDirectoryService(_globalWinappDir));
    }

    private FileInfo CreateDlibInCache()
    {
        var binDir = Path.Combine(
            _nugetCacheDir.FullName,
            PackageDirName,
            AzureSignToolService.ArtifactSigningClientVersion,
            "bin",
            "x64");
        Directory.CreateDirectory(binDir);
        var dlibPath = Path.Combine(binDir, DlibDllName);
        File.WriteAllText(dlibPath, "fake dlib");
        return new FileInfo(dlibPath);
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenDlibAlreadyCached_ReturnsPathWithoutInstalling()
    {
        var expected = CreateDlibInCache();

        var result = await _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(expected.FullName, result.FullName);
        Assert.AreEqual(0, _installer.EnsureCallCount, "Should not install when the dlib is already cached");
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenMissingThenInstalled_InstallsAndReturnsPath()
    {
        // Simulate the package install materializing the dlib on disk.
        _installer.EnsureResult = true;
        _installer.OnEnsure = () => CreateDlibInCache();

        var result = await _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(1, _installer.EnsureCallCount, "Should attempt to install the signing client package");
        Assert.AreEqual(DlibDllName, result.Name);
        Assert.IsTrue(result.Exists);
        StringAssert.Contains(result.FullName, AzureSignToolService.ArtifactSigningClientVersion);
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenInstallSucceedsButDlibMissing_Throws()
    {
        // Install reports success but nothing is written to the cache.
        _installer.EnsureResult = true;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Could not find the Trusted Signing client library");
        Assert.AreEqual(1, _installer.EnsureCallCount);
    }

    [TestMethod]
    public async Task EnsureTrustedSigningDlibAsync_WhenInstallFails_Throws()
    {
        _installer.EnsureResult = false;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.EnsureTrustedSigningDlibAsync(TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Could not find the Trusted Signing client library");
    }
}

internal sealed class FakeSignToolNugetService(DirectoryInfo cacheDir) : INugetService
{
    public DirectoryInfo GetNuGetGlobalPackagesDir() => cacheDir;

    public Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
        => throw new NotImplementedException();
}

internal sealed class FakeSignToolWinappDirectoryService(DirectoryInfo globalDir) : IWinappDirectoryService
{
    public DirectoryInfo GetGlobalWinappDirectory() => globalDir;

    public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null)
        => throw new NotImplementedException();

    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
    {
        // no-op
    }
}

internal sealed class FakeSignToolPackageInstallationService : IPackageInstallationService
{
    public bool EnsureResult { get; set; } = true;
    public int EnsureCallCount { get; private set; }
    public Action? OnEnsure { get; set; }

    public void InitializeWorkspace(DirectoryInfo rootDirectory)
        => throw new NotImplementedException();

    public Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
    {
        EnsureCallCount++;
        OnEnsure?.Invoke();
        return Task.FromResult(EnsureResult);
    }
}

internal sealed class FakeSignToolBuildToolsService : IBuildToolsService
{
    public FileInfo? GetBuildToolPath(string toolName) => throw new NotImplementedException();

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
