// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
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
            Path.GetFullPath(
                $"ProjectPublishServiceTests_{Guid.NewGuid():N}",
                Path.GetTempPath()));
        _project = new FileInfo(TempPath("App.csproj"));
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
            TempPath("custom", "publish"));
        var executable = new FileInfo(ChildPath(publishDirectory.FullName, "App.exe"));
        File.WriteAllText(executable.FullName, "native fixture");

        var properties = Properties(
            ("TargetDir", _tempDirectory.FullName),
            ("PublishDir", RelativePath("custom", "publish")),
            ("PublishAot", "false"),
            ("RuntimeIdentifier", "win-x64"),
            ("Platform", "x64"),
            ("AssemblyName", "App"),
            ("TargetName", "App"),
            ("TargetFileName", "App.dll"),
            ("WindowsPackageType", "None"),
            ("WindowsAppSDKSelfContained", "true"),
            ("OutputType", "WinExe"),
            ("ProjectAssetsFile", TempPath("obj", "project.assets.json")),
            ("PublishProfile", "Custom"));

        var (service, dotnet) = CreateService(properties);
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
        Assert.AreEqual(1, dotnet.ArgumentListInvocations.Count, "The publish pass should execute exactly once.");
        CollectionAssert.Contains(dotnet.ArgumentListInvocations[0].ToArray(), "-p:PublishProfile=Custom");
    }

    [TestMethod]
    public async Task PreparePublish_NoBuildStillInvokesDotnetPublishWithNoBuild()
    {
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "fixture");
        var properties = UnpackagedProperties(publishDirectory.FullName, publishAot: false);
        var (service, dotnet) = CreateService(properties);
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
    public async Task PreparePublish_DefaultOutputPrintsRedactedInvocation()
    {
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "fixture");
        var properties = UnpackagedProperties(publishDirectory.FullName, publishAot: false);
        using var output = new StringWriter();
        var (service, _) = CreateService(properties, output: output, logLevel: LogLevel.Information);
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: true,
            Properties: ["PackageCertificatePassword=hunter2"]);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual(0, outcome.ExitCode, outcome.Error);
        StringAssert.Contains(output.ToString(), "dotnet publish");
        StringAssert.Contains(output.ToString(), "PackageCertificatePassword=***");
        StringAssert.Contains(output.ToString(), "Publish completed");
        Assert.IsFalse(output.ToString().Contains("Native AOT publish completed", StringComparison.Ordinal));
        Assert.IsFalse(output.ToString().Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PrepareNativeAot_DefaultOutputUsesNativeAotCompletionLabel()
    {
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "native fixture");
        var properties = UnpackagedProperties(publishDirectory.FullName, publishAot: true);
        using var output = new StringWriter();
        var (service, _) = CreateService(properties, output: output, logLevel: LogLevel.Information);
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
        StringAssert.Contains(output.ToString(), "Native AOT publish completed");
    }

    [TestMethod]
    public async Task PreparePublish_RidSplitGraphPreservesRuntimeIdentifierOmission()
    {
        WriteRidSplitGraph();
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "fixture");
        var (service, dotnet) = CreateService(
            UnpackagedProperties(publishDirectory.FullName, publishAot: false));
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

        Assert.AreEqual(0, outcome.ExitCode, outcome.Error);
        var publishArguments = dotnet.ArgumentListInvocations.Single();
        Assert.IsFalse(publishArguments.Contains("-r"));
        Assert.IsFalse(publishArguments.Contains("win-x64"));
        CollectionAssert.Contains(publishArguments.ToArray(), "-p:Platform=x64");
    }

    [TestMethod]
    public async Task PrepareNativeAot_RidSplitGraphRestoresAndPublishesWithRuntimeIdentifier()
    {
        WriteRidSplitGraph();
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "fixture");
        var shim = new FakeCsWinRTMetadataShimService
        {
            WindowsSdkAbsent = true,
        };
        shim.FolderSequence.Enqueue(null);
        shim.FolderSequence.Enqueue(null);
        shim.FolderSequence.Enqueue(TempPath("shim"));
        var (service, dotnet) = CreateService(
            UnpackagedProperties(publishDirectory.FullName, publishAot: true),
            shim);
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

        Assert.AreEqual(0, outcome.ExitCode, outcome.Error);
        var restore = dotnet.StringInvocations.Single(invocation =>
            invocation.StartsWith("restore ", StringComparison.Ordinal));
        StringAssert.Contains(restore, "-r win-x64");
        var publish = dotnet.ArgumentListInvocations.Single();
        CollectionAssert.Contains(publish.ToArray(), "-r");
        CollectionAssert.Contains(publish.ToArray(), "win-x64");
        CollectionAssert.Contains(publish.ToArray(), "--no-restore");
    }

    [TestMethod]
    public async Task PreparePublishDryRun_RidSplitGraphEvaluatesWithoutRuntimeIdentifier()
    {
        WriteRidSplitGraph();
        var (service, dotnet) = CreateService(
            UnpackagedProperties(TempPath("publish"), publishAot: false));
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: true,
            Properties: [],
            DryRun: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual(true, outcome.Ready);
        var evaluations = dotnet.StringInvocations
            .Where(invocation => invocation.StartsWith("msbuild ", StringComparison.Ordinal))
            .ToList();
        Assert.HasCount(2, evaluations);
        StringAssert.Contains(evaluations[0], "-p:RuntimeIdentifier=win-x64");
        Assert.IsFalse(evaluations[1].Contains("-p:RuntimeIdentifier=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PreparePublish_DryRunNeverExecutesPublish()
    {
        var properties = UnpackagedProperties(
            TempPath("not-created"),
            publishAot: false);
        var (service, dotnet) = CreateService(properties);
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
           ("PublishDir", TempPath("publish")),
           ("PublishAot", "false"),
           ("RuntimeIdentifier", "win-x64"),
           ("AssemblyName", "Library"),
           ("WindowsPackageType", "None"),
           ("OutputType", "Library"));
       var (service, dotnet) = CreateService(properties);
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
        var assets = TempPath("obj", "project.assets.json");
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true,
            projectAssetsFile: assets);
        var (service, dotnet) = CreateService(properties);
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
        Assert.AreEqual(0, dotnet.ArgumentListInvocations.Count);
    }

    [TestMethod]
    public async Task PrepareNativeAotDryRun_RedactsSecretPropertiesFromRestoreSuggestion()
    {
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true);
        var (service, dotnet) = CreateService(properties);
        dotnet.RunDotnetCommandHandler = arguments =>
            arguments == "--version"
                ? (0, "10.0.303", string.Empty)
                : (1, string.Empty, "project.assets.json was not found");
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: ["PublishAot=true", "PackageCertificatePassword=hunter2"],
            DryRun: true,
            VerifyNativeAot: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual("RestoreRequired", outcome.ErrorCode);
        StringAssert.Contains(outcome.SuggestedCommand, "-p:PackageCertificatePassword=***");
        Assert.IsFalse(outcome.SuggestedCommand.Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PrepareBuildDryRun_RedactsSecretPropertiesFromRestoreSuggestion()
    {
        var (service, dotnet) = CreateService(
            UnpackagedProperties(TempPath("publish"), publishAot: false));
        dotnet.RunDotnetCommandHandler = _ =>
            (1, string.Empty, "project.assets.json was not found");
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: ["PackageCertificatePassword=hunter2"],
            DryRun: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Build,
            CancellationToken.None);

        Assert.AreEqual("RestoreRequired", outcome.ErrorCode);
        StringAssert.Contains(outcome.SuggestedCommand, "-p:PackageCertificatePassword=***");
        Assert.IsFalse(outcome.SuggestedCommand.Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PrepareNativeAotDryRun_Arm64UsesRestoredPack()
    {
        var assets = _tempDirectory.CreateSubdirectory("obj");
        var assetsFile = ChildPath(assets.FullName, "project.assets.json");
        var packageFolder = CreateNativeAotPackages("win-arm64", "10.0.0");
        WriteAssetsFile(
            assetsFile,
            packageFolder,
            includeNativeAotGraph: true,
            rid: "win-arm64",
            version: "10.0.0");
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true,
            projectAssetsFile: assetsFile,
            runtimeIdentifier: "win-arm64",
            platform: "ARM64");
        var (service, _) = CreateService(properties);
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
        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual("arm64", outcome.Resolution.Architecture);
        Assert.AreEqual("win-arm64", outcome.Resolution.RuntimeIdentifier);
    }

    [TestMethod]
    public async Task PrepareNativeAot_X86IsRejectedBeforePublish()
    {
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true,
            runtimeIdentifier: "win-x86",
            platform: "x86");
        var (service, dotnet) = CreateService(properties);
        var options = new ProjectRunOptions(
            "Release",
            "x86",
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

        Assert.AreEqual("UnsupportedNativeAotArchitecture", outcome.ErrorCode);
        Assert.IsFalse(outcome.Executed);
        Assert.AreEqual(0, dotnet.ArgumentListInvocations.Count);
    }

    [TestMethod]
    public async Task PrepareNativeAotDryRun_RemainsReadyWhenAssetsGraphIsRewritten()
    {
        var assetsDirectory = _tempDirectory.CreateSubdirectory("obj");
        var assetsFile = ChildPath(assetsDirectory.FullName, "project.assets.json");
        var packageFolder = CreateNativeAotPackages("win-x64", "10.0.0");
        WriteAssetsFile(
            assetsFile,
            packageFolder,
            includeNativeAotGraph: true,
            rid: "win-x64",
            version: "10.0.0");
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true,
            projectAssetsFile: assetsFile);
        var (service, _) = CreateService(properties);
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
    public async Task PrepareNativeAot_DotnetPublishFailurePropagatesOriginalExitCode()
    {
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true);
        var (service, dotnet) = CreateService(properties);
        dotnet.RunDotnetArgumentListHandler = _ =>
            (87, string.Empty, "Native AOT toolchain prerequisite missing.");
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

        Assert.AreEqual("PublishFailed", outcome.ErrorCode);
        Assert.AreEqual(87, outcome.ExitCode);
        Assert.IsTrue(outcome.Executed);
        Assert.AreEqual(1, dotnet.ArgumentListInvocations.Count);
        Assert.IsNull(dotnet.ArgumentListEnvironmentInvocations.Single());
    }

    [TestMethod]
    public async Task PrepareNativeAot_GlobalPublishAotFailureRecommendsProjectProperty()
    {
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true);
        var (service, dotnet) = CreateService(properties);
        dotnet.RunDotnetArgumentListHandler = _ =>
            (1, string.Empty, "error NETSDK1207: Ahead-of-time compilation is not supported for netstandard2.0.");
        var options = new ProjectRunOptions(
            "Release",
            "x64",
            null,
            NoBuild: false,
            NoRestore: false,
            Properties: ["PublishAot=true"],
            VerifyNativeAot: true);

        var outcome = await service.PrepareAndResolveAsync(
            _project,
            options,
            ProjectPreparationOperation.Publish,
            CancellationToken.None);

        Assert.AreEqual("PublishFailed", outcome.ErrorCode);
        StringAssert.Contains(outcome.Error, "global MSBuild property");
        StringAssert.Contains(outcome.Error, "runnable app project");
    }

    [TestMethod]
    public async Task PrepareNativeAot_InstalledVsWhereMissingFromPath_IsAddedForPublishOnly()
    {
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "native fixture");
        var properties = UnpackagedProperties(publishDirectory.FullName, publishAot: true);
        var (service, dotnet) = CreateService(properties);
        service.NativeAotToolchainSetupOverrideForTests = static () =>
            new ProjectRunService.NativeAotToolchainSetup(
                @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe",
                AddedToPath: true,
                new Dictionary<string, string>
                {
                    ["PATH"] = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer;C:\Windows",
                });
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
        var environment = dotnet.ArgumentListEnvironmentInvocations.Single();
        Assert.IsNotNull(environment);
        Assert.AreEqual(
            @"C:\Program Files (x86)\Microsoft Visual Studio\Installer;C:\Windows",
            environment["PATH"]);
    }

    [TestMethod]
    public async Task PrepareNativeAot_VsWhereFailureExplainsPathAndStandardLocation()
    {
        var properties = UnpackagedProperties(
            TempPath("publish"),
            publishAot: true);
        var (service, dotnet) = CreateService(properties);
        var standardPath = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
        service.NativeAotToolchainSetupOverrideForTests = () =>
            new ProjectRunService.NativeAotToolchainSetup(
                VsWherePath: null,
                standardPath,
                AddedToPath: false,
                EnvironmentOverrides: null);
        dotnet.RunDotnetArgumentListHandler = _ =>
            (123, string.Empty, "'vswhere.exe' is not recognized as an internal or external command.");
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

        Assert.AreEqual("PublishFailed", outcome.ErrorCode);
        StringAssert.Contains(outcome.Error, "vswhere.exe was not found on PATH");
        StringAssert.Contains(outcome.Error, standardPath);
        StringAssert.Contains(outcome.Error, "Desktop development with C++");
    }

    [TestMethod]
    public void ResolveNativeAotToolchainSetup_InstalledVsWhereMissingFromPath_AddsOnlyChildPath()
    {
        var installerDirectory = _tempDirectory.CreateSubdirectory("installer");
        var vsWhere = Path.Combine(installerDirectory.FullName, "vswhere.exe");
        File.WriteAllText(vsWhere, "fixture");
        var inheritedPath = Path.Combine(_tempDirectory.FullName, "existing");

        var setup = ProjectRunService.ResolveNativeAotToolchainSetup(inheritedPath, vsWhere);

        Assert.IsTrue(setup.AddedToPath);
        Assert.AreEqual(vsWhere, setup.VsWherePath);
        Assert.IsNotNull(setup.EnvironmentOverrides);
        Assert.AreEqual(
            $"{installerDirectory.FullName}{Path.PathSeparator}{inheritedPath}",
            setup.EnvironmentOverrides["PATH"]);
    }

    [TestMethod]
    public void ResolveNativeAotToolchainSetup_VsWhereAlreadyOnPath_DoesNotOverrideEnvironment()
    {
        var pathDirectory = _tempDirectory.CreateSubdirectory("path");
        var vsWhere = Path.Combine(pathDirectory.FullName, "vswhere.exe");
        File.WriteAllText(vsWhere, "fixture");

        var setup = ProjectRunService.ResolveNativeAotToolchainSetup(
            pathDirectory.FullName,
            TempPath("missing", "vswhere.exe"));

        Assert.IsFalse(setup.AddedToPath);
        Assert.AreEqual(vsWhere, setup.VsWherePath);
        Assert.IsNull(setup.EnvironmentOverrides);
    }

    [TestMethod]
    public void ResolveNativeAotToolchainSetup_VsWhereMissing_ExplainsBothLocations()
    {
        var missing = TempPath("missing", "vswhere.exe");

        var setup = ProjectRunService.ResolveNativeAotToolchainSetup(
            TempPath("empty-path"),
            missing);

        Assert.IsNull(setup.VsWherePath);
        Assert.AreEqual(missing, setup.StandardVsWherePath);
        Assert.IsFalse(setup.AddedToPath);
        Assert.IsNull(setup.EnvironmentOverrides);
    }

    [TestMethod]
    public async Task PrepareNativeAot_PublishWarningsDoNotBlockVerification()
    {
       var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
       File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "native fixture");
       var properties = UnpackagedProperties(
           publishDirectory.FullName,
           publishAot: true);
       var (service, dotnet) = CreateService(properties);
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

       Assert.AreEqual(0, outcome.ExitCode, outcome.Error);
       Assert.IsNotNull(outcome.Resolution);
       Assert.IsTrue(outcome.Resolution.PublishAot);
    }

    [TestMethod]
    public async Task PrepareNativeAot_UnrestoredImportedPublishAotDefersRequirementUntilPostPublishEvaluation()
    {
       File.WriteAllText(
           TempPath("Directory.Build.props"),
           """
           <Project>
             <PropertyGroup>
               <PublishAot>true</PublishAot>
             </PropertyGroup>
           </Project>
           """);
       var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
       File.WriteAllText(ChildPath(publishDirectory.FullName, "App.exe"), "native fixture");
       var properties = UnpackagedProperties(publishDirectory.FullName, publishAot: true);
       var (service, dotnet) = CreateService(properties);
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
    }

    [TestMethod]
    public async Task PreparePackagedPublish_UsesEvaluatedGeneratedManifestNotPublishSourceManifest()
    {
        var targetDirectory = _tempDirectory.CreateSubdirectory("target");
        var publishDirectory = _tempDirectory.CreateSubdirectory("publish");
        var generatedManifest = ChildPath(targetDirectory.FullName, "AppxManifest.xml");
        File.WriteAllText(generatedManifest, Manifest("Generated.exe"));
        File.WriteAllText(ChildPath(publishDirectory.FullName, "Generated.exe"), "fixture");
        File.WriteAllText(ChildPath(publishDirectory.FullName, "Package.appxmanifest"), Manifest("Wrong.exe"));

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
        var (service, _) = CreateService(properties);
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
            ChildPath(publishDirectory.FullName, "Generated.exe"),
            outcome.Resolution.SourceExecutable);
    }

    private static (ProjectRunService Service, FakeDotNetService Dotnet)
        CreateService(
            string propertiesJson,
            FakeCsWinRTMetadataShimService? shim = null,
            TextWriter? output = null,
            LogLevel? logLevel = null)
    {
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = arguments =>
                arguments == "--version"
                    ? (0, "10.0.303", string.Empty)
                    : (0, propertiesJson, string.Empty),
            RunDotnetArgumentListHandler = _ => (0, "publish succeeded", string.Empty),
        };
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output ?? TextWriter.Null),
        });
        var service = new ProjectRunService(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            shim ?? new FakeCsWinRTMetadataShimService(),
            console,
            logLevel is null
                ? NullLogger<ProjectRunService>.Instance
                : new LevelLogger<ProjectRunService>(logLevel.Value));
        service.NativeAotToolchainSetupOverrideForTests = static () =>
            new ProjectRunService.NativeAotToolchainSetup(
                "vswhere.exe",
                "vswhere.exe",
                AddedToPath: false,
                EnvironmentOverrides: null);
        return (service, dotnet);
    }

    private void WriteRidSplitGraph()
    {
        File.WriteAllText(
            ChildPath(_tempDirectory.FullName, "Shared.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <Platforms>AnyCPU;x64;ARM64</Platforms>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            ChildPath(_tempDirectory.FullName, "Middle.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <Platforms>AnyCPU;x64;ARM64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj" GlobalPropertiesToRemove="RuntimeIdentifier" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            _project.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
                <Platforms>x86;x64;ARM64</Platforms>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Shared.csproj" />
                <ProjectReference Include="Middle.csproj" />
              </ItemGroup>
            </Project>
            """);
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
        var packageFolder = _tempDirectory.CreateSubdirectory("packages").FullName;
        var versionDirectories = new[]
        {
            $"Microsoft.NETCore.App.Runtime.NativeAOT.{rid}",
            $"runtime.{rid}.Microsoft.DotNet.ILCompiler",
        }.Select(packageId => Path.GetFullPath(
            $"{packageId.ToLowerInvariant()}{Path.DirectorySeparatorChar}{version}",
            packageFolder));
        foreach (var versionDirectory in versionDirectories)
        {
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(Path.GetFullPath(".nupkg.metadata", versionDirectory), "{}");
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

    private string TempPath(params string[] segments) =>
        ChildPath(_tempDirectory.FullName, segments);

    private static string ChildPath(string root, params string[] segments) =>
        Path.GetFullPath(RelativePath(segments), root);

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
}
