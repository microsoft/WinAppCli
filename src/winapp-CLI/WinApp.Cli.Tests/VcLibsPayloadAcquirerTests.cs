// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// The one dependency family winapp fetches from a URL, and the checks that keep that narrow
/// (spec §"Runtime provisioning" steps 2 and 3).
/// </summary>
/// <remarks>
/// The desktop VC runtime ships in no package a build restores and is not in the Windows SDK, so a
/// packaged desktop app that declares it cannot register in a fresh guest without it. Fetching it is
/// worth doing; fetching anything a manifest happens to name is not, and the difference is entirely
/// in the allowlist and the identity validation covered here.
/// </remarks>
[TestClass]
public class VcLibsPayloadAcquirerTests
{
    private const string DesktopVcLibs = "Microsoft.VCLibs.140.00.UWPDesktop";

    private string _root = null!;
    private string _sdkCache = null!;
    private VcLibsPayloadAcquirer _acquirer = null!;
    private List<string> _requested = null!;
    private byte[]? _download;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(VcLibsPayloadAcquirerTests));
        _sdkCache = TestPaths.Under(_root, "sdk");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_sdkCache);

        _requested = [];
        _download = null;

        _acquirer = new VcLibsPayloadAcquirer(new FakeWinappDirectoryService(new DirectoryInfo(_root)))
        {
            CacheDirectories = () => [new DirectoryInfo(_sdkCache)],
            Downloader = (address, _) =>
            {
                _requested.Add(address);

                return _download is null
                    ? throw new HttpRequestException("no route in this test")
                    : Task.FromResult(_download);
            },
        };
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
    public async Task Acquire_PrefersACopyTheHostAlreadyHas()
    {
        WritePackage(Path.Join(_sdkCache, "vclibs.appx"), DesktopVcLibs, "14.0.33728.0", "x64");

        var payload = await AcquireAsync(DesktopVcLibs, "14.0.33519.0", "x64");

        Assert.IsNotNull(payload);
        Assert.AreEqual("14.0.33728.0", payload.Version);
        Assert.IsEmpty(_requested, "a cached official copy must not trigger a download");
    }

    [TestMethod]
    public async Task Acquire_FetchesTheOfficialAddressForTheRequiredArchitecture()
    {
        _download = BuildPackage(DesktopVcLibs, "14.0.33728.0", "arm64");

        var payload = await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "arm64");

        Assert.IsNotNull(payload);
        Assert.AreEqual(
            "https://aka.ms/Microsoft.VCLibs.arm64.14.00.Desktop.appx",
            _requested.Single());
    }

    [TestMethod]
    public async Task Acquire_CachesWhatItFetchedSoALaterRunNeedsNoNetwork()
    {
        _download = BuildPackage(DesktopVcLibs, "14.0.33728.0", "x64");

        await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "x64");
        var second = await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "x64");

        Assert.IsNotNull(second);
        Assert.AreEqual(1, _requested.Count, "the second run must resolve from the host cache");
        StringAssert.StartsWith(second.File.FullName, _root);
    }

    [TestMethod]
    public async Task Acquire_RefusesADownloadWhoseIdentityIsNotWhatWasAskedFor()
    {
        // A redirect that ended somewhere unexpected, or a mirror serving the wrong package. The URL
        // is not the evidence; the manifest inside the bytes is.
        _download = BuildPackage("Contoso.SomethingElse", "14.0.33728.0", "x64");

        var payload = await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "x64");

        Assert.IsNull(payload);
        Assert.IsEmpty(Directory.GetFiles(_root, "*.appx", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task Acquire_RefusesADownloadForTheWrongArchitecture()
    {
        _download = BuildPackage(DesktopVcLibs, "14.0.33728.0", "x86");

        var payload = await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "x64");

        // An x86 VC runtime cannot satisfy an x64 dependency, and accepting it would move the
        // failure to registration where nothing explains it.
        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task Acquire_RefusesADownloadOlderThanTheConstraint()
    {
        _download = BuildPackage(DesktopVcLibs, "14.0.30704.0", "x64");

        Assert.IsNull(await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "x64"));
    }

    [TestMethod]
    public async Task Acquire_RefusesAPackageThatIsNotPublishedByMicrosoft()
    {
        _download = BuildPackage(DesktopVcLibs, "14.0.33728.0", "x64", publisher: "CN=Someone Else");

        // The declared requirement may name no publisher at all, which would otherwise accept a
        // same-named package from anyone — the wrong default for bytes that arrived over a network.
        Assert.IsNull(await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "x64", declaredPublisher: null));
    }

    [TestMethod]
    public async Task Acquire_NeverFetchesAPackageOutsideTheAllowlist()
    {
        _download = BuildPackage("Contoso.Framework", "1.0.0.0", "x64");

        var payload = await AcquireAsync("Contoso.Framework", "1.0.0.0", "x64");

        // This is the boundary that stops runtime provisioning from becoming a general downloader.
        Assert.IsNull(payload);
        Assert.IsEmpty(_requested);
    }

    [TestMethod]
    public async Task Acquire_TreatsAFailedDownloadAsSomethingToVerifyInTheGuest()
    {
        // _download stays null, so the seam throws exactly as an offline host would.
        var payload = await AcquireAsync(DesktopVcLibs, "14.0.33728.0", "x64");

        // Not an error here: the guest may already have it, and only an unsatisfied guest
        // verification is grounds for refusing to launch.
        Assert.IsNull(payload);
        Assert.AreEqual(1, _requested.Count);
    }

    private Task<RuntimePayload?> AcquireAsync(
        string name,
        string minVersion,
        string architecture,
        string? declaredPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US") =>
        _acquirer.TryAcquireAsync(
            new RuntimePackageRequirement
            {
                Name = name,
                MinVersion = minVersion,
                Architecture = architecture,
                Publisher = declaredPublisher,
            },
            new DirectoryInfo(_root),
            CreateTaskContext(),
            TestContext.CancellationToken);

    private static void WritePackage(string path, string name, string version, string architecture)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, BuildPackage(name, version, architecture));
    }

    private static byte[] BuildPackage(
        string name,
        string version,
        string architecture,
        string publisher = VcLibsPayloadAcquirer.MicrosoftPublisher)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open()))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="{name}" Publisher="{publisher}" Version="{version}" ProcessorArchitecture="{architecture}" />
                </Package>
                """);
        }

        return buffer.ToArray();
    }

    private static TaskContext CreateTaskContext() =>
        new(new GroupableTask("vclibs-test", null), null, new TestConsole(), NullLogger.Instance, new Lock());
}
