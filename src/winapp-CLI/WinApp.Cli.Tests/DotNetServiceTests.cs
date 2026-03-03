// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class DotNetServiceTests : BaseCommandTests
{
    private string _testTempDirectory = null!;
    private IDotNetService _dotNetService = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a temporary directory for testing
        _testTempDirectory = Path.Combine(Path.GetTempPath(), $"winappDotNetServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDirectory);

        _dotNetService = GetRequiredService<IDotNetService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up temporary files and directories
        if (Directory.Exists(_testTempDirectory))
        {
            try
            {
                Directory.Delete(_testTempDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region FindCsproj Tests

    [TestMethod]
    public void FindCsproj_NoFiles_ReturnsEmpty()
    {
        // Arrange
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void FindCsproj_SingleCsprojFile_ReturnsSingleFile()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "TestProject.csproj");
        File.WriteAllText(csprojPath, "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.HasCount(1, result);
        Assert.AreEqual("TestProject.csproj", result[0].Name);
    }

    [TestMethod]
    public void FindCsproj_MultipleCsprojFiles_ReturnsAllFiles()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testTempDirectory, "ProjectA.csproj"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_testTempDirectory, "ProjectB.csproj"), "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.HasCount(2, result);
        Assert.IsTrue(result.All(f => f.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void FindCsproj_DirectoryDoesNotExist_ReturnsEmpty()
    {
        // Arrange
        var nonExistentDirectory = new DirectoryInfo(Path.Combine(_testTempDirectory, "NonExistent"));

        // Act
        var result = _dotNetService.FindCsproj(nonExistentDirectory);

        // Assert
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void FindCsproj_CsprojInSubdirectory_ReturnsEmpty()
    {
        // Arrange - csproj is in a subdirectory, not the root
        var subDir = Path.Combine(_testTempDirectory, "SubFolder");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "SubProject.csproj"), "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.IsEmpty(result, "Should not find csproj files in subdirectories");
    }

    [TestMethod]
    public void FindCsproj_OtherFileTypes_ReturnsEmpty()
    {
        // Arrange - only non-csproj files
        File.WriteAllText(Path.Combine(_testTempDirectory, "Project.sln"), "");
        File.WriteAllText(Path.Combine(_testTempDirectory, "Project.fsproj"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_testTempDirectory, "Project.vbproj"), "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.IsEmpty(result);
    }

    #endregion

    #region GetTargetFramework Tests

    [TestMethod]
    public void GetTargetFramework_ValidTfm_ReturnsValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net8.0-windows10.0.19041.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_PlainNetTfm_ReturnsValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net8.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_NoTargetFramework_ReturnsNull()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetTargetFramework_FileDoesNotExist_ReturnsNull()
    {
        // Arrange
        var nonExistentFile = new FileInfo(Path.Combine(_testTempDirectory, "NonExistent.csproj"));

        // Act
        var result = _dotNetService.GetTargetFramework(nonExistentFile);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetTargetFramework_WithWhitespace_ReturnsTrimmedValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>  net10.0-windows10.0.19041.0  </TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net10.0-windows10.0.19041.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_MultilineContent_ReturnsValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>
      net9.0-windows10.0.22621.0
    </TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.IsNotNull(result);
        Assert.Contains("net9.0-windows10.0.22621.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_MultiTargeted_ReturnsFirstTfm()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net8.0", result);
    }

    #endregion

    #region IsMultiTargeted Tests

    [TestMethod]
    public void IsMultiTargeted_SingleTarget_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act & Assert
        Assert.IsFalse(_dotNetService.IsMultiTargeted(new FileInfo(csprojPath)));
    }

    [TestMethod]
    public void IsMultiTargeted_MultipleTargets_ReturnsTrue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
</Project>");

        // Act & Assert
        Assert.IsTrue(_dotNetService.IsMultiTargeted(new FileInfo(csprojPath)));
    }

    [TestMethod]
    public void IsMultiTargeted_FileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var nonExistentFile = new FileInfo(Path.Combine(_testTempDirectory, "NonExistent.csproj"));

        // Act & Assert
        Assert.IsFalse(_dotNetService.IsMultiTargeted(nonExistentFile));
    }

    [TestMethod]
    public void IsMultiTargeted_NoTargetFramework_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act & Assert
        Assert.IsFalse(_dotNetService.IsMultiTargeted(new FileInfo(csprojPath)));
    }

    #endregion

    #region IsTargetFrameworkSupported Tests

    [TestMethod]
    public void IsTargetFrameworkSupported_ValidWindowsTfm_ReturnsTrue()
    {
        // Act & Assert
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net8.0-windows10.0.19041.0"));
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net9.0-windows10.0.22621.0"));
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net10.0-windows10.0.19041.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_MinimumSupportedVersion_ReturnsTrue()
    {
        // Minimum supported Windows SDK is 10.0.17763.0
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net6.0-windows10.0.17763.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_BelowMinimumWindowsSdk_ReturnsFalse()
    {
        // Below minimum Windows SDK version (10.0.17763.0)
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0-windows10.0.17762.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0-windows10.0.16299.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_BelowMinimumNetVersion_ReturnsFalse()
    {
        // Below minimum .NET version (6.0)
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net5.0-windows10.0.19041.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net4.8-windows10.0.19041.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_PlainNetTfm_ReturnsFalse()
    {
        // Plain .NET TFM without Windows specifier
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net10.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_PlainWindowsTfm_ReturnsFalse()
    {
        // Windows TFM without SDK version
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0-windows"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_NullOrEmpty_ReturnsFalse()
    {
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported(null!));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported(""));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("   "));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_InvalidFormat_ReturnsFalse()
    {
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("invalid"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("netstandard2.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("netcoreapp3.1"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_CaseInsensitive_ReturnsTrue()
    {
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("NET8.0-WINDOWS10.0.19041.0"));
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("Net8.0-Windows10.0.19041.0"));
    }

    #endregion

    #region GetRecommendedTargetFramework Tests

    [TestMethod]
    public void GetRecommendedTargetFramework_NullInput_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(null);

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_EmptyInput_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_WhitespaceInput_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("   ");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_AlreadySupported_ReturnsSame()
    {
        // Arrange - already a fully supported TFM
        var input = "net8.0-windows10.0.26100.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainNetTfm_AddsWindowsSdk()
    {
        // Arrange - plain .NET TFM needs Windows SDK added
        var input = "net8.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net8.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainWindowsTfm_AddsSdkVersion()
    {
        // Arrange - Windows TFM without SDK version
        var input = "net9.0-windows";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net9.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_OldWindowsSdk_UpdatesSdkVersion()
    {
        // Arrange - supported .NET version but old Windows SDK
        var input = "net8.0-windows10.0.17762.0"; // Below minimum 10.0.17763.0

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net8.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_OldNetVersion_ReturnsDefault()
    {
        // Arrange - .NET version below minimum (6.0)
        var input = "net5.0-windows10.0.26100.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PreservesHigherNetVersion()
    {
        // Arrange - higher .NET version should be preserved
        var input = "net10.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_InvalidFormat_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("invalid-tfm");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_NetStandard_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("netstandard2.0");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    #endregion

    #region SetTargetFramework Tests

    [TestMethod]
    public void SetTargetFramework_ReplacesExistingTfm()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
        Assert.IsFalse(content.Contains("net6.0", StringComparison.Ordinal), "Old TFM should be removed");
    }

    [TestMethod]
    public void SetTargetFramework_InsertsWhenMissing()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
    }

    [TestMethod]
    public void SetTargetFramework_PreservesOtherElements()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<OutputType>Exe</OutputType>");
        StringAssert.Contains(content, "<Nullable>enable</Nullable>");
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
    }

    [TestMethod]
    public void SetTargetFramework_HandlesMultiplePropertyGroups()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <PropertyGroup Condition=""'$(Configuration)'=='Debug'"">
    <DefineConstants>DEBUG</DefineConstants>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
        StringAssert.Contains(content, "<DefineConstants>DEBUG</DefineConstants>");
    }

    [TestMethod]
    public void SetTargetFramework_UpdatesInPlace()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        var originalContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, originalContent);

        // Act - update to a different TFM
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.22621.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>");
        Assert.IsFalse(content.Contains("net8.0", StringComparison.Ordinal));
    }

    #endregion

    #region AddOrUpdatePackageReferenceAsync Tests

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_InvalidProject_ThrowsException()
    {
        // Arrange - create an invalid csproj file
        var csprojPath = Path.Combine(_testTempDirectory, "Invalid.csproj");
        File.WriteAllText(csprojPath, "This is not valid XML");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _dotNetService.AddOrUpdatePackageReferenceAsync(
                new FileInfo(csprojPath),
                "Microsoft.WindowsAppSDK",
                "1.5.0",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_NonExistentProject_ThrowsException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testTempDirectory, "NonExistent.csproj");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _dotNetService.AddOrUpdatePackageReferenceAsync(
                new FileInfo(nonExistentPath),
                "Microsoft.WindowsAppSDK",
                "1.5.0",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_ValidProject_AddsPackage()
    {
        // Arrange - create a valid SDK-style project
        var csprojPath = Path.Combine(_testTempDirectory, "ValidProject.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        await _dotNetService.AddOrUpdatePackageReferenceAsync(
            new FileInfo(csprojPath),
            "Newtonsoft.Json",
            "13.0.3",
            TestContext.CancellationToken);

        // Assert - verify the package was added
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "Newtonsoft.Json");
    }

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_InvalidPackageName_ThrowsException()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _dotNetService.AddOrUpdatePackageReferenceAsync(
                new FileInfo(csprojPath),
                "This.Package.Does.Not.Exist.12345",
                "1.0.0",
                TestContext.CancellationToken));
    }

    #endregion

    #region EnsureRuntimeIdentifierAsync Tests (RuntimeIdentifierElementRegex)

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_NoRuntimeIdentifier_InsertsOne()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "NoRid.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when RuntimeIdentifier was inserted");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifier_DoesNotModify()
    {
        // Arrange — singular <RuntimeIdentifier>
        var csprojPath = Path.Combine(_testTempDirectory, "HasRid.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when RuntimeIdentifier already exists");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifiers_StillInserts()
    {
        // Arrange — plural <RuntimeIdentifiers> should NOT prevent inserting singular <RuntimeIdentifier>
        var csprojPath = Path.Combine(_testTempDirectory, "HasRids.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert RuntimeIdentifier even when RuntimeIdentifiers (plural) exists");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
        // Verify it's inserted right after </RuntimeIdentifiers>
        var ridsEnd = content.IndexOf("</RuntimeIdentifiers>", StringComparison.Ordinal);
        var ridStart = content.IndexOf("<RuntimeIdentifier Condition=", StringComparison.Ordinal);
        Assert.IsTrue(ridStart > ridsEnd, "RuntimeIdentifier should be placed after RuntimeIdentifiers");
        // Nothing but whitespace between them
        var between = content[(ridsEnd + "</RuntimeIdentifiers>".Length)..ridStart];
        Assert.IsTrue(string.IsNullOrWhiteSpace(between), $"Only whitespace expected between elements, got: '{between}'");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifierWithCondition_DoesNotModify()
    {
        // Arrange — <RuntimeIdentifier with a Condition attribute (whitespace after tag name)
        var csprojPath = Path.Combine(_testTempDirectory, "HasRidCondition.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier Condition=""'$(RuntimeIdentifier)' == ''"">win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when RuntimeIdentifier with attributes already exists");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifiersWithCondition_StillInserts()
    {
        // Arrange — plural <RuntimeIdentifiers with a Condition attribute should NOT block insertion
        var csprojPath = Path.Combine(_testTempDirectory, "HasRidsCondition.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifiers Condition=""'$(RuntimeIdentifiers)' == ''"">win-x64;win-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert RuntimeIdentifier even when RuntimeIdentifiers (plural) with attributes exists");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
        // Verify it's inserted right after </RuntimeIdentifiers>
        var ridsEnd = content.IndexOf("</RuntimeIdentifiers>", StringComparison.Ordinal);
        var ridStart = content.IndexOf("<RuntimeIdentifier Condition=", StringComparison.Ordinal);
        Assert.IsTrue(ridStart > ridsEnd, "RuntimeIdentifier should be placed after RuntimeIdentifiers");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_FileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "NonExistent.csproj");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_DoesNotMatchSimilarElementNames()
    {
        // Arrange — contains <RuntimeIdentifierGraph> which should NOT prevent insertion
        var csprojPath = Path.Combine(_testTempDirectory, "SimilarName.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
  <!-- RuntimeIdentifierGraph should not be confused with RuntimeIdentifier -->
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert RuntimeIdentifier — RuntimeIdentifierGraph is not RuntimeIdentifier");
    }

    #endregion

    #region HasPackageReferenceAsync Tests

    [TestMethod]
    public async Task HasPackageReferenceAsync_FileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "NonExistent.csproj");

        // Act
        var result = await _dotNetService.HasPackageReferenceAsync(new FileInfo(csprojPath), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false for non-existent file");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_WithMatchingPackage_ReturnsTrue()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [new DotNetPackage("Microsoft.WindowsAppSDK", "1.6.0", "1.6.0")],
                        [])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should detect existing PackageReference");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_CaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [new DotNetPackage("microsoft.windowsappsdk", "1.6.0", "1.6.0")],
                        [])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Package name comparison should be case-insensitive");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_DifferentPackage_ReturnsFalse()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [new DotNetPackage("Newtonsoft.Json", "13.0.3", "13.0.3")],
                        [])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when a different package is referenced");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_NullPackageListResult_ReturnsFalse()
    {
        // Arrange
        var fake = new FakeDotNetService { PackageListResult = null };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when package list is null");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_EmptyProjects_ReturnsFalse()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson([])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when project list is empty");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_TransitiveOnly_ReturnsFalse()
    {
        // Arrange — package exists only as a transitive dependency, not a top-level reference
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [],
                        [new DotNetPackage("Microsoft.WindowsAppSDK", "1.6.0", "1.6.0")])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when package is only a transitive dependency");
    }

    #endregion
}
