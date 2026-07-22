// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="WindowsAppRuntimeService"/> NuGet-cache runtime discovery, in particular the
/// <c>requireExactVersion</c> gate (C40): project-mode unpackaged launches must resolve the runtime the
/// app was actually built against and never fall back to an unrelated cached WinAppSDK version, because
/// the presence gate derived from that fallback would otherwise pass against the wrong runtime family.
/// </summary>
[TestClass]
public class WindowsAppRuntimeServiceTests
{
    private DirectoryInfo _cacheRoot = null!;
    private FakeNugetService _fakeNuget = null!;
    private WindowsAppRuntimeService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "winapp-wart-" + Guid.NewGuid().ToString("N")));
        _cacheRoot.Create();
        _fakeNuget = new FakeNugetService { CacheDirectory = _cacheRoot };
        _service = new WindowsAppRuntimeService(new FakePackageRegistrationService(), _fakeNuget);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (_cacheRoot.Exists)
            {
                _cacheRoot.Delete(recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup; never fail a test on cleanup.
        }
    }

    /// <summary>Creates a valid MSIX package layout (<c>&lt;id&gt;/&lt;version&gt;/tools/MSIX</c>) in the fake NuGet cache.</summary>
    private void SeedCachedRuntime(string packageId, string version)
    {
        var packagesDir = _fakeNuget.GetNuGetGlobalPackagesDir();
        Directory.CreateDirectory(Path.Combine(packagesDir.FullName, packageId.ToLowerInvariant(), version, "tools", "MSIX"));
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_DefaultScan_ReturnsUnrelatedCachedRuntime()
    {
        // Only an UNRELATED runtime version is cached; the app's exact version is absent.
        SeedCachedRuntime(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "9.9.9-unrelated");
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = "1.6.240701",
        };

        var result = _service.FindWindowsAppSdkMsixDirectory(usedVersions, requireExactVersion: false);

        // Legacy/packaged behavior: the general scan happily returns the unrelated cached runtime.
        Assert.IsNotNull(result, "default (tolerant) scan should fall back to any cached runtime");
        StringAssert.Contains(result!.FullName, "9.9.9-unrelated");
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_RequireExact_DoesNotReturnUnrelatedCachedRuntime()
    {
        // Same cache: only an unrelated runtime version is present, the app's exact version is absent.
        SeedCachedRuntime(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "9.9.9-unrelated");
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = "1.6.240701",
        };

        var result = _service.FindWindowsAppSdkMsixDirectory(usedVersions, requireExactVersion: true);

        // C40: exact-version callers must NOT accept the unrelated runtime — return null so the caller
        // surfaces "exact version unavailable" instead of installing/gating on the wrong runtime.
        Assert.IsNull(result, "requireExactVersion must skip the general scan when the exact version is absent");
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_RequireExact_ReturnsExactMatchWhenPresent()
    {
        // The exact runtime the app needs IS restored to the cache (the normal post-build state).
        SeedCachedRuntime(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "1.6.240701");
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE] = "1.6.240701",
        };

        var result = _service.FindWindowsAppSdkMsixDirectory(usedVersions, requireExactVersion: true);

        Assert.IsNotNull(result, "requireExactVersion must still return the exact match when it is cached");
        StringAssert.Contains(result!.FullName, "1.6.240701");
    }
}
