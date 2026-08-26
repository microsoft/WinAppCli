// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Xml.Linq;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Single-file-mode routing tests for <see cref="RunCommand"/>: a .NET file-based app (a single
/// <c>.cs</c>) builds, gets a manifest, and reaches the shared loose-layout pipeline. A
/// <see cref="FakeProjectRunService"/> supplies canned build outcomes so the routing, option-rejection
/// and manifest-precedence logic is verified without invoking the real .NET SDK.
/// </summary>
[TestClass]
public class RunCommandSingleFileModeTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakeDebugOutputService _fakeDebugOutputService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakeDebugOutputService = new FakeDebugOutputService();
        _fakeProjectRunService = new FakeProjectRunService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IDebugOutputService>(_fakeDebugOutputService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    /// <summary>Writes a .cs file-based app and the build output folder its evaluate pass would report.</summary>
    private (FileInfo SingleFile, DirectoryInfo OutputDirectory) CreateSingleFileApp(string name = "counter.cs")
    {
        var sourceDir = _tempDirectory.CreateSubdirectory($"src_{Guid.NewGuid():N}");
        var singleFile = new FileInfo(Path.Combine(sourceDir.FullName, name));
        File.WriteAllText(singleFile.FullName, "Console.WriteLine(\"hi\");");

        // Stands in for %TEMP%\dotnet\runfile\<stem>-<hash>\bin\debug\, which belongs to exactly one .cs.
        var outputDir = _tempDirectory.CreateSubdirectory($"runfile_{Guid.NewGuid():N}");
        File.WriteAllText(Path.Combine(outputDir.FullName, "counter.exe"), "MZ");
        return (singleFile, outputDir);
    }

    private void SetOutcome(
        FileInfo singleFile,
        DirectoryInfo outputDirectory,
        string executableName = "counter.exe",
        params (string Name, string Value)[] properties)
        => SetOutcome(singleFile, outputDirectory, executableName, selfContained: false, properties);

    private void SetOutcome(
        FileInfo singleFile,
        DirectoryInfo outputDirectory,
        string executableName,
        bool selfContained,
        params (string Name, string Value)[] properties)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in properties)
        {
            props[name] = value;
        }

        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile, outputDirectory.FullName, executableName, "x64", "net10.0-windows10.0.19041.0", selfContained, props), 0);
    }

    private static XDocument LoadGeneratedManifest(DirectoryInfo outputDirectory)
    {
        var path = Path.Combine(outputDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(path), $"A manifest should have been generated at {path}");
        return XDocument.Load(path);
    }

    #region Routing

    [TestMethod]
    public async Task SingleFileMode_BuildsAndRegistersThroughTheSharedLooseLayoutPipeline()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count, "The .cs should be built via the single-file pass");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "The .csproj project pass must not run for a .cs");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "A file-based app registers as a packaged loose layout");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "A packaged app launches via AUMID");
    }

    [TestMethod]
    public async Task SingleFileMode_ForwardsConfigurationAndPropertiesToTheBuild()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [singleFile.FullName, "-c", "Release", "-p", "Foo=Bar", "--no-restore", "--detach"]);

        Assert.AreEqual(0, exitCode);
        var options = _fakeProjectRunService.SingleFileBuildOptions.Single();
        Assert.AreEqual("Release", options.Configuration);
        Assert.IsTrue(options.NoRestore);
        CollectionAssert.Contains(options.Properties.ToList(), "Foo=Bar");
    }

    [TestMethod]
    public async Task SingleFileMode_BuildFailure_PropagatesTheDotnetExitCode()
    {
        var (singleFile, _) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(null, 3);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(3, exitCode);
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    [TestMethod]
    public async Task SingleFileMode_SdkTooOld_FailsBeforeBuilding()
    {
        // Building a bare .cs through a virtual project only exists from .NET 10, a higher floor than the
        // 8.0.100 that --getProperty alone needs.
        var (singleFile, _) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileSdkError = "The .NET SDK 9.0.100 cannot build .NET file-based apps.";
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count, "The SDK gate must run before the build");
    }

    [TestMethod]
    public async Task SingleFileMode_GuardrailViolation_ReportsTheMessageAndFails()
    {
        var (singleFile, _) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileBuildThrows =
            new ProjectRunException("'counter.cs' declares WindowsPackageType=None");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    #endregion

    #region Rejected options

    [TestMethod]
    [DataRow("--project", "MyApp", DisplayName = "--project")]
    [DataRow("--framework", "net10.0-windows10.0.22621.0", DisplayName = "--framework")]
    [DataRow("--arch", "arm64", DisplayName = "--arch")]
    [DataRow("--runtime", "win-arm64", DisplayName = "--runtime")]
    public async Task SingleFileMode_ProjectOnlyBuildOptions_AreRejectedBeforeBuilding(string option, string value)
    {
        // A file-based app declares its own TFM/Platform via #:property, and injecting a RID would move
        // the build output away from the path the evaluate pass reads back.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, option, value, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count, "Rejection must happen before any build work");
    }

    [TestMethod]
    public async Task SingleFileMode_RejectedOption_EmitsAStructuredErrorUnderJson()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--arch", "arm64", "--json"]);

        Assert.AreEqual(1, exitCode);
        var stdout = TestAnsiConsole.Output.Trim();
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("Error").GetString();
        StringAssert.Contains(error, "--arch", "The JSON error should name the rejected option");
        StringAssert.Contains(error, "#:property Platform", "The JSON error should point at the replacement directive");
    }

    [TestMethod]
    public async Task SingleFileMode_MalformedProperty_IsRejected()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "-p", "A=1;B=2", "--detach"]);

        Assert.AreEqual(1, exitCode, "A packed -p must be rejected in single-file mode as it is in project mode");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveSingleFileCalls.Count);
    }

    #endregion

    #region Manifest inference

    [TestMethod]
    public async Task SingleFileMode_GeneratesAManifestFromTheDeclaredProperties()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("WinAppPackageName", "com.contoso.counter"),
            ("WinAppDisplayName", "Contoso Counter"),
            ("WinAppDescription", "Counts things"),
            ("Version", "1.2.3-preview.4"));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        var root = LoadGeneratedManifest(outputDir).Root!;
        var identity = root.Element(Ns + "Identity")!;
        Assert.AreEqual("com.contoso.counter", identity.Attribute("Name")!.Value);
        Assert.AreEqual("1.2.3.0", identity.Attribute("Version")!.Value, "the semver suffix is cut and the version padded to four parts");
        Assert.AreEqual("Contoso Counter", root.Element(Ns + "Properties")!.Element(Ns + "DisplayName")!.Value);

        var app = root.Element(Ns + "Applications")!.Element(Ns + "Application")!;
        Assert.AreEqual("App", app.Attribute("Id")!.Value, "single-file mode uses a fixed Application Id");
        Assert.AreEqual("counter.exe", app.Attribute("Executable")!.Value,
            "the executable is concrete, not $targetnametoken$.exe, so RestartAgent.exe can't make it ambiguous");

        var visual = app.Element(Uap + "VisualElements")!;
        Assert.AreEqual("Contoso Counter", visual.Attribute("DisplayName")!.Value);
        Assert.AreEqual("Counts things", visual.Attribute("Description")!.Value);
    }

    [TestMethod]
    public async Task SingleFileMode_GeneratedManifestDeclaresOnlyRunFullTrust()
    {
        // WinUI 3 desktop apps are full-trust by default, so capabilities are fixed template boilerplate
        // rather than something a #:property can widen.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var capabilities = LoadGeneratedManifest(outputDir).Root!.Element(Ns + "Capabilities")!;
        var names = capabilities.Elements().Select(e => e.Attribute("Name")!.Value).ToList();
        CollectionAssert.AreEqual(new List<string> { "runFullTrust" }, names);
    }

    [TestMethod]
    public async Task SingleFileMode_GeneratesTheDefaultAssetSet()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        var assets = new DirectoryInfo(Path.Combine(outputDir.FullName, "Assets"));
        Assert.IsTrue(assets.Exists, "The manifest references Assets\\*, so they must be generated alongside it");
        Assert.IsTrue(assets.GetFiles("*.png").Length > 0);
    }

    [TestMethod]
    public async Task SingleFileMode_UnusableVersion_FailsWithoutRegistering()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppVersion", "70000.1.2.3"));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode, "An out-of-range version is rejected rather than silently truncated");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    #endregion

    #region Manifest precedence

    [TestMethod]
    public async Task SingleFileMode_ExplicitManifestOption_WinsAndSkipsGeneration()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var explicitManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, $"explicit_{Guid.NewGuid():N}.appxmanifest"));
        File.WriteAllText(explicitManifest.FullName, "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [singleFile.FullName, "--manifest", explicitManifest.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(explicitManifest.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "Package.appxmanifest")),
            "--manifest should short-circuit generation entirely");
    }

    [TestMethod]
    public async Task SingleFileMode_WinAppManifestPath_IsHonoredAndSkipsGeneration()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        var declared = new FileInfo(Path.Combine(_tempDirectory.FullName, $"declared_{Guid.NewGuid():N}.appxmanifest"));
        File.WriteAllText(declared.FullName, "<Package/>");
        SetOutcome(singleFile, outputDir, "counter.exe", ("WinAppManifestPath", declared.FullName));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(declared.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "Package.appxmanifest")));
    }

    [TestMethod]
    public async Task SingleFileMode_WinAppManifestPathPointingNowhere_FailsWithAnActionableError()
    {
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe",
            ("WinAppManifestPath", Path.Combine(_tempDirectory.FullName, "does-not-exist.appxmanifest")));
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode, "A declared manifest path that resolves to nothing is a misconfiguration, not a reason to generate");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
    }

    [TestMethod]
    public async Task SingleFileMode_ManifestAuthoredNextToTheFile_IsUsedVerbatim()
    {
        // The generated manifest lives in the OUTPUT directory, so probing the output first (as the
        // NuGet targets do) would permanently shadow a manifest the user hand-wrote next to their .cs.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var authored = new FileInfo(Path.Combine(singleFile.DirectoryName!, "counter.appxmanifest"));
        File.WriteAllText(authored.FullName, "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(authored.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "Package.appxmanifest")),
            "An authored manifest must suppress generation, not be shadowed by it");
    }

    [TestMethod]
    [DataRow("Package.appxmanifest", DisplayName = "Package.appxmanifest")]
    [DataRow("appxmanifest.xml", DisplayName = "appxmanifest.xml")]
    public async Task SingleFileMode_DirectoryWideManifestNextToTheFile_IsNotPickedUpImplicitly(string manifestName)
    {
        // foo.cs and bar.cs can share a source directory, so a directory-wide manifest name would be
        // silently applied to BOTH — registering one under the other's identity. Only the per-file
        // <stem>.appxmanifest is discovered implicitly; a shared one must be named with --manifest.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        File.WriteAllText(Path.Combine(singleFile.DirectoryName!, manifestName), "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(
            Path.Combine(outputDir.FullName, "Package.appxmanifest"),
            _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath,
            "A directory-wide manifest beside the .cs must not be adopted; generate one instead");
    }

    [TestMethod]
    public async Task SingleFileMode_PerFileAuthoredManifest_BeatsThePerDirectoryOne()
    {
        // foo.cs and bar.cs can share a source directory, so the per-file name wins when both exist.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var perFile = new FileInfo(Path.Combine(singleFile.DirectoryName!, "counter.appxmanifest"));
        File.WriteAllText(perFile.FullName, "<Package/>");
        File.WriteAllText(Path.Combine(singleFile.DirectoryName!, "Package.appxmanifest"), "<Package/>");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(perFile.FullName, _fakeMsixService.AddLooseLayoutCalls.Single().ManifestPath);
    }

    #endregion

    #region Architecture

    [TestMethod]
    public async Task SingleFileMode_ProvisionsTheRuntimeForTheAppsArchitecture()
    {
        // Single-file mode rejects --arch, so the architecture the app declared via #:property is the
        // ONLY thing that can reach runtime provisioning. Passing null there would install the machine's
        // architecture instead, and a cross-architecture app would fail to launch.
        var (singleFile, outputDir) = CreateSingleFileApp();
        _fakeProjectRunService.SingleFileBuildOutcome = new SingleFileBuildOutcome(
            new SingleFileRunResolution(
                singleFile, outputDir.FullName, "counter.exe", "arm64", "net10.0-windows10.0.22621.0", false,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), 0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        var runtimeCall = _fakeMsixService.AddLooseLayoutRuntimeCalls.Single();
        Assert.AreEqual("arm64", runtimeCall.RuntimeArch);
        Assert.AreEqual("net10.0-windows10.0.22621.0", runtimeCall.Framework,
            "The built TFM must reach runtime provisioning so the Windows App SDK version resolves correctly");
    }

    [TestMethod]
    public async Task SingleFileMode_ResolvesPackagesFromTheCsFile_NotAnUnrelatedProjectInTheCurrentDirectory()
    {
        // With a null project file the loose-layout pipeline globs the CURRENT DIRECTORY for any .csproj
        // and uses its package list. A .cs file-based app has no project, so that could only ever find an
        // unrelated one — and would write ITS Windows App SDK dependency into this app's manifest.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(singleFile.FullName, _fakeMsixService.AddLooseLayoutRuntimeCalls.Single().ProjectFile,
            "The .cs itself must be the package-list source");
    }

    [TestMethod]
    public async Task SingleFileMode_SelfContainedApp_SkipsFrameworkDependencyAndRuntimeProvisioning()
    {
        // A self-contained app already carries the Windows App SDK. Adding a framework PackageDependency
        // for it makes registration fail on a machine that lacks that framework, even though the app
        // never needed it.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir, "counter.exe", selfContained: true);        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeMsixService.AddLooseLayoutSelfContainedCalls.Single(),
            "WindowsAppSDKSelfContained must reach the loose-layout pipeline, not be discarded");
    }

    [TestMethod]
    public async Task SingleFileMode_NoRestore_ReachesTheLooseLayoutPipeline()
    {
        // The generated manifest carries no MSBuild metadata, so registration takes the raw-manifest
        // branch. That branch used to hard-code noRestore:false, so `--no-restore` silently still ran an
        // implicit restore during package discovery — contrary to the option and the docs.
        var (singleFile, outputDir) = CreateSingleFileApp();
        SetOutcome(singleFile, outputDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--no-restore", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeMsixService.AddLooseLayoutRuntimeCalls.Single().NoRestore,
            "--no-restore must be threaded into loose-layout package discovery");
    }

    #endregion
}

