// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ZipRangeExtractorTests
{
    private const string InnerMsixName = "windbg_win-arm64.msix";
    private const string JsProviderPath = "arm64/winext/JsProvider.dll";

    // A fake PE payload (starts with the MZ signature) that compresses well via deflate.
    private static byte[] FakeJsProvider()
    {
        var payload = new byte[4096];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        for (var i = 2; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 7);
        }

        return payload;
    }

    /// <summary>Builds a ZIP containing the supplied entries (forward-slash names).</summary>
    private static byte[] BuildZip(IEnumerable<(string Name, byte[] Data, CompressionLevel Level)> entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, level) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Builds an outer bundle ZIP that STORES an inner msix ZIP containing JsProvider.dll.</summary>
    private static (byte[] Bundle, byte[] ExpectedJsProvider) BuildNestedBundle(string jsProviderPath = JsProviderPath)
    {
        var js = FakeJsProvider();
        var innerMsix = BuildZip(
        [
            ("AppxManifest.xml", Encoding.UTF8.GetBytes("<manifest/>"), CompressionLevel.Optimal),
            (jsProviderPath, js, CompressionLevel.Optimal),
            ("arm64/winext/chakra/JsProvider.dll", Encoding.UTF8.GetBytes("MZchakra"), CompressionLevel.Optimal),
        ]);

        var bundle = BuildZip(
        [
            ("AppxMetadata/AppxBundleManifest.xml", Encoding.UTF8.GetBytes("<bundle/>"), CompressionLevel.Optimal),
            // Inner msix packages are STORED in the real bundle, so the inner archive is contiguous.
            (InnerMsixName, innerMsix, CompressionLevel.NoCompression),
            ("windbg_win-x64.msix", Encoding.UTF8.GetBytes("not a real zip"), CompressionLevel.NoCompression),
        ]);

        return (bundle, js);
    }

    [TestMethod]
    public async Task ExtractJsProvider_NestedBundle_ReturnsPeBytes()
    {
        var (bundle, expected) = BuildNestedBundle();
        var reader = new MemoryRangeReader(bundle);

        var bytes = await WinDbgJsProviderAcquirer.ExtractJsProviderAsync(reader, InnerMsixName, "arm64", CancellationToken.None);

        Assert.IsNotNull(bytes, "JsProvider.dll should be extracted from the nested bundle.");
        CollectionAssert.AreEqual(expected, bytes, "Extracted bytes must match the original (deflate round-trip).");
    }

    [TestMethod]
    public async Task ExtractJsProvider_MissingInnerMsix_ReturnsNull()
    {
        var (bundle, _) = BuildNestedBundle();
        var reader = new MemoryRangeReader(bundle);

        var bytes = await WinDbgJsProviderAcquirer.ExtractJsProviderAsync(reader, "windbg_win-does-not-exist.msix", "arm64", CancellationToken.None);

        Assert.IsNull(bytes, "A missing inner msix must yield null, not throw.");
    }

    [TestMethod]
    public async Task ExtractJsProvider_MissingFileInInner_ReturnsNull()
    {
        var (bundle, _) = BuildNestedBundle();
        var reader = new MemoryRangeReader(bundle);

        // The inner msix exists but contains no amd64/winext/JsProvider.dll.
        var bytes = await WinDbgJsProviderAcquirer.ExtractJsProviderAsync(reader, InnerMsixName, "amd64", CancellationToken.None);

        Assert.IsNull(bytes, "A missing JsProvider path must yield null, not throw.");
    }

    [TestMethod]
    public async Task ExtractEntry_StoredAndDeflate_RoundTrip()
    {
        var stored = Encoding.UTF8.GetBytes("stored-payload-exactly");
        var deflated = FakeJsProvider();
        var zip = BuildZip(
        [
            ("stored.bin", stored, CompressionLevel.NoCompression),
            ("deflated.bin", deflated, CompressionLevel.Optimal),
        ]);
        var reader = new MemoryRangeReader(zip);

        var (cdOffset, cdSize) = await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, zip.Length, CancellationToken.None);
        var entries = ZipRangeExtractor.ParseCentralDirectory(
            await reader.ReadAsync(cdOffset, (int)cdSize, CancellationToken.None), 0);

        var storedEntry = entries.Single(e => e.Name == "stored.bin");
        var deflatedEntry = entries.Single(e => e.Name == "deflated.bin");
        Assert.AreEqual(0, storedEntry.Method, "NoCompression must produce a STORED entry.");
        Assert.AreEqual(8, deflatedEntry.Method, "Optimal must produce a DEFLATE entry.");

        CollectionAssert.AreEqual(stored, await ZipRangeExtractor.ExtractEntryAsync(reader, storedEntry, CancellationToken.None));
        CollectionAssert.AreEqual(deflated, await ZipRangeExtractor.ExtractEntryAsync(reader, deflatedEntry, CancellationToken.None));
    }

    [TestMethod]
    public void HostTokens_KnownArchitectures_Map()
    {
        Assert.AreEqual(("windbg_win-x64.msix", "amd64"), WinDbgJsProviderAcquirer.HostTokens(Architecture.X64));
        Assert.AreEqual(("windbg_win-arm64.msix", "arm64"), WinDbgJsProviderAcquirer.HostTokens(Architecture.Arm64));
        Assert.AreEqual(("windbg_win-x86.msix", "x86"), WinDbgJsProviderAcquirer.HostTokens(Architecture.X86));
    }

    [TestMethod]
    public void HostTokens_UnsupportedArchitecture_ReturnsNulls()
    {
        var (msix, prefix) = WinDbgJsProviderAcquirer.HostTokens(Architecture.Wasm);
        Assert.IsNull(msix);
        Assert.IsNull(prefix);
    }
}
