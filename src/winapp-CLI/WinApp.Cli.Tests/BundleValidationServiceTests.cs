// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class BundleValidationServiceTests
{
    private BundleValidationService _service = null!;
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _service = new BundleValidationService();
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"BundleValidTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    private static AppxManifestDocument CreateManifest(
        string name = "TestApp",
        string publisher = "CN=TestPublisher",
        string version = "1.0.0.0",
        string arch = "x64",
        string? capability = null)
    {
        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""{name}"" Publisher=""{publisher}"" Version=""{version}"" ProcessorArchitecture=""{arch}"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
  </Dependencies>
  <Capabilities>
    <Capability Name=""internetClient"" />{(capability != null ? $"\n    <Capability Name=\"{capability}\" />" : "")}
  </Capabilities>
  <Applications>
    <Application Id=""App"" Executable=""MyApp.exe"" EntryPoint=""Windows.FullTrustApplication"">
      <uap:VisualElements DisplayName=""TestApp"" Description=""Test"" Square150x150Logo=""logo.png"" Square44x44Logo=""logo.png"" BackgroundColor=""transparent"" />
    </Application>
  </Applications>
</Package>";
        return AppxManifestDocument.Parse(xml);
    }

    private DirectoryInfo CreateFolder(string name)
    {
        var dir = new DirectoryInfo(Path.Combine(_tempDir.FullName, name));
        dir.Create();
        return dir;
    }

    #region Architecture Validation

    [TestMethod]
    public void Validate_DuplicateArchitectures_ReturnsError()
    {
        var manifests = new[] { CreateManifest(arch: "x64"), CreateManifest(arch: "x64") };
        var arches = new[] { "x64", "x64" };
        var folders = new[] { CreateFolder("a"), CreateFolder("b") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Architecture" && e.Message.Contains("Duplicate")));
    }

    [TestMethod]
    public void Validate_AllNeutral_ReturnsError()
    {
        var manifests = new[] { CreateManifest(arch: "neutral"), CreateManifest(arch: "neutral") };
        var arches = new[] { "neutral", "neutral" };
        var folders = new[] { CreateFolder("a"), CreateFolder("b") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Architecture" && e.Message.Contains("neutral")));
    }

    [TestMethod]
    public void Validate_ValidArchitectures_NoErrors()
    {
        var manifests = new[] { CreateManifest(arch: "x64"), CreateManifest(arch: "arm64") };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.AreEqual(0, errors.Count);
    }

    #endregion

    #region Identity Consistency

    [TestMethod]
    public void Validate_MismatchedVersion_ReturnsError()
    {
        var manifests = new[] { CreateManifest(version: "1.0.0.0"), CreateManifest(version: "2.0.0.0") };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Identity/@Version"));
    }

    [TestMethod]
    public void Validate_MismatchedName_ReturnsError()
    {
        var manifests = new[] { CreateManifest(name: "AppA"), CreateManifest(name: "AppB") };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Identity/@Name"));
    }

    [TestMethod]
    public void Validate_MismatchedPublisher_ReturnsError()
    {
        var manifests = new[] { CreateManifest(publisher: "CN=A"), CreateManifest(publisher: "CN=B") };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Identity/@Publisher"));
    }

    [TestMethod]
    public void Validate_MatchingIdentity_NoErrors()
    {
        var manifests = new[] { CreateManifest(), CreateManifest() };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.AreEqual(0, errors.Count);
    }

    #endregion

    #region Capabilities Consistency

    [TestMethod]
    public void Validate_MismatchedCapabilities_ReturnsError()
    {
        var manifests = new[]
        {
            CreateManifest(capability: "microphone"),
            CreateManifest() // only internetClient
        };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Capabilities"));
    }

    #endregion

    #region Applications Consistency

    [TestMethod]
    public void Validate_DifferentAppId_ReturnsError()
    {
        var xml1 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""x64"" />
  <Applications>
    <Application Id=""App1"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";
        var xml2 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""arm64"" />
  <Applications>
    <Application Id=""App2"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";

        var manifests = new[] { AppxManifestDocument.Parse(xml1), AppxManifestDocument.Parse(xml2) };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Applications"));
    }

    #endregion

    #region PackageDependency Consistency

    [TestMethod]
    public void Validate_MismatchedPackageDependency_ReturnsError()
    {
        var xml1 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""x64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
    <PackageDependency Name=""Microsoft.VCLibs.140.00"" MinVersion=""14.0.30704.0"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";
        var xml2 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""arm64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
    <PackageDependency Name=""Microsoft.VCLibs.140.00"" MinVersion=""14.0.33519.0"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";

        var manifests = new[] { AppxManifestDocument.Parse(xml1), AppxManifestDocument.Parse(xml2) };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Dependencies/PackageDependency"));
    }

    [TestMethod]
    public void Validate_MatchingPackageDependencies_NoErrors()
    {
        var xml1 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""x64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
    <PackageDependency Name=""Microsoft.VCLibs.140.00"" MinVersion=""14.0.30704.0"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";
        var xml2 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""arm64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
    <PackageDependency Name=""Microsoft.VCLibs.140.00"" MinVersion=""14.0.30704.0"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";

        var manifests = new[] { AppxManifestDocument.Parse(xml1), AppxManifestDocument.Parse(xml2) };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.AreEqual(0, errors.Count);
    }

    #endregion

    #region TargetDeviceFamily Consistency

    [TestMethod]
    public void Validate_MismatchedTargetDeviceFamily_ReturnsError()
    {
        var xml1 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""x64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";
        var xml2 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""arm64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.22000.0"" MaxVersionTested=""10.0.22621.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";

        var manifests = new[] { AppxManifestDocument.Parse(xml1), AppxManifestDocument.Parse(xml2) };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.IsTrue(errors.Any(e => e.Field == "Dependencies/TargetDeviceFamily"));
    }

    [TestMethod]
    public void Validate_MatchingTargetDeviceFamily_NoErrors()
    {
        var xml1 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""x64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";
        var xml2 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""App"" Publisher=""CN=Test"" Version=""1.0.0.0"" ProcessorArchitecture=""arm64"" />
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.22621.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""a.exe"" EntryPoint=""Windows.FullTrustApplication"" />
  </Applications>
</Package>";

        var manifests = new[] { AppxManifestDocument.Parse(xml1), AppxManifestDocument.Parse(xml2) };
        var arches = new[] { "x64", "arm64" };
        var folders = new[] { CreateFolder("x64"), CreateFolder("arm64") };

        var errors = _service.Validate(manifests, arches, folders);

        Assert.AreEqual(0, errors.Count);
    }

    #endregion
}