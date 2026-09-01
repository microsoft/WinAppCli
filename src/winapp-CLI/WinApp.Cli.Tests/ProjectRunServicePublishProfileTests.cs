// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ProjectRunServicePublishProfileTests
{
    private DirectoryInfo _tempDirectory = null!;
    private readonly List<TestConsole> _consoles = [];

    [TestInitialize]
    public void Setup()
    {
        _tempDirectory = new DirectoryInfo(
            Path.Join(Path.GetTempPath(), $"ProjectRunPublishProfileTests_{Guid.NewGuid():N}"));
        _tempDirectory.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var console in _consoles)
        {
            console.Dispose();
        }
        _consoles.Clear();

        try
        {
            _tempDirectory.Delete(recursive: true);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Could not delete test directory '{_tempDirectory.FullName}': {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Could not delete test directory '{_tempDirectory.FullName}': {ex}");
        }
    }

    [TestMethod]
    public void AnyCpuProjectReference_SelectsArm64ProfileWithoutGlobalPlatform()
    {
        var library = WriteFile("Library\\Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platform>AnyCPU</Platform>
              </PropertyGroup>
            </Project>
            """);
        var app = WriteApp("""
            <ItemGroup>
              <ProjectReference Include="Library\Library.csproj" />
            </ItemGroup>
            """);
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");

        var options = Options("arm64");
        options = ProjectRunService.ResolvePlatformInjection(app, options);
        Assert.IsNull(options.Platform, $"{library.Name} is AnyCPU, so Platform must remain unset");

        options = ResolveProfile(app, options);
        Assert.AreEqual("win-arm64.pubxml", options.PublishProfile);

        var buildArguments = ProjectRunService.BuildBuildPassArguments(app, options, "minimal");
        StringAssert.Contains(buildArguments, "-r win-arm64");
        StringAssert.Contains(buildArguments, "-p:PublishProfile=win-arm64.pubxml");
        Assert.IsFalse(buildArguments.Contains("-p:Platform=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolvedProfile_FlowsThroughRestoreBuildAndEvaluate()
    {
        var app = WriteApp();
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");
        var options = ResolveProfile(app, Options("arm64"));

        StringAssert.Contains(
            ProjectRunService.BuildRestorePassArguments(app, options),
            "-p:PublishProfile=win-arm64.pubxml");
        StringAssert.Contains(
            ProjectRunService.BuildBuildPassArguments(app, options, "minimal"),
            "-p:PublishProfile=win-arm64.pubxml");
        StringAssert.Contains(
            ProjectRunService.BuildEvaluateArguments(app, options),
            "-p:PublishProfile=win-arm64.pubxml");

        var fallback = ProjectRunService.BuildEvaluateArguments(
            app,
            options,
            includePublishProfile: false);
        Assert.IsFalse(fallback.Contains("-p:PublishProfile=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExplicitPublishProfile_IsNeverOverridden()
    {
        var app = WriteApp();
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");
        var options = Options("arm64", "PublishProfile=custom.pubxml");

        var resolved = ResolveProfile(app, options);

        Assert.IsNull(resolved.PublishProfile);
        StringAssert.Contains(
            ProjectRunService.BuildBuildPassArguments(app, resolved, "minimal"),
            "-p:PublishProfile=custom.pubxml");
    }

    [TestMethod]
    public void LiteralPublishProfile_IsNotReplaced()
    {
        var app = WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <PublishProfile>custom.pubxml</PublishProfile>
              </PropertyGroup>
            </Project>
            """);
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");

        var resolved = ResolveProfile(
            app,
            Options("arm64"),
            currentProfile: "custom.pubxml",
            candidateProfile: "custom.pubxml");

        Assert.IsNull(resolved.PublishProfile);
    }

    [TestMethod]
    public void DirectoryQualifiedProfile_ValidatesSdkResolvedFile()
    {
        var app = WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <Platforms>x64;ARM64</Platforms>
                <PublishProfile>Custom\win-$(Platform.ToLower()).pubxml</PublishProfile>
              </PropertyGroup>
            </Project>
            """);
        WriteFile("Custom\\win-arm64.pubxml", """
            <Project>
              <PropertyGroup>
                <Platform>ARM64</Platform>
                <RuntimeIdentifier>win-arm64</RuntimeIdentifier>
                <SelfContained>true</SelfContained>
              </PropertyGroup>
            </Project>
            """);
        WriteProfile("win-arm64.pubxml", "x64", "win-x64");

        var resolved = ResolveProfile(
            app,
            Options("arm64"),
            currentProfile: "Custom\\win-anycpu.pubxml",
            candidateProfile: "Custom\\win-arm64.pubxml");

        Assert.IsNull(
            resolved.PublishProfile,
            "the SDK imports the profile basename from Properties\\PublishProfiles, not the directory in PublishProfile");
    }

    [TestMethod]
    public void ResolvedProfile_EscapesMsBuildPropertySeparators()
    {
        var app = WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <Platforms>x64;ARM64</Platforms>
                <PublishProfile>win-$(Platform.ToLower());Extra=true.pubxml</PublishProfile>
              </PropertyGroup>
            </Project>
            """);
        WriteProfile("win-arm64;Extra=true.pubxml", "ARM64", "win-arm64");

        var resolved = ResolveProfile(
            app,
            Options("arm64"),
            currentProfile: "win-anycpu;Extra=true.pubxml",
            candidateProfile: "win-arm64;Extra=true.pubxml");
        var arguments = ProjectRunService.BuildBuildPassArguments(app, resolved, "minimal");

        StringAssert.Contains(arguments, "-p:PublishProfile=win-arm64%3BExtra=true.pubxml");
    }

    [TestMethod]
    public void FrameworkDependentProfile_IsNotSelectedAsSelfContainedFallback()
    {
        var app = WriteApp();
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64", selfContained: false);

        var resolved = ResolveProfile(app, Options("arm64"), candidateSelfContained: false);

        Assert.IsNull(resolved.PublishProfile);
    }

    [TestMethod]
    public void ReferencedProjectWithSameProfileName_SuppressesFallback()
    {
        WriteFile("Library\\Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platform>AnyCPU</Platform>
              </PropertyGroup>
            </Project>
            """);
        WriteFile("Library\\Properties\\PublishProfiles\\win-arm64.pubxml", """
            <Project>
              <PropertyGroup>
                <Platform>ARM64</Platform>
                <RuntimeIdentifier>win-arm64</RuntimeIdentifier>
                <SelfContained>true</SelfContained>
              </PropertyGroup>
            </Project>
            """);
        var app = WriteApp("""
            <ItemGroup>
              <ProjectReference Include="Library\Library.csproj" />
            </ItemGroup>
            """);
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");

        var resolved = ResolveProfile(app, Options("arm64"));

        Assert.IsNull(
            resolved.PublishProfile,
            "a global PublishProfile must not be inferred when a referenced project could import it too");
    }

    [TestMethod]
    public void EffectivePublishProfileName_SuppressesInference()
    {
        var app = WriteApp();
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");
        var current = ProfileProperties(app, "win-anycpu.pubxml", imported: false, selfContained: false);
        current["PublishProfileName"] = "custom";

        var resolved = ProjectRunService.ResolvePublishProfileFallback(
            app,
            Options("arm64"),
            current,
            ProfileProperties(app, "win-arm64.pubxml", imported: true, selfContained: true));

        Assert.IsNull(resolved.PublishProfile);
    }

    [TestMethod]
    public void EffectivePublishProfileFullPath_SuppressesInference()
    {
        var app = WriteApp();
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");
        var current = ProfileProperties(app, "win-anycpu.pubxml", imported: false, selfContained: false);
        current["PublishProfileFullPath"] = Path.Join(_tempDirectory.FullName, "custom.pubxml");

        var resolved = ProjectRunService.ResolvePublishProfileFallback(
            app,
            Options("arm64"),
            current,
            ProfileProperties(app, "win-arm64.pubxml", imported: true, selfContained: true));

        Assert.IsNull(resolved.PublishProfile);
    }

    [TestMethod]
    public void EffectiveWebPublishProfileFile_SuppressesInference()
    {
        var app = WriteApp();
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");
        var current = ProfileProperties(app, "win-anycpu.pubxml", imported: false, selfContained: false);
        current["WebPublishProfileFile"] = Path.Join(_tempDirectory.FullName, "custom.pubxml");

        var resolved = ProjectRunService.ResolvePublishProfileFallback(
            app,
            Options("arm64"),
            current,
            ProfileProperties(app, "win-arm64.pubxml", imported: true, selfContained: true));

        Assert.IsNull(resolved.PublishProfile);
    }

    [TestMethod]
    public async Task SuccessfulRidOnlyBuild_DoesNotActivateFallbackProfile()
    {
        WriteFile("Library\\Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platform>AnyCPU</Platform>
              </PropertyGroup>
            </Project>
            """);
        var app = WriteApp("""
            <ItemGroup>
              <ProjectReference Include="Library\Library.csproj" />
            </ItemGroup>
            """);
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");

        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = ProfileEvaluationHandler(app),
        };
        var service = NewService(dotnet);
        var options = Options("arm64");

        var outcome = await service.BuildAndResolveAsync(app, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(1, dotnet.StreamingCalls.Count);
        Assert.IsFalse(
            dotnet.StreamingCalls[0].Contains("-p:PublishProfile=", StringComparison.Ordinal),
            "a successful RID-only build must retain its established build semantics");
        Assert.IsFalse(
            dotnet.StringInvocations.Last().Contains("-p:PublishProfile=", StringComparison.Ordinal),
            "evaluation must match the successful RID-only build");
    }

    [TestMethod]
    public async Task TrimmedFrameworkDependentBuild_SelectsProfileBeforeBuild()
    {
        WriteFile("Library\\Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platform>AnyCPU</Platform>
              </PropertyGroup>
            </Project>
            """);
        var app = WriteApp("""
            <ItemGroup>
              <ProjectReference Include="Library\Library.csproj" />
            </ItemGroup>
            """);
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");

        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = ProfileEvaluationHandler(app, publishTrimmed: true),
        };
        var service = NewService(dotnet);

        var outcome = await service.BuildAndResolveAsync(app, Options("arm64"), CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(1, dotnet.StreamingCalls.Count);
        StringAssert.Contains(dotnet.StreamingCalls[0], "-p:PublishProfile=win-arm64.pubxml");
        StringAssert.Contains(dotnet.StringInvocations.Last(), "-p:PublishProfile=win-arm64.pubxml");
    }

    [TestMethod]
    public async Task NativeAotBuild_DoesNotActivateProfile()
    {
        var app = WriteApp();
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");

        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = ProfileEvaluationHandler(
                app,
                publishTrimmed: true,
                publishAot: true),
        };
        var service = NewService(dotnet);

        var outcome = await service.BuildAndResolveAsync(app, Options("arm64"), CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(1, dotnet.StreamingCalls.Count);
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("-p:PublishProfile=", StringComparison.Ordinal));
        Assert.IsFalse(dotnet.StringInvocations.Last().Contains("-p:PublishProfile=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RequiredProfile_DoesNotLeakIntoSolutionSiblingRestore()
    {
        WriteFile("Library\\Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platform>AnyCPU</Platform>
              </PropertyGroup>
            </Project>
            """);
        var app = WriteApp("""
            <ItemGroup>
              <ProjectReference Include="Library\Library.csproj" />
            </ItemGroup>
            """);
        WriteFile("Server\\Server.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var solution = WriteFile("App.slnx", """
            <Solution>
              <Project Path="App.csproj" />
              <Project Path="Server/Server.csproj" />
            </Solution>
            """);
        WriteProfile("win-arm64.pubxml", "ARM64", "win-arm64");

        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = ProfileEvaluationHandler(app, publishTrimmed: true),
        };
        var service = NewService(dotnet);
        var options = Options("arm64") with { Solution = solution };

        var outcome = await service.BuildAndResolveAsync(app, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsFalse(dotnet.StringInvocations.Any(arguments =>
            arguments.StartsWith($"restore {solution.FullName}", StringComparison.Ordinal)));
        var siblingRestore = dotnet.StringInvocations.Single(arguments =>
            arguments.StartsWith("restore ", StringComparison.Ordinal)
            && arguments.Contains("Server.csproj", StringComparison.Ordinal));
        Assert.IsFalse(
            siblingRestore.Contains("-p:PublishProfile=", StringComparison.Ordinal),
            "the selected app's profile must not flow into an unrelated solution sibling");
        Assert.AreEqual(1, dotnet.StreamingCalls.Count);
        StringAssert.Contains(dotnet.StreamingCalls[0], "-p:PublishProfile=win-arm64.pubxml");
        Assert.IsFalse(
            dotnet.StreamingCalls[0].Contains("--no-restore", StringComparison.Ordinal),
            "the target still needs its profile-specific restore graph");
    }

    [TestMethod]
    public async Task ConditionalDeclarations_UseEffectiveConfigurationAndPlatform()
    {
        WriteFile("Library\\Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <Platform>AnyCPU</Platform>
              </PropertyGroup>
            </Project>
            """);
        var app = WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <Platforms>x64;ARM64</Platforms>
                <PublishTrimmed>true</PublishTrimmed>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <PublishProfile>debug-$(Platform.ToLower()).pubxml</PublishProfile>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <PublishProfile>release-$(Platform.ToLower()).pubxml</PublishProfile>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Library\Library.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteProfile("debug-arm64.pubxml", "ARM64", "win-arm64");
        WriteProfile("release-arm64.pubxml", "ARM64", "win-arm64");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = ProfileEvaluationHandler(
                app,
                publishTrimmed: true,
                currentProfile: "release-anycpu.pubxml",
                candidateProfile: "release-arm64.pubxml"),
        };
        var service = NewService(dotnet);

        var outcome = await service.BuildAndResolveAsync(app, Options("arm64"), CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        StringAssert.Contains(dotnet.StreamingCalls.Single(), "-p:PublishProfile=release-arm64.pubxml");
        Assert.IsFalse(dotnet.StreamingCalls.Single().Contains("debug-arm64", StringComparison.Ordinal));
    }

    private static ProjectRunOptions Options(string architecture, params string[] properties) =>
        new("Release", architecture, null, NoBuild: false, NoRestore: false, Properties: properties);

    private ProjectRunOptions ResolveProfile(
        FileInfo app,
        ProjectRunOptions options,
        string currentProfile = "win-anycpu.pubxml",
        string candidateProfile = "win-arm64.pubxml",
        bool candidateSelfContained = true) =>
        ProjectRunService.ResolvePublishProfileFallback(
            app,
            options,
            ProfileProperties(app, currentProfile, imported: false, selfContained: false),
            ProfileProperties(app, candidateProfile, imported: true, selfContained: candidateSelfContained));

    private Dictionary<string, string> ProfileProperties(
        FileInfo app,
        string publishProfile,
        bool imported,
        bool selfContained,
        bool publishTrimmed = true,
        bool publishAot = false)
    {
        var root = Path.Join(app.Directory!.FullName, "Properties", "PublishProfiles")
            + Path.DirectorySeparatorChar;
        var name = Path.GetFileNameWithoutExtension(publishProfile);
        var fullPath = Path.Join(root, name + ".pubxml");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetDir"] = _tempDirectory.FullName,
            ["RunCommand"] = string.Empty,
            ["WindowsPackageType"] = "MSIX",
            ["OutputType"] = "WinExe",
            ["WindowsAppSDKSelfContained"] = string.Empty,
            ["PublishTrimmed"] = publishTrimmed.ToString(),
            ["PublishAot"] = publishAot.ToString(),
            ["SelfContained"] = selfContained.ToString(),
            ["PublishProfile"] = publishProfile,
            ["PublishProfileName"] = name,
            ["PublishProfileFullPath"] = fullPath,
            ["WebPublishProfileFile"] = imported ? fullPath : string.Empty,
            ["PublishProfileImported"] = imported.ToString(),
            ["_PublishProfileRootFolder"] = root,
        };
    }

    private Func<string, (int ExitCode, string Output, string Error)> ProfileEvaluationHandler(
        FileInfo app,
        bool publishTrimmed = false,
        bool publishAot = false,
        string currentProfile = "win-anycpu.pubxml",
        string candidateProfile = "win-arm64.pubxml") =>
        arguments =>
        {
            var useCandidate = arguments.Contains("-p:Platform=ARM64", StringComparison.Ordinal)
                || arguments.Contains($"-p:PublishProfile={candidateProfile}", StringComparison.Ordinal);
            var properties = ProfileProperties(
                app,
                useCandidate ? candidateProfile : currentProfile,
                imported: useCandidate,
                selfContained: useCandidate,
                publishTrimmed,
                publishAot);
            return (0, PropertiesJson(properties), string.Empty);
        };

    private ProjectRunService NewService(FakeDotNetService dotnet)
    {
        var console = new TestConsole();
        _consoles.Add(console);
        return new(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            console,
            NullLogger<ProjectRunService>.Instance)
        {
            NativeTerminalGateOverrideForTests = () => false,
        };
    }

    private FileInfo WriteApp(string extra = "") =>
        WriteFile("App.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <Platforms>x86;x64;ARM64</Platforms>
                <PublishProfile>win-$(Platform.ToLower()).pubxml</PublishProfile>
              </PropertyGroup>
              {{extra}}
            </Project>
            """);

    private void WriteProfile(
        string name,
        string platform,
        string runtimeIdentifier,
        bool selfContained = true) =>
        WriteFile($"Properties\\PublishProfiles\\{name}", $$"""
            <Project>
              <PropertyGroup>
                <Platform>{{platform}}</Platform>
                <RuntimeIdentifier>{{runtimeIdentifier}}</RuntimeIdentifier>
                <SelfContained>{{selfContained}}</SelfContained>
              </PropertyGroup>
            </Project>
            """);

    private FileInfo WriteFile(string relativePath, string content)
    {
        var path = Path.Join(_tempDirectory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    private static string PropertiesJson(IReadOnlyDictionary<string, string> properties) =>
        System.Text.Json.JsonSerializer.Serialize(new { Properties = properties });
}
