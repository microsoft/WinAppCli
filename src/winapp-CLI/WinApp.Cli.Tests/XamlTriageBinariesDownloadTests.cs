// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Offline tests for the NuGet flat-container download core of <see cref="XamlTriageBinaries"/>
/// (<c>ResolveDownloadVersionAsync</c> → <c>TryMaterializePackageAsync</c> → <c>FindBestArchMatch</c>).
/// The two flat-container HTTP GETs are redirected through the <see cref="XamlTriageBinaries.HttpGetAsync"/>
/// seam to canned in-memory responses — an <c>index.json</c> and a purpose-built <c>.nupkg</c> whose
/// SHA-512 is computed here — so the version-resolution, integrity-verification, archive-extraction, and
/// architecture-preference logic all run without any network I/O. Only the seam's default delegate (a
/// single real <c>HttpClient.GetAsync</c> call) is left as a documented network boundary. Marked
/// <c>[DoNotParallelize]</c> because it swaps the process-wide <c>HttpGetAsync</c> seam.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class XamlTriageBinariesDownloadTests
{
    private const string DbgEngPackage = "Microsoft.Debugging.Platform.DbgEng";
    private static readonly string PinnedVersion = XamlTriageBinaries.DbgPackageVersion;

    private string _tempDir = null!;
    private Func<HttpClient, string, CancellationToken, Task<HttpResponseMessage>> _originalGet = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"XamlBinDl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalGet = XamlTriageBinaries.HttpGetAsync;
    }

    [TestCleanup]
    public void Cleanup()
    {
        XamlTriageBinaries.HttpGetAsync = _originalGet;
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task TryMaterializePackageAsync_ValidPinnedPackage_ExtractsArchPreferredAndFallbackFiles()
    {
        // A .nupkg containing dbgeng.dll under an arch-tagged folder and dbghelp.dll at a non-arch path.
        // FindBestArchMatch must prefer the arch-tagged copy for the former and fall back to the sole
        // match for the latter; both are copied, so materialization succeeds.
        var nupkg = BuildNupkg(zip =>
        {
            AddEntry(zip, $"pkg/{XamlTriageBinaries.NuGetArch}/dbgeng.dll", "ENGINE_BYTES");
            AddEntry(zip, "lib/dbghelp.dll", "HELP_BYTES");
        });
        var sha = Convert.ToHexString(SHA512.HashData(nupkg));
        StubFeed(indexHasPinned: true, nupkgBytes: nupkg);

        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        binDir.Create();
        using var http = new HttpClient();
        var ok = await XamlTriageBinaries.TryMaterializePackageAsync(
            http, DbgEngPackage, PinnedVersion, sha, ["dbgeng.dll", "dbghelp.dll"],
            binDir, NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(ok, "Both files should be materialized from the verified package.");
        Assert.IsTrue(File.Exists(Path.Combine(binDir.FullName, "dbgeng.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(binDir.FullName, "dbghelp.dll")));
    }

    [TestMethod]
    public async Task TryMaterializePackageAsync_HashMismatch_RefusesAndReturnsFalse()
    {
        var nupkg = BuildNupkg(zip => AddEntry(zip, $"{XamlTriageBinaries.NuGetArch}/dbgeng.dll", "ENGINE"));
        StubFeed(indexHasPinned: true, nupkgBytes: nupkg);

        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        using var http = new HttpClient();
        var ok = await XamlTriageBinaries.TryMaterializePackageAsync(
            http, DbgEngPackage, PinnedVersion, new string('0', 128), ["dbgeng.dll"],
            binDir, NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(ok, "A package whose SHA-512 does not match the pinned hash must be refused.");
        Assert.IsFalse(File.Exists(Path.Combine(binDir.FullName, "dbgeng.dll")),
            "Nothing may be extracted when the integrity check fails (fail closed).");
    }

    [TestMethod]
    public async Task TryMaterializePackageAsync_NupkgReturnsNotFound_ReturnsFalse()
    {
        XamlTriageBinaries.HttpGetAsync = (_, url, _) =>
            Task.FromResult(url.Contains("index.json", StringComparison.Ordinal)
                ? JsonResponse(IndexJson(includePinned: true))
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        using var http = new HttpClient();
        var ok = await XamlTriageBinaries.TryMaterializePackageAsync(
            http, DbgEngPackage, PinnedVersion, new string('0', 128), ["dbgeng.dll"],
            new DirectoryInfo(Path.Combine(_tempDir, "bin")), NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(ok, "A failed .nupkg download must report no materialization.");
    }

    [TestMethod]
    public async Task TryMaterializePackageAsync_PinnedVersionNotOnFeed_ReturnsFalse()
    {
        XamlTriageBinaries.HttpGetAsync = (_, _, _) =>
            Task.FromResult(JsonResponse(IndexJson(includePinned: false)));

        using var http = new HttpClient();
        var ok = await XamlTriageBinaries.TryMaterializePackageAsync(
            http, DbgEngPackage, PinnedVersion, new string('0', 128), ["dbgeng.dll"],
            new DirectoryInfo(Path.Combine(_tempDir, "bin")), NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(ok, "When the feed index lacks the pinned version, no download is attempted.");
    }

    [TestMethod]
    public async Task TryMaterializePackageAsync_IndexReturnsNotFound_ReturnsFalse()
    {
        XamlTriageBinaries.HttpGetAsync = (_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        using var http = new HttpClient();
        var ok = await XamlTriageBinaries.TryMaterializePackageAsync(
            http, DbgEngPackage, PinnedVersion, new string('0', 128), ["dbgeng.dll"],
            new DirectoryInfo(Path.Combine(_tempDir, "bin")), NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(ok, "A failed index request short-circuits to no resolved version.");
    }

    [TestMethod]
    public async Task TryMaterializePackageAsync_FileMissingFromPackage_ReturnsFalse()
    {
        // Valid, hash-matched package that does not contain the requested file → FindBestArchMatch
        // returns null → nothing is copied → materialization reports failure.
        var nupkg = BuildNupkg(zip => AddEntry(zip, "docs/readme.txt", "not a dll"));
        var sha = Convert.ToHexString(SHA512.HashData(nupkg));
        StubFeed(indexHasPinned: true, nupkgBytes: nupkg);

        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        using var http = new HttpClient();
        var ok = await XamlTriageBinaries.TryMaterializePackageAsync(
            http, DbgEngPackage, PinnedVersion, sha, ["dbgeng.dll"],
            binDir, NullLogger.Instance, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.IsFalse(File.Exists(Path.Combine(binDir.FullName, "dbgeng.dll")));
    }

    [TestMethod]
    public async Task TryAcquireFromNuGetAsync_CacheMissAndDownloadFails_ReturnsZero()
    {
        // No global cache and every flat-container request fails → the public entry point walks the
        // "already usable" (no), "copy from cache" (skipped: null cache), and "download" (fails)
        // branches for each component and reports nothing acquired, without throwing.
        XamlTriageBinaries.HttpGetAsync = (_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        var acquired = await XamlTriageBinaries.TryAcquireFromNuGetAsync(
            binDir, nugetCacheDir: null, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, acquired);
    }

    [TestMethod]
    public async Task TryAcquireFromNuGetAsync_HttpThrows_SwallowsPerComponentAndReturnsZero()
    {
        // A transport-level failure (not cancellation) while acquiring a component must be caught
        // per-component and logged, so one broken download can't abort the whole acquisition.
        XamlTriageBinaries.HttpGetAsync = (_, _, _) =>
            throw new HttpRequestException("simulated transport failure");

        var binDir = new DirectoryInfo(Path.Combine(_tempDir, "bin"));
        var acquired = await XamlTriageBinaries.TryAcquireFromNuGetAsync(
            binDir, nugetCacheDir: null, NullLogger.Instance, CancellationToken.None);

        Assert.AreEqual(0, acquired, "A throwing download must be swallowed per component, acquiring nothing.");
    }

    private static void StubFeed(bool indexHasPinned, byte[] nupkgBytes)
    {
        XamlTriageBinaries.HttpGetAsync = (_, url, _) =>
            Task.FromResult(url.Contains("index.json", StringComparison.Ordinal)
                ? JsonResponse(IndexJson(indexHasPinned))
                : BytesResponse(nupkgBytes));
    }

    private static string IndexJson(bool includePinned)
    {
        var versions = includePinned ? $"\"1.0.0\",\"{PinnedVersion}\"" : "\"1.0.0\",\"2.0.0\"";
        return $"{{\"versions\":[{versions}]}}";
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BytesResponse(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] BuildNupkg(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            fill(zip);
        }

        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
