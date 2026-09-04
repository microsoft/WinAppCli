// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Runtime.InteropServices;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Installing a shared .NET framework inside a guest, into a root winapp owns
/// (spec §"Runtime provisioning" step 5).
/// </summary>
/// <remarks>
/// The two properties that matter are the ones a shared guest depends on: nothing already present is
/// replaced or removed, and an interrupted install leaves only disposable content rather than a
/// framework directory that exists and cannot load.
/// </remarks>
[TestClass]
public class DotNetRuntimeInstallerTests
{
    private const string Core = "Microsoft.NETCore.App";

    private string _root = null!;
    private string _managedRoot = null!;
    private string _staging = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(DotNetRuntimeInstallerTests));
        _managedRoot = TestPaths.Under(_root, "dotnet");
        _staging = TestPaths.Under(_root, "staged");

        Directory.CreateDirectory(_staging);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [TestMethod]
    public void Ensure_UnpacksTheLayoutIntoTheManagedRoot()
    {
        WriteLayout("layout.zip", Core, "10.0.2");

        var outcome = Ensure(Requirement(Core, "10.0.0", "layout.zip"));

        Assert.IsTrue(outcome.Installed);
        Assert.AreEqual(new Version(10, 0, 2), outcome.PresentVersion);

        Assert.IsTrue(File.Exists(
            Path.Join(_managedRoot, "shared", Core, "10.0.2", $"{Core}.deps.json")));

        Assert.IsTrue(File.Exists(Path.Join(_managedRoot, "host", "fxr", "10.0.2", "hostfxr.dll")));
    }

    [TestMethod]
    public void Ensure_LeavesTheStagingFolderEmptyAfterwards()
    {
        WriteLayout("layout.zip", Core, "10.0.2");

        Ensure(Requirement(Core, "10.0.0", "layout.zip"));

        // Everything unpacked is either published or disposable. Staging that survived would grow by
        // a full runtime on every pass.
        var stagingRoot = Path.Join(_managedRoot, DotNetRuntimeInstaller.StagingFolderName);
        Assert.IsEmpty(Directory.GetDirectories(stagingRoot));
    }

    [TestMethod]
    public void Ensure_WhenTheVersionIsAlreadyPresent_DoesNotTouchIt()
    {
        var existing = WriteInstalledFramework(_managedRoot, Core, "10.0.2");

        var marker = Path.Join(existing, "already-here.dll");
        File.WriteAllText(marker, "the copy an app in this guest is running on");

        WriteLayout("layout.zip", Core, "10.0.2");

        var outcome = Ensure(Requirement(Core, "10.0.0", "layout.zip"));

        // Present already satisfies it, so nothing is unpacked at all — replacing a live framework
        // is exactly what "never downgrades or removes a shared runtime" forbids.
        Assert.IsFalse(outcome.Installed);
        Assert.AreEqual("the copy an app in this guest is running on", File.ReadAllText(marker));
        Assert.IsTrue(File.Exists(Path.Join(existing, $"{Core}.deps.json")));
    }

    [TestMethod]
    public void Ensure_InstallsBesideAnOlderVersionRatherThanOverIt()
    {
        var older = Path.Join(_managedRoot, "shared", Core, "10.0.1");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Join(older, "old.dll"), "older");

        WriteLayout("layout.zip", Core, "10.0.4");

        var outcome = Ensure(Requirement(Core, "10.0.3", "layout.zip"));

        Assert.IsTrue(outcome.Installed);
        Assert.AreEqual(new Version(10, 0, 4), outcome.PresentVersion);

        // Side-by-side: the older version another application may be using is still there.
        Assert.IsTrue(File.Exists(Path.Join(older, "old.dll")));
    }

    [TestMethod]
    public void Ensure_WhenACompletedFrameworkWasPublishedBeforeItsResolver_RepairsTheResolver()
    {
        var existing = WriteInstalledFramework(_managedRoot, Core, "10.0.2");
        Directory.Delete(Path.Join(_managedRoot, "host"), recursive: true);
        File.WriteAllText(Path.Join(existing, "still-complete.txt"), "shared framework was published atomically");

        WriteLayout("layout.zip", Core, "10.0.2");

        var outcome = Ensure(Requirement(Core, "10.0.0", "layout.zip"));

        Assert.IsTrue(outcome.Installed);
        Assert.AreEqual(new Version(10, 0, 2), outcome.PresentVersion);
        Assert.IsTrue(File.Exists(Path.Join(_managedRoot, "host", "fxr", "10.0.2", "hostfxr.dll")));
        Assert.AreEqual(
            "shared framework was published atomically",
            File.ReadAllText(Path.Join(existing, "still-complete.txt")));
    }

    [TestMethod]
    public void Ensure_WhenAnotherRootAlreadySatisfiesIt_InstallsNothing()
    {
        var guestInstall = TestPaths.Under(_root, "program-files-dotnet");
        WriteInstalledFramework(guestInstall, Core, "10.0.9");

        WriteLayout("layout.zip", Core, "10.0.2");

        var outcome = Ensure(Requirement(Core, "10.0.0", "layout.zip"), extraRoots: [guestInstall]);

        // A guest that already has the framework needs nothing from winapp, and installing anyway
        // would move tens of megabytes to no effect.
        Assert.IsFalse(outcome.Installed);
        Assert.IsFalse(Directory.Exists(Path.Join(_managedRoot, "shared")));
    }

    [TestMethod]
    public void Ensure_ADifferentMajorInAnotherRootDoesNotSatisfyIt()
    {
        var guestInstall = TestPaths.Under(_root, "program-files-dotnet");
        WriteInstalledFramework(guestInstall, Core, "10.0.9");

        WriteLayout("layout.zip", Core, "8.0.4");

        var outcome = Ensure(Requirement(Core, "8.0.0", "layout.zip"), extraRoots: [guestInstall]);

        // .NET rolls forward across patches and minors only.
        Assert.IsTrue(outcome.Installed);
        Assert.AreEqual(new Version(8, 0, 4), outcome.PresentVersion);
    }

    [TestMethod]
    public void Ensure_UsesTheMatchingArchitectureWhenSeveralRootsHaveTheFramework()
    {
        var x64 = TestPaths.Under(_root, "dotnet-x64");
        var x86 = TestPaths.Under(_root, "dotnet-x86");
        WriteInstalledFramework(x64, Core, "10.0.9", "x64");
        WriteInstalledFramework(x86, Core, "10.0.3", "x86");

        var outcome = Ensure(
            Requirement(Core, "10.0.0", payloadFile: null, architecture: "x86"),
            extraRoots: [x64, x86]);

        Assert.IsFalse(outcome.Installed);
        Assert.AreEqual(new Version(10, 0, 3), outcome.PresentVersion);
    }

    [TestMethod]
    public void Ensure_WhenOnlyAnotherArchitectureIsInstalled_DoesNotAcceptIt()
    {
        var x64 = TestPaths.Under(_root, "dotnet-x64");
        WriteInstalledFramework(x64, Core, "10.0.9", "x64");

        var outcome = Ensure(
            Requirement(Core, "10.0.0", payloadFile: null, architecture: "x86"),
            extraRoots: [x64]);

        Assert.IsFalse(outcome.Installed);
        Assert.IsNull(outcome.PresentVersion);
        StringAssert.Contains(outcome.Detail!, "no runtime layout");
    }

    [TestMethod]
    public void Ensure_AfterInstall_ReprobesTheManagedRootForThePlanArchitecture()
    {
        var x64 = TestPaths.Under(_root, "dotnet-x64");
        WriteInstalledFramework(x64, Core, "10.0.9", "x64");
        WriteLayout("x86-layout.zip", Core, "10.0.2", "x86");

        var outcome = Ensure(
            Requirement(Core, "10.0.0", "x86-layout.zip", architecture: "x86"),
            extraRoots: [x64]);

        Assert.IsTrue(outcome.Installed);
        Assert.AreEqual(new Version(10, 0, 2), outcome.PresentVersion);
    }

    [TestMethod]
    [DataRow("x64", "DOTNET_ROOT_X64")]
    [DataRow("arm64", "DOTNET_ROOT_ARM64")]
    [DataRow("x86", "DOTNET_ROOT_X86")]
    public void DefaultRoots_PrefersTheArchitectureSpecificEnvironmentVariable(
        string architecture,
        string expectedVariable)
    {
        var roots = DotNetLayout.DefaultRoots(
            architecture,
            variable => variable switch
            {
                "DOTNET_ROOT" => @"C:\generic-dotnet",
                _ when variable == expectedVariable => @"C:\specific-dotnet",
                _ => null,
            }).ToArray();

        Assert.AreEqual(1, roots.Length);
        Assert.AreEqual(@"C:\specific-dotnet", roots[0]);
    }

    [TestMethod]
    public void DefaultRoots_UsesGenericEnvironmentVariableWhenTheSpecificOneIsUnset()
    {
        var roots = DotNetLayout.DefaultRoots(
            "x64",
            variable => variable == DotNetLayout.DiscoveryVariable ? @"C:\generic-dotnet" : null).ToArray();

        Assert.AreEqual(1, roots.Length);
        Assert.AreEqual(@"C:\generic-dotnet", roots[0]);
    }

    [TestMethod]
    public void DefaultRoots_X86UsesTheLegacyVariableBeforeTheGenericOne()
    {
        var roots = DotNetLayout.DefaultRoots(
            "x86",
            variable => variable switch
            {
                DotNetLayout.LegacyX86DiscoveryVariable => @"C:\legacy-x86-dotnet",
                DotNetLayout.DiscoveryVariable => @"C:\generic-dotnet",
                _ => null,
            },
            Architecture.X64).ToArray();

        Assert.AreEqual(1, roots.Length);
        Assert.AreEqual(@"C:\legacy-x86-dotnet", roots[0]);
    }

    [TestMethod]
    public void DefaultRoots_X64OnArm64UsesTheEmulatedRuntimeLocation()
    {
        var roots = DotNetLayout.DefaultRoots(
            "x64",
            _ => null,
            Architecture.Arm64).ToArray();

        Assert.AreEqual(1, roots.Length);
        Assert.AreEqual(
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "x64"),
            roots[0]);
    }

    [TestMethod]
    public void Ensure_WithNoStagedLayout_ReportsWhyRatherThanThrowing()
    {
        var outcome = Ensure(Requirement(Core, "10.0.0", payloadFile: null));

        Assert.IsFalse(outcome.Installed);
        Assert.IsNull(outcome.PresentVersion);
        Assert.IsNotNull(outcome.Detail);
    }

    [TestMethod]
    public void Ensure_WithAMissingStagedLayout_ReportsWhyRatherThanThrowing()
    {
        var outcome = Ensure(Requirement(Core, "10.0.0", "never-transferred.zip"));

        Assert.IsFalse(outcome.Installed);
        StringAssert.Contains(outcome.Detail!, "missing");
    }

    [TestMethod]
    public void Ensure_RefusesAnArchiveEntryThatWouldEscapeTheRoot()
    {
        var path = Path.Join(_staging, "evil.zip");

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var entry = new StreamWriter(archive.CreateEntry("../../escaped.dll").Open());
            entry.Write("nope");
        }

        var outcome = Ensure(Requirement(Core, "10.0.0", "evil.zip"));

        // Defence in depth — the archive was built by the host and arrived over the verified file
        // channel — but an extractor that can be talked into writing outside its root is worth never
        // having.
        Assert.IsFalse(outcome.Installed);
        Assert.IsFalse(File.Exists(Path.Join(_root, "escaped.dll")));
    }

    private static RuntimeFrameworkRequirement Requirement(
        string name,
        string minVersion,
        string? payloadFile,
        string architecture = "x64") =>
        new()
        {
            Name = name,
            MinVersion = minVersion,
            Architecture = architecture,
            PayloadFile = payloadFile,
        };

    private DotNetInstallOutcome Ensure(RuntimeFrameworkRequirement requirement, string[]? extraRoots = null) =>
        DotNetRuntimeInstaller.Ensure(
            requirement,
            _managedRoot,
            _staging,
            new[] { _managedRoot }.Concat(extraRoots ?? []),
            TestContext.CancellationToken);

    private void WriteLayout(string fileName, string framework, string version, string architecture = "x64")
    {
        var path = Path.Join(_staging, fileName);

        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        Write(archive, $"shared/{framework}/{version}/{framework}.deps.json", "{}");

        if (framework == Core)
        {
            WriteBytes(
                archive,
                $"shared/{framework}/{version}/hostpolicy.dll",
                MinimalPe.ForArchitecture(architecture));
            Write(archive, $"shared/{framework}/{version}/coreclr.dll", "mz");
            Write(archive, $"shared/{framework}/{version}/System.Private.CoreLib.dll", "mz");
            Write(archive, $"host/fxr/{version}/hostfxr.dll", "mz");
        }

        static void Write(ZipArchive archive, string entryPath, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
            writer.Write(content);
        }

        static void WriteBytes(ZipArchive archive, string entryPath, byte[] content)
        {
            using var stream = archive.CreateEntry(entryPath).Open();
            stream.Write(content);
        }
    }

    private static string WriteInstalledFramework(
        string root,
        string framework,
        string version,
        string architecture = "x64")
    {
        var directory = Path.Join(root, "shared", framework, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, $"{framework}.deps.json"), "{}");

        if (framework == Core)
        {
            File.WriteAllBytes(Path.Join(directory, "hostpolicy.dll"), MinimalPe.ForArchitecture(architecture));
            File.WriteAllText(Path.Join(directory, "coreclr.dll"), "mz");
            File.WriteAllText(Path.Join(directory, "System.Private.CoreLib.dll"), "mz");

            var resolver = Path.Join(root, "host", "fxr", version);
            Directory.CreateDirectory(resolver);
            File.WriteAllText(Path.Join(resolver, "hostfxr.dll"), "mz");
        }

        return directory;
    }
}
