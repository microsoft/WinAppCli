// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ProjectPublishServiceTests
{
    private DirectoryInfo _tempDirectory = null!;
    private FileInfo _project = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"ProjectPublishServiceTests_{Guid.NewGuid():N}"));
        _project = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));
        File.WriteAllText(
            _project.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
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
            // Best effort for test output held by a failed process.
        }
    }

    [TestMethod]
    public void PublishArguments_ForwardNoBuildAndAllPublishGlobals()
    {
        var options = new ProjectRunOptions(
            "Release",
            "arm64",
            "net10.0-windows10.0.26100.0",
            NoBuild: true,
            NoRestore: true,
            Properties: ["PublishProfile=Custom", @"PublishDir=C:\drop\app"],
            Platform: "ARM64");

        var arguments = ProjectRunService.BuildPublishPassArguments(
            _project,
            options,
            "minimal");

        CollectionAssert.Contains(arguments.ToArray(), "publish");
        CollectionAssert.Contains(arguments.ToArray(), "--no-build");
        CollectionAssert.Contains(arguments.ToArray(), "--no-restore");
        CollectionAssert.Contains(arguments.ToArray(), "win-arm64");
        CollectionAssert.Contains(arguments.ToArray(), "-p:PublishProfile=Custom");
        CollectionAssert.Contains(arguments.ToArray(), @"-p:PublishDir=C:\drop\app");
        CollectionAssert.Contains(arguments.ToArray(), "-p:Platform=ARM64");
    }

    [TestMethod]
    public async Task PreparePublish_ResolvesRelativePublishDirAndLaunchArtifactFromSameProperties()
    {
        var publishDirectory = Directory.CreateDirectory(
            Path.Combine(_tempDirectory.FullName, "custom", "publish"));
        var executable = new FileInfo(Path.Combine(publishDirectory.FullName, "App.exe"));
        File.WriteAllText(executable.FullName, "native fixture");

        var properties = Properties(
            ("TargetDir", _tempDirectory.FullName),
            ("PublishDir", Path.Combine("custom", "publish")),
            ("PublishAot", "false"),
            ("RuntimeIdentifier", "win-x64"),
            ("Platform", "x64"),
            ("AssemblyName", "App"),
            ("TargetName", "App"),
            ("TargetFileName", "App.dll"),
            ("WindowsPackageType", "None"),
            ("WindowsAppSDKSelfContained", "true"),
            ("OutputType", "WinExe"),
            ("ProjectAssetsFile", Path.Combine(_tempDirectory.FullName, "obj", "project.assets.json")),
            ("PublishProfile", "Custom"));

        var (service, dotnet, toolchain) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: true,
            Properties: ["PublishProfile=Custom"]);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual(0, outcome.ExitCode);
        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(publishDirectory.FullName, outcome.Resolution.PublishDirectory);
        Assert.AreEqual(executable.FullName, outcome.Resolution.SourceExecutable);
        Assert.AreEqual(executable.FullName, outcome.Resolution.RunCommand);
        Assert.AreEqual("Custom", outcome.Resolution.PublishProfile);
        Assert.AreEqual(0, toolchain.Calls.Count, "Non-AOT publish should not require the native linker preflight.");
        Assert.AreEqual(1, dotnet.ArgumentListInvocations.Count, "The publish pass should execute exactly once.");
        CollectionAssert.Contains(dotnet.ArgumentListInvocations[0].ToArray(), "-p:PublishProfile=Custom");
    }

    [TestMethod]
    public async Task PreparePublish_NoBuildStillInvokesDotnetPublishWithNoBuild()
    {
        var publishDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "publish"));
        File.WriteAllText(Path.Combine(publishDirectory.FullName, "App.exe"), "fixture");
        var properties = UnpackagedProperties(publishDirectory.FullName, publishAot: false);
        var (service, dotnet, _) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: true,
            NoRestore: true,
            Properties: []);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(1, dotnet.ArgumentListInvocations.Count);
        CollectionAssert.Contains(dotnet.ArgumentListInvocations[0].ToArray(), "--no-build");
    }

    [TestMethod]
    public async Task PreparePublish_DryRunNeverExecutesPublish()
    {
        var properties = UnpackagedProperties(
            Path.Combine(_tempDirectory.FullName, "not-created"),
            publishAot: false);
        var (service, dotnet, _) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: [],
            DryRun: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.IsFalse(outcome.Executed);
        Assert.AreEqual(true, outcome.Ready);
        Assert.AreEqual(0, dotnet.ArgumentListInvocations.Count, "Dry-run must not invoke dotnet publish or restore.");
    }

    [TestMethod]
    public async Task PreparePublish_DryRunRejectsNonExecutableProject()
    {
       var properties = Properties(
           ("TargetDir", _tempDirectory.FullName),
           ("PublishDir", Path.Combine(_tempDirectory.FullName, "publish")),
           ("PublishAot", "false"),
           ("RuntimeIdentifier", "win-x64"),
           ("AssemblyName", "Library"),
           ("WindowsPackageType", "None"),
           ("OutputType", "Library"));
       var (service, dotnet, _) = CreateService(properties);
       var options = new ProjectRunOptions(
           "Release",
           "x64",
           null,
           NoBuild: false,
           NoRestore: false,
           Properties: [],
           DryRun: true);

       var outcome = await service.PrepareAndResolveAsync(
           _project,
           options,
           ProjectPreparationOperation.Publish,
           CancellationToken.None);

       Assert.AreEqual(false, outcome.Ready);
       Assert.AreEqual("PublishPlanInvalid", outcome.ErrorCode);
       StringAssert.Contains(outcome.Error, "not a runnable project");
       Assert.AreEqual(0, dotnet.ArgumentListInvocations.Count);
    }

    [TestMethod]
    public async Task PrepareNativeAotDryRun_MissingRuntimePackIsIndeterminateWithRestoreCommand()
    {
        var assets = Path.Combine(_tempDirectory.FullName, "obj", "project.assets.json");
        var properties = UnpackagedProperties(
            Path.Combine(_tempDirectory.FullName, "publish"),
            publishAot: true,
            projectAssetsFile: assets);
        var (service, dotnet, toolchain) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: [],
            DryRun: true,
            VerifyNativeAot: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.IsNull(outcome.Ready);
        Assert.AreEqual("RestoreRequired", outcome.Reason);
        StringAssert.Contains(outcome.SuggestedCommand, "dotnet restore");
        StringAssert.Contains(outcome.SuggestedCommand, "win-x64");
        StringAssert.Contains(outcome.SuggestedCommand, "PublishAot=true");
        Assert.AreEqual(1, toolchain.Calls.Count);
        Assert.AreEqual(0, dotnet.ArgumentListInvocations.Count);
    }

    [TestMethod]
    public async Task PrepareNativeAotDryRun_Arm64UsesArm64ToolchainAndRestoredPack()
    {
        var assets = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "obj"));
        var assetsFile = Path.Combine(assets.FullName, "project.assets.json");
        var packageFolder = CreateNativeAotPackages("win-arm64", "10.0.0");
        WriteAssetsFile(
            assetsFile,
            packageFolder,
            includeNativeAotGraph: true,
            rid: "win-arm64",
            version: "10.0.0");
        var properties = UnpackagedProperties(
            Path.Combine(_tempDirectory.FullName, "publish"),
            publishAot: true,
            projectAssetsFile: assetsFile,
            runtimeIdentifier: "win-arm64",
            platform: "ARM64");
        var (service, _, toolchain) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "arm64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: [],
            DryRun: true,
            VerifyNativeAot: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual(true, outcome.Ready);
        Assert.AreEqual(Architecture.Arm64, toolchain.Calls.Single().TargetArchitecture);
        Assert.IsTrue(toolchain.Calls.Single().RequireLinker);
        Assert.IsTrue(toolchain.Calls.Single().RequireWindowsSdk);
    }

    [TestMethod]
    public async Task PrepareNativeAotDryRun_RemainsReadyWhenAssetsGraphIsRewritten()
    {
        var assetsDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "obj"));
        var assetsFile = Path.Combine(assetsDirectory.FullName, "project.assets.json");
        var packageFolder = CreateNativeAotPackages("win-x64", "10.0.0");
        WriteAssetsFile(
            assetsFile,
            packageFolder,
            includeNativeAotGraph: true,
            rid: "win-x64",
            version: "10.0.0");
        var properties = UnpackagedProperties(
            Path.Combine(_tempDirectory.FullName, "publish"),
            publishAot: true,
            projectAssetsFile: assetsFile);
        var (service, _, _) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: [],
            DryRun: true,
            VerifyNativeAot: true);

        var beforeRewrite = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        WriteAssetsFile(
            assetsFile,
            packageFolder,
            includeNativeAotGraph: false,
            rid: "win-x64",
            version: "10.0.0");
        var afterRewrite = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual(true, beforeRewrite.Ready);
        Assert.AreEqual(true, afterRewrite.Ready,
            "Readiness must use completed exact-version packages, not a transient PackageDownload entry.");
    }

    [TestMethod]
    public async Task PrepareNativeAot_MissingToolchainFailsBeforePublish()
    {
        var properties = UnpackagedProperties(
            Path.Combine(_tempDirectory.FullName, "publish"),
            publishAot: true);
        var (service, dotnet, toolchain) = CreateService(properties);
        toolchain.Result = new WindowsNativeToolchainResolution(
            null,
            "LinkerNotFound",
            "The x64 linker was not found.",
            "Microsoft.VisualStudio.Component.VC.Tools.x86.x64");
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: [],
            VerifyNativeAot: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual("LinkerNotFound", outcome.ErrorCode);
        StringAssert.Contains(outcome.Error, "Microsoft.VisualStudio.Component.VC.Tools.x86.x64");
        Assert.AreEqual(0, dotnet.ArgumentListInvocations.Count, "Toolchain failure must happen before publish.");
    }

    [TestMethod]
    public async Task PrepareNativeAot_PublishDiagnosticsHaveDistinctFailure()
    {
       var properties = UnpackagedProperties(
           Path.Combine(_tempDirectory.FullName, "publish"),
           publishAot: true);
       var (service, dotnet, _) = CreateService(properties);
       dotnet.RunDotnetArgumentListHandler = _ =>
           (0, "warning IL2026: Native code analysis found an incompatible call", string.Empty);
       var options = new ProjectRunOptions(
           "Release",
           "x64",
           null,
           NoBuild: false,
           NoRestore: true,
           Properties: [],
           VerifyNativeAot: true);

       var outcome = await service.PrepareAndResolveAsync(
           _project,
           options,
           ProjectPreparationOperation.Publish,
           CancellationToken.None);

       Assert.AreEqual("NativeAotPublishDiagnostics", outcome.ErrorCode);
       StringAssert.Contains(outcome.Error, "IL2026");
    }

    [TestMethod]
    public async Task PrepareNativeAot_UnrestoredImportedPublishAotDefersRequirementUntilPostPublishEvaluation()
    {
       File.WriteAllText(
           Path.Combine(_tempDirectory.FullName, "Directory.Build.props"),
           """
           <Project>
             <PropertyGroup>
               <PublishAot>true</PublishAot>
             </PropertyGroup>
           </Project>
           """);
       var publishDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "publish"));
       File.WriteAllText(Path.Combine(publishDirectory.FullName, "App.exe"), "native fixture");
       var properties = UnpackagedProperties(publishDirectory.FullName, publishAot: true);
       var (service, dotnet, toolchain) = CreateService(properties);
       var evaluateCount = 0;
       dotnet.RunDotnetCommandHandler = arguments =>
       {
           if (arguments == "--version")
           {
               return (0, "10.0.303", string.Empty);
           }

           evaluateCount++;
           return evaluateCount == 1
               ? (1, string.Empty, "NETSDK1004: project.assets.json was not found")
               : (0, properties, string.Empty);
       };
       var options = new ProjectRunOptions(
           "Release",
           "x64",
           null,
           NoBuild: false,
           NoRestore: true,
           Properties: [],
           VerifyNativeAot: true);

       var outcome = await service.PrepareAndResolveAsync(
           _project,
           options,
           ProjectPreparationOperation.Publish,
           CancellationToken.None);

       Assert.AreEqual(0, outcome.ExitCode, outcome.Error);
       Assert.IsNotNull(outcome.Resolution);
       Assert.IsTrue(outcome.Resolution.PublishAot);
       Assert.AreEqual(1, dotnet.ArgumentListInvocations.Count, "The real publish must run despite the indeterminate preflight evaluation.");
       Assert.AreEqual(1, toolchain.Calls.Count);
    }

    [TestMethod]
    public async Task PreparePackagedPublish_UsesEvaluatedGeneratedManifestNotPublishSourceManifest()
    {
        var targetDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "target"));
        var publishDirectory = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "publish"));
        var generatedManifest = Path.Combine(targetDirectory.FullName, "AppxManifest.xml");
        File.WriteAllText(generatedManifest, Manifest("Generated.exe"));
        File.WriteAllText(Path.Combine(publishDirectory.FullName, "Generated.exe"), "fixture");
        File.WriteAllText(Path.Combine(publishDirectory.FullName, "Package.appxmanifest"), Manifest("Wrong.exe"));

        var properties = Properties(
            ("TargetDir", targetDirectory.FullName),
            ("PublishDir", publishDirectory.FullName),
            ("PublishAot", "false"),
            ("RuntimeIdentifier", "win-x64"),
            ("AssemblyName", "App"),
            ("TargetName", "App"),
            ("FinalAppxManifestName", "AppxManifest.xml"),
            ("WindowsPackageType", "MSIX"),
            ("WindowsAppSDKSelfContained", "true"),
            ("OutputType", "WinExe"));
        var (service, _, _) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: true,
            Properties: []);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(generatedManifest, outcome.Resolution.FinalAppxManifestPath);
        Assert.AreEqual(
            Path.Combine(publishDirectory.FullName, "Generated.exe"),
            outcome.Resolution.SourceExecutable);
    }

    private static (ProjectRunService Service, FakeDotNetService Dotnet, FakeWindowsNativeToolchainResolver Toolchain)
        CreateService(string propertiesJson)
    {
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = arguments =>
                arguments == "--version"
                    ? (0, "10.0.303", string.Empty)
                    : (0, propertiesJson, string.Empty),
            RunDotnetArgumentListHandler = _ => (0, "publish succeeded", string.Empty),
        };
        var toolchain = new FakeWindowsNativeToolchainResolver();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(TextWriter.Null),
        });
        var service = new ProjectRunService(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            console,
            NullLogger<ProjectRunService>.Instance,
            toolchain);
        return (service, dotnet, toolchain);
    }

    private static string UnpackagedProperties(
        string publishDirectory,
        bool publishAot,
        string? projectAssetsFile = null,
        string runtimeIdentifier = "win-x64",
        string platform = "x64") =>
        Properties(
            ("TargetDir", publishDirectory),
            ("PublishDir", publishDirectory),
            ("PublishAot", publishAot ? "true" : "false"),
            ("RuntimeIdentifier", runtimeIdentifier),
            ("Platform", platform),
            ("AssemblyName", "App"),
            ("TargetName", "App"),
            ("TargetFileName", "App.dll"),
            ("WindowsPackageType", "None"),
            ("WindowsAppSDKSelfContained", "true"),
            ("OutputType", "WinExe"),
            ("ProjectAssetsFile", projectAssetsFile ?? string.Empty),
            ("BundledNETCoreAppPackageVersion", "10.0.0"));

    private string CreateNativeAotPackages(string rid, string version)
    {
        var packageFolder = Path.Combine(_tempDirectory.FullName, "packages");
        foreach (var normalizedPackageId in new[]
                 {
                     $"Microsoft.NETCore.App.Runtime.NativeAOT.{rid}",
                     $"runtime.{rid}.Microsoft.DotNet.ILCompiler",
                 }.Select(packageId => packageId.ToLowerInvariant()))
        {
            var versionDirectory = Path.Combine(
                packageFolder,
                normalizedPackageId,
                version);
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(Path.Combine(versionDirectory, ".nupkg.metadata"), "{}");
        }

        return packageFolder;
    }

    private static void WriteAssetsFile(
        string assetsFile,
        string packageFolder,
        bool includeNativeAotGraph,
        string rid,
        string version)
    {
        var libraries = includeNativeAotGraph
            ? new Dictionary<string, object>
            {
                [$"Microsoft.NETCore.App.Runtime.NativeAOT.{rid}/{version}"] = new { },
                [$"runtime.{rid}.Microsoft.DotNet.ILCompiler/{version}"] = new { },
            }
            : new Dictionary<string, object>
            {
                ["Some.Unrelated.Package/1.0.0"] = new { },
            };
        File.WriteAllText(
            assetsFile,
            JsonSerializer.Serialize(new
            {
                libraries,
                packageFolders = new Dictionary<string, object>
                {
                    [packageFolder + Path.DirectorySeparatorChar] = new { },
                },
            }));
    }

    private static string Properties(params (string Name, string Value)[] values) =>
        JsonSerializer.Serialize(new
        {
            Properties = values.ToDictionary(pair => pair.Name, pair => pair.Value),
        });

    private static string Manifest(string executable) =>
        $"""
         <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
           <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" />
           <Applications>
             <Application Id="App" Executable="{executable}" EntryPoint="Windows.FullTrustApplication" />
           </Applications>
         </Package>
         """;
}
