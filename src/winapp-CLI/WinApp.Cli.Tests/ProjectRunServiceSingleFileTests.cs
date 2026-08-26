// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Single-file-mode tests for <see cref="ProjectRunService"/>: classifying a <c>.cs</c> input, and
/// building the mirrored build/evaluate argument sets a .NET file-based app needs.
/// </summary>
[TestClass]
public class ProjectRunServiceSingleFileTests : IDisposable
{
    private DirectoryInfo _tempDir = null!;
    private ProjectRunService _service = null!;
    private TestConsole _testConsole = null!;

    /// <summary>Releases the console the service writes to. MSTest disposes the instance after each test.</summary>
    public void Dispose()
    {
        _testConsole?.Dispose();
        GC.SuppressFinalize(this);
    }

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Directory.CreateTempSubdirectory("winapp_sfrun_");
        _testConsole = new TestConsole();
        var dotnet = new FakeDotNetService();
        _service = new ProjectRunService(
            dotnet,
            new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet),
            new FakeCsWinRTMetadataShimService(),
            _testConsole,
            NullLogger<ProjectRunService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDir?.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle must not fail the test run.
        }
    }

    private FileInfo WriteSingleFile(string name = "counter.cs")
    {
        var path = Path.Join(_tempDir.FullName, name);
        File.WriteAllText(path, "Console.WriteLine(\"hi\");");
        return new FileInfo(path);
    }

    #region Input resolution

    [TestMethod]
    public async Task ResolveInput_CsFile_ResolvesToSingleFileMode()
    {
        var singleFile = WriteSingleFile();

        var resolution = await _service.ResolveInputAsync(singleFile, TestContext.CancellationToken);

        Assert.AreEqual(WinAppRunMode.SingleFile, resolution.Mode);
        Assert.AreEqual(singleFile.FullName, resolution.SingleFile!.FullName);
        Assert.IsNull(resolution.Csproj, "A file-based app has no .csproj");
        Assert.AreEqual(_tempDir.FullName, resolution.ProjectDirectory.FullName,
            "MSBuildProjectDirectory for a file-based app is the .cs file's own directory");
    }

    [TestMethod]
    public async Task ResolveInput_CsFileWithProjectSelector_Errors()
    {
        var singleFile = WriteSingleFile();

        var exception = await Assert.ThrowsExactlyAsync<ProjectRunException>(async () =>
            await _service.ResolveInputAsync(singleFile, TestContext.CancellationToken, projectSelector: "MyApp"));

        StringAssert.Contains(exception.Message, "--project");
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryContainingOnlyACsFile_StaysInFolderMode()
    {
        // Single-file mode is reachable ONLY from an explicitly-typed .cs path. Inferring it from a
        // directory would change how existing build-output folders are classified.
        WriteSingleFile();

        var resolution = await _service.ResolveInputAsync(_tempDir, TestContext.CancellationToken);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode);
        Assert.IsNull(resolution.SingleFile);
    }

    [TestMethod]
    public async Task ResolveInput_UnsupportedFileType_MentionsCsInTheGuidance()
    {
        var path = Path.Join(_tempDir.FullName, "notes.txt");
        File.WriteAllText(path, "hello");

        var exception = await Assert.ThrowsExactlyAsync<ProjectRunException>(async () =>
            await _service.ResolveInputAsync(new FileInfo(path), TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, ".cs file-based app");
    }

    #endregion

    #region Argument construction

    private static SingleFileRunOptions Options(
        string configuration = "Debug",
        bool noBuild = false,
        bool noRestore = false,
        string? injectedRid = null,
        params string[] properties) =>
        new(configuration, "x64", ArchitectureIsExplicit: false, noBuild, noRestore, properties)
        {
            InjectedRuntimeIdentifier = injectedRid,
        };

    [TestMethod]
    public void BuildPassArguments_UseDotnetBuildWithConfigurationOnly()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(), "minimal");

        StringAssert.StartsWith(args, "build ");
        StringAssert.Contains(args, singleFile.FullName);
        StringAssert.Contains(args, "-c Debug");
        StringAssert.Contains(args, "-tl:off");
    }

    [TestMethod]
    public void BuildPassArguments_NeverInjectARuntimeIdentifierOrPlatform()
    {
        // A file-based app declares its own Platform via #:property. Injecting -r/-p:Platform would move
        // the output away from the path the evaluate pass reads back, desynchronizing the two passes.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(), "minimal");

        Assert.IsFalse(args.Contains("-r win-", StringComparison.OrdinalIgnoreCase), $"Unexpected RID in: {args}");
        Assert.IsFalse(args.Contains("-p:Platform", StringComparison.OrdinalIgnoreCase), $"Unexpected Platform in: {args}");
    }

    [TestMethod]
    public void BuildPassArguments_NativeTerminal_OmitsTerminalLoggerOff()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(), "minimal", nativeTerminal: true);

        Assert.IsFalse(args.Contains("-tl:off", StringComparison.Ordinal),
            "On a real terminal, dotnet's native terminal logger should render the live build");
    }

    [TestMethod]
    public void BuildPassArguments_NoRestore_IsForwarded()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, Options(noRestore: true), "minimal");

        StringAssert.Contains(args, "--no-restore");
    }

    [TestMethod]
    public void EvaluateArguments_UseDotnetBuildNotDotnetMsbuild()
    {
        // MSBuild has no .cs project loader: `dotnet msbuild counter.cs --getProperty:X` fails with
        // MSB4025. `dotnet build counter.cs --getProperty:X` evaluates without building.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, Options());

        StringAssert.StartsWith(args, "build ");
        Assert.IsFalse(args.StartsWith("msbuild", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EvaluateArguments_RequestEveryPropertyTheInferenceReads()
    {
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, Options());

        foreach (var property in ProjectRunService.SingleFileRequestedProperties)
        {
            StringAssert.Contains(args, $"--getProperty:{property}");
        }

        // $(Version) is what the version inference reads; $(VersionPrefix) is empty once Version is set.
        StringAssert.Contains(args, "--getProperty:Version");
        StringAssert.Contains(args, "--getProperty:WinAppManifestPath");
    }

    [TestMethod]
    public void EvaluateArguments_DoNotRequestPlatform()
    {
        // Platform is NOT the architecture for a file-based app: '#:property Platform=arm64' is accepted
        // but leaves RuntimeIdentifier empty and still emits an x64 apphost on an x64 host. Reading it
        // would provision arm64 runtime packages for an x64 binary, so it must not be requested at all.
        var singleFile = WriteSingleFile();

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, Options());

        Assert.IsFalse(args.Contains("--getProperty:Platform", StringComparison.Ordinal), $"Unexpected in: {args}");
        StringAssert.Contains(args, "--getProperty:RuntimeIdentifier");
    }

    [TestMethod]
    public void EvaluateArguments_MirrorTheBuildPassConfigurationAndProperties()
    {
        // The evaluate must describe the output the build actually wrote, so both passes get the same
        // Configuration and the same forwarded -p.
        var singleFile = WriteSingleFile();
        var options = Options("Release", properties: "Foo=Bar");

        var buildArgs = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, options, "minimal");
        var evaluateArgs = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, options);

        StringAssert.Contains(buildArgs, "-c Release");
        StringAssert.Contains(evaluateArgs, "-c Release");
        StringAssert.Contains(buildArgs, "-p:Foo=Bar");
        StringAssert.Contains(evaluateArgs, "-p:Foo=Bar");
    }

    [TestMethod]
    public void EvaluateArguments_DropAUserPropertyOwnedByADedicatedSwitch()
    {
        // A -p:Configuration would otherwise fight the -c the two passes share.
        var singleFile = WriteSingleFile();
        var options = Options("Debug", properties: "Configuration=Release");

        var args = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, options);

        Assert.IsFalse(args.Contains("-p:Configuration=Release", StringComparison.Ordinal), $"Unexpected in: {args}");
        StringAssert.Contains(args, "-c Debug");
    }

    [TestMethod]
    [DataRow("TargetFramework=net10.0-windows10.0.26100.0", DisplayName = "TargetFramework")]
    [DataRow("RuntimeIdentifier=win-arm64", DisplayName = "RuntimeIdentifier")]
    public void Arguments_ForwardPropertiesProjectModeReservesForItsOwnSwitches(string property)
    {
        // --arch/--runtime/--framework are rejected for a .cs, so -p is the ONLY way to express these.
        // Project mode reserves them for its dedicated switches; reusing that filter here would drop them
        // from both passes and silently ignore what the user asked for. Both passes must carry them so
        // they still agree on the output path.
        var singleFile = WriteSingleFile();
        var options = Options(properties: property);

        var buildArgs = ProjectRunService.BuildSingleFileBuildPassArguments(singleFile, options, "minimal");
        var evaluateArgs = ProjectRunService.BuildSingleFileEvaluateArguments(singleFile, options);

        StringAssert.Contains(buildArgs, $"-p:{property}");
        StringAssert.Contains(evaluateArgs, $"-p:{property}");
    }

    #endregion

    public TestContext TestContext { get; set; } = null!;
}


