// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="MsixService"/> identity workflows (sparse / loose-layout registration)
/// defined in <c>MsixService.Identity.cs</c>. External SDK tools and package registration are
/// faked; no real makeappx/makepri/Add-AppxPackage is invoked.
/// </summary>
[TestClass]
public class MsixServiceIdentityTests : BaseCommandTests
{
    private MsixService _msixService = null!;
    private FakePackageRegistrationService _fakeRegistration = null!;
    private FakeDevModeService _fakeDevMode = null!;
    private ScriptedMtBuildToolsService _fakeBuildTools = null!;
    private FakeWorkspaceSetupService _fakeWorkspaceSetup = null!;

    private static readonly MethodInfo CopyFilesFromRecipeMethod =
        typeof(MsixService).GetMethod("CopyFilesFromRecipeAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo SyncFilesToOutputMethod =
        typeof(MsixService).GetMethod("SyncFilesToOutputDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo EmbedManifestFileToExeMethod =
        typeof(MsixService).GetMethod("EmbedManifestFileToExeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo EnsureWindowsAppRuntimeInstalledMethod =
        typeof(MsixService).GetMethod("EnsureWindowsAppRuntimeInstalledAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeRegistration = new FakePackageRegistrationService();
        _fakeDevMode = new FakeDevModeService();
        _fakeBuildTools = new ScriptedMtBuildToolsService();
        _fakeWorkspaceSetup = new FakeWorkspaceSetupService();

        return services
            .AddSingleton<IPackageRegistrationService>(_fakeRegistration)
            .AddSingleton<IDevModeService>(_fakeDevMode)
            .AddSingleton<IBuildToolsService>(_fakeBuildTools)
            .AddSingleton<IWorkspaceSetupService>(_fakeWorkspaceSetup)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    [TestInitialize]
    public void SetupService()
    {
        _msixService = (MsixService)GetRequiredService<IMsixService>();
    }

    // ---- Manifest / recipe helpers ------------------------------------------------

    private const string ManifestNamespaces =
        "xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
        "xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\" " +
        "xmlns:build=\"http://schemas.microsoft.com/developer/appx/2015/build\" " +
        "IgnorableNamespaces=\"uap build\"";

    /// <summary>An MSBuild-generated manifest (contains a build:Metadata makepri.exe item).</summary>
    /// <summary>A raw (non-MSBuild) manifest: no build:Metadata makepri.exe item.</summary>
    private static string BuildRawManifest(string packageName = "TestApp", string exe = "TestApp.exe")
    {
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package {ManifestNamespaces}>
              <Identity Name="{packageName}" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>{packageName}</DisplayName>
                <PublisherDisplayName>Test</PublisherDisplayName>
                <Logo>Assets\logo.png</Logo>
              </Properties>
              <Resources>
                <Resource Language="x-generate" />
              </Resources>
              <Applications>
                <Application Id="App" Executable="{exe}" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """;
    }

    private static string BuildMSBuildManifest(string packageName = "TestApp", string exe = "TestApp.exe")
    {
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package {ManifestNamespaces}>
              <Identity Name="{packageName}" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>{packageName}</DisplayName>
                <PublisherDisplayName>Test</PublisherDisplayName>
                <Logo>Assets\logo.png</Logo>
              </Properties>
              <Applications>
                <Application Id="App" Executable="{exe}" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
              <build:Metadata>
                <build:Item Name="makepri.exe" Version="10.0.0.0" />
              </build:Metadata>
            </Package>
            """;
    }

    private string WriteRecipe(FileInfo srcManifest, params (string Include, string PackagePath)[] files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <AppXManifest Include=\"{srcManifest.FullName}\">");
        sb.AppendLine("      <PackagePath>appxmanifest.xml</PackagePath>");
        sb.AppendLine("    </AppXManifest>");
        foreach (var (include, packagePath) in files)
        {
            sb.AppendLine($"    <AppxPackagedFile Include=\"{include}\">");
            sb.AppendLine($"      <PackagePath>{packagePath}</PackagePath>");
            sb.AppendLine("    </AppxPackagedFile>");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        var recipePath = Path.Combine(_tempDirectory.FullName, "TestApp.build.appxrecipe");
        File.WriteAllText(recipePath, sb.ToString());
        return recipePath;
    }

    private Task InvokeCopyFilesFromRecipeAsync(FileInfo recipe, DirectoryInfo outputDir)
    {
        return (Task)CopyFilesFromRecipeMethod.Invoke(
            null, [recipe, outputDir, TestTaskContext, CancellationToken.None])!;
    }

    private void InvokeSyncFilesToOutputDirectory(DirectoryInfo input, DirectoryInfo output, FileInfo manifest)
    {
        SyncFilesToOutputMethod.Invoke(null, [input, output, manifest, TestTaskContext]);
    }

    // ---- CopyFilesFromRecipeAsync -------------------------------------------------

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_CopiesManifestAndPackagedFiles()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcData = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.exe"));
        await File.WriteAllTextAsync(srcData.FullName, "exe-bytes", TestContext.CancellationToken);

        var recipe = new FileInfo(WriteRecipe(srcManifest, (srcData.FullName, "TestApp.exe")));
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "Manifest should be copied to PackagePath");
        Assert.AreEqual("exe-bytes", await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "TestApp.exe"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_NestedPackagePath_CreatesSubdirectories()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcData = new FileInfo(Path.Combine(srcDir.FullName, "logo.png"));
        await File.WriteAllTextAsync(srcData.FullName, "png", TestContext.CancellationToken);

        var recipe = new FileInfo(WriteRecipe(srcManifest, (srcData.FullName, @"Assets\logo.png")));
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "Assets", "logo.png")), "Nested asset should be created under Assets");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_SkipsUnchangedFiles()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcData = new FileInfo(Path.Combine(srcDir.FullName, "data.bin"));
        await File.WriteAllTextAsync(srcData.FullName, "AAAAA", TestContext.CancellationToken);
        var timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(srcData.FullName, timestamp);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));
        outputDir.Create();
        // Pre-existing dest: same length + same timestamp but DIFFERENT content -> must be skipped.
        var destData = Path.Combine(outputDir.FullName, "data.bin");
        await File.WriteAllTextAsync(destData, "BBBBB", TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(destData, timestamp);

        var recipe = new FileInfo(WriteRecipe(srcManifest, (srcData.FullName, "data.bin")));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.AreEqual("BBBBB", await File.ReadAllTextAsync(destData, TestContext.CancellationToken),
            "Unchanged file (same size + timestamp) should be skipped, not overwritten");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_MissingSourceFile_IsSkipped()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var missing = Path.Combine(srcDir.FullName, "does-not-exist.dll");
        var recipe = new FileInfo(WriteRecipe(srcManifest, (missing, "does-not-exist.dll")));
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "Manifest still copied");
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "does-not-exist.dll")), "Missing source must not produce a dest file");
    }

    // ---- SyncFilesToOutputDirectory -----------------------------------------------

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_CopiesFilesAndManifest()
    {
        var inputDir = _tempDirectory.CreateSubdirectory("input");
        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        InvokeSyncFilesToOutputDirectory(inputDir, outputDir, manifest);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "TestApp.exe")), "Input files should be synced");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "Manifest should be copied");
    }

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_RenamesPackageAppxmanifest()
    {
        var inputDir = _tempDirectory.CreateSubdirectory("input");
        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "app.exe"), "exe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        InvokeSyncFilesToOutputDirectory(inputDir, outputDir, manifest);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "Package.appxmanifest should be renamed to appxmanifest.xml");
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "Package.appxmanifest")), "Original Package.appxmanifest name should not remain");
    }

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_ProtectsExistingManifestAndPri()
    {
        var inputDir = _tempDirectory.CreateSubdirectory("input");
        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "app.exe"), "exe", TestContext.CancellationToken);

        var outputDir = _tempDirectory.CreateSubdirectory("out");
        // Pre-existing protected files that a naive mirror-sync would delete.
        await File.WriteAllTextAsync(Path.Combine(outputDir.FullName, "resources.pri"), "keep-pri", TestContext.CancellationToken);

        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        InvokeSyncFilesToOutputDirectory(inputDir, outputDir, manifest);

        Assert.AreEqual("keep-pri", await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "resources.pri"), TestContext.CancellationToken),
            "Existing resources.pri must be protected during sync");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "app.exe")), "New input file should still be copied");
    }

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_InputEqualsOutput_CopiesManifestOnly()
    {
        var dir = _tempDirectory.CreateSubdirectory("same");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        // input == output: the SyncDirectory step is skipped, only the manifest copy runs.
        InvokeSyncFilesToOutputDirectory(dir, dir, manifest);

        Assert.IsTrue(File.Exists(Path.Combine(dir.FullName, "appxmanifest.xml")), "Manifest should still be copied into the directory");
        Assert.IsTrue(File.Exists(Path.Combine(dir.FullName, "TestApp.exe")), "Existing file should remain untouched");
    }

    // ---- IsPathInsideDirectory (additional branch coverage) -----------------------

    [TestMethod]
    public void IsPathInsideDirectory_ParentOfContainer_ReturnsFalse()
    {
        var container = _tempDirectory.CreateSubdirectory("child").FullName;
        var parent = _tempDirectory.FullName;
        Assert.IsFalse(MsixService.IsPathInsideDirectory(parent, container), "'..' traversal must be rejected");
    }

    [TestMethod]
    public void IsPathInsideDirectory_DifferentRoot_ReturnsFalse()
    {
        // A UNC path is on a different root from the local temp container, so GetRelativePath
        // returns a rooted path -> not contained.
        Assert.IsFalse(MsixService.IsPathInsideDirectory(@"\\server\share\file", _tempDirectory.FullName));
    }

    [TestMethod]
    public void IsPathInsideDirectory_InvalidCandidatePath_ReturnsFalse()
    {
        // Embedded null char makes Path.GetFullPath throw -> caught -> false.
        Assert.IsFalse(MsixService.IsPathInsideDirectory("bad\0path", _tempDirectory.FullName));
    }

    // ---- Register wrappers --------------------------------------------------------

    [TestMethod]
    public async Task RegisterSparsePackageAsync_Success_DelegatesToRegistrationService()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);
        var external = _tempDirectory;

        await _msixService.RegisterSparsePackageAsync(manifest, external, TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(1, _fakeRegistration.RegisterSparseCalls);
        Assert.AreEqual(manifest.FullName, _fakeRegistration.RegisterSparseCalls[0].ManifestPath);
        Assert.AreEqual(external.FullName, _fakeRegistration.RegisterSparseCalls[0].ExternalLocation);
    }

    [TestMethod]
    public async Task RegisterSparsePackageAsync_RegistrationFails_ThrowsInvalidOperation()
    {
        _fakeRegistration.RegisterSparseThrows = new InvalidOperationException("boom");
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.RegisterSparsePackageAsync(manifest, _tempDirectory, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("Failed to register sparse package", ex.Message);
    }

    [TestMethod]
    public async Task RegisterLooseLayoutPackageAsync_Success_DelegatesToRegistrationService()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);

        await _msixService.RegisterLooseLayoutPackageAsync(manifest, TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
        Assert.AreEqual(manifest.FullName, _fakeRegistration.RegisterLooseLayoutCalls[0]);
    }

    [TestMethod]
    public async Task RegisterLooseLayoutPackageAsync_RegistrationFails_ThrowsInvalidOperation()
    {
        _fakeRegistration.RegisterLooseLayoutThrows = new InvalidOperationException("boom");
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.RegisterLooseLayoutPackageAsync(manifest, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("Failed to register package", ex.Message);
    }

    // ---- AddSparseIdentityAsync guards --------------------------------------------

    [TestMethod]
    public async Task AddSparseIdentityAsync_ManifestMissing_ThrowsFileNotFound()
    {
        var missingManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "nope.xml"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddSparseIdentityAsync(null, missingManifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_DevModeDisabled_ThrowsInvalidOperation()
    {
        _fakeDevMode.Enabled = false;
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddSparseIdentityAsync("app.exe", manifest, noInstall: false, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("Developer Mode", ex.Message);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_PlaceholderExecutableWithoutEntryPoint_Throws()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(exe: "$targetnametoken$.exe"), TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddSparseIdentityAsync(null, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("placeholder", ex.Message);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_EntryPointNotFound_ThrowsFileNotFound()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var missingExe = Path.Combine(_tempDirectory.FullName, "not-here.exe");

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddSparseIdentityAsync(missingExe, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
    }

    // ---- AddLooseLayoutIdentityAsync guards ---------------------------------------

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_ManifestMissing_ThrowsFileNotFound()
    {
        var missingManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "nope.xml"));
        var input = _tempDirectory.CreateSubdirectory("input");
        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddLooseLayoutIdentityAsync(missingManifest, input, output, TestTaskContext, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_DevModeDisabled_ThrowsInvalidOperation()
    {
        _fakeDevMode.Enabled = false;
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var input = _tempDirectory.CreateSubdirectory("input");
        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddLooseLayoutIdentityAsync(manifest, input, output, TestTaskContext, cancellationToken: TestContext.CancellationToken));
        Assert.Contains("Developer Mode", ex.Message);
    }

    // ---- AddLooseLayoutIdentityAsync MSBuild workflow -----------------------------

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_MSBuildManifestWithRecipe_RegistersLooseLayout()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcExe = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.exe"));
        await File.WriteAllTextAsync(srcExe.FullName, "exe", TestContext.CancellationToken);

        // Recipe file lives in the input directory (srcDir) with the *.build.appxrecipe extension.
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <AppXManifest Include=\"{srcManifest.FullName}\"><PackagePath>appxmanifest.xml</PackagePath></AppXManifest>");
        sb.AppendLine($"    <AppxPackagedFile Include=\"{srcExe.FullName}\"><PackagePath>TestApp.exe</PackagePath></AppxPackagedFile>");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.build.appxrecipe"), sb.ToString(), TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.AreEqual("CN=TestPublisher", result.Publisher);
        Assert.AreEqual("App", result.ApplicationId);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "appxmanifest.xml")), "Layout manifest should be produced from the recipe");
        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_MSBuildManifestNoRecipe_FallsBackToSync()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "appxmanifest.xml")), "Fallback sync should copy/rename manifest into the layout");
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "TestApp.exe")), "Fallback sync should copy input files");
        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_ProcessesAndRegisters()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        // A concrete (non-runtime) executable so placeholder resolution/PE detection has a target.
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "not-a-real-pe", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.AreEqual("App", result.ApplicationId);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "appxmanifest.xml")), "Processed manifest should be written to the layout");
        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
        // x-generate should have been resolved during processing.
        var written = await File.ReadAllTextAsync(Path.Combine(output.FullName, "appxmanifest.xml"), TestContext.CancellationToken);
        Assert.DoesNotContain("x-generate", written, "x-generate language token should be resolved");
    }

    // ---- AddSparseIdentityAsync workflow ------------------------------------------

    private (FileInfo manifest, string exePath) ArrangeSparseInputs()
    {
        // Manifest + assets live in one directory; the executable in another, so the
        // asset-copy branch (originalManifestDir != entryPointDir) is exercised.
        var srcDir = _tempDirectory.CreateSubdirectory("src");
        var assets = srcDir.CreateSubdirectory("Assets");
        File.WriteAllText(Path.Combine(assets.FullName, "logo.png"), "png-bytes");
        var manifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        File.WriteAllText(manifest.FullName, BuildRawManifest());

        var binDir = _tempDirectory.CreateSubdirectory("bin");
        var exePath = Path.Combine(binDir.FullName, "TestApp.exe");
        File.WriteAllText(exePath, "not-a-real-pe");

        return (manifest, exePath);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_NoInstall_GeneratesSparseStructureAndDebugIdentity()
    {
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        // keepIdentity:false -> CreateDebugIdentity appends ".debug".
        Assert.AreEqual("TestApp.debug", result.PackageName);
        Assert.AreEqual("App.debug", result.ApplicationId);
        Assert.AreEqual("CN=TestPublisher", result.Publisher);
        // Asset copy + PRI generation should have produced resources.pri next to the exe.
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(exePath)!, "resources.pri")),
            "resources.pri should be generated in the entry-point directory");
        // noInstall -> registration is skipped.
        Assert.IsEmpty(_fakeRegistration.RegisterSparseCalls);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_KeepIdentityWithInstall_RegistersSparsePackage()
    {
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: false, keepIdentity: true, TestTaskContext, TestContext.CancellationToken);

        // keepIdentity:true -> original identity is preserved (no ".debug" suffix).
        Assert.AreEqual("TestApp", result.PackageName);
        Assert.AreEqual("App", result.ApplicationId);
        // Install path -> unregister probe + sparse registration with external location.
        Assert.HasCount(1, _fakeRegistration.RegisterSparseCalls);
        Assert.AreEqual(Path.GetDirectoryName(exePath), _fakeRegistration.RegisterSparseCalls[0].ExternalLocation);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_PlaceholderManifest_ResolvesForDebugIdentity()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("src");
        var manifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildRawManifest(exe: "$targetnametoken$.exe"), TestContext.CancellationToken);

        var binDir = _tempDirectory.CreateSubdirectory("bin");
        var exePath = Path.Combine(binDir.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exePath, "pe", TestContext.CancellationToken);

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        // Placeholder Executable ($targetnametoken$) resolves to the entry-point name.
        Assert.AreEqual("TestApp.debug", result.PackageName);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_ExeInManifestDirectory_SkipsAssetCopy()
    {
        // Manifest, exe and assets all in one directory -> the "same directory" skip branch.
        var dir = _tempDirectory.CreateSubdirectory("app");
        var assets = dir.CreateSubdirectory("Assets");
        await File.WriteAllTextAsync(Path.Combine(assets.FullName, "logo.png"), "png", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(dir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        var exePath = Path.Combine(dir.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exePath, "pe", TestContext.CancellationToken);

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
        Assert.IsTrue(File.Exists(Path.Combine(dir.FullName, "resources.pri")), "PRI should still be generated when manifest and exe share a directory");
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_NullEntryPoint_ResolvesEntryPointTokenAndDerivesExe()
    {
        // No entrypoint argument: the executable is derived from the manifest's Application/@Executable.
        // The EntryPoint attribute carries a resolvable token, so the placeholder-resolution branch runs
        // while the Executable attribute itself stays concrete.
        var exeAbs = Path.Combine(_tempDirectory.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exeAbs, "pe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        var content = BuildRawManifest(exe: exeAbs).Replace("Windows.FullTrustApplication", "$targetentrypoint$");
        await File.WriteAllTextAsync(manifest.FullName, content, TestContext.CancellationToken);

        var result = await _msixService.AddSparseIdentityAsync(
            entryPointPath: null, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_NullEntryPoint_ExecutablePlaceholder_Throws()
    {
        // The Executable attribute is itself a placeholder and no entrypoint is provided, so the
        // executable cannot be resolved -> the workflow fails fast with an actionable error.
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildRawManifest(exe: "$targetnametoken$.exe"), TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddSparseIdentityAsync(
                entryPointPath: null, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
    }

    // ---- AddLooseLayoutIdentityAsync (non-MSBuild edge/error paths) ----------------

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_ExecutableMissing_ThrowsFileNotFound()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        // Executable declared in manifest but not present in the input/output layout.
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(exe: "Missing.exe"), TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddLooseLayoutIdentityAsync(srcManifest, srcDir, output, TestTaskContext, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_RenamesExePriToResourcesPri()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "pe", TestContext.CancellationToken);
        // A pri named after the executable should be renamed to resources.pri.
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.pri"), "pri-bytes", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "resources.pri")), "TestApp.pri should be renamed to resources.pri");
        Assert.IsFalse(File.Exists(Path.Combine(output.FullName, "TestApp.pri")), "The original <exe>.pri should no longer exist after rename");
    }

    // ---- Executable manifest embedding (mt.exe) ----------------------------------

    private Task InvokeEmbedManifestFileToExeAsync(FileInfo exe, FileInfo manifest) =>
        (Task)EmbedManifestFileToExeMethod.Invoke(
            _msixService, [exe, manifest, TestTaskContext, TestContext.CancellationToken])!;

    [TestMethod]
    public async Task AddSparseIdentityAsync_ExecutableHasExistingManifest_MergesAndSkipsDuplicateIdentity()
    {
        // mt.exe -inputresource now "extracts" a manifest that already declares an
        // assemblyIdentity, so the identity-merge branches are exercised.
        _fakeBuildTools.SimulateExistingExeManifest = true;
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
        // Because an existing manifest was found, mt.exe should have been asked to MERGE
        // (-manifest ... -out) rather than copy the new manifest verbatim.
        Assert.IsTrue(
            _fakeBuildTools.MtInvocations.Any(a =>
                a.Contains("-manifest", StringComparison.OrdinalIgnoreCase) &&
                a.Contains("-out:", StringComparison.OrdinalIgnoreCase)),
            "Expected an mt.exe merge invocation when the executable already carries a manifest");
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_ManifestExtractionFails_FallsBackWithoutThrowing()
    {
        // mt.exe -inputresource throws -> TryExtractManifestFromExeAsync swallows it and
        // reports "no existing manifest", so the flow proceeds with the new manifest.
        _fakeBuildTools.ThrowWhen = (tool, args) =>
            tool.Contains("mt", StringComparison.OrdinalIgnoreCase) &&
            args.Contains("-inputresource", StringComparison.OrdinalIgnoreCase);
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
    }

    [TestMethod]
    public async Task EmbedManifestFileToExeAsync_ExecutableMissing_ThrowsFileNotFound()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "some.manifest"));
        await File.WriteAllTextAsync(manifest.FullName, "<assembly/>", TestContext.CancellationToken);
        var missingExe = new FileInfo(Path.Combine(_tempDirectory.FullName, "missing.exe"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => InvokeEmbedManifestFileToExeAsync(missingExe, manifest));
    }

    [TestMethod]
    public async Task EmbedManifestFileToExeAsync_ManifestMissing_ThrowsFileNotFound()
    {
        var exe = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.exe"));
        await File.WriteAllTextAsync(exe.FullName, "pe", TestContext.CancellationToken);
        var missingManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "missing.manifest"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => InvokeEmbedManifestFileToExeAsync(exe, missingManifest));
    }

    [TestMethod]
    public async Task EmbedManifestFileToExeAsync_MtToolFails_ThrowsInvalidOperation()
    {
        // mt.exe fails while writing the merged manifest back into the executable -> the
        // failure is wrapped in an InvalidOperationException.
        _fakeBuildTools.ThrowWhen = (tool, args) =>
            tool.Contains("mt", StringComparison.OrdinalIgnoreCase) &&
            args.Contains("-outputresource", StringComparison.OrdinalIgnoreCase);
        var exe = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.exe"));
        await File.WriteAllTextAsync(exe.FullName, "pe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "new.manifest"));
        await File.WriteAllTextAsync(manifest.FullName, "<assembly/>", TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InvokeEmbedManifestFileToExeAsync(exe, manifest));
    }

    // ---- PRI generation failure paths --------------------------------------------

    [TestMethod]
    public async Task AddSparseIdentityAsync_PriGenerationFails_ContinuesWithWarning()
    {
        // makepri failing during sparse PRI staging must be swallowed (best-effort).
        _fakeBuildTools.ThrowWhen = (tool, _) => tool.Contains("makepri", StringComparison.OrdinalIgnoreCase);
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
        // PRI generation failed, so resources.pri should NOT have been produced next to the exe.
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(exePath)!, "resources.pri")));
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_PriGenerationFails_ContinuesWithWarning()
    {
        _fakeBuildTools.ThrowWhen = (tool, _) => tool.Contains("makepri", StringComparison.OrdinalIgnoreCase);
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "pe", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        // The PRI failure is swallowed; registration still proceeds and returns the identity.
        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
    }

    // ---- GenerateSparsePackageStructureAsync direct edge cases -------------------

    [TestMethod]
    public async Task GenerateSparsePackageStructureAsync_ExistingDebugDirectory_IsRecreated()
    {
        var (manifest, exePath) = ArrangeSparseInputs();

        // First call creates the debug directory and an initial resources.pri next to the exe.
        await _msixService.GenerateSparsePackageStructureAsync(
            manifest, exePath, keepIdentity: false, null, TestTaskContext, TestContext.CancellationToken);

        // Second call must delete the pre-existing debug directory and replace the
        // pre-existing target resources.pri. keepIdentity:true keeps the original identity.
        var (debugManifestPath, debugIdentity) = await _msixService.GenerateSparsePackageStructureAsync(
            manifest, exePath, keepIdentity: true, null, TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(debugManifestPath.FullName));
        Assert.AreEqual("TestApp", debugIdentity.PackageName);
    }

    // ---- EnsureWindowsAppRuntimeInstalledAsync -----------------------------------

    private static DotNetPackageListJson WinAppSdkPackageList(string version = "1.6.240701") =>
        new([
            new DotNetProject([
                new DotNetFramework(
                    "net8.0-windows10.0.19041.0",
                    [new DotNetPackage("Microsoft.WindowsAppSDK", version, version)],
                    [])
            ])
        ]);

    private Task InvokeEnsureWindowsAppRuntimeInstalledAsync(DotNetPackageListJson? list) =>
        (Task)EnsureWindowsAppRuntimeInstalledMethod.Invoke(
            _msixService, [list, TestTaskContext, TestContext.CancellationToken])!;

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_InstallsMissingPackages()
    {
        _fakeWorkspaceSetup.MsixDirectory = _tempDirectory.CreateSubdirectory("runtime-msix");
        _fakeWorkspaceSetup.InstallRuntimeResult = (InstalledCount: 3, ErrorCount: 0);

        await InvokeEnsureWindowsAppRuntimeInstalledAsync(WinAppSdkPackageList());

        Assert.HasCount(1, _fakeWorkspaceSetup.InstallRuntimeCalls);
        var messages = TestTask.SubTasks.OfType<StatusMessageTask>().Select(t => t.CompletedMessage ?? string.Empty).ToList();
        Assert.IsTrue(
            messages.Any(m => m.Contains("Installed 3 Windows App Runtime package(s)", StringComparison.OrdinalIgnoreCase)),
            $"Expected a success message naming the 3 installed packages. Messages:\n{string.Join("\n", messages)}");
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_ReportsInstallErrors()
    {
        _fakeWorkspaceSetup.MsixDirectory = _tempDirectory.CreateSubdirectory("runtime-msix");
        _fakeWorkspaceSetup.InstallRuntimeResult = (InstalledCount: 0, ErrorCount: 2);

        await InvokeEnsureWindowsAppRuntimeInstalledAsync(WinAppSdkPackageList());

        Assert.HasCount(1, _fakeWorkspaceSetup.InstallRuntimeCalls);
        var messages = TestTask.SubTasks.OfType<StatusMessageTask>().Select(t => t.CompletedMessage ?? string.Empty).ToList();
        Assert.IsTrue(
            messages.Any(m => m.Contains("2 runtime package(s) failed to install", StringComparison.OrdinalIgnoreCase)),
            $"Expected a warning naming the 2 failed installs. Messages:\n{string.Join("\n", messages)}");
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_RuntimeDirNotFound_SkipsInstall()
    {
        // FindWindowsAppSdkMsixDirectory returns null -> nothing to install.
        _fakeWorkspaceSetup.MsixDirectory = null;

        await InvokeEnsureWindowsAppRuntimeInstalledAsync(WinAppSdkPackageList());

        Assert.IsEmpty(_fakeWorkspaceSetup.InstallRuntimeCalls);
    }

    // ---- UnregisterExistingPackageAsync error handling ---------------------------

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_TransientFailure_IsSwallowedAndReturnsFalse()
    {
        // A non-InvalidOperation/non-cancellation failure during inspection is logged and
        // treated as "no package removed" so it never blocks the caller's overall flow.
        _fakeRegistration.FindDevPackagesThrows = new InvalidDataException("transient deployment error");

        var removed = await _msixService.UnregisterExistingPackageAsync(
            "Contoso.App", TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(removed);
    }
}

/// <summary>
/// Build-tools fake that layers mt.exe scripting on top of <see cref="FakeBuildToolsService"/>:
/// it can simulate an executable that already carries an embedded Win32 manifest, and can throw
/// for selected tool invocations. makepri/makeappx emulation is delegated to the inner fake.
/// </summary>
internal sealed class ScriptedMtBuildToolsService : IBuildToolsService
{
    private static readonly Regex MtOutRegex =
        new("-out:?\\s*\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly FakeBuildToolsService _inner = new() { Handler = FakeBuildToolsService.EmulateSdkToolOutput };

    /// <summary>Recorded mt.exe argument strings.</summary>
    public List<string> MtInvocations { get; } = [];

    /// <summary>
    /// When true, an mt.exe "-inputresource ... -out:PATH" invocation writes
    /// <see cref="ExtractedManifestXml"/> to PATH, simulating an executable that already
    /// carries an embedded Win32 manifest.
    /// </summary>
    public bool SimulateExistingExeManifest { get; set; }

    public string ExtractedManifestXml { get; set; } =
        "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">" +
        "<assemblyIdentity version=\"1.0.0.0\" name=\"Existing.App\" type=\"win32\"/></assembly>";

    /// <summary>
    /// When set, any tool invocation for which the predicate (executableName, arguments) returns
    /// true throws an <see cref="InvalidOperationException"/> instead of running.
    /// </summary>
    public Func<string, string, bool>? ThrowWhen { get; set; }

    public FileInfo? GetBuildToolPath(string toolName) => _inner.GetBuildToolPath(toolName);

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default) =>
        _inner.EnsureBuildToolAvailableAsync(toolName, taskContext, cancellationToken);

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default) =>
        _inner.EnsureBuildToolsAsync(taskContext, forceLatest, cancellationToken);

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, CancellationToken cancellationToken = default)
    {
        if (ThrowWhen?.Invoke(tool.ExecutableName, arguments) == true)
        {
            throw new InvalidOperationException($"Simulated {tool.ExecutableName} failure");
        }

        if (tool.ExecutableName.Contains("mt", StringComparison.OrdinalIgnoreCase))
        {
            MtInvocations.Add(arguments);
            if (SimulateExistingExeManifest && arguments.Contains("-inputresource", StringComparison.OrdinalIgnoreCase))
            {
                var match = MtOutRegex.Match(arguments);
                if (match.Success)
                {
                    var outPath = match.Groups["path"].Value;
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.WriteAllText(outPath, ExtractedManifestXml);
                }
            }
            return Task.FromResult<(string, string)>((string.Empty, string.Empty));
        }

        return _inner.RunBuildToolAsync(tool, arguments, taskContext, printErrors, cancellationToken);
    }
}
