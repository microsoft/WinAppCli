// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class YamlPackagesHasherTests
{
    [TestMethod]
    public void Compute_EmptyInput_ReturnsStableHash()
    {
        var h = YamlPackagesHasher.Compute(Array.Empty<PackagePin>());
        Assert.IsFalse(string.IsNullOrEmpty(h));
        Assert.AreEqual(64, h.Length, "SHA-256 hex digest should be 64 chars.");
    }

    [TestMethod]
    public void Compute_SameInputs_SameHash_IndependentOfOrder()
    {
        // Hash must be order-independent (yaml order vs dict order may differ).
        var a = new[]
        {
            new PackagePin { Name = "Foo", Version = "1.0" },
            new PackagePin { Name = "Bar", Version = "2.0" },
        };
        var b = new[]
        {
            new PackagePin { Name = "Bar", Version = "2.0" },
            new PackagePin { Name = "Foo", Version = "1.0" },
        };
        Assert.AreEqual(YamlPackagesHasher.Compute(a), YamlPackagesHasher.Compute(b));
    }

    [TestMethod]
    public void Compute_DifferentVersions_DifferentHash()
    {
        var a = new[] { new PackagePin { Name = "Foo", Version = "1.0" } };
        var b = new[] { new PackagePin { Name = "Foo", Version = "1.1" } };
        Assert.AreNotEqual(YamlPackagesHasher.Compute(a), YamlPackagesHasher.Compute(b));
    }

    [TestMethod]
    public void Compute_CaseInsensitiveOnName_VersionExactMatch()
    {
        // NuGet treats package IDs case-insensitively; hash must too.
        var a = new[] { new PackagePin { Name = "Foo", Version = "1.0" } };
        var b = new[] { new PackagePin { Name = "FOO", Version = "1.0" } };
        Assert.AreEqual(YamlPackagesHasher.Compute(a), YamlPackagesHasher.Compute(b));
    }

    [TestMethod]
    public void Compute_AddedPackage_DifferentHash()
    {
        var a = new[] { new PackagePin { Name = "Foo", Version = "1.0" } };
        var b = new[]
        {
            new PackagePin { Name = "Foo", Version = "1.0" },
            new PackagePin { Name = "Bar", Version = "2.0" },
        };
        Assert.AreNotEqual(YamlPackagesHasher.Compute(a), YamlPackagesHasher.Compute(b));
    }

    [TestMethod]
    public void Compute_SkipsBlankNames()
    {
        // Defensive: a yaml glitch shouldn't crash hashing.
        var a = new[]
        {
            new PackagePin { Name = "Foo", Version = "1.0" },
            new PackagePin { Name = "", Version = "ignored" },
            new PackagePin { Name = "   ", Version = "alsoIgnored" },
        };
        var b = new[] { new PackagePin { Name = "Foo", Version = "1.0" } };
        Assert.AreEqual(YamlPackagesHasher.Compute(a), YamlPackagesHasher.Compute(b));
    }

    [TestMethod]
    public void Compute_GoldenFixture_PinsHashForCrossLanguageParity()
    {
        // PINNED REFERENCE FIXTURE — the TS implementation in
        // src/winapp-npm/src/jsbindings/yaml-packages-hash.ts must produce
        // EXACTLY this hex for the same logical input. If the two drift,
        // stale-lockfile detection silently breaks (TS sees a different
        // hash than what restore wrote, but reports no change).
        //
        // When updating either side, recompute on both and update this hex
        // together. Sample inputs taken from a realistic Electron workspace
        // (lowercase normalization, ordinal sort by `lower(name)|version`).
        var packages = new[]
        {
            new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "2.1.3" },
            new PackagePin { Name = "Microsoft.Windows.SDK.CPP", Version = "10.0.28000.1839" },
        };
        Assert.AreEqual(
            "8581abfcb53fa04056a066fc7098c5d94064cc275e20f0e547365c1b8b146e54",
            YamlPackagesHasher.Compute(packages),
            "Hash drift detected — update yaml-packages-hash.ts to match (or vice-versa).");
    }
}
