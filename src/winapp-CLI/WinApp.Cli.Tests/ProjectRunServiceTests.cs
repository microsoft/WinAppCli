// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProjectRunServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private ProjectRunService _service = null!;
    private FakeCsWinRTMetadataShimService _shim = null!;

    private const string ExecutableCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string LibraryCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string TestProjectCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <IsTestProject>true</IsTestProject>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    // No inline OutputType: a static parse treats this as non-executable, but an MSBuild evaluation
    // resolves OutputType from an import (SDK/props). Used by the M5 disambiguation tests.
    private const string NoInlineOutputTypeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    // Inline OutputType=Exe with no inline IsTestProject: a static parse treats this as a runnable
    // executable, but an MSBuild evaluation can reveal IsTestProject=true (set by the test SDK).
    private const string InlineExeNoTestFlagCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"ProjectRunServiceTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
        var fakeDotnet = new FakeDotNetService();
        _shim = new FakeCsWinRTMetadataShimService();
        _service = new ProjectRunService(fakeDotnet, NewDetection(fakeDotnet), _shim, new TestConsole(), NullLogger<ProjectRunService>.Instance);
    }

    // Project classification is owned by ProjectDetectionService; build a real one over the same fake
    // dotnet so evaluated OutputType/IsTestProject resolution behaves exactly as the test configured.
    private static ProjectDetectionService NewDetection(FakeDotNetService dotnet)
        => new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, dotnet);

    [TestCleanup]
    public void Cleanup()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private FileInfo WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    // Writes a file at a (possibly nested) path relative to _tempDir, creating intermediate dirs.
    private FileInfo WriteFileAt(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    // Minimal classic .sln listing the given project paths (relative to the solution dir, backslashes).
    private static string SlnListing(params string[] relativeProjectPaths)
    {
        var entries = relativeProjectPaths.Select((p, i) =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"P{i}\", \"{p}\", \"{{{Guid.NewGuid():D}}}\"" +
            Environment.NewLine + "EndProject");
        return "Microsoft Visual Studio Solution File, Format Version 12.00" + Environment.NewLine +
            string.Join(Environment.NewLine, entries);
    }

    // Minimal XML .slnx listing the given project paths (relative to the solution dir, forward slashes).
    private static string SlnxListing(params string[] relativeProjectPaths) =>
        "<Solution>" + Environment.NewLine +
        string.Join(Environment.NewLine, relativeProjectPaths.Select(p => $"  <Project Path=\"{p}\" />")) +
        Environment.NewLine + "</Solution>";

    #region BuildBuildPassArguments (streamed build pass, Change #1)

    [TestMethod]
    public void BuildBuildPassArguments_WithCsWinRTMetadataFolder_InjectsProperty()
    {
        // SHIM: a resolved ref-pack winmd folder is injected as -p:CsWinRTWindowsMetadata so cswinrt
        // can build without a registered Windows SDK.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal", folder);

        StringAssert.Contains(args, $"-p:CsWinRTWindowsMetadata={folder}");
    }

    [TestMethod]
    public void BuildBuildPassArguments_NoCsWinRTMetadataFolder_OmitsProperty()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal", csWinRTMetadataFolder: null);

        Assert.IsFalse(args.Contains("CsWinRTWindowsMetadata"), "no metadata property should be injected when the shim resolved nothing");
    }

    [TestMethod]
    public void BuildEvaluateArguments_WithCsWinRTMetadataFolder_InjectsProperty()
    {
        // The evaluate pass must be fed the same inputs as the build pass, so the shim folder flows here too.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options, folder);

        StringAssert.Contains(args, $"-p:CsWinRTWindowsMetadata={folder}");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Default_UsesBuildAndRid_NoGetProperty()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.StartsWith(args, "build ");
        StringAssert.Contains(args, "-c Debug");
        StringAssert.Contains(args, "-r win-x64");
        StringAssert.Contains(args, "-p:Platform=x64");
        StringAssert.Contains(args, "-v minimal");
        // The build pass must NOT request properties: --getProperty SUPPRESSES MSBuild's console log,
        // which is exactly the streamed output we want the user to see (Change #1). Nor does it need
        // an explicit -t:Build (Build is the default target when no --getProperty is present).
        Assert.IsFalse(args.Contains("--getProperty"), "build pass must not request properties");
        Assert.IsFalse(args.Contains("-t:Build"), "build pass does not need an explicit -t:Build");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Arm64_UsesArmRidAndPlatform()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Release", "arm64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-c Release");
        StringAssert.Contains(args, "-r win-arm64");
        StringAssert.Contains(args, "-p:Platform=ARM64");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Verbosity_ForwardedAsDashV()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "normal");

        StringAssert.Contains(args, "-v normal");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserPlatformProperty_SuppressesDerivedPlatform()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["Platform=ARM64"]);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:Platform=ARM64");
        Assert.IsFalse(args.Contains("-p:Platform=x64"), "derived Platform must not override a user-specified one");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Default_EnablesDynamicPlatformResolution()
    {
        // A forced global -p:Platform=<arch> leaks into AnyCPU/netstandard2.0 ProjectReferences and
        // breaks multi-project apps (CS0006). EnableDynamicPlatformResolution negotiates each
        // reference's own platform; it must be enabled by default in project mode (no-op for
        // single-project apps).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:EnableDynamicPlatformResolution=true");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserEnableDynamicPlatformResolution_NotOverridden()
    {
        // An explicit user value (even =false) must be respected: winapp must NOT append its own
        // =true, which as a command-line global would override a project that deliberately opted out.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["EnableDynamicPlatformResolution=false"]);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:EnableDynamicPlatformResolution=false");
        Assert.IsFalse(args.Contains("-p:EnableDynamicPlatformResolution=true"),
            "winapp must not override an explicit user EnableDynamicPlatformResolution value");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserProperties_ForwardedToBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: ["WindowsPackageType=None", "Foo=Bar"]);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:WindowsPackageType=None");
        StringAssert.Contains(args, "-p:Foo=Bar");
        StringAssert.Contains(args, "--no-restore");
    }

    [TestMethod]
    public void BuildBuildPassArguments_Framework_ForwardedToBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-f net10.0-windows10.0.26100.0");
    }

    #endregion

    #region BuildEvaluateArguments (evaluate-only property pass, Change #1)

    [TestMethod]
    public void BuildEvaluateArguments_UsesMsbuildGetProperty_NotBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.StartsWith(args, "msbuild ");
        // dotnet msbuild rejects -c/-r (MSB1001); the evaluate pass must use -p: equivalents and must
        // not build (no -t:Build) — the build pass already produced the output.
        Assert.IsFalse(args.Contains("-t:Build"), "evaluate pass must not build");
        Assert.IsFalse(args.Contains("-c Debug"), "evaluate pass must not pass -c");
        Assert.IsFalse(args.Contains("-r win-x64"), "evaluate pass must not pass -r");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        StringAssert.Contains(args, "-p:RuntimeIdentifier=win-x64");
        StringAssert.Contains(args, "-p:Platform=x64");
        StringAssert.Contains(args, "--getProperty:TargetDir");
        StringAssert.Contains(args, "--getProperty:RunCommand");
        StringAssert.Contains(args, "--getProperty:WindowsPackageType");
        StringAssert.Contains(args, "--getProperty:OutputType");
    }

    [TestMethod]
    public void BuildEvaluateArguments_Framework_ForwardedAsProperty()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, "-p:TargetFramework=net10.0-windows10.0.26100.0");
    }

    [TestMethod]
    public void BuildEvaluateArguments_DedicatedConfigAndRidWinOverUserProperty()
    {
        // Spec M2: the dedicated Configuration/RID are emitted as -p: on the evaluate pass. A conflicting
        // user -p must NOT override them — the dedicated value is emitted LAST so MSBuild's last-wins
        // makes the dedicated flag win, matching the build path and WarnOnOverriddenFlags.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["Configuration=Release", "RuntimeIdentifier=win-arm64"]);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        var userConfigIdx = args.IndexOf("-p:Configuration=Release", StringComparison.Ordinal);
        var dedicatedConfigIdx = args.IndexOf("-p:Configuration=Debug", StringComparison.Ordinal);
        Assert.IsTrue(userConfigIdx >= 0, "user -p:Configuration must still be forwarded to the evaluation");
        Assert.IsTrue(dedicatedConfigIdx >= 0, "dedicated Configuration must be emitted");
        Assert.IsTrue(dedicatedConfigIdx > userConfigIdx,
            "dedicated -p:Configuration must come AFTER the user -p so last-wins makes it win");

        var userRidIdx = args.IndexOf("-p:RuntimeIdentifier=win-arm64", StringComparison.Ordinal);
        var dedicatedRidIdx = args.IndexOf("-p:RuntimeIdentifier=win-x64", StringComparison.Ordinal);
        Assert.IsTrue(userRidIdx >= 0, "user -p:RuntimeIdentifier must still be forwarded");
        Assert.IsTrue(dedicatedRidIdx >= 0, "dedicated RuntimeIdentifier must be emitted");
        Assert.IsTrue(dedicatedRidIdx > userRidIdx,
            "dedicated -p:RuntimeIdentifier must come AFTER the user -p so last-wins makes it win");
    }

    [TestMethod]
    public void BuildEvaluateArguments_UserPlatformProperty_SuppressesDerivedPlatform()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["Platform=ARM64"]);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, "-p:Platform=ARM64");
        Assert.IsFalse(args.Contains("-p:Platform=x64"), "derived Platform must not override a user-specified one");
    }

    [TestMethod]
    public void BuildEvaluateArguments_Default_EnablesDynamicPlatformResolution()
    {
        // The evaluate pass must see the SAME project graph as the build pass so TargetDir/RunCommand
        // resolve against the same P2P references — so EDPR is enabled here too (spec: safe, doesn't
        // change the app's own TargetDir).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, "-p:EnableDynamicPlatformResolution=true");
    }

    [TestMethod]
    public void BuildEvaluateArguments_UserEnableDynamicPlatformResolution_NotOverridden()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["EnableDynamicPlatformResolution=false"]);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, "-p:EnableDynamicPlatformResolution=false");
        Assert.IsFalse(args.Contains("-p:EnableDynamicPlatformResolution=true"),
            "winapp must not override an explicit user EnableDynamicPlatformResolution value");
    }

    [TestMethod]
    public void BuildBuildPassArguments_WithSolution_EmitsSolutionProperties()
    {
        // Solution mode resolves a startup .csproj but hands MSBuild the $(SolutionDir) family so the
        // project builds exactly as it does under `dotnet build <sln>` / VS (the AI-Dev-Gallery class
        // of failure where a bare .csproj build has $(SolutionDir) undefined).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var solution = new FileInfo(Path.Combine(_tempDir.FullName, "MyApp.sln"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, $"-p:SolutionDir={_tempDir.FullName}\\");
        StringAssert.Contains(args, $"-p:SolutionPath={solution.FullName}");
        StringAssert.Contains(args, "-p:SolutionName=MyApp");
        StringAssert.Contains(args, "-p:SolutionFileName=MyApp.sln");
        StringAssert.Contains(args, "-p:SolutionExt=.sln");
    }

    [TestMethod]
    public void BuildBuildPassArguments_NoSolution_OmitsSolutionProperties()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        Assert.IsFalse(args.Contains("-p:SolutionDir"), "bare .csproj mode must not define solution properties");
    }

    [TestMethod]
    public void BuildBuildPassArguments_UserSolutionDir_NotOverridden()
    {
        // When the user passes their own -p:SolutionDir, winapp must not re-emit a second (winning,
        // last-wins) SolutionDir that clobbers it — the user's value stays authoritative. The sibling
        // Solution* properties are still defined.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var solution = new FileInfo(Path.Combine(_tempDir.FullName, "MyApp.sln"));
        var options = new ProjectRunOptions(
            "Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: ["SolutionDir=Z:\\Custom\\"], Solution: solution);

        var args = ProjectRunService.BuildBuildPassArguments(csproj, options, "minimal");

        StringAssert.Contains(args, "-p:SolutionDir=Z:\\Custom\\");
        Assert.IsFalse(
            args.Contains($"-p:SolutionDir={_tempDir.FullName}\\"),
            "winapp must not add a second SolutionDir that overrides the user's value");
        StringAssert.Contains(args, "-p:SolutionName=MyApp");
    }

    [TestMethod]
    public void BuildEvaluateArguments_WithSolution_EmitsSolutionProperties()
    {
        // The evaluate pass must define the same $(SolutionDir) family as the build pass so
        // TargetDir/RunCommand resolve against identical inputs.
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var solution = new FileInfo(Path.Combine(_tempDir.FullName, "MyApp.slnx"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Solution: solution);

        var args = ProjectRunService.BuildEvaluateArguments(csproj, options);

        StringAssert.Contains(args, $"-p:SolutionDir={_tempDir.FullName}\\");
        StringAssert.Contains(args, "-p:SolutionName=MyApp");
        StringAssert.Contains(args, "-p:SolutionFileName=MyApp.slnx");
        StringAssert.Contains(args, "-p:SolutionExt=.slnx");
    }

    #endregion

    #region MatchProjectSelector (--project resolution, M7)

    [TestMethod]
    public void MatchProjectSelector_RelativePath_ResolvesAgainstBaseDir_NotCwd()
    {
        // The project lives under the input/solution dir (_tempDir), which is NOT the process cwd.
        // A relative `--project src\App\App.csproj` must resolve against baseDir so it still matches;
        // resolving against the cwd (the old behavior) would miss it entirely.
        var projectPath = Path.Combine(_tempDir.FullName, "src", "App", "App.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        var projects = new List<FileInfo> { new(projectPath), new(Path.Combine(_tempDir.FullName, "src", "Other", "Other.csproj")) };

        var match = ProjectRunService.MatchProjectSelector(projects, "src\\App\\App.csproj", _tempDir);

        Assert.IsNotNull(match);
        Assert.AreEqual(projectPath, match!.FullName);
    }

    [TestMethod]
    public void MatchProjectSelector_ByProjectName_Matches()
    {
        var appPath = Path.Combine(_tempDir.FullName, "src", "App", "App.csproj");
        var projects = new List<FileInfo> { new(appPath), new(Path.Combine(_tempDir.FullName, "src", "Other", "Other.csproj")) };

        var byFileName = ProjectRunService.MatchProjectSelector(projects, "App.csproj", _tempDir);
        var byBareName = ProjectRunService.MatchProjectSelector(projects, "App", _tempDir);

        Assert.AreEqual(appPath, byFileName!.FullName);
        Assert.AreEqual(appPath, byBareName!.FullName);
    }

    [TestMethod]
    public void MatchProjectSelector_NoMatch_ReturnsNull()
    {
        var projects = new List<FileInfo> { new(Path.Combine(_tempDir.FullName, "App", "App.csproj")) };

        var match = ProjectRunService.MatchProjectSelector(projects, "DoesNotExist", _tempDir);

        Assert.IsNull(match);
    }

    [TestMethod]
    public void MatchProjectSelector_AmbiguousLeafName_ReturnsNull()
    {
        // Two projects with the same leaf file name: a leaf-name selector is ambiguous, so no single
        // match — the caller then errors listing candidates rather than guessing.
        var projects = new List<FileInfo>
        {
            new(Path.Combine(_tempDir.FullName, "a", "App.csproj")),
            new(Path.Combine(_tempDir.FullName, "b", "App.csproj")),
        };

        var match = ProjectRunService.MatchProjectSelector(projects, "App.csproj", _tempDir);

        Assert.IsNull(match);
    }

    #endregion

    #region TryParseSdkVersion

    [TestMethod]
    [DataRow("8.0.100", 8, 0, 100)]
    [DataRow("10.0.301", 10, 0, 301)]
    [DataRow("8.0.100-preview.1.23456", 8, 0, 100)]
    [DataRow("9.0.203", 9, 0, 203)]
    public void TryParseSdkVersion_ValidVersions_Parsed(string input, int major, int minor, int patch)
    {
        Assert.IsTrue(ProjectRunService.TryParseSdkVersion(input, out var ma, out var mi, out var pa));
        Assert.AreEqual(major, ma);
        Assert.AreEqual(minor, mi);
        Assert.AreEqual(patch, pa);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("8.0")]
    [DataRow("")]
    public void TryParseSdkVersion_Invalid_ReturnsFalse(string input)
    {
        Assert.IsFalse(ProjectRunService.TryParseSdkVersion(input, out _, out _, out _));
    }

    #endregion

    #region ResolveInput

    [TestMethod]
    public async Task ResolveInput_CsprojFile_ReturnsProjectMode()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(csproj.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_OwningSolutionOneLevelUp_AttachesSolution()
    {
        // A bare .csproj input must discover its owning solution (walking up) so $(SolutionDir) is
        // defined for the build — the AI-Dev-Gallery/Richasy class where a project imports shared props
        // via $(SolutionDir). The solution sits above the project and lists it.
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        var solution = WriteFile("MyApp.sln", SlnListing(Path.Combine("src", "App", "App.csproj")));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.IsNotNull(resolution.Solution, "the owning solution must be discovered so $(SolutionDir) is defined");
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_OwningSlnxListsProject_AttachesSolution()
    {
        // Same discovery for the newer XML .slnx format (forward-slash project paths).
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        var solution = WriteFile("MyApp.slnx", SlnxListing("src/App/App.csproj"));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.IsNotNull(resolution.Solution);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_PrefersSolutionThatListsProject()
    {
        // Two solutions at the same level: the one that actually lists the project wins over the one
        // that doesn't (so we attach the real owner, not an unrelated sibling solution).
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        WriteFile("Unrelated.sln", SlnListing(Path.Combine("src", "Other", "Other.csproj")));
        var owner = WriteFile("Owner.sln", SlnListing(Path.Combine("src", "App", "App.csproj")));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.IsNotNull(resolution.Solution);
        Assert.AreEqual(owner.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_MultipleSolutionsNoneList_LeavesSolutionNull()
    {
        // Several solutions at the nearest ancestor, none of which lists the project → we don't guess.
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        WriteFile("One.sln", SlnListing(Path.Combine("src", "Other", "Other.csproj")));
        WriteFile("Two.sln", SlnListing(Path.Combine("src", "Another", "Another.csproj")));

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.IsNull(resolution.Solution, "ambiguous solutions that don't list the project must not be guessed");
    }

    [TestMethod]
    public async Task ResolveInput_CsprojFile_SingleSolutionNotListing_AttachesByLocality()
    {
        // Exactly one solution at the nearest ancestor: attach it even when we can't confirm the listing
        // (e.g. an as-yet-empty/opaque solution), matching how a developer opens that lone solution in VS.
        var csproj = WriteFileAt(Path.Combine("src", "App", "App.csproj"), ExecutableCsproj);
        var solution = WriteFile("Lonely.sln", "");

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.IsNotNull(resolution.Solution);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_NonCsprojFile_Throws()
    {
        var txt = WriteFile("readme.txt", "hello");

        await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(txt, CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithNoCsproj_ReturnsFolderMode()
    {
        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode);
        Assert.IsNull(resolution.Csproj);
    }

    [TestMethod]
    public async Task FolderMode_NeverResolvesProject_SoProjectBuildArgsCannotLeak()
    {
        // Folder mode must stay byte-identical: a folder without a top-level .csproj routes to folder
        // mode, which never resolves a project and never invokes the project-mode build. The
        // project-mode build args — including the EnableDynamicPlatformResolution negotiation added for
        // multi-project builds — are emitted ONLY by BuildBuildPassArguments/BuildEvaluateArguments,
        // both of which are unreachable in folder mode. This guards against those args ever leaking
        // into a folder-mode run.
        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode, "a manifest/output folder must route to folder mode");
        Assert.IsNull(resolution.Csproj, "folder mode must not resolve a project to build (so no EDPR build args)");
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSingleCsproj_ReturnsProjectMode()
    {
        WriteFile("App.csproj", ExecutableCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_SingleExecutable_PicksExecutable()
    {
        // With no canned evaluation the classifier falls back to the static parse, which reads the
        // inline OutputType of these fixtures: App=WinExe (executable), Lib=Library (not).
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("Lib.csproj", LibraryCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleExecutableCsproj_ThrowsAmbiguity()
    {
        WriteFile("App1.csproj", ExecutableCsproj);
        WriteFile("App2.csproj", ExecutableCsproj);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_ExecutablePlusTestProject_PicksExecutable()
    {
        // A test project (IsTestProject=true) is excluded from the executable set even when its
        // OutputType is Exe, so an app + its test project disambiguates to the app (spec M5).
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("App.Tests.csproj", TestProjectCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_NoExecutable_ThrowsAmbiguity()
    {
        // Multiple projects, none statically executable → we cannot pick one; guide the user to
        // name a project explicitly rather than silently building a non-runnable one (spec M5).
        WriteFile("Lib1.csproj", LibraryCsproj);
        WriteFile("Lib2.csproj", LibraryCsproj);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_EvaluationDetectsExecutableFromImport_DoesNotSilentlyPickWrongProject()
    {
        // Spec M5: App.csproj gets its OutputType from an import (nothing inline) while Tool.csproj
        // declares OutputType=Exe inline. A STATIC parse sees only Tool as executable and would
        // silently build+run Tool. MSBuild evaluation reveals BOTH are runnable → ambiguity error,
        // so the wrong project is never launched behind the user's back.
        var app = WriteFile("App.csproj", NoInlineOutputTypeCsproj);
        var tool = WriteFile("Tool.csproj", InlineExeNoTestFlagCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : args.Contains(tool.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Exe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_EvaluationDetectsTestFromImport_PicksApp()
    {
        // Spec M5: App.Tests.csproj declares OutputType=Exe inline but no inline IsTestProject (the
        // test SDK sets it via an import). A STATIC parse would treat BOTH App and App.Tests as
        // executable → ambiguity. MSBuild evaluation reveals App.Tests is a test project, so the app
        // is correctly and unambiguously selected.
        var app = WriteFile("App.csproj", ExecutableCsproj);
        var tests = WriteFile("App.Tests.csproj", InlineExeNoTestFlagCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Exe", isTestProject: "true"), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_TestContainerCapability_PicksApp()
    {
        // The AI Dev Gallery / WinUI Gallery shape: the test project is itself a packaged WinUI app
        // (WinExe, EnableMsixTooling) that never sets IsTestProject, so OutputType alone can't tell it
        // apart from the real app. The VS `TestContainer` project capability marks it as a test, so the
        // real app is selected without an explicit --project.
        var app = WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("Gallery.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_TestFrameworkPackageReference_PicksApp()
    {
        // Same shape as above but detected via a test-framework PackageReference (MSTest.TestAdapter),
        // since WinUI MSTest projects reference MSTest.* / Microsoft.TestPlatform.TestHost yet omit
        // Microsoft.NET.Test.Sdk (which is what would set IsTestProject).
        var app = WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", packageReferences: ["MSTest.TestAdapter", "MSTest.TestFramework"]), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", packageReferences: ["CommunityToolkit.WinUI"]), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("Gallery.csproj", resolution.Csproj!.Name);
    }

    private static string EvalJson(string outputType, string isTestProject = "") =>
        $$"""{ "Properties": { "OutputType": "{{outputType}}", "IsTestProject": "{{isTestProject}}" } }""";

    // Adds an "Items" object alongside "Properties" so the evaluated classifier can see a project's
    // ProjectCapability / PackageReference items — the markers that identify a WinUI MSTest app that is
    // a WinExe with no IsTestProject (AI Dev Gallery / WinUI Gallery test project shape).
    private static string EvalJsonWithItems(
        string outputType,
        string[]? capabilities = null,
        string[]? packageReferences = null,
        string isTestProject = "")
    {
        static string ItemArray(string[]? ids) =>
            "[ " + string.Join(", ", (ids ?? []).Select(id => $$"""{ "Identity": "{{id}}" }""")) + " ]";

        return $$"""
            { "Properties": { "OutputType": "{{outputType}}", "IsTestProject": "{{isTestProject}}" },
              "Items": { "ProjectCapability": {{ItemArray(capabilities)}}, "PackageReference": {{ItemArray(packageReferences)}} } }
            """;
    }

    // Reproduces `dotnet sln <sln> list` output: a "Project(s)" header, a dashed underline, then one
    // project path per line (relative to the solution directory).
    private static string SlnListOutput(params string[] relativePaths) =>
        "Project(s)" + Environment.NewLine + "----------" + Environment.NewLine +
        string.Join(Environment.NewLine, relativePaths);

    private static bool IsSlnListCall(string args) =>
        args.Contains("sln", StringComparison.OrdinalIgnoreCase) && args.Contains("list", StringComparison.OrdinalIgnoreCase);

    [TestMethod]
    public async Task ResolveInput_SolutionFile_SingleExecutable_ReturnsProjectModeWithSolution()
    {
        var solution = WriteFile("MyApp.sln", "");
        var app = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            // sln list → the one project; any eval falls through to the static parse (App=WinExe).
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(app.FullName, resolution.Csproj!.FullName);
        Assert.IsNotNull(resolution.Solution, "solution mode must record the solution so $(SolutionDir) is defined");
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSolution_PrefersSolutionOverLooseCsproj()
    {
        WriteFile("MyApp.sln", "");
        WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.IsNotNull(resolution.Solution, "a solution in the directory must be preferred over loose .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleSolutions_ThrowsAmbiguity()
    {
        WriteFile("One.sln", "");
        WriteFile("Two.sln", "");

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple solution files");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_MultipleExecutables_RequiresProjectSelector()
    {
        var solution = WriteFile("MyApp.sln", "");
        var app1 = WriteFile("App1.csproj", ExecutableCsproj);
        var app2 = WriteFile("App2.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App1.csproj", "App2.csproj"), string.Empty)
                : args.Contains(app1.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : args.Contains(app2.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "--project");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_ProjectSelector_PicksMatch()
    {
        var solution = WriteFile("MyApp.sln", "");
        WriteFile("App1.csproj", ExecutableCsproj);
        var app2 = WriteFile("App2.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            // The selector short-circuits classification, so only the sln list call is needed.
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App1.csproj", "App2.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None, "App2");

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(app2.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public void MatchProjectSelector_RelativeLeaf_Matches()
    {
        var baseDir = _tempDir;
        var app = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var other = new FileInfo(Path.Combine(_tempDir.FullName, "src", "Lib", "Lib.csproj"));

        var match = ProjectRunService.MatchProjectSelector(new[] { app, other }, "App", baseDir);

        Assert.IsNotNull(match);
        Assert.AreEqual(app.FullName, match!.FullName);
    }

    [TestMethod]
    public void MatchProjectSelector_FullyQualifiedWrongPath_DoesNotFallBackToName()
    {
        // A fully qualified selector that points at a location no project occupies must NOT silently
        // match a same-named project elsewhere in the solution.
        var baseDir = _tempDir;
        var app = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));
        var wrong = Path.Combine(_tempDir.FullName, "somewhere", "else", "App.csproj");

        var match = ProjectRunService.MatchProjectSelector(new[] { app }, wrong, baseDir);

        Assert.IsNull(match);
    }

    [TestMethod]
    public void MatchProjectSelector_FullyQualifiedCorrectPath_Matches()
    {
        var baseDir = _tempDir;
        var app = new FileInfo(Path.Combine(_tempDir.FullName, "src", "App", "App.csproj"));

        var match = ProjectRunService.MatchProjectSelector(new[] { app }, app.FullName, baseDir);

        Assert.IsNotNull(match);
        Assert.AreEqual(app.FullName, match!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_Solution_NoCsprojProjects_Throws()
    {
        var solution = WriteFile("Native.sln", "");
        var dotnet = new FakeDotNetService
        {
            // Only a C++ project → filtered out because `winapp run` builds managed app projects.
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Native.vcxproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "No .csproj projects");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_AppPlusTestContainer_PicksApp()
    {
        // The real gallery scenario at the solution level: a solution with the app plus a WinUI MSTest
        // project (WinExe + TestContainer capability, no IsTestProject). The app is auto-selected — no
        // ambiguity error and no --project needed.
        var solution = WriteFile("Gallery.sln", "");
        var app = WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Gallery.csproj", "Gallery.Tests.csproj"), string.Empty)
                : args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(app.FullName, resolution.Csproj!.FullName);
        Assert.AreEqual(solution.FullName, resolution.Solution!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_Solution_OnlyTestProject_RunsIt()
    {
        // A solution whose only runnable project is a test project (no real app) still runs — a
        // tests-only solution is a legitimate `winapp run` target. We note that we're running a test.
        var solution = WriteFile("Tests.sln", "");
        var tests = WriteFile("App.Tests.csproj", ExecutableCsproj);
        var lib = WriteFile("App.csproj", LibraryCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("App.csproj", "App.Tests.csproj"), string.Empty)
                : args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("Exe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(lib.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Library"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out var console);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(tests.FullName, resolution.Csproj!.FullName);
        StringAssert.Contains(console.Output, "test project");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_OnlyMultipleTestProjects_RequiresProjectSelector()
    {
        // Several test projects and no app → we can't guess which test host to launch; require --project
        // and say the solution contains only test projects so the message is actionable.
        var solution = WriteFile("Tests.sln", "");
        var t1 = WriteFile("A.Tests.csproj", ExecutableCsproj);
        var t2 = WriteFile("B.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("A.Tests.csproj", "B.Tests.csproj"), string.Empty)
                : args.Contains(t1.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("Exe", capabilities: ["TestContainer"]), string.Empty)
                : args.Contains(t2.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJsonWithItems("Exe", capabilities: ["TestContainer"]), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "only test projects");
        StringAssert.Contains(ex.Message, "--project");
    }

    [TestMethod]
    public async Task ResolveInput_Solution_ProjectSelectorPicksTestProject_NoFiltering()
    {
        // An explicit --project selector is always honored, even when it names a test project: the user
        // asked for it, so no test filtering and no evaluation is applied.
        var solution = WriteFile("Gallery.sln", "");
        WriteFile("Gallery.csproj", ExecutableCsproj);
        var tests = WriteFile("Gallery.Tests.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (0, SlnListOutput("Gallery.csproj", "Gallery.Tests.csproj"), string.Empty) : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(solution, CancellationToken.None, "Gallery.Tests");

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(tests.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_Solution_SlnListFails_Throws()
    {
        var solution = WriteFile("Broken.sln", "");
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                IsSlnListCall(args) ? (1, string.Empty, "MSB1234: invalid solution") : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(solution, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Broken.sln");
    }

    #endregion

    #region BuildAndResolveAsync (--json banner suppression, spec H2)

    private static ProjectRunService NewServiceWith(FakeDotNetService dotnet, out TestConsole console)
        => NewServiceWith(dotnet, new FakeCsWinRTMetadataShimService(), out console);

    private static ProjectRunService NewServiceWith(FakeDotNetService dotnet, FakeCsWinRTMetadataShimService shim, out TestConsole console)
    {
        console = new TestConsole();
        return new ProjectRunService(dotnet, NewDetection(dotnet), shim, console, NullLogger<ProjectRunService>.Instance);
    }

    private static ProjectRunService NewServiceWith(FakeDotNetService dotnet, LogLevel minLevel, out TestConsole console)
    {
        console = new TestConsole();
        return new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(minLevel));
    }

    private string PackagedPropertiesJson() =>
        // TargetDir must be non-empty and the packaging must resolve to Packaged (WindowsPackageType=MSIX)
        // so BuildAndResolveAsync succeeds without needing a real apphost .exe on disk.
        $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "MSIX", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "" } }""";

    [TestMethod]
    public async Task BuildAndResolveAsync_ShimResolvesFolder_InjectsMetadataIntoBuildPass()
    {
        // SHIM threading: when the shim resolves a folder (SDK absent), the build pass args must carry
        // -p:CsWinRTWindowsMetadata and the shim must be consulted with the project's target framework.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";
        var shim = new FakeCsWinRTMetadataShimService { FolderToReturn = folder };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(1, dotnet.StreamingCalls.Count, "exactly one build pass should have run");
        StringAssert.Contains(dotnet.StreamingCalls[0], $"-p:CsWinRTWindowsMetadata={folder}");
        Assert.AreEqual(1, shim.ResolvedMonikers.Count, "the shim should be consulted once");
        Assert.AreEqual("net10.0-windows10.0.26100.0", shim.ResolvedMonikers[0],
            "the shim must be consulted with the project's target framework moniker");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_UserSetMetadata_ShimNotConsulted()
    {
        // The user's explicit CsWinRTWindowsMetadata wins: the shim must not be consulted or injected.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var shim = new FakeCsWinRTMetadataShimService { FolderToReturn = @"C:\should\not\be\used\winmd" };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false,
            Properties: [@"CsWinRTWindowsMetadata=C:\user\choice\winmd"]);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.AreEqual(0, shim.ResolvedMonikers.Count, "the shim must not be consulted when the user set the property");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains(@"C:\should\not\be\used\winmd"),
            "the shim's value must never override the user's explicit property");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SdkAbsentShimInitiallyNull_RestoresThenReResolvesAndInjects()
    {
        // C1 (restore ordering): on a clean SDK-less host the ref pack isn't restored yet, so the first
        // shim resolve returns null. We must run an explicit `dotnet restore`, re-resolve (now a folder),
        // inject it into the build, and pass --no-restore to the build so we don't restore twice.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var folder = @"C:\cache\microsoft.windows.sdk.net.ref\10.0.26100.57\winmd";
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = true };
        shim.FolderSequence.Enqueue(null);   // first resolve: ref pack not restored yet
        shim.FolderSequence.Enqueue(folder); // second resolve: after the explicit restore
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsTrue(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "an explicit restore pass should run before the build when the shim initially resolves null on an SDK-less host");
        Assert.AreEqual(2, shim.ResolvedMonikers.Count, "the shim should be re-consulted after the restore");
        Assert.AreEqual(1, dotnet.StreamingCalls.Count, "exactly one build pass should have run");
        StringAssert.Contains(dotnet.StreamingCalls[0], $"-p:CsWinRTWindowsMetadata={folder}",
            "the re-resolved folder must be injected into the build");
        StringAssert.Contains(dotnet.StreamingCalls[0], "--no-restore",
            "the build pass should skip its own restore since the explicit restore already ran");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoRestore_SkipsPreShimRestore()
    {
        // C1: --no-restore opts out of the pre-shim restore; the shim is consulted once and no restore runs.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = true, FolderToReturn = null };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: []);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "--no-restore must suppress the pre-shim restore");
        Assert.AreEqual(1, shim.ResolvedMonikers.Count, "the shim should be consulted exactly once when restore is suppressed");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_SdkPresent_NoPreShimRestore()
    {
        // C1: when a Windows SDK is registered the shim no-ops; there is nothing to restore-and-retry.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var commandArgs = new List<string>();
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args => { commandArgs.Add(args); return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var shim = new FakeCsWinRTMetadataShimService { WindowsSdkAbsent = false, FolderToReturn = null };
        var service = NewServiceWith(dotnet, shim, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(commandArgs.Any(a => a.StartsWith("restore ", StringComparison.Ordinal)),
            "no pre-shim restore should run when a Windows SDK is registered");
        Assert.AreEqual(1, shim.ResolvedMonikers.Count);
    }

    [TestMethod]
    public void BuildRestorePassArguments_IncludesRidAndUserPropertiesWithoutNoRestore()
    {
        // C1: the pre-shim restore mirrors the build's RID + user -p but never adds --no-restore
        // (restoring is the whole point) and omits -c/-f/-v.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var options = new ProjectRunOptions("Debug", "arm64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false,
            Properties: ["WindowsPackageType=None"]);

        var args = ProjectRunService.BuildRestorePassArguments(csproj, options);

        StringAssert.StartsWith(args, "restore ");
        StringAssert.Contains(args, "-r win-arm64");
        StringAssert.Contains(args, "-p:WindowsPackageType=None");
        Assert.IsFalse(args.Contains("--no-restore", StringComparison.Ordinal), "restore must not carry --no-restore");
        Assert.IsFalse(args.Contains(" -c ", StringComparison.Ordinal), "restore is config-agnostic for pulling the ref pack");
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_WindowsPackageTypeNone_ReturnsTrue()
    {
        // Issue #676: an explicit WindowsPackageType=None is the one definitive unpackaged signal, so
        // the pre-build fast-fail probe reports true.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, UnpackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(0, dotnet.StreamingCalls.Count, "the probe must never build — it only evaluates");
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_PackagedType_ReturnsFalse()
    {
        // WindowsPackageType=MSIX → packaged → not definitively unpackaged.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_EmptyWindowsPackageType_ReturnsFalseSoRecipeFallbackStaysAuthoritative()
    {
        // An unset WindowsPackageType is INDETERMINATE pre-build — a packaged app that declares
        // identity via an emitted recipe also evaluates empty here. Reporting unpackaged would
        // misclassify it, so the probe returns false and defers to the authoritative post-build gate.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var emptyJson = $$"""{ "Properties": { "TargetDir": "", "RunCommand": "", "WindowsPackageType": "", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, emptyJson, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsDefinitivelyUnpackagedAsync_EvaluationFails_ReturnsFalse()
    {
        // A failed evaluation is treated as indeterminate (never throws), so the normal build path
        // still runs and surfaces the real error.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, string.Empty, "MSB1009: Project file does not exist.") };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var result = await service.IsDefinitivelyUnpackagedAsync(csproj, options, CancellationToken.None);

        Assert.IsFalse(result);
    }

    private string UnpackagedPropertiesJson() =>
        $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "" } }""";

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_DoesNotPrintBuildBannerToConsole()
    {
        // Spec H2: in --json mode stdout must be pure JSON, so the human-readable "Building…" banner
        // must not be written to the (stdout) console.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the canned packaged build should resolve successfully");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase),
            "--json mode must not print the build banner to stdout");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_BuildFailureDiagnosticsGoToStderrNotStdout()
    {
        // Spec H2: on a failed build in --json mode, dotnet's captured diagnostics must be routed to
        // stderr so the (stdout) console stays free of non-JSON noise.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        const string diag = "error NETSDK9999: totally broken build";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, diag, "MSB1234: also broken") };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNull(outcome.Resolution, "a failed build should not resolve");
        Assert.AreEqual(1, outcome.ExitCode, "the dotnet exit code should propagate");
        Assert.IsFalse(console.Output.Contains(diag, StringComparison.OrdinalIgnoreCase),
            "--json mode must not write build diagnostics to stdout");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonJsonMode_PrintsBuildBanner()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        // Information enabled models the default (non-quiet) run where the human banner is shown.
        var service = NewServiceWith(dotnet, LogLevel.Information, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(console.Output, "Building",
            "non-json mode should print the human-readable build banner");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_QuietMode_SuppressesBuildBanner()
    {
        // M8: --quiet (Information suppressed) must keep stdout clean like --json — no "Building" banner
        // written to the console; build output is routed to stderr instead.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Warning, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the canned packaged build should still resolve under --quiet");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase),
            "--quiet must not print the build banner to stdout");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_WindowsPackageTypeNone_ResolvesUnpackaged()
    {
        // Spec §7.1: WindowsPackageType=None => unpackaged; RunCommand is the launchable apphost .exe.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var exe = WriteFile("App.exe", "stub"); // must exist for the unpackaged launch path
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "{{exe.FullName.Replace("\\", "\\\\")}}", "WindowsPackageType": "None", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "false" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(ProjectPackaging.Unpackaged, outcome.Resolution!.Packaging);
        Assert.AreEqual(exe.FullName, outcome.Resolution.RunCommand);
        Assert.IsFalse(outcome.Resolution.SelfContained);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_EmptyPackageTypeWithMsixTooling_ResolvesPackaged()
    {
        // --no-build evaluate-only path: MSIX targets don't run so WindowsPackageType is empty;
        // fall back to EnableMsixTooling=true => packaged (spec §7.1).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "", "EnableMsixTooling": "true", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(ProjectPackaging.Packaged, outcome.Resolution!.Packaging);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_HappyPath_CarriesResolvedArchitecture()
    {
        // The resolved architecture must flow onto the resolution so the correct-arch runtime is
        // installed for the packaged/unpackaged launch (spec §8.4 / H1).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "arm64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual("arm64", outcome.Resolution!.Architecture);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonExecutableOutputType_Throws()
    {
        // Guardrail: a non-runnable project (OutputType=Library) must fail fast, never launch.
        var csproj = WriteFile("Lib.csproj", LibraryCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "Library" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "OutputType");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_EmptyTargetDir_Throws()
    {
        // Guardrail: an empty TargetDir means we have nowhere to register/launch from (the M4 surface
        // — a braced build preamble that broke parsing used to reach here with an empty dict).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = """{ "Properties": { "TargetDir": "", "RunCommand": "", "WindowsPackageType": "MSIX", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "TargetDir");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_UnpackagedMissingRunCommand_Throws()
    {
        // Guardrail: an unpackaged app with no launchable .exe (empty/absent RunCommand) must error.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "unpackaged");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoBuildUnpackagedMissingExe_HintsToRemoveNoBuild()
    {
        // With --no-build the guardrail should point the user at removing --no-build so the exe exists.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "{{Path.Combine(_tempDir.FullName, "missing.exe").Replace("\\", "\\\\")}}", "WindowsPackageType": "None", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "--no-build");
    }

    #endregion

    #region Two-pass build + verbosity + spinner (Change #1 / #4)

    [TestMethod]
    public async Task BuildAndResolveAsync_TwoPass_StreamsBuildThenEvaluatesProperties()
    {
        // Change #1: the build must run as TWO dotnet invocations — a streamed `dotnet build` (no
        // --getProperty, which would suppress the console log) followed by an evaluate-only
        // `dotnet msbuild --getProperty` that returns the resolved paths.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        string? evalArgs = null;
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = a => { evalArgs = a; return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the canned packaged build should resolve");
        Assert.AreEqual(1, dotnet.StreamingCalls.Count, "the build pass should stream exactly once");
        StringAssert.StartsWith(dotnet.StreamingCalls[0], "build ");
        Assert.IsFalse(dotnet.StreamingCalls[0].Contains("--getProperty"),
            "the streamed build pass must not request properties");
        Assert.IsNotNull(evalArgs, "the evaluate pass must run");
        StringAssert.StartsWith(evalArgs!, "msbuild ");
        StringAssert.Contains(evalArgs!, "--getProperty:TargetDir");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_VerboseLogger_MapsToNormalDotnetVerbosity()
    {
        // Change #1: verbose (ILogger Debug, the signal behind --verbose) must reach dotnet as -v normal.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Debug, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-v normal");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_DefaultLogger_MapsToMinimalDotnetVerbosity()
    {
        // Change #1: an ordinary (Information) run keeps dotnet tidy with -v minimal.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-v minimal");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_QuietLogger_MapsToQuietDotnetVerbosity()
    {
        // Change #1: --quiet (Information suppressed) keeps dotnet quiet too.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Warning, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(dotnet.StreamingCalls[0], "-v quiet");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoBuild_SkipsBuildPass_EvaluatesOnly()
    {
        // Change #1: --no-build must skip the streamed build pass and only evaluate properties.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(0, dotnet.StreamingCalls.Count, "--no-build must not run the streamed build pass");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_BuildFailure_ShortCircuitsBeforeEvaluate()
    {
        // Change #1: a failed build pass must propagate its exit code and NOT evaluate properties.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var evaluated = false;
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, _, _) => 7,
            RunDotnetCommandHandler = _ => { evaluated = true; return (0, PackagedPropertiesJson(), string.Empty); },
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNull(outcome.Resolution, "a failed build must not resolve");
        Assert.AreEqual(7, outcome.ExitCode, "the build exit code must propagate");
        Assert.IsFalse(evaluated, "a failed build must short-circuit before the evaluate pass");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonJsonNonSpinner_StreamsBuildLinesLive()
    {
        // Change #1: in a non-json, non-spinner terminal the streamed build output must be visible.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, _) => { onOut?.Invoke("MSBuild-line-ABC"); return 0; },
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, LogLevel.Information, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(console.Output, "Building", "the plain build banner should be shown");
        StringAssert.Contains(console.Output, "MSBuild-line-ABC", "streamed build output should be visible");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_StreamedBuildLinesNotOnStdout()
    {
        // Change #1 + spec H2: under --json the streamed build output must go to stderr, never stdout,
        // so the final stdout stays pure JSON.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, onErr) => { onOut?.Invoke("STDOUT-POISON"); onErr?.Invoke("STDERR-POISON"); return 0; },
            RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty),
        };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.IsFalse(console.Output.Contains("STDOUT-POISON"), "--json must not write build output to stdout");
        Assert.IsFalse(console.Output.Contains("STDERR-POISON"), "--json must not write build stderr to stdout");
    }

    [TestMethod]
    public async Task RunBuildPassAsync_Spinner_SuccessHidesBuildOutput()
    {
        // Change #4: the interactive spinner path hides raw build lines on success (clean output).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, _) => { onOut?.Invoke("hidden-spinner-noise"); return 0; },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Information));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var exit = await service.RunBuildPassAsync(csproj, options, _tempDir, useLiveSpinner: true, csWinRTMetadataFolder: null, CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.IsFalse(console.Output.Contains("hidden-spinner-noise"),
            "the spinner path must hide streamed build lines on success");
    }

    [TestMethod]
    public async Task RunBuildPassAsync_Spinner_FailureDumpsBuildOutput()
    {
        // Change #4: on failure the spinner path must dump the captured output so the error is visible.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, _, onErr) => { onErr?.Invoke("error CS9999: the real failure"); return 1; },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Information));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var exit = await service.RunBuildPassAsync(csproj, options, _tempDir, useLiveSpinner: true, csWinRTMetadataFolder: null, CancellationToken.None);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(console.Output, "error CS9999: the real failure",
            "the spinner path must reveal build output when the build fails");
    }

    [TestMethod]
    public async Task RunBuildPassAsync_Verbose_StreamsLiveEvenWhenSpinnerEligible()
    {
        // Change #4: --verbose wins over the spinner — the user asked for detail, so stream full output.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetStreamingHandler = (_, onOut, _) => { onOut?.Invoke("detailed-build-output"); return 0; },
        };
        var console = new TestConsole();
        var service = new ProjectRunService(dotnet, NewDetection(dotnet), new FakeCsWinRTMetadataShimService(), console, new LevelLogger<ProjectRunService>(LogLevel.Debug));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var exit = await service.RunBuildPassAsync(csproj, options, _tempDir, useLiveSpinner: true, csWinRTMetadataFolder: null, CancellationToken.None);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(console.Output, "detailed-build-output",
            "verbose mode must stream full build output even when a spinner would otherwise be used");
    }

    #endregion

    #region CheckSdkAsync

    [TestMethod]
    public async Task CheckSdkAsync_DotnetNotOnPath_ReturnsNotFoundError()
    {
        // Process.Start throws when dotnet is not on PATH → surfaced as an actionable install hint.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => throw new System.ComponentModel.Win32Exception("not found") };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "not found");
    }

    [TestMethod]
    public async Task CheckSdkAsync_NonZeroExit_ReturnsError()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, string.Empty, "boom") };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "Could not determine");
    }

    [TestMethod]
    public async Task CheckSdkAsync_TooOldVersion_ReturnsError()
    {
        // 8.0.99 < 8.0.100 (the first SDK with --getProperty) → too old.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "8.0.99", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "too old");
    }

    [TestMethod]
    public async Task CheckSdkAsync_CapableVersion_ReturnsNull()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "8.0.100", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task CheckSdkAsync_NewerVersion_ReturnsNull()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "10.0.301", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task CheckSdkAsync_UnparseableVersion_ReturnsNull()
    {
        // Present but unparseable → assume a modern SDK; the build surfaces a real error if not.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "not-a-version", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    #endregion
}
