// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the WinUI analyzer detection + injection wiring in <see cref="ProjectRunService"/>
/// (issue #634): the cheap WinUI text gate, the out-of-band probe arguments, detect-and-skip,
/// chaining of the user's CustomAfterMicrosoftCommonTargets, and build-pass argument injection.
/// </summary>
[TestClass]
public class ProjectRunServiceAnalyzerTests
{
    private DirectoryInfo _tempDir = null!;
    private DirectoryInfo _cacheDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"PrsAnalyzer_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _cacheDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "cache"));
        _cacheDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir.FullName))
        {
            try { Directory.Delete(_tempDir.FullName, true); } catch { }
        }
    }

    private const string WinUiCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
            <UseWinUI>true</UseWinUI>
          </PropertyGroup>
        </Project>
        """;

    private const string PlainCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private FileInfo WriteCsproj(string content, string name = "App.csproj")
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    private ProjectRunService NewService(FakeDotNetService dotnet) =>
        new(dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            new AnalyzerInjectionService(new StubWinappDirectoryService(_cacheDir)),
            new TestConsole(),
            NullLogger<ProjectRunService>.Instance);

    private static ProjectRunOptions Options() =>
        new("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

    /// <summary>Canned combined --getProperty/--getItem JSON the probe parses.</summary>
    private static string ProbeJson(bool useWinUI, string customAfter = "", params string[] packageRefs)
    {
        // JSON-escape backslashes the way real 'dotnet msbuild --getProperty' output does.
        static string Esc(string s) => s.Replace("\\", "\\\\");
        var pkgArray = string.Join(",", packageRefs.Select(p => $$"""{"Identity":"{{Esc(p)}}"}"""));
        return $$"""
            {
              "Properties": {
                "UseWinUI": "{{(useWinUI ? "true" : "")}}",
                "CustomAfterMicrosoftCommonTargets": "{{Esc(customAfter)}}"
              },
              "Items": {
                "PackageReference": [ {{pkgArray}} ]
              }
            }
            """;
    }

    // ---- Cheap WinUI text gate (LooksLikeWinUi) --------------------------------------------

    [TestMethod]
    public void LooksLikeWinUi_TrueForUseWinUIInCsproj()
    {
        Assert.IsTrue(ProjectRunService.LooksLikeWinUi(WriteCsproj(WinUiCsproj)));
    }

    [TestMethod]
    public void LooksLikeWinUi_FalseForPlainProject()
    {
        Assert.IsFalse(ProjectRunService.LooksLikeWinUi(WriteCsproj(PlainCsproj)));
    }

    [TestMethod]
    public void LooksLikeWinUi_TrueWhenDirectoryBuildPropsMentionsWindowsAppSdk()
    {
        File.WriteAllText(
            Path.Combine(_tempDir.FullName, "Directory.Build.props"),
            """<Project><ItemGroup><PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.0" /></ItemGroup></Project>""");
        Assert.IsTrue(ProjectRunService.LooksLikeWinUi(WriteCsproj(PlainCsproj)));
    }

    // ---- Probe arguments ------------------------------------------------------------------

    [TestMethod]
    public void BuildAnalyzerProbeArguments_RequestsWinUiCustomAfterAndPackageReferences()
    {
        var csproj = WriteCsproj(WinUiCsproj);
        var args = ProjectRunService.BuildAnalyzerProbeArguments(csproj, Options());

        StringAssert.Contains(args, "--getProperty:UseWinUI");
        StringAssert.Contains(args, "--getProperty:CustomAfterMicrosoftCommonTargets");
        StringAssert.Contains(args, "--getItem:PackageReference");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        // The probe must NOT set CustomAfterMicrosoftCommonTargets (that's what it reads).
        Assert.IsFalse(args.Contains("-p:CustomAfterMicrosoftCommonTargets="));
    }

    // ---- Build-pass argument injection (T5.2/T5.3) -----------------------------------------

    [TestMethod]
    public void BuildBuildPassArguments_WithoutInjection_HasNoCustomAfter()
    {
        var args = ProjectRunService.BuildBuildPassArguments(WriteCsproj(WinUiCsproj), Options(), "minimal");
        Assert.IsFalse(args.Contains("CustomAfterMicrosoftCommonTargets"));
        Assert.IsFalse(args.Contains("_WinAppChainedCustomAfter"));
    }

    [TestMethod]
    public void BuildBuildPassArguments_WithInjection_ThreadsHookAndChainedValue()
    {
        var injection = new ProjectRunService.AnalyzerBuildInjection(
            @"C:\cache\winapp-winui-analyzer.props", @"C:\user\after.props");

        var args = ProjectRunService.BuildBuildPassArguments(
            WriteCsproj(WinUiCsproj), Options(), "minimal", analyzerInjection: injection);

        StringAssert.Contains(args, "-p:CustomAfterMicrosoftCommonTargets=");
        StringAssert.Contains(args, "winapp-winui-analyzer.props");
        StringAssert.Contains(args, "-p:_WinAppChainedCustomAfter=");
        StringAssert.Contains(args, "after.props");
    }

    // ---- TryPrepareAnalyzerInjectionAsync end-to-end --------------------------------------

    [TestMethod]
    public async Task TryPrepare_ReturnsNull_ForNonWinUiProject_AndSkipsProbe()
    {
        var dotnet = new FakeDotNetService();
        var service = NewService(dotnet);

        var result = await service.TryPrepareAnalyzerInjectionAsync(
            WriteCsproj(PlainCsproj), Options(), _tempDir, CancellationToken.None);

        Assert.IsNull(result);
        // Cheap gate must short-circuit before any MSBuild probe runs.
        Assert.AreEqual(0, dotnet.StringInvocations.Count, "No probe should run for a non-WinUI project.");
    }

    [TestMethod]
    public async Task TryPrepare_ReturnsInjection_ForWinUiProject()
    {
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, ProbeJson(useWinUI: true), string.Empty),
        };
        var service = NewService(dotnet);

        var result = await service.TryPrepareAnalyzerInjectionAsync(
            WriteCsproj(WinUiCsproj), Options(), _tempDir, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(File.Exists(result.HookPropsPath));
        Assert.AreEqual(string.Empty, result.ChainedCustomAfter);
        Assert.AreEqual(1, dotnet.StringInvocations.Count, "Exactly one probe should run for a WinUI project.");
    }

    [TestMethod]
    public async Task TryPrepare_ReturnsNull_WhenProbeReportsNotWinUi()
    {
        var dotnet = new FakeDotNetService
        {
            // Text gate passes (csproj mentions UseWinUI) but the authoritative probe says false.
            RunDotnetCommandHandler = _ => (0, ProbeJson(useWinUI: false), string.Empty),
        };
        var service = NewService(dotnet);

        var result = await service.TryPrepareAnalyzerInjectionAsync(
            WriteCsproj(WinUiCsproj), Options(), _tempDir, CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryPrepare_ReturnsNull_WhenAnalyzerPackageAlreadyReferenced()
    {
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0,
                ProbeJson(useWinUI: true, customAfter: "", ProjectRunService.AnalyzerPackageId),
                string.Empty),
        };
        var service = NewService(dotnet);

        var result = await service.TryPrepareAnalyzerInjectionAsync(
            WriteCsproj(WinUiCsproj), Options(), _tempDir, CancellationToken.None);

        Assert.IsNull(result, "Detect-and-skip: no injection when the analyzer package is already referenced.");
    }

    [TestMethod]
    public async Task TryPrepare_ChainsExistingCustomAfterValue()
    {
        var existing = @"C:\repo\my.after.props";
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (0, ProbeJson(useWinUI: true, customAfter: existing), string.Empty),
        };
        var service = NewService(dotnet);

        var result = await service.TryPrepareAnalyzerInjectionAsync(
            WriteCsproj(WinUiCsproj), Options(), _tempDir, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(existing, result.ChainedCustomAfter);
    }

    [TestMethod]
    public async Task TryPrepare_ReturnsNull_WhenProbeFails()
    {
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = _ => (1, string.Empty, "boom"),
        };
        var service = NewService(dotnet);

        var result = await service.TryPrepareAnalyzerInjectionAsync(
            WriteCsproj(WinUiCsproj), Options(), _tempDir, CancellationToken.None);

        Assert.IsNull(result, "A failed probe must degrade to no injection, never break the build.");
    }
}
