// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class WindowsNativeToolchainResolverTests
{
    private DirectoryInfo _tempDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"WindowsNativeToolchainResolverTests_{Guid.NewGuid():N}"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDirectory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort for diagnostics held by a failed test.
        }
    }

    [TestMethod]
    public async Task ResolveX64_FindsVsMsvcSdkAndPrependsVswhereToPath()
    {
        var fixture = CreateFixture("x64");

        var result = await fixture.Resolver.ResolveAsync(
            new WindowsNativeToolchainRequirements(
                Architecture.X64,
                RequireCompiler: false,
                RequireLinker: true,
                RequireWindowsSdk: true),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Toolchain);
        Assert.AreEqual(fixture.VisualStudioDirectory, result.Toolchain.VisualStudioInstallPath);
        Assert.AreEqual("18.8.12105.206", result.Toolchain.VisualStudioVersion);
        Assert.AreEqual("14.51.36231", result.Toolchain.VcToolsVersion);
        StringAssert.EndsWith(result.Toolchain.LinkerPath, @"Hostx64\x64\link.exe");
        Assert.AreEqual("10.0.26100.0", result.Toolchain.WindowsSdkVersion);
        StringAssert.StartsWith(
            result.Toolchain.Environment["PATH"],
            Path.GetDirectoryName(fixture.VswherePath)!);
        Assert.IsTrue(fixture.Runner.Requests.Any(request =>
            request.Arguments.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64")));
    }

    [TestMethod]
    public async Task ResolveArm64_RequiresArm64ComponentAndCrossLinker()
    {
        var fixture = CreateFixture("arm64");

        var result = await fixture.Resolver.ResolveAsync(
            new WindowsNativeToolchainRequirements(
                Architecture.Arm64,
                RequireCompiler: true,
                RequireLinker: true,
                RequireWindowsSdk: true),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Toolchain);
        StringAssert.EndsWith(result.Toolchain.LinkerPath, @"Hostx64\arm64\link.exe");
        StringAssert.EndsWith(result.Toolchain.CompilerPath, @"Hostx64\arm64\cl.exe");
        Assert.IsTrue(fixture.Runner.Requests.Any(request =>
            request.Arguments.Contains("Microsoft.VisualStudio.Component.VC.Tools.ARM64")));
    }

    [TestMethod]
    public async Task Resolve_MissingVswhereReturnsActionableFailureWithoutSpawning()
    {
        var runner = new FakeProcessRunner();
        var resolver = new WindowsNativeToolchainResolver(runner)
        {
            VswherePathProvider = () => Path.Combine(_tempDirectory.FullName, "missing", "vswhere.exe"),
            WindowsKitsRootProvider = () => Path.Combine(_tempDirectory.FullName, "kits"),
        };

        var result = await resolver.ResolveAsync(
            new WindowsNativeToolchainRequirements(
                Architecture.X64,
                RequireCompiler: false,
                RequireLinker: true,
                RequireWindowsSdk: true),
            CancellationToken.None);

        Assert.AreEqual("VswhereNotFound", result.ErrorCode);
        StringAssert.Contains(result.Error, "Visual Studio Build Tools");
        Assert.AreEqual(0, runner.Requests.Count);
    }

    [TestMethod]
    public async Task Resolve_MissingArchitectureComponentReturnsInstallerComponent()
    {
        var fixture = CreateFixture("x64");
        fixture.Runner.ResultFactory = _ => new ProcessRunResult(0, string.Empty, string.Empty);

        var result = await fixture.Resolver.ResolveAsync(
            new WindowsNativeToolchainRequirements(
                Architecture.X64,
                RequireCompiler: false,
                RequireLinker: true,
                RequireWindowsSdk: true),
            CancellationToken.None);

        Assert.AreEqual("VisualStudioComponentMissing", result.ErrorCode);
        Assert.AreEqual(
            "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            result.RequiredComponent);
    }

    [TestMethod]
    public async Task Resolve_X86IsRejectedBeforeDiscovery()
    {
        var runner = new FakeProcessRunner();
        var resolver = new WindowsNativeToolchainResolver(runner);

        var result = await resolver.ResolveAsync(
            new WindowsNativeToolchainRequirements(
                Architecture.X86,
                RequireCompiler: false,
                RequireLinker: true,
                RequireWindowsSdk: true),
            CancellationToken.None);

        Assert.AreEqual("UnsupportedArchitecture", result.ErrorCode);
        Assert.AreEqual(0, runner.Requests.Count);
    }

    private ToolchainFixture CreateFixture(string targetArchitecture)
    {
        var vswherePath = WriteFile(
            Path.Combine("installer", "vswhere.exe"),
            string.Empty);
        var visualStudioDirectory = Directory.CreateDirectory(
            Path.Combine(_tempDirectory.FullName, "VS")).FullName;
        WriteFile(
            Path.Combine(
                "VS",
                "VC",
                "Auxiliary",
                "Build",
                "Microsoft.VCToolsVersion.default.txt"),
            "14.51.36231");

        WriteFile(
            Path.Combine(
                "VS",
                "VC",
                "Tools",
                "MSVC",
                "14.51.36231",
                "bin",
                "Hostx64",
                targetArchitecture,
                "link.exe"),
            string.Empty);
        WriteFile(
            Path.Combine(
                "VS",
                "VC",
                "Tools",
                "MSVC",
                "14.51.36231",
                "bin",
                "Hostx64",
                targetArchitecture,
                "cl.exe"),
            string.Empty);

        var kitsRoot = Directory.CreateDirectory(
            Path.Combine(_tempDirectory.FullName, "Windows Kits", "10")).FullName;
        WriteFile(
            Path.Combine("Windows Kits", "10", "Lib", "10.0.26100.0", "um", targetArchitecture, "kernel32.lib"),
            string.Empty);
        WriteFile(
            Path.Combine("Windows Kits", "10", "Lib", "10.0.26100.0", "ucrt", targetArchitecture, "ucrt.lib"),
            string.Empty);
        WriteFile(
            Path.Combine("Windows Kits", "10", "bin", "10.0.26100.0", "x64", "mt.exe"),
            string.Empty);

        var runner = new FakeProcessRunner
        {
            ResultFactory = request =>
                request.Arguments.Contains("installationVersion")
                    ? new ProcessRunResult(0, "18.8.12105.206", string.Empty)
                    : new ProcessRunResult(0, visualStudioDirectory, string.Empty),
        };
        var resolver = new WindowsNativeToolchainResolver(runner)
        {
            VswherePathProvider = () => vswherePath,
            WindowsKitsRootProvider = () => kitsRoot,
        };

        return new ToolchainFixture(
            resolver,
            runner,
            vswherePath,
            visualStudioDirectory);
    }

    private string WriteFile(string relativePath, string contents)
    {
        var fullPath = Path.Combine(_tempDirectory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    private sealed record ToolchainFixture(
        WindowsNativeToolchainResolver Resolver,
        FakeProcessRunner Runner,
        string VswherePath,
        string VisualStudioDirectory);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<ProcessRunRequest, ProcessRunResult> ResultFactory { get; set; } =
            _ => new ProcessRunResult(0, string.Empty, string.Empty);

        public List<ProcessRunRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(ResultFactory(request));
        }
    }
}
