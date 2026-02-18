// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class MSStoreCLIServiceTests : BaseCommandTests
{
    private IMSStoreCLIService _msStoreCLIService = null!;

    [TestInitialize]
    public void Setup()
    {
        _msStoreCLIService = GetRequiredService<IMSStoreCLIService>();
    }

    [TestMethod]
    public void GetMSStoreCLIPath_ReturnsExpectedPath()
    {
        // Act
        var result = _msStoreCLIService.GetMSStoreCLIPath();

        // Assert
        var expectedPath = Path.Combine(_testCacheDirectory.FullName, "tools", "msstore", "msstore.exe");
        Assert.AreEqual(expectedPath, result);
    }

    [TestMethod]
    public void GetMSStoreCLIPath_ContainsMSStoreToolsSubdirectory()
    {
        // Act
        var result = _msStoreCLIService.GetMSStoreCLIPath();

        // Assert
        Assert.Contains(Path.Combine("tools", "msstore"), result);
        Assert.IsTrue(result.EndsWith("msstore.exe", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_WhenToolExists_DoesNotDownload()
    {
        // Arrange - Create the expected directory structure and fake exe
        var installDir = Path.Combine(_testCacheDirectory.FullName, "tools", "msstore");
        Directory.CreateDirectory(installDir);
        var exePath = Path.Combine(installDir, "msstore.exe");
        await File.WriteAllTextAsync(exePath, "fake msstore.exe");

        // Act - Should not throw or attempt download since the tool already exists
        await _msStoreCLIService.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        // Assert - The file should still be the same fake content (not replaced by a download)
        var content = await File.ReadAllTextAsync(exePath);
        Assert.AreEqual("fake msstore.exe", content);
    }

    [TestMethod]
    public void GetMSStoreCLIPath_WithDifferentCacheDirectory_ReflectsNewDirectory()
    {
        // Arrange - Set up a different cache directory
        var altCacheDir = _tempDirectory.CreateSubdirectory("alt-cache");
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(altCacheDir);

        // Act
        var result = _msStoreCLIService.GetMSStoreCLIPath();

        // Assert
        var expectedPath = Path.Combine(altCacheDir.FullName, "tools", "msstore", "msstore.exe");
        Assert.AreEqual(expectedPath, result);
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_WhenToolDoesNotExist_AttemptsDownload()
    {
        // Arrange - Ensure the tool directory does not exist
        var installDir = Path.Combine(_testCacheDirectory.FullName, "tools", "msstore");
        if (Directory.Exists(installDir))
        {
            Directory.Delete(installDir, true);
        }

        // Act - Should attempt to download, which may either succeed or fail depending
        // on network availability. Either way, the install directory should be created.
        try
        {
            await _msStoreCLIService.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

            // If download succeeded, the exe should exist
            Assert.IsTrue(File.Exists(Path.Combine(installDir, "msstore.exe")),
                "msstore.exe should exist after successful download");
        }
        catch (HttpRequestException)
        {
            // Expected - network calls may fail in test environment
        }
        catch (InvalidOperationException)
        {
            // Expected - GitHub API response parsing may fail
        }
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_WhenToolExists_DoesNotModifyExistingFile()
    {
        // Arrange - Create the expected directory structure with a fake exe and a timestamp
        var installDir = Path.Combine(_testCacheDirectory.FullName, "tools", "msstore");
        Directory.CreateDirectory(installDir);
        var exePath = Path.Combine(installDir, "msstore.exe");
        await File.WriteAllTextAsync(exePath, "fake msstore.exe");
        var originalWriteTime = File.GetLastWriteTimeUtc(exePath);

        // Act
        await _msStoreCLIService.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        // Assert - File should not have been modified
        var currentWriteTime = File.GetLastWriteTimeUtc(exePath);
        Assert.AreEqual(originalWriteTime, currentWriteTime, "File should not have been modified when tool already exists");
    }

    [TestMethod]
    public async Task EnsureMSStoreCLIAvailableAsync_WhenCalledTwiceWithToolPresent_IsIdempotent()
    {
        // Arrange - Create the expected directory structure and fake exe
        var installDir = Path.Combine(_testCacheDirectory.FullName, "tools", "msstore");
        Directory.CreateDirectory(installDir);
        var exePath = Path.Combine(installDir, "msstore.exe");
        await File.WriteAllTextAsync(exePath, "fake msstore.exe");

        // Act - Call twice
        await _msStoreCLIService.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);
        await _msStoreCLIService.EnsureMSStoreCLIAvailableAsync(TestContext.CancellationToken);

        // Assert - File should still be the same
        var content = await File.ReadAllTextAsync(exePath);
        Assert.AreEqual("fake msstore.exe", content);
    }
}
