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

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance);

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

        var resolved = XamlTriageBinaries.ResolveExisting(new DirectoryInfo(_tempDir), NullLogger.Instance);

        Assert.IsNotNull(resolved);
        Assert.IsFalse(resolved.HasSymSrv, "No symsrv.dll present, so HasSymSrv must be false.");
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
}
