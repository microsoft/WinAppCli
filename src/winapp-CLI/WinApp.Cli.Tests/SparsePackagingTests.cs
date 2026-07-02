// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the sparse identity packaging workflow: init --exe --sparse, embed-identity (XML mode),
/// and version normalization. Uses real services (no build tools required for these paths).
/// </summary>
[TestClass]
public class SparsePackagingTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services.AddSingleton<IDevModeService, FakeDevModeService>();
    }

    private string CopyTestExe(string fileName = "app.exe")
    {
        // A real PE file is needed so FileVersionInfo/icon extraction succeed.
        var dest = Path.Combine(_tempDirectory.FullName, fileName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), dest, overwrite: true);
        return dest;
    }

    [TestMethod]
    public async Task InitSparse_WithExe_GeneratesSparseIdentityManifest()
    {
        // Arrange
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--sparse", "--use-defaults", "--name", "MySparseApp" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Sparse init should succeed");

        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "appxmanifest.xml should be generated in the exe's directory");

        var content = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.Contains("uap10:AllowExternalContent", content, "Should be a sparse manifest");
        Assert.Contains("ProcessorArchitecture=\"neutral\"", content, "Identity should be neutral arch");
        Assert.Contains("MinVersion=\"10.0.19041.0\"", content, "MinVersion should be 19041");
        Assert.Contains("uap10:RuntimeBehavior=\"win32App\"", content, "Application should use win32App");
        Assert.DoesNotContain("EntryPoint=", content, "win32App must not declare EntryPoint");
        Assert.Contains("Executable=\"app.exe\"", content, "Executable should be substituted with the exe name");
        Assert.Contains("Name=\"MySparseApp\"", content, "Package name override should be applied");

        Assert.IsTrue(Directory.Exists(Path.Combine(_tempDirectory.FullName, "Assets")), "Assets directory should be generated");
    }

    [TestMethod]
    public async Task InitSparse_WithOutputDir_WritesToThatDirectory()
    {
        // Arrange
        var exe = CopyTestExe();
        var outDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "identity"));
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--sparse", "--use-defaults", "--output-dir", outDir.FullName };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Sparse init should succeed");
        Assert.IsTrue(File.Exists(Path.Combine(outDir.FullName, "appxmanifest.xml")), "Manifest should be written to --output-dir");
    }

    [TestMethod]
    public async Task InitSparse_ExeWithoutSparse_ReturnsError()
    {
        // Arrange
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "--exe without --sparse should fail");
        Assert.IsFalse(File.Exists(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml")), "No manifest should be generated");
    }

    [TestMethod]
    public async Task EmbedIdentity_XmlMode_InsertsMsixElement()
    {
        // Arrange: generate a sparse manifest to read identity from
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "EmbeddedApp", "--publisher", "CN=Contoso"]);
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var targetManifest = Path.Combine(_tempDirectory.FullName, "app.manifest");

        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "embed-identity XML mode should succeed");
        Assert.IsTrue(File.Exists(targetManifest), "SxS manifest should be created");
        var content = await File.ReadAllTextAsync(targetManifest, TestContext.CancellationToken);
        Assert.Contains("packageName=\"EmbeddedApp\"", content, "msix element should carry the package name");
        Assert.Contains("urn:schemas-microsoft-com:msix.v1", content, "msix namespace should be present");
    }

    [TestMethod]
    public async Task EmbedIdentity_XmlMode_ReplacesExistingElement()
    {
        // Arrange
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "IdempotentApp"]);
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        var targetManifest = Path.Combine(_tempDirectory.FullName, "app.manifest");
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act: run twice
        await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);

        // Assert: still exactly one <msix> element
        Assert.AreEqual(0, exitCode);
        var content = await File.ReadAllTextAsync(targetManifest, TestContext.CancellationToken);
        var occurrences = content.Split("<msix", StringSplitOptions.None).Length - 1;
        Assert.AreEqual(1, occurrences, "Re-running embed-identity should replace, not duplicate, the msix element");
    }

    [TestMethod]
    public void NormalizeManifestVersion_HandlesVariousInputs()
    {
        Assert.AreEqual("1.2.3.4", ManifestService.NormalizeManifestVersion("1.2.3.4"));
        Assert.AreEqual("2.0.0.0", ManifestService.NormalizeManifestVersion("2.0"));
        Assert.AreEqual("1.0.0.0", ManifestService.NormalizeManifestVersion("1.0.0.0"));
        Assert.IsNull(ManifestService.NormalizeManifestVersion("not-a-version"));
        Assert.IsNull(ManifestService.NormalizeManifestVersion(null));
        Assert.IsNull(ManifestService.NormalizeManifestVersion(""));
    }
}

/// <summary>
/// Tests for sparse-aware routing in the pack command. Uses a fake MSIX service to verify the
/// command dispatches to the sparse identity path without invoking real build tools.
/// </summary>
[TestClass]
public class SparsePackRoutingTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        return services.AddSingleton<IMsixService>(_fakeMsixService);
    }

    private const string SparseManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap uap10 rescap">
          <Identity Name="SparsePkg" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="neutral" />
          <Properties>
            <DisplayName>SparsePkg</DisplayName>
            <PublisherDisplayName>Test</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="SparsePkg" Executable="app.exe" uap10:RuntimeBehavior="win32App" uap10:TrustLevel="mediumIL" />
          </Applications>
          <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
        </Package>
        """;

    private const string PackagedManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="FullPkg" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>FullPkg</DisplayName>
            <PublisherDisplayName>Test</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="FullPkg" Executable="app.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
          <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
        </Package>
        """;

    [TestMethod]
    public async Task Pack_SparseManifestFile_RoutesToSparseIdentityPath()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, SparseManifest, TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Sparse pack should succeed");
        Assert.AreEqual(1, _fakeMsixService.CreateSparseIdentityCalls.Count, "Should route to CreateSparseIdentityPackageAsync");
        Assert.IsFalse(_fakeMsixService.CreateSparseIdentityCalls[0].AutoSign, "No cert provided means no signing");
    }

    [TestMethod]
    public async Task Pack_NonSparseManifestFile_ReturnsError()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, PackagedManifest, TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [manifestPath]);

        // Assert
        Assert.AreEqual(1, exitCode, "Passing a non-sparse manifest file should fail");
        Assert.AreEqual(0, _fakeMsixService.CreateSparseIdentityCalls.Count, "Should not route to sparse path");
    }

    [TestMethod]
    public async Task Pack_SparseManifestFile_WithCert_Signs()
    {
        // Arrange
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, SparseManifest, TestContext.CancellationToken);
        var certPath = Path.Combine(_tempDirectory.FullName, "dev.pfx");
        await File.WriteAllTextAsync(certPath, "not-a-real-cert", TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [manifestPath, "--cert", certPath]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.CreateSparseIdentityCalls.Count);
        Assert.IsTrue(_fakeMsixService.CreateSparseIdentityCalls[0].AutoSign, "Providing --cert should request signing");
    }
}
