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
}
