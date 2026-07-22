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

    [TestMethod]
    public async Task SignAsync_ForwardsTenantAndSelectsArchMatchedSigntool()
    {
        var dlib = CreateDlibInCache(); // bin/x64/Azure.CodeSigning.Dlib.dll
        var signtool = CreateFakeSigntool("x64");
        var recording = new RecordingBuildToolsService { SignToolToReturn = signtool };
        var service = new AzureSignToolService(
            recording, _nuget, _installer, new FakeSignToolWinappDirectoryService(_globalWinappDir));

        var fileToSign = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.msix"));
        await File.WriteAllTextAsync(fileToSign.FullName, "MZ", TestContext.CancellationToken);
        var metadata = new FileInfo(Path.Combine(_tempDirectory.FullName, "metadata.json"));
        await File.WriteAllTextAsync(metadata.FullName, "{}", TestContext.CancellationToken);

        await service.SignAsync(fileToSign, metadata, "my-tenant-id", TestTaskContext, TestContext.CancellationToken);

        // The x64 dlib forces the architecture-matched x64 signtool to be used as the override.
        Assert.AreEqual(signtool.FullName, recording.CapturedToolPathOverride!.FullName);

        // signtool arguments reference the dlib, the metadata file, and the target file.
        StringAssert.Contains(recording.CapturedArguments!, dlib.FullName);
        StringAssert.Contains(recording.CapturedArguments!, metadata.FullName);
        StringAssert.Contains(recording.CapturedArguments!, fileToSign.FullName);

        // The tenant id is forwarded to the dlib via AZURE_TENANT_ID.
        Assert.IsNotNull(recording.CapturedEnvironment);
        Assert.AreEqual("my-tenant-id", recording.CapturedEnvironment!["AZURE_TENANT_ID"]);
    }

    [TestMethod]
    public async Task SignAsync_WithoutTenant_DoesNotInjectEnvironment()
    {
        CreateDlibInCache();
        var signtool = CreateFakeSigntool("x64");
        var recording = new RecordingBuildToolsService { SignToolToReturn = signtool };
        var service = new AzureSignToolService(
            recording, _nuget, _installer, new FakeSignToolWinappDirectoryService(_globalWinappDir));

        var fileToSign = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.msix"));
        await File.WriteAllTextAsync(fileToSign.FullName, "MZ", TestContext.CancellationToken);
        var metadata = new FileInfo(Path.Combine(_tempDirectory.FullName, "metadata.json"));
        await File.WriteAllTextAsync(metadata.FullName, "{}", TestContext.CancellationToken);

        await service.SignAsync(fileToSign, metadata, tenantId: null, TestTaskContext, TestContext.CancellationToken);

        Assert.IsNull(recording.CapturedEnvironment, "No AZURE_TENANT_ID should be injected when no tenant is known");
    }

    [TestMethod]
    public async Task SignAsync_WhenResolvedSigntoolArchMismatchesDlib_SwapsToMatchingArchSibling()
    {
        // The dlib only ships x64/x86, so on an ARM64 host the resolved arm64 signtool must be
        // swapped for the sibling x64 signtool that matches the x64 dlib.
        var dlib = CreateDlibInCache(); // bin/x64/Azure.CodeSigning.Dlib.dll
        var arm64Signtool = CreateFakeSigntool("arm64"); // what tool resolution returns
        var x64Signtool = CreateFakeSigntool("x64");      // sibling under the same bin parent
        var recording = new RecordingBuildToolsService { SignToolToReturn = arm64Signtool };
        var service = new AzureSignToolService(
            recording, _nuget, _installer, new FakeSignToolWinappDirectoryService(_globalWinappDir));

        var fileToSign = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.msix"));
        await File.WriteAllTextAsync(fileToSign.FullName, "MZ", TestContext.CancellationToken);
        var metadata = new FileInfo(Path.Combine(_tempDirectory.FullName, "metadata.json"));
        await File.WriteAllTextAsync(metadata.FullName, "{}", TestContext.CancellationToken);

        await service.SignAsync(fileToSign, metadata, "tenant", TestTaskContext, TestContext.CancellationToken);

        Assert.IsNotNull(dlib);
        Assert.AreEqual(x64Signtool.FullName, recording.CapturedToolPathOverride!.FullName,
            "The x64 dlib should force a swap from the resolved arm64 signtool to the sibling x64 signtool");
    }

    private FileInfo CreateFakeSigntool(string architecture)
    {
        var dir = Path.Combine(_tempDirectory.FullName, "sdk", "bin", architecture);
        Directory.CreateDirectory(dir);
        var signtool = new FileInfo(Path.Combine(dir, "signtool.exe"));
        File.WriteAllText(signtool.FullName, "fake signtool");
        return signtool;
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

internal sealed class RecordingBuildToolsService : IBuildToolsService
{
    public FileInfo SignToolToReturn { get; set; } = null!;

    public Tool? CapturedTool { get; private set; }
    public string? CapturedArguments { get; private set; }
    public FileInfo? CapturedToolPathOverride { get; private set; }
    public IReadOnlyDictionary<string, string>? CapturedEnvironment { get; private set; }

    public FileInfo? GetBuildToolPath(string toolName) => throw new NotImplementedException();

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
        => Task.FromResult(SignToolToReturn);

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        CapturedTool = tool;
        CapturedArguments = arguments;
        CapturedToolPathOverride = toolPathOverride;
        CapturedEnvironment = environment;
        return Task.FromResult((string.Empty, string.Empty));
    }
}
