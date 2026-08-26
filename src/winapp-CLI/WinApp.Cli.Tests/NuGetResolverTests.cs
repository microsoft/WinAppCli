// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the metadata-selection rules that decide *which* .winmd files answer a
/// query. These are correctness rules rather than formatting: picking the wrong
/// SDK or runtime produces a confident answer about an API the project cannot
/// actually compile against.
/// </summary>
[TestClass]
public sealed class NuGetResolverTests
{
    private static readonly string[] SelectedWinmdOnly = ["Contoso.winmd"];
    private static readonly string[] ScannedRuntimeWinmd = ["Contoso.Runtime.winmd"];

    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"NuGetResolverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WriteAssets(string json)
    {
        string path = Path.Combine(_dir, "project.assets.json");
        File.WriteAllText(path, json);
        return path;
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReadsWindowsVersionFromTargetMoniker()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0-windows10.0.26100.0": {} },
          "libraries": {}
        }
        """);

        Assert.AreEqual("10.0.26100.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_FallsBackToProjectFrameworks()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0": {} },
          "project": { "frameworks": { "net8.0-windows10.0.22621.0": {} } }
        }
        """);

        Assert.AreEqual("10.0.22621.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReturnsNullWhenNotWindowsTargeted()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0": {} },
          "project": { "frameworks": { "net8.0": {} } }
        }
        """);

        Assert.IsNull(NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReturnsNullForUnreadableAssets()
    {
        string path = WriteAssets("{ this is not json");

        Assert.IsNull(NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void FindPackagesFromAssets_PrefersSelectedCompileAssetsOverEveryWinmdOnDisk()
    {
        // The package ships metadata for two targets; restore selected only one.
        // Indexing both lets a query confirm an API the project cannot compile against.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string selected = Path.Combine(packageDir, "lib", "net8.0-windows10.0.26100.0");
        string notSelected = Path.Combine(packageDir, "lib", "uap10.0");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(notSelected);
        File.WriteAllText(Path.Combine(selected, "Contoso.winmd"), "x");
        File.WriteAllText(Path.Combine(notSelected, "Legacy.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object>
                        {
                            ["lib/net8.0-windows10.0.26100.0/Contoso.winmd"] = new { },
                        },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            SelectedWinmdOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_FallsBackToScanWhenRestoreNamedNoCompileAssets()
    {
        // Some WinRT metadata packages carry .winmd outside any compile group. Those
        // must still index, or the fix for over-broad scanning would lose real APIs.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.runtime", "2.0.0");
        string metadata = Path.Combine(packageDir, "metadata");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "Contoso.Runtime.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Runtime/2.0.0"] = new { },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime/2.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            ScannedRuntimeWinmd,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void RuntimeReleaseLabel_KeepsNameEncodedReleaseForOnePointX()
    {
        // 1.x encodes the release in the name and uses an unrelated package version.
        Assert.AreEqual(
            "1.8",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_CombinesMajorOnlyNameWithPackageVersion()
    {
        // 2.x carries only the major in the name; the real release is in the version,
        // so a bare "2" would understate which runtime answered.
        Assert.AreEqual(
            "2.4",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.2_2.4.0.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_PreservesExperimentalSuffix()
    {
        Assert.AreEqual(
            "1.7-experimental3",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.1.7-experimental3_7000.392.2319.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_ReturnsFolderNameWhenNotARuntimePackage()
    {
        Assert.AreEqual("SomethingElse", NuGetResolver.RuntimeReleaseLabel("SomethingElse"));
    }
}
