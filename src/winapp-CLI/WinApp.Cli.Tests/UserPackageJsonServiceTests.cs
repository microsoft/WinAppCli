// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// Unit tests for UserPackageJsonService. Covers each
// RuntimeDependencyOutcome branch plus formatting/ordering
// preservation guarantees.
[TestClass]
public class UserPackageJsonServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private UserPackageJsonService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"UserPkgJsonTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _service = new UserPackageJsonService();
    }

    [TestCleanup]
    public void Teardown()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private string PackageJsonPath => Path.Combine(_tempDir.FullName, "package.json");

    [TestMethod]
    public void EnsureRuntimeDependency_NoPackageJson_ReturnsNoPackageJson()
    {
        var outcome = _service.EnsureRuntimeDependency(
            _tempDir, "@microsoft/dynwinrt", "1.0.0");
        Assert.AreEqual(RuntimeDependencyOutcome.NoPackageJson, outcome);
        Assert.IsFalse(File.Exists(PackageJsonPath),
            "We must not synthesize a package.json on the user's behalf");
    }

    [TestMethod]
    public void EnsureRuntimeDependency_NoDependenciesObject_AddsAndReturnsAdded()
    {
        File.WriteAllText(PackageJsonPath,
            "{\n  \"name\": \"my-app\",\n  \"version\": \"1.0.0\"\n}\n");

        var outcome = _service.EnsureRuntimeDependency(
            _tempDir, "@microsoft/dynwinrt", "1.0.0");

        Assert.AreEqual(RuntimeDependencyOutcome.Added, outcome);
        var content = File.ReadAllText(PackageJsonPath);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        Assert.AreEqual("my-app", root.GetProperty("name").GetString());
        Assert.AreEqual("1.0.0",
            root.GetProperty("dependencies").GetProperty("@microsoft/dynwinrt").GetString());
    }

    [TestMethod]
    public void EnsureRuntimeDependency_DependenciesExistsButMissingPackage_AddsAndReturnsAdded()
    {
        File.WriteAllText(PackageJsonPath,
            "{\n  \"name\": \"my-app\",\n  \"dependencies\": {\n    \"react\": \"19.0.0\"\n  }\n}\n");

        var outcome = _service.EnsureRuntimeDependency(
            _tempDir, "@microsoft/dynwinrt", "1.0.0");

        Assert.AreEqual(RuntimeDependencyOutcome.Added, outcome);
        using var doc = JsonDocument.Parse(File.ReadAllText(PackageJsonPath));
        var deps = doc.RootElement.GetProperty("dependencies");
        Assert.AreEqual("19.0.0", deps.GetProperty("react").GetString(),
            "Pre-existing deps must survive untouched");
        Assert.AreEqual("1.0.0", deps.GetProperty("@microsoft/dynwinrt").GetString());
    }

    [TestMethod]
    public void EnsureRuntimeDependency_AlreadyInDependencies_NoOpReturnsAlreadyPresent()
    {
        File.WriteAllText(PackageJsonPath,
            "{\n  \"dependencies\": {\n    \"@microsoft/dynwinrt\": \"0.5.0\"\n  }\n}\n");
        var beforeMtime = File.GetLastWriteTimeUtc(PackageJsonPath);

        // Sleep to ensure mtime granularity reveals any unintended write.
        Thread.Sleep(50);

        var outcome = _service.EnsureRuntimeDependency(
            _tempDir, "@microsoft/dynwinrt", "1.0.0");

        Assert.AreEqual(RuntimeDependencyOutcome.AlreadyPresent, outcome);
        // We must not overwrite the user's pinned version.
        using var doc = JsonDocument.Parse(File.ReadAllText(PackageJsonPath));
        Assert.AreEqual("0.5.0",
            doc.RootElement.GetProperty("dependencies").GetProperty("@microsoft/dynwinrt").GetString());
        Assert.AreEqual(beforeMtime, File.GetLastWriteTimeUtc(PackageJsonPath),
            "AlreadyPresent must not rewrite the file");
    }

    [TestMethod]
    public void EnsureRuntimeDependency_OnlyInDevDependencies_ReturnsPresentInDev()
    {
        File.WriteAllText(PackageJsonPath,
            "{\n  \"devDependencies\": {\n    \"@microsoft/dynwinrt\": \"0.5.0\"\n  }\n}\n");
        var beforeMtime = File.GetLastWriteTimeUtc(PackageJsonPath);
        Thread.Sleep(50);

        var outcome = _service.EnsureRuntimeDependency(
            _tempDir, "@microsoft/dynwinrt", "1.0.0");

        Assert.AreEqual(RuntimeDependencyOutcome.PresentInDevDependencies, outcome);
        Assert.AreEqual(beforeMtime, File.GetLastWriteTimeUtc(PackageJsonPath),
            "PresentInDevDependencies must not auto-promote (don't surprise the user)");
    }

    [TestMethod]
    public void EnsureRuntimeDependency_PreservesUnrelatedKeysAndOrder()
    {
        File.WriteAllText(PackageJsonPath,
            "{\n" +
            "  \"name\": \"my-app\",\n" +
            "  \"version\": \"2.3.4\",\n" +
            "  \"scripts\": { \"start\": \"node .\" },\n" +
            "  \"author\": \"alice\"\n" +
            "}\n");

        _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0");

        var content = File.ReadAllText(PackageJsonPath);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Walk properties in their actual order.
        var keysInOrder = root.EnumerateObject().Select(p => p.Name).ToList();
        // dependencies should appear right after version (matching the
        // conventional layout); other keys should keep their relative order.
        var versionIndex = keysInOrder.IndexOf("version");
        var depsIndex = keysInOrder.IndexOf("dependencies");
        Assert.IsTrue(versionIndex >= 0 && depsIndex == versionIndex + 1,
            $"Expected dependencies right after version; got: [{string.Join(", ", keysInOrder)}]");

        // Author still present (not lost during rebuild).
        Assert.AreEqual("alice", root.GetProperty("author").GetString());
    }

    [TestMethod]
    public void EnsureRuntimeDependency_PreservesTrailingNewline()
    {
        File.WriteAllText(PackageJsonPath, "{\n  \"name\": \"my-app\"\n}\n");
        _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0");
        var content = File.ReadAllText(PackageJsonPath);
        Assert.IsTrue(content.EndsWith('\n'),
            "POSIX text file convention: trailing newline must be preserved");
    }

    [TestMethod]
    public void EnsureRuntimeDependency_NoTrailingNewline_DoesNotAddOne()
    {
        File.WriteAllText(PackageJsonPath, "{\"name\":\"my-app\"}");
        _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0");
        var content = File.ReadAllText(PackageJsonPath);
        Assert.IsFalse(content.EndsWith('\n'),
            "Should preserve original trailing-newline state (none → none)");
    }

    [TestMethod]
    public void EnsureRuntimeDependency_MalformedJson_Throws()
    {
        File.WriteAllText(PackageJsonPath, "not valid json{");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0"));
    }

    [TestMethod]
    public void EnsureRuntimeDependency_RootIsNotObject_Throws()
    {
        File.WriteAllText(PackageJsonPath, "[1, 2, 3]");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0"));
    }

    [TestMethod]
    public void EnsureRuntimeDependency_NullOrEmptyArgs_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            _service.EnsureRuntimeDependency(null!, "x", "1.0.0"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            _service.EnsureRuntimeDependency(_tempDir, "", "1.0.0"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            _service.EnsureRuntimeDependency(_tempDir, "x", ""));
    }

    // ---- Reparse-point guard (M9) ----

    [TestMethod]
    public void EnsureRuntimeDependency_PackageJsonIsSymlink_Throws()
    {
        // Plant a real package.json elsewhere, then symlink it into the
        // workspace. The guard must refuse to rewrite via the symlink so a
        // malicious workspace can't redirect the edit to a victim file.
        var realDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"UserPkgJsonTests_Real_{Guid.NewGuid():N}"));
        realDir.Create();
        try
        {
            var realPackageJson = Path.Combine(realDir.FullName, "package.json");
            File.WriteAllText(realPackageJson, "{\"name\":\"victim\"}");

            try
            {
                File.CreateSymbolicLink(PackageJsonPath, realPackageJson);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Creating a symlink on Windows requires admin or Developer
                // Mode. Skip this assertion silently rather than fail the
                // suite on locked-down CI/dev machines.
                Assert.Inconclusive($"Could not create a symbolic link in this environment: {ex.Message}");
                return;
            }

            var ex2 = Assert.ThrowsExactly<InvalidOperationException>(() =>
                _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0"));
            StringAssert.Contains(ex2.Message, "symbolic link", "Error must explain the refusal");
            // Real file must be untouched.
            Assert.AreEqual("{\"name\":\"victim\"}", File.ReadAllText(realPackageJson));
        }
        finally
        {
            try { realDir.Delete(true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void EnsureRuntimeDependency_AncestorIsJunction_Throws()
    {
        // Same threat as a file-level symlink, but at a directory ancestor:
        // `<temp>\<wkspace>\nested\` is a junction pointing at a real dir
        // that holds a package.json. Refusing must cover this case too.
        var realDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), $"UserPkgJsonTests_RealDir_{Guid.NewGuid():N}"));
        realDir.Create();
        try
        {
            File.WriteAllText(Path.Combine(realDir.FullName, "package.json"), "{\"name\":\"victim\"}");

            var junctionPath = Path.Combine(_tempDir.FullName, "nested");
            try
            {
                // mklink /J is non-elevating on Windows even without Dev Mode.
                Directory.CreateSymbolicLink(junctionPath, realDir.FullName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Inconclusive($"Could not create a directory link in this environment: {ex.Message}");
                return;
            }

            var junctionWorkspace = new DirectoryInfo(junctionPath);
            var ex2 = Assert.ThrowsExactly<InvalidOperationException>(() =>
                _service.EnsureRuntimeDependency(junctionWorkspace, "@microsoft/dynwinrt", "1.0.0"));
            StringAssert.Contains(ex2.Message, "symbolic link", "Error must explain the refusal");
        }
        finally
        {
            try { realDir.Delete(true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void EnsureRuntimeDependency_LockedPackageJson_ThrowsWrapped()
    {
        File.WriteAllText(PackageJsonPath, "{\"name\":\"my-app\",\"version\":\"1.0.0\"}");
        // Hold an exclusive lock so the service's atomic write
        // (or its preceding read) fails. The wrapper must surface this as
        // an InvalidOperationException, not a raw IOException — otherwise
        // CLI orchestration aborts mid-init instead of degrading to a
        // warning.
        using var locker = new FileStream(
            PackageJsonPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0"));
        Assert.IsNotNull(ex.InnerException);
        Assert.IsTrue(
            ex.InnerException is IOException or UnauthorizedAccessException,
            $"Inner exception should be IOException or UnauthorizedAccessException, was {ex.InnerException?.GetType().Name}");
    }

    // ---------------------------------------------------------------------
    // L2 — write-path catch reachable when destination can be READ but
    // cannot be REPLACED. Pre-existing LockedPackageJson_ThrowsWrapped uses
    // FileShare.None which lights up the READ catch path; this test holds
    // the destination open for FileShare.Read (so the service's read
    // succeeds) and asserts the WRITE catch wraps the rename failure with
    // the actionable "Failed to write" prefix.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void EnsureRuntimeDependency_DestinationWriteLocked_WrapsWithFailedToWrite()
    {
        File.WriteAllText(PackageJsonPath, "{\"name\":\"my-app\",\"version\":\"1.0.0\"}");

        // Open WITH FileShare.Read: other readers (the service's
        // File.OpenRead / File.ReadAllText) succeed, but File.Move
        // overwriting the destination fails because no FileShare.Write
        // is granted. That lands in the catch at lines 120-128 of
        // UserPackageJsonService.
        using var locker = new FileStream(
            PackageJsonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            _service.EnsureRuntimeDependency(_tempDir, "@microsoft/dynwinrt", "1.0.0"));

        StringAssert.Matches(
            ex.Message,
            new System.Text.RegularExpressions.Regex("(Failed|No permission) to write"),
            "Wrapper must surface the I/O / permission failure with an actionable 'to write' prefix.");
        Assert.IsTrue(
            ex.InnerException is IOException or UnauthorizedAccessException,
            $"Inner must be IOException / UnauthorizedAccessException, was {ex.InnerException?.GetType().Name}");
    }
}
