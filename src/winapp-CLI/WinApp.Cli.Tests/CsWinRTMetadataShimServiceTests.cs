// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the temporary SDK-less <see cref="CsWinRTMetadataShimService"/>. The Windows-SDK
/// registry check is stubbed via the service's <c>IsWindowsSdkRegistered</c> seam and the NuGet cache
/// is simulated with <see cref="FakeNugetService"/> over a temp directory, so these run deterministically
/// on any host regardless of its real SDK/registry state.
/// </summary>
[TestClass]
public class CsWinRTMetadataShimServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private FakeNugetService _nuget = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"CsWinRTShimTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _nuget = new FakeNugetService { CacheDirectory = _tempDir };
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private CsWinRTMetadataShimService NewService(bool sdkRegistered)
        => new(_nuget, NullLogger<CsWinRTMetadataShimService>.Instance)
        {
            IsWindowsSdkRegistered = () => sdkRegistered,
        };

    /// <summary>Creates a ref-pack version folder with (optionally) the sentinel FoundationContract winmd.</summary>
    private DirectoryInfo CreateRefPackVersion(string version, bool withSentinel = true)
    {
        var cache = _nuget.GetNuGetGlobalPackagesDir();
        var winmd = new DirectoryInfo(Path.Combine(cache.FullName, "microsoft.windows.sdk.net.ref", version, "winmd"));
        winmd.Create();
        if (withSentinel)
        {
            File.WriteAllText(Path.Combine(winmd.FullName, "Windows.Foundation.FoundationContract.winmd"), string.Empty);
        }
        return winmd;
    }

    [TestMethod]
    public void ResolveMetadataFolder_SdkRegistered_ReturnsNull()
    {
        CreateRefPackVersion("10.0.26100.57");
        var service = NewService(sdkRegistered: true);

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.26100.0");

        Assert.IsNull(result, "an installed SDK means cswinrt's own resolution works; do not inject.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_SdkAbsent_ReturnsHighestWinmdFolder()
    {
        CreateRefPackVersion("10.0.19041.57");
        var expected = CreateRefPackVersion("10.0.26100.57");
        var service = NewService(sdkRegistered: false);

        var result = service.ResolveMetadataFolder(targetFrameworkMoniker: null);

        Assert.AreEqual(expected.FullName, result, "with no TFM hint the highest usable ref-pack winmd folder wins.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_SdkAbsent_PrefersTfmMatchOverHighest()
    {
        var expected = CreateRefPackVersion("10.0.19041.57");
        CreateRefPackVersion("10.0.26100.57"); // higher, but doesn't match the targeted platform
        var service = NewService(sdkRegistered: false);

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.19041.0");

        Assert.AreEqual(expected.FullName, result, "a ref-pack version matching the project's platform is preferred over the highest.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_TfmMatchMissingSentinel_FallsBackToHighestUsable()
    {
        CreateRefPackVersion("10.0.19041.57", withSentinel: false); // matches TFM but unusable
        var expected = CreateRefPackVersion("10.0.26100.57");
        var service = NewService(sdkRegistered: false);

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.19041.0");

        Assert.AreEqual(expected.FullName, result, "an unusable TFM match is skipped in favour of the highest usable version.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_RefPackNotRestored_ReturnsNull()
    {
        // No ref-pack folders created at all.
        var service = NewService(sdkRegistered: false);

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.26100.0");

        Assert.IsNull(result, "a missing ref pack must no-op (not throw) so the normal build error can surface.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_NoVersionHasSentinel_ReturnsNull()
    {
        CreateRefPackVersion("10.0.26100.57", withSentinel: false);
        var service = NewService(sdkRegistered: false);

        var result = service.ResolveMetadataFolder(targetFrameworkMoniker: null);

        Assert.IsNull(result, "when no version contains FoundationContract.winmd there is nothing safe to inject.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_PrereleaseLosesTieToStable()
    {
        var stable = CreateRefPackVersion("10.0.26100.57");
        CreateRefPackVersion("10.0.26100.57-preview");
        var service = NewService(sdkRegistered: false);

        var result = service.ResolveMetadataFolder(targetFrameworkMoniker: null);

        Assert.AreEqual(stable.FullName, result, "a stable build wins the tie against a same-version prerelease.");
    }

    [TestMethod]
    [DataRow("net10.0-windows10.0.19041.0", "10.0.19041")]
    [DataRow("net8.0-windows10.0.26100.0", "10.0.26100")]
    [DataRow("net10.0-windows10.0.22621", "10.0.22621")]
    [DataRow("net10.0", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void ExtractPlatformVersionPrefix_ParsesMoniker(string? tfm, string? expected)
    {
        Assert.AreEqual(expected, CsWinRTMetadataShimService.ExtractPlatformVersionPrefix(tfm));
    }

    [TestMethod]
    public void SelectBestRefPackVersionDir_NoUsable_ReturnsNull()
    {
        var result = CsWinRTMetadataShimService.SelectBestRefPackVersionDir(
            ["10.0.19041.57", "10.0.26100.57"], platformVersionPrefix: null, isUsable: _ => false);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void SelectBestRefPackVersionDir_IgnoresUnparseableNames()
    {
        var result = CsWinRTMetadataShimService.SelectBestRefPackVersionDir(
            ["not-a-version", "10.0.19041.57"], platformVersionPrefix: null, isUsable: _ => true);

        Assert.AreEqual("10.0.19041.57", result);
    }
}
