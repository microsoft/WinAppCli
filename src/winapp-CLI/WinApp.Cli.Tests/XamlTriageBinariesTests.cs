// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class XamlTriageBinariesTests
{
    private string _tempDir = null!;
    private string? _originalOverride;

    // Pass-through verifier for tests that use dummy (unsigned) binary files: resolution logic is under
    // test here, not the real Authenticode gate (covered by AuthenticodeVerifierTests + the L4 test).
    private static readonly Func<string, bool> AcceptAny = _ => true;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"XamlTriageBin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalOverride = Environment.GetEnvironmentVariable(XamlTriageBinaries.EnvOverride);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, _originalOverride);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void ResolveExisting_OverrideToEmptyDir_ReturnsNull()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, emptyDir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance);

        Assert.IsNull(resolved, "An override pointing at a directory without dbgeng.dll must not resolve.");
    }

    [TestMethod]
    public void ResolveExisting_FullLayout_ResolvesWithSymSrv()
    {
        var dir = Path.Combine(_tempDir, "full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        File.WriteAllText(Path.Combine(dir, "symsrv.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, AcceptAny);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(dir, resolved.BinDir);
        Assert.IsTrue(resolved.HasSymSrv, "symsrv.dll is present, so HasSymSrv must be true.");
    }

    [TestMethod]
    public void ResolveExisting_JsProviderInWinext_ResolvesWithoutSymSrv()
    {
        var dir = Path.Combine(_tempDir, "winext-layout");
        Directory.CreateDirectory(Path.Combine(dir, "winext"));
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "winext", "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, AcceptAny);

        Assert.IsNotNull(resolved);
        Assert.IsFalse(resolved.HasSymSrv, "No symsrv.dll present, so HasSymSrv must be false.");
        Assert.AreEqual(Path.Combine(dir, "winext", "JsProvider.dll"), resolved.JsProviderPath,
            "The resolved JsProvider path must point at the winext copy so the child runner can .load it.");
    }

    [TestMethod]
    public void DescribeOverrideGap_NoOverride_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);

        Assert.IsNull(XamlTriageBinaries.DescribeOverrideGap());
    }

    [TestMethod]
    public void DescribeOverrideGap_MissingDirectory_ReportsNonexistent()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, missing);

        var gap = XamlTriageBinaries.DescribeOverrideGap();

        Assert.IsNotNull(gap);
        StringAssert.Contains(gap, "does not exist");
        StringAssert.Contains(gap, missing);
    }

    [TestMethod]
    public void DescribeOverrideGap_EmptyDir_ListsBothMissingComponents()
    {
        var dir = Path.Combine(_tempDir, "override-empty");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var gap = XamlTriageBinaries.DescribeOverrideGap();

        Assert.IsNotNull(gap);
        StringAssert.Contains(gap, "dbgeng.dll");
        StringAssert.Contains(gap, "JsProvider.dll");
    }

    [TestMethod]
    public void DescribeOverrideGap_EngineOnly_ListsOnlyJsProvider()
    {
        var dir = Path.Combine(_tempDir, "override-engine-only");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var gap = XamlTriageBinaries.DescribeOverrideGap();

        Assert.IsNotNull(gap);
        StringAssert.Contains(gap, "JsProvider.dll");
        Assert.IsFalse(gap.Contains("dbgeng.dll"), "dbgeng.dll is present, so it must not be listed as missing.");
    }

    [TestMethod]
    public void DescribeOverrideGap_FullLayout_ReturnsNull()
    {
        var dir = Path.Combine(_tempDir, "override-full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        Assert.IsNull(XamlTriageBinaries.DescribeOverrideGap(),
            "A complete override layout has no gap to describe.");
    }

    [TestMethod]
    public void ResolveExisting_JsProviderFailsVerification_ReturnsNull()
    {
        // L4: a full layout on disk whose JsProvider.dll fails Authenticode verification (e.g. it was
        // replaced in the cache after download) must be rejected rather than loaded into the debugger.
        var dir = Path.Combine(_tempDir, "tampered");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, _ => false);

        Assert.IsNull(resolved, "A JsProvider.dll that fails signature verification must not resolve.");
    }

    [TestMethod]
    public void ResolveExisting_JsProviderInRoot_PrefersRootPath()
    {
        var dir = Path.Combine(_tempDir, "root-layout");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        File.WriteAllText(Path.Combine(dir, "JsProvider.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance, AcceptAny);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(Path.Combine(dir, "JsProvider.dll"), resolved.JsProviderPath);
    }

    [TestMethod]
    public void ResolveExisting_MissingJsProvider_ReturnsNull()
    {
        var dir = Path.Combine(_tempDir, "no-jsprovider");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbgeng.dll"), "");
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, dir);

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance);

        Assert.IsNull(resolved, "Without JsProvider.dll the JS extension cannot load, so resolution must fail.");
    }

    [TestMethod]
    public void ArchTokens_AreNonEmpty()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(XamlTriageBinaries.KitsArch));
        Assert.IsFalse(string.IsNullOrWhiteSpace(XamlTriageBinaries.NuGetArch));
    }

    [TestMethod]
    public void IsEnvOverrideSet_ReflectsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);
        Assert.IsFalse(XamlTriageBinaries.IsEnvOverrideSet, "No override set: IsEnvOverrideSet must be false.");

        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, "   ");
        Assert.IsFalse(XamlTriageBinaries.IsEnvOverrideSet, "Whitespace-only override must be treated as unset.");

        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, _tempDir);
        Assert.IsTrue(XamlTriageBinaries.IsEnvOverrideSet, "A non-empty override must report as set.");
    }

    [TestMethod]
    public void TryCopyFromGlobalCache_PinnedVersionPresent_CopiesFromPinned()
    {
        const string package = "Test.Package.Bits";
        const string pinned = "2.0.0";
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "cache"));
        // Pinned version + a numerically newer version; the newer one must be ignored.
        WriteCachePackage(cache, package, pinned, "engine.dll", "pinned");
        WriteCachePackage(cache, package, "9.9.9", "engine.dll", "newer");
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();

        var ok = XamlTriageBinaries.TryCopyFromGlobalCache(
            package, pinned, ["engine.dll"], cache, binDir, NullLogger.Instance);

        Assert.IsTrue(ok);
        Assert.AreEqual("pinned", File.ReadAllText(Path.Combine(binDir.FullName, "engine.dll")),
            "The pinned version must win even when a higher version number exists in the cache.");
    }

    [TestMethod]
    public void TryCopyFromGlobalCache_PinnedAbsent_FallsBackToNewest()
    {
        const string package = "Test.Package.Bits";
        var cache = new DirectoryInfo(Path.Combine(_tempDir, "cache"));
        WriteCachePackage(cache, package, "1.0.0", "engine.dll", "older");
        WriteCachePackage(cache, package, "1.5.0", "engine.dll", "newer");
        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();

        var ok = XamlTriageBinaries.TryCopyFromGlobalCache(
            package, "2.0.0", ["engine.dll"], cache, binDir, NullLogger.Instance);

        Assert.IsTrue(ok, "When the pinned version is missing, the newest cached version is an acceptable fallback.");
        Assert.AreEqual("newer", File.ReadAllText(Path.Combine(binDir.FullName, "engine.dll")));
    }

    [TestMethod]
    public void DbgPackageVersion_MatchesDirectoryPackagesProps()
    {
        var propsPath = FindUpwards("Directory.Packages.props",
            p => File.ReadAllText(p).Contains("Microsoft.Debugging.Platform.DbgEng", StringComparison.Ordinal));
        Assert.IsNotNull(propsPath, "Could not locate the Directory.Packages.props that pins the DbgEng package.");

        var text = File.ReadAllText(propsPath);
        var match = System.Text.RegularExpressions.Regex.Match(
            text, "Microsoft\\.Debugging\\.Platform\\.DbgEng\"\\s+Version=\"([^\"]+)\"");
        Assert.IsTrue(match.Success, "Could not find the DbgEng PackageVersion entry in Directory.Packages.props.");
        Assert.AreEqual(XamlTriageBinaries.DbgPackageVersion, match.Groups[1].Value,
            "XamlTriageBinaries.DbgPackageVersion drifted from the version pinned in Directory.Packages.props.");
    }

    [TestMethod]
    public void VerifyPackageHash_MatchingSha512_ReturnsTrue()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("winapp-dbgtools-package-content");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(bytes));

        Assert.IsTrue(XamlTriageBinaries.VerifyPackageHash(bytes, expected),
            "The exact pinned content hash must verify.");
        Assert.IsTrue(XamlTriageBinaries.VerifyPackageHash(bytes, expected.ToLowerInvariant()),
            "Hash comparison must be case-insensitive so lower-case hex pins also verify.");
    }

    [TestMethod]
    public void VerifyPackageHash_TamperedContent_ReturnsFalse()
    {
        var original = System.Text.Encoding.UTF8.GetBytes("winapp-dbgtools-package-content");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(original));
        var tampered = System.Text.Encoding.UTF8.GetBytes("winapp-dbgtools-package-contenX");

        Assert.IsFalse(XamlTriageBinaries.VerifyPackageHash(tampered, expected),
            "A single altered byte must fail the integrity check so mirrored/compromised feeds are rejected.");
    }

    [TestMethod]
    public void PinnedPackages_Sha512_MatchesRestoredNupkg()
    {
        // Guards against a mistyped or stale pinned hash: the packages are restore-only PackageReferences,
        // so their .nupkg is present in the NuGet global cache. If this can't be located (e.g. a clean
        // machine that restored elsewhere), the assertion is inconclusive rather than a false failure.
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(cache))
        {
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        foreach (var (package, version, expectedSha) in XamlTriageBinaries.PinnedPackages)
        {
            var id = package.ToLowerInvariant();
            var nupkg = Path.Combine(cache, id, version, $"{id}.{version}.nupkg");
            if (!File.Exists(nupkg))
            {
                Assert.Inconclusive($"Pinned package not found in NuGet cache: {nupkg}");
            }

            var actual = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(nupkg)));
            Assert.IsTrue(StringComparer.OrdinalIgnoreCase.Equals(expectedSha, actual),
                $"Pinned SHA-512 for {package} {version} drifted from the restored .nupkg. Expected {expectedSha}, got {actual.ToLowerInvariant()}. Update the compiled-in hash.");
        }
    }

    private static void WriteCachePackage(DirectoryInfo cache, string package, string version, string file, string content)
    {
        var archDir = Path.Combine(cache.FullName, package.ToLowerInvariant(), version, "content", XamlTriageBinaries.NuGetArch);
        Directory.CreateDirectory(archDir);
        File.WriteAllText(Path.Combine(archDir, file), content);
    }

    private static string? FindUpwards(string fileName, Func<string, bool> predicate)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                try
                {
                    if (predicate(candidate))
                    {
                        return candidate;
                    }
                }
                catch (IOException) { }
            }

            dir = dir.Parent;
        }

        return null;
    }
}
