// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;
using System.Diagnostics;

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
        // The comment is expected between the elements
        Assert.IsTrue(between.Contains("<!-- Added by winapp"), $"Expected comment between elements, got: '{between}'");
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
        Assert.IsFalse(result, "Should return false when package is only a transitive dependency");    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_FastPath_DetectsInlinePackageReference_WithoutDotnetRestore()
    {
        // Arrange — write an SDK-style csproj with an inline PackageReference. Use a non-existent
        // SDK so a `dotnet list package` fallback would fail; success here proves the XML fast path
        // returned true without invoking dotnet (#463).
        var csprojPath = Path.Combine(_testTempDirectory, "Inline.csproj");
        await File.WriteAllTextAsync(csprojPath, """
<Project Sdk="DefinitelyNotARealSdk/0.0.0">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.0" />
  </ItemGroup>
</Project>
""", TestContext.CancellationToken);

        // Act
        var result = await _dotNetService.HasPackageReferenceAsync(
            new FileInfo(csprojPath), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "XML fast path should detect inline <PackageReference Include=\"Microsoft.WindowsAppSDK\"/>.");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_FastPath_CaseInsensitiveOnIncludeAttribute()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Cased.csproj");
        await File.WriteAllTextAsync(csprojPath, """
<Project Sdk="DefinitelyNotARealSdk/0.0.0">
  <ItemGroup>
    <PackageReference Include="microsoft.windowsappsdk" Version="1.6.0" />
  </ItemGroup>
</Project>
""", TestContext.CancellationToken);

        // Act
        var result = await _dotNetService.HasPackageReferenceAsync(
            new FileInfo(csprojPath), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "XML fast path should be case-insensitive on the Include attribute.");
    }

    #endregion

    #region EnsureAssetContentItemsAsync

    [TestMethod]
    public async Task EnsureAssetContentItems_AddsMissingAssetsGlob()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should modify csproj when no asset Content items exist");
        var content = File.ReadAllText(csprojPath);
        Assert.IsTrue(content.Contains(@"<Content Include=""Assets\**\*"" />"),
            "Should add Assets glob Content item");
        Assert.IsTrue(content.Contains("</Project>"),
            "Should preserve </Project> closing tag");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_SkipsWhenAlreadyPresent()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Content Include=""Assets\**\*"" />
  </ItemGroup>
</Project>
");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should not modify csproj when asset Content items already exist");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_SkipsWhenIndividualAssetPresent()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <Content Include=""Assets\StoreLogo.png"" />
  </ItemGroup>
</Project>
");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should not modify csproj when individual asset Content items exist");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_ReturnsFalseForMissingFile()
    {
        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "NonExistent.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region UpdatePublishProfileAsync Tests

    [TestMethod]
    public async Task UpdatePublishProfile_WithPlatformProfile_AddsExistsCondition()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Pub.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <PublishProfile>win-$(Platform).pubxml</PublishProfile>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.UpdatePublishProfileAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when the PublishProfile was rewritten");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content,
            @"<PublishProfile Condition=""Exists('Properties\PublishProfiles\win-$(Platform).pubxml')"">win-$(Platform).pubxml</PublishProfile>");
    }

    [TestMethod]
    public async Task UpdatePublishProfile_WithoutPlatformToken_DoesNotModify()
    {
        // Arrange — a PublishProfile that does not reference $(Platform) should be ignored
        var csprojPath = Path.Combine(_testTempDirectory, "Pub.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <PublishProfile>Release.pubxml</PublishProfile>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.UpdatePublishProfileAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no $(Platform) PublishProfile");
        var content = File.ReadAllText(csprojPath);
        Assert.IsFalse(content.Contains("Condition="), "Content should be untouched");
    }

    [TestMethod]
    public async Task UpdatePublishProfile_MissingFile_ReturnsFalse()
    {
        // Act
        var result = await _dotNetService.UpdatePublishProfileAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region EnsureEnableMsixToolingAsync Tests

    [TestMethod]
    public async Task EnsureEnableMsixTooling_AlreadyTrue_DoesNotModify()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <EnableMsixTooling>true</EnableMsixTooling>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when EnableMsixTooling is already true");
        Assert.AreEqual(original, File.ReadAllText(csprojPath), "Content should be unchanged");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_FalseWithoutComment_SetsTrueAndAddsComment()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <EnableMsixTooling>false</EnableMsixTooling>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when flipping false to true");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        StringAssert.Contains(content, "Enables targets that generate package layout");
        Assert.IsFalse(content.Contains("<EnableMsixTooling>false"), "Old false value should be gone");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_FalseWithExistingComment_SetsTrueWithoutDuplicateComment()
    {
        // Arrange — an XML comment already sits directly above the element
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <!-- existing note -->
    <EnableMsixTooling>false</EnableMsixTooling>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when flipping false to true");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        StringAssert.Contains(content, "<!-- existing note -->");
        Assert.IsFalse(content.Contains("Enables targets that generate package layout"),
            "Should not inject a second comment when one already exists");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_UnexpectedValue_DoesNotModify()
    {
        // Arrange — a non true/false value is left untouched
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <EnableMsixTooling>maybe</EnableMsixTooling>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false for an unexpected existing value");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_InsertsAfterRuntimeIdentifier()
    {
        // Arrange — no EnableMsixTooling, but a RuntimeIdentifier is present
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        // Should be inserted after the RuntimeIdentifier element
        var ridIdx = content.IndexOf("</RuntimeIdentifier>", StringComparison.Ordinal);
        var msixIdx = content.IndexOf("<EnableMsixTooling>true", StringComparison.Ordinal);
        Assert.IsTrue(ridIdx >= 0 && msixIdx > ridIdx, "EnableMsixTooling should follow RuntimeIdentifier");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_InsertsAfterTargetFramework_WhenNoRuntimeIdentifier()
    {
        // Arrange — no EnableMsixTooling and no RuntimeIdentifier, but a TargetFramework is present
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        var tfmIdx = content.IndexOf("</TargetFramework>", StringComparison.Ordinal);
        var msixIdx = content.IndexOf("<EnableMsixTooling>true", StringComparison.Ordinal);
        Assert.IsTrue(tfmIdx >= 0 && msixIdx > tfmIdx, "EnableMsixTooling should follow TargetFramework");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_InsertsAfterPropertyGroup_WhenNoRidOrTfm()
    {
        // Arrange — no EnableMsixTooling, no RuntimeIdentifier, no TargetFramework
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        var pgIdx = content.IndexOf("<PropertyGroup>", StringComparison.Ordinal);
        var msixIdx = content.IndexOf("<EnableMsixTooling>true", StringComparison.Ordinal);
        Assert.IsTrue(pgIdx >= 0 && msixIdx > pgIdx, "EnableMsixTooling should be inserted inside the PropertyGroup");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_NoPropertyGroup_DoesNotModify()
    {
        // Arrange — nothing to anchor the insertion to
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Some.Package"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no anchor to insert after");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_MissingFile_ReturnsFalse()
    {
        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region RemoveWindowsPackageTypeNoneAsync Tests

    [TestMethod]
    public async Task RemoveWindowsPackageTypeNone_WhenPresent_RemovesElement()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Wpt.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <WindowsPackageType>None</WindowsPackageType>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.RemoveWindowsPackageTypeNoneAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when the element was removed");
        var content = File.ReadAllText(csprojPath);
        Assert.IsFalse(content.Contains("WindowsPackageType"), "WindowsPackageType element should be gone");
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
    }

    [TestMethod]
    public async Task RemoveWindowsPackageTypeNone_WhenAbsent_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Wpt.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.RemoveWindowsPackageTypeNoneAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no WindowsPackageType element");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task RemoveWindowsPackageTypeNone_MissingFile_ReturnsFalse()
    {
        // Act
        var result = await _dotNetService.RemoveWindowsPackageTypeNoneAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region AnnotatePackageReferencesAsync Tests

    [TestMethod]
    public async Task AnnotatePackageReferences_AddsCommentAbovePackage()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "Windows App SDK runtime",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when a comment was inserted");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<!-- Windows App SDK runtime -->");
        var commentIdx = content.IndexOf("<!-- Windows App SDK runtime -->", StringComparison.Ordinal);
        var pkgIdx = content.IndexOf("<PackageReference Include=\"Microsoft.WindowsAppSDK\"", StringComparison.Ordinal);
        Assert.IsTrue(commentIdx >= 0 && commentIdx < pkgIdx, "Comment should appear above the PackageReference");
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_SkipsWhenCommentAlreadyPresent()
    {
        // Arrange — a comment already sits directly above the package reference
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <!-- Windows App SDK runtime -->
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(csprojPath, original);
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "Windows App SDK runtime",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when the package already has a comment");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_SkipsWhenPackageNotFound()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Some.Other.Package"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(csprojPath, original);
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "Windows App SDK runtime",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when the target package is not referenced");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_AnnotatesMultiplePackages()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
    <PackageReference Include=""Microsoft.Windows.SDK.BuildTools"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "SDK runtime",
            ["Microsoft.Windows.SDK.BuildTools"] = "Build tools",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<!-- SDK runtime -->");
        StringAssert.Contains(content, "<!-- Build tools -->");
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_EmptyDictionary_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), new Dictionary<string, string>(), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false for an empty comment set");
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_MissingFile_ReturnsFalse()
    {
        // Arrange
        var comments = new Dictionary<string, string> { ["X"] = "Y" };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            comments, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region Additional branch coverage

    [TestMethod]
    public void IsTargetFrameworkSupported_WindowsVersionUnparseable_ReturnsFalse()
    {
        // A windows TFM whose SDK version has too many components is not a valid Version
        var result = _dotNetService.IsTargetFrameworkSupported("net8.0-windows1.2.3.4.5");

        Assert.IsFalse(result, "An unparseable Windows SDK version should be unsupported");
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainWindowsTfmWithUnsupportedNet_ReturnsRecommended()
    {
        // net5.0-windows matches the plain-windows TFM shape but 5.0 < 6.0, so it falls through
        var result = _dotNetService.GetRecommendedTargetFramework("net5.0-windows");

        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainNetTfmWithUnsupportedNet_ReturnsRecommended()
    {
        // net5.0 matches the plain .NET TFM shape but 5.0 < 6.0, so it falls through
        var result = _dotNetService.GetRecommendedTargetFramework("net5.0");

        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifier_NoTfmOrRids_InsertsAtStartOfPropertyGroup()
    {
        // Arrange — a PropertyGroup with neither TargetFramework nor RuntimeIdentifiers
        var csprojPath = Path.Combine(_testTempDirectory, "NoTfm.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert a RuntimeIdentifier at the start of the PropertyGroup");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
        var pgIdx = content.IndexOf("<PropertyGroup>", StringComparison.Ordinal);
        var ridIdx = content.IndexOf("<RuntimeIdentifier Condition=", StringComparison.Ordinal);
        var outputIdx = content.IndexOf("<OutputType>", StringComparison.Ordinal);
        Assert.IsTrue(ridIdx > pgIdx && ridIdx < outputIdx,
            "RuntimeIdentifier should be inserted at the start of the PropertyGroup, before existing children");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_NoProjectClosingTag_ReturnsFalse()
    {
        // Arrange — malformed csproj with no </Project> to anchor the insertion
        var csprojPath = Path.Combine(_testTempDirectory, "NoClose.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no </Project> to insert before");
    }

    #endregion

    #region Process cancellation Tests

    [TestMethod]
    public async Task RunDotnetProcessAsync_Cancellation_KillsProcessTree()
    {
        // Spawn a long-lived process tree (powershell -> ping) so the entireProcessTree kill path is
        // genuinely exercised, then cancel and assert BOTH the root and its descendant are terminated.
        // Before the kill-on-cancel fix, WaitForExitAsync only stopped awaiting and left the child
        // running; asserting the descendant also exits guards the tree-kill (a bare Kill() would leave
        // ping alive and still pass a root-only assertion).
        //
        // The descendant PID is captured via a fully-managed handshake: the PowerShell root starts ping
        // with -PassThru, writes ping's PID to a temp file, then blocks on Wait-Process. The test polls
        // that file — no WMI or Win32 process enumeration required.
        var pidFile = Path.Join(Path.GetTempPath(), $"winapp_treekill_{Guid.NewGuid():N}.pid");
        var script =
            "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','60','127.0.0.1' -PassThru -WindowStyle Hidden; " +
            $"Set-Content -LiteralPath '{pidFile}' -Value $p.Id; " +
            "Wait-Process -Id $p.Id";

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        try
        {
            using var cts = new CancellationTokenSource();
            var pidTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runTask = DotNetService.RunDotnetProcessAsync(
                startInfo,
                cts.Token,
                onProcessStarted: p => pidTcs.TrySetResult(p.Id));

            // Wait until the root process is actually running (capture its id before the service disposes it).
            var pid = await pidTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
            Assert.IsFalse(Process.GetProcessById(pid).HasExited, "The root process should be running before cancellation.");

            // Capture the ping descendant via the PID file the root script writes.
            var pingPid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(15), TestContext.CancellationToken);
            Assert.IsFalse(Process.GetProcessById(pingPid).HasExited, "The ping descendant should be running before cancellation.");

            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await runTask);

            // Both the root of the spawned tree AND its ping descendant must be gone. Kill(entireProcessTree)
            // reaps the whole tree; GetProcessById throws ArgumentException once an id no longer maps to a
            // running process.
            Assert.IsTrue(await WaitForProcessExitAsync(pid, TestContext.CancellationToken),
                "The spawned root process (powershell.exe) should be killed on cancellation.");
            Assert.IsTrue(await WaitForProcessExitAsync(pingPid, TestContext.CancellationToken),
                "The ping descendant should be killed on cancellation (proves the whole process tree was terminated).");
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    /// <summary>Polls until the process with <paramref name="pid"/> has exited (or its id is reused/freed).</summary>
    private static async Task<bool> WaitForProcessExitAsync(int pid, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                if (Process.GetProcessById(pid).HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(20, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Polls for the PID file written by the spawned PowerShell root and returns the descendant PID it
    /// contains. This is the managed alternative to enumerating the process table for a child of the
    /// root: the root itself reports its child's id, so the test needs no WMI or Win32 interop.
    /// </summary>
    private static async Task<int> WaitForPidFileAsync(string pidFile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                var text = (await File.ReadAllTextAsync(pidFile, cancellationToken)).Trim();
                if (int.TryParse(text, out var pid))
                {
                    return pid;
                }
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException($"The descendant PID file '{pidFile}' was not written within {timeout}.");
    }

    #endregion
}
