// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Direct coverage for <see cref="WinappConfig"/>'s pin lookup/mutation contract — the model
/// behind every <c>config.GetVersion(...)</c> consumer (build-tools resolution, MSIX runtime
/// resolution, package installation) and every <c>config.SetVersion(...)</c> writer
/// (<c>winapp update</c>, workspace setup). These assert the real add-vs-update and
/// case-insensitive matching behavior, not merely that the methods run.
/// </summary>
[TestClass]
public sealed class WinappConfigTests
{
    [TestMethod]
    public void GetVersion_UnknownPackage_ReturnsNull()
    {
        var cfg = new WinappConfig();
        Assert.IsNull(cfg.GetVersion("Microsoft.WindowsAppSDK"),
            "An unpinned package must report no version so callers fall back to their defaults.");
    }

    [TestMethod]
    public void GetVersion_KnownPackage_ReturnsPinnedVersion()
    {
        var cfg = new WinappConfig();
        cfg.Packages.Add(new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.6.0" });

        Assert.AreEqual("1.6.0", cfg.GetVersion("Microsoft.WindowsAppSDK"));
    }

    [TestMethod]
    public void GetVersion_IsCaseInsensitiveOnName()
    {
        var cfg = new WinappConfig();
        cfg.Packages.Add(new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.6.0" });

        // Consumers pass package names from manifests, YAML and CLI args with varying casing.
        Assert.AreEqual("1.6.0", cfg.GetVersion("microsoft.windowsappsdk"));
    }

    [TestMethod]
    public void SetVersion_NewPackage_AppendsPin()
    {
        var cfg = new WinappConfig();

        cfg.SetVersion("Vendor.Pkg", "2.0.1");

        Assert.AreEqual(1, cfg.Packages.Count);
        Assert.AreEqual("Vendor.Pkg", cfg.Packages[0].Name);
        Assert.AreEqual("2.0.1", cfg.Packages[0].Version);
        Assert.AreEqual("2.0.1", cfg.GetVersion("Vendor.Pkg"));
    }

    [TestMethod]
    public void SetVersion_ExistingPackage_UpdatesInPlaceWithoutAddingDuplicate()
    {
        var cfg = new WinappConfig();
        cfg.SetVersion("Vendor.Pkg", "2.0.1");

        cfg.SetVersion("Vendor.Pkg", "3.1.4");

        Assert.AreEqual(1, cfg.Packages.Count, "Re-pinning an existing package must update, not duplicate.");
        Assert.AreEqual("3.1.4", cfg.Packages[0].Version);
        Assert.AreEqual("3.1.4", cfg.GetVersion("Vendor.Pkg"));
    }

    [TestMethod]
    public void SetVersion_ExistingPackage_MatchesNameCaseInsensitively()
    {
        var cfg = new WinappConfig();
        cfg.Packages.Add(new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.6.0" });

        // A differently-cased update must land on the same pin rather than creating a second one.
        cfg.SetVersion("MICROSOFT.WINDOWSAPPSDK", "1.7.0");

        Assert.AreEqual(1, cfg.Packages.Count);
        Assert.AreEqual("1.7.0", cfg.Packages[0].Version);
        Assert.AreEqual("Microsoft.WindowsAppSDK", cfg.Packages[0].Name,
            "Updating by a differently-cased name must preserve the original pin's name.");
    }
}
