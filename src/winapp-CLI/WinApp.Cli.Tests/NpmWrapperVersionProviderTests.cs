// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// Tests for NpmWrapperVersionProvider. ProcessPath in `dotnet test` points
// at testhost.exe (outside any npm layout), so we exercise the failure path.
[TestClass]
public class NpmWrapperVersionProviderTests
{
    private DirectoryInfo _temp = null!;

    [TestInitialize]
    public void Init()
    {
        _temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"NpmWrapperTests_{Guid.NewGuid():N}"));
        _temp.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _temp.Delete(recursive: true); } catch { /* ignore */ }
    }

    [TestMethod]
    public void DynWinrtVersion_OutsideNpmLayout_ThrowsInvalidOperationWithDIHint()
    {
        var provider = new NpmWrapperVersionProvider();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => _ = provider.DynWinrtVersion);
        StringAssert.Contains(ex.Message, "@microsoft/winappcli");
        StringAssert.Contains(ex.Message, "INpmWrapperVersionProvider",
            "Error must point users at the DI override they need to register");
    }

    [TestMethod]
    public void DynWinrtCodegenVersion_OutsideNpmLayout_ThrowsInvalidOperation()
    {
        var provider = new NpmWrapperVersionProvider();
        Assert.ThrowsExactly<InvalidOperationException>(
            () => _ = provider.DynWinrtCodegenVersion);
    }

    [TestMethod]
    public void Versions_AreLazyAndShared()
    {
        // Lazy<T> should cache and replay the same failure across both props.
        var provider = new NpmWrapperVersionProvider();
        var first = Assert.ThrowsExactly<InvalidOperationException>(
            () => _ = provider.DynWinrtVersion);
        var second = Assert.ThrowsExactly<InvalidOperationException>(
            () => _ = provider.DynWinrtCodegenVersion);
        Assert.AreEqual(first.Message, second.Message,
            "Lazy<T> should cache and replay the same failure");
    }

    // ── Happy-path / structural tests against the LocateFrom seam ───────

    [TestMethod]
    public void LocateFrom_ValidWrapperLayout_ReturnsCodegenDependencyVersion()
    {
        // Simulates node_modules/@microsoft/winappcli/{package.json + bin/<arch>/winapp.exe}
        var pkgDir = Directory.CreateDirectory(
            Path.Combine(_temp.FullName, "node_modules", "@microsoft", "winappcli"));
        var binDir = Directory.CreateDirectory(Path.Combine(pkgDir.FullName, "bin", "win-arm64"));
        File.WriteAllText(Path.Combine(pkgDir.FullName, "package.json"), """
            {
              "name": "@microsoft/winappcli",
              "version": "0.3.2",
              "dependencies": {
                "@microsoft/dynwinrt-codegen": "0.1.0-preview.1"
              }
            }
            """);

        var version = NpmWrapperVersionProvider.LocateFrom(binDir.FullName);

        Assert.AreEqual("0.1.0-preview.1", version);
    }

    [TestMethod]
    public void LocateFrom_UnrelatedPackageJsonInParent_KeepsWalkingForWrapper()
    {
        // Common case: project package.json appears before the wrapper one.
        var workspace = Directory.CreateDirectory(Path.Combine(_temp.FullName, "user-workspace"));
        File.WriteAllText(Path.Combine(workspace.FullName, "package.json"), """
            { "name": "some-user-project", "version": "1.0.0" }
            """);
        var wrapperDir = Directory.CreateDirectory(
            Path.Combine(workspace.FullName, "node_modules", "@microsoft", "winappcli"));
        File.WriteAllText(Path.Combine(wrapperDir.FullName, "package.json"), """
            {
              "name": "@microsoft/winappcli",
              "version": "0.3.2",
              "dependencies": { "@microsoft/dynwinrt-codegen": "9.9.9-from-wrapper" }
            }
            """);
        var binDir = Directory.CreateDirectory(Path.Combine(wrapperDir.FullName, "bin", "win-x64"));

        var version = NpmWrapperVersionProvider.LocateFrom(binDir.FullName);

        Assert.AreEqual("9.9.9-from-wrapper", version,
            "Walker must skip unrelated package.json files and only accept the wrapper one.");
    }

    [TestMethod]
    public void LocateFrom_WrapperPackageJsonWithoutDependencies_Throws()
    {
        var pkgDir = Directory.CreateDirectory(
            Path.Combine(_temp.FullName, "node_modules", "@microsoft", "winappcli"));
        File.WriteAllText(Path.Combine(pkgDir.FullName, "package.json"), """
            { "name": "@microsoft/winappcli", "version": "0.3.2" }
            """);
        var binDir = Directory.CreateDirectory(Path.Combine(pkgDir.FullName, "bin"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => NpmWrapperVersionProvider.LocateFrom(binDir.FullName));
        StringAssert.Contains(ex.Message, "dependencies");
    }

    [TestMethod]
    public void LocateFrom_WrapperPackageJsonMissingCodegenDep_Throws()
    {
        var pkgDir = Directory.CreateDirectory(
            Path.Combine(_temp.FullName, "node_modules", "@microsoft", "winappcli"));
        File.WriteAllText(Path.Combine(pkgDir.FullName, "package.json"), """
            {
              "name": "@microsoft/winappcli",
              "version": "0.3.2",
              "dependencies": { "some-other-pkg": "1.0.0" }
            }
            """);
        var binDir = Directory.CreateDirectory(Path.Combine(pkgDir.FullName, "bin"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => NpmWrapperVersionProvider.LocateFrom(binDir.FullName));
        StringAssert.Contains(ex.Message, "@microsoft/dynwinrt-codegen");
    }

    [TestMethod]
    public void LocateFrom_MalformedPackageJson_Throws()
    {
        var pkgDir = Directory.CreateDirectory(
            Path.Combine(_temp.FullName, "node_modules", "@microsoft", "winappcli"));
        File.WriteAllText(Path.Combine(pkgDir.FullName, "package.json"), "{ not valid json");
        var binDir = Directory.CreateDirectory(Path.Combine(pkgDir.FullName, "bin"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => NpmWrapperVersionProvider.LocateFrom(binDir.FullName));
        StringAssert.Contains(ex.Message, "Failed to parse");
    }

    [TestMethod]
    public void LocateFrom_NoWrapperAnywhere_ThrowsWithDIHint()
    {
        // No package.json at any ancestor.
        var bare = Directory.CreateDirectory(Path.Combine(_temp.FullName, "bare"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => NpmWrapperVersionProvider.LocateFrom(bare.FullName));
        StringAssert.Contains(ex.Message, "@microsoft/winappcli");
        StringAssert.Contains(ex.Message, "INpmWrapperVersionProvider");
    }
}
