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
}
