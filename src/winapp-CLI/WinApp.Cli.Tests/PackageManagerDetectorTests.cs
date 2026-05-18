// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// Unit tests for PackageManagerDetector. Covers each detection
// signal (Corepack packageManager field, lockfile sniffing, fallback)
// and the priority ordering between them.
[TestClass]
public class PackageManagerDetectorTests
{
    private DirectoryInfo _tempDir = null!;
    private PackageManagerDetector _detector = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"PMDetectorTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _detector = new PackageManagerDetector();
    }

    [TestCleanup]
    public void Teardown()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    [TestMethod]
    public void Detect_NoSignals_ReturnsNpmDefault()
    {
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("npm", result.Name);
        Assert.AreEqual("npm install", result.InstallCommand);
    }

    [TestMethod]
    public void Detect_PnpmLockfile_ReturnsPnpm()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "pnpm-lock.yaml"), "lockfileVersion: 9\n");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("pnpm", result.Name);
        Assert.AreEqual("pnpm install", result.InstallCommand);
    }

    [TestMethod]
    public void Detect_YarnLockfile_ReturnsYarn()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "yarn.lock"), "# yarn lockfile v1\n");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("yarn", result.Name);
        Assert.AreEqual("yarn install", result.InstallCommand);
    }

    [TestMethod]
    public void Detect_BunLockfile_ReturnsBun()
    {
        // Bun ships either `bun.lockb` (binary, older) or `bun.lock` (text, newer).
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "bun.lockb"), new byte[] { 0x00, 0x01 });
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("bun", result.Name);
        Assert.AreEqual("bun install", result.InstallCommand);
    }

    [TestMethod]
    public void Detect_BunTextLockfile_ReturnsBun()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "bun.lock"), "{}\n");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("bun", result.Name);
    }

    [TestMethod]
    public void Detect_PackageLockJson_ReturnsNpm()
    {
        File.WriteAllText(Path.Combine(_tempDir.FullName, "package-lock.json"), "{}\n");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("npm", result.Name);
        Assert.AreEqual("npm install", result.InstallCommand);
    }

    [TestMethod]
    public void Detect_PnpmLockBeatsPackageLock_PnpmWins()
    {
        // When both lockfiles exist (e.g. user migrated), pnpm-lock.yaml is
        // the stronger signal because package-lock.json can be auto-created
        // by other tools.
        File.WriteAllText(Path.Combine(_tempDir.FullName, "pnpm-lock.yaml"), "lockfileVersion: 9\n");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "package-lock.json"), "{}\n");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("pnpm", result.Name);
    }

    [TestMethod]
    public void Detect_CorepackPackageManagerField_BeatsLockfile()
    {
        // Even with an npm lockfile, an explicit `packageManager: pnpm@…`
        // declaration in package.json is the authoritative signal.
        File.WriteAllText(Path.Combine(_tempDir.FullName, "package-lock.json"), "{}\n");
        File.WriteAllText(
            Path.Combine(_tempDir.FullName, "package.json"),
            "{ \"packageManager\": \"pnpm@9.5.0\" }");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("pnpm", result.Name);
        Assert.AreEqual("pnpm install", result.InstallCommand);
    }

    [TestMethod]
    public void Detect_CorepackWithShaSuffix_StillParses()
    {
        // Corepack format allows `<name>@<version>+sha512.<hash>`.
        File.WriteAllText(
            Path.Combine(_tempDir.FullName, "package.json"),
            "{ \"packageManager\": \"yarn@4.1.1+sha224.abcdef\" }");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("yarn", result.Name);
    }

    [TestMethod]
    public void Detect_CorepackUnknownPM_FallsThroughToLockfile()
    {
        // Future PMs we haven't heard of should not crash detection; we fall
        // through to the lockfile sniffing layer instead.
        File.WriteAllText(Path.Combine(_tempDir.FullName, "yarn.lock"), "# yarn lockfile v1\n");
        File.WriteAllText(
            Path.Combine(_tempDir.FullName, "package.json"),
            "{ \"packageManager\": \"futurepm@1.0.0\" }");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("yarn", result.Name);
    }

    [TestMethod]
    public void Detect_MalformedPackageJson_FallsBack()
    {
        // Detection must not crash if package.json is invalid JSON.
        File.WriteAllText(Path.Combine(_tempDir.FullName, "package.json"), "not valid json{");
        var result = _detector.Detect(_tempDir);
        Assert.AreEqual("npm", result.Name);
    }

    [TestMethod]
    public void Detect_NullArg_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _detector.Detect(null!));
    }
}
