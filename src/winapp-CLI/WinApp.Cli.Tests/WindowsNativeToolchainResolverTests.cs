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
            Path.GetFullPath(
                $"WindowsNativeToolchainResolverTests_{Guid.NewGuid():N}",
                Path.GetTempPath()));
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
            VswherePathProvider = () => TempPath("missing", "vswhere.exe"),
            WindowsKitsRootProvider = () => TempPath("kits"),
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
            RelativePath("installer", "vswhere.exe"),
            string.Empty);
        var visualStudioDirectory = Directory.CreateDirectory(
            TempPath("VS")).FullName;
        WriteFile(
            RelativePath(
                "VS",
                "VC",
                "Auxiliary",
                "Build",
                "Microsoft.VCToolsVersion.default.txt"),
            "14.51.36231");

        WriteFile(
            RelativePath(
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
            RelativePath(
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
            TempPath("Windows Kits", "10")).FullName;
        WriteFile(
            RelativePath("Windows Kits", "10", "Lib", "10.0.26100.0", "um", targetArchitecture, "kernel32.lib"),
            string.Empty);
        WriteFile(
            RelativePath("Windows Kits", "10", "Lib", "10.0.26100.0", "ucrt", targetArchitecture, "ucrt.lib"),
            string.Empty);
        WriteFile(
            RelativePath("Windows Kits", "10", "bin", "10.0.26100.0", "x64", "mt.exe"),
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
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException(
                $"Fixture path must be relative: '{relativePath}'.",
                nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(relativePath, _tempDirectory.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    private string TempPath(params string[] segments) =>
        Path.GetFullPath(RelativePath(segments), _tempDirectory.FullName);

    private static string RelativePath(params string[] segments)
    {
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                Path.IsPathRooted(segment) ||
                segment is "." or ".." ||
                !string.Equals(Path.GetFileName(segment), segment, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Fixture path contains an invalid segment.", nameof(segments));
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
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
