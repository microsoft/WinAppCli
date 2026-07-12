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
    public async Task FindCentralDirectory_Zip64Eocd_ResolvesOffsetAndSize()
    {
        // Hand-build a ZIP64 tail: [cd placeholder][zip64 eocd][zip64 locator][eocd], because
        // System.IO.Compression only emits ZIP64 for archives too large to construct in a unit test.
        const long cdOffset = 0;
        const long cdSize = 10;
        var buf = new List<byte>();

        buf.AddRange(new byte[cdSize]);                       // central-directory placeholder
        var recordRelative = buf.Count;                       // offset of the ZIP64 EOCD record

        var record = new byte[56];
        WriteU32(record, 0, 0x06064b50);                      // ZIP64 EOCD signature
        WriteU64(record, 40, unchecked((ulong)cdSize));       // size of the central directory
        WriteU64(record, 48, unchecked((ulong)cdOffset));     // offset of the central directory
        buf.AddRange(record);

        var locator = new byte[20];
        WriteU32(locator, 0, 0x07064b50);                     // ZIP64 locator signature
        WriteU64(locator, 8, (ulong)recordRelative);          // relative offset of the ZIP64 EOCD record
        buf.AddRange(locator);

        var eocd = new byte[22];
        WriteU32(eocd, 0, 0x06054b50);                        // EOCD signature
        WriteU16(eocd, 10, 0xFFFF);                           // entry count → ZIP64 marker
        WriteU32(eocd, 12, 0xFFFFFFFF);                       // cd size → ZIP64 marker
        WriteU32(eocd, 16, 0xFFFFFFFF);                       // cd offset → ZIP64 marker
        buf.AddRange(eocd);

        var data = buf.ToArray();
        var reader = new MemoryRangeReader(data);

        var (offset, size) = await ZipRangeExtractor.FindCentralDirectoryAsync(reader, 0, data.Length, CancellationToken.None);

        Assert.AreEqual(cdOffset, offset, "ZIP64 EOCD central-directory offset must be honored.");
        Assert.AreEqual(cdSize, size, "ZIP64 EOCD central-directory size must be honored.");
    }

    [TestMethod]
    public void ParseCentralDirectory_Zip64ExtraField_Applies64BitValues()
    {
        const long compressed = 0x1_0000_0001;   // > 4 GiB, forcing the 0xFFFFFFFF marker
        const long uncompressed = 0x2_0000_0002;
        const long localOffset = 0x3_0000_0003;
        var name = Encoding.UTF8.GetBytes("big.bin");

        // ZIP64 extra: id 0x0001, dataLen 24, then uncompressed, compressed, localOffset (the fixed
        // order the parser walks, only for fields whose 32-bit slot was 0xFFFFFFFF).
        var extra = new byte[4 + 24];
        WriteU16(extra, 0, 0x0001);
        WriteU16(extra, 2, 24);
        WriteU64(extra, 4, unchecked((ulong)uncompressed));
        WriteU64(extra, 12, unchecked((ulong)compressed));
        WriteU64(extra, 20, unchecked((ulong)localOffset));

        var header = new byte[46 + name.Length + extra.Length];
        WriteU32(header, 0, 0x02014b50);          // central header signature
        WriteU16(header, 10, 0);                  // method: stored
        WriteU32(header, 20, 0xFFFFFFFF);         // compressed → ZIP64 marker
        WriteU32(header, 24, 0xFFFFFFFF);         // uncompressed → ZIP64 marker
        WriteU16(header, 28, (ushort)name.Length);
        WriteU16(header, 30, (ushort)extra.Length);
        WriteU16(header, 32, 0);                  // comment length
        WriteU32(header, 42, 0xFFFFFFFF);         // local-header offset → ZIP64 marker
        name.CopyTo(header, 46);
        extra.CopyTo(header, 46 + name.Length);

        var entries = ZipRangeExtractor.ParseCentralDirectory(header, archiveBase: 1000);

        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual("big.bin", entry.Name);
        Assert.AreEqual(compressed, entry.CompressedSize, "64-bit compressed size must come from the ZIP64 extra field.");
        Assert.AreEqual(uncompressed, entry.UncompressedSize, "64-bit uncompressed size must come from the ZIP64 extra field.");
        Assert.AreEqual(1000 + localOffset, entry.LocalHeaderOffset, "64-bit local-header offset must be applied then rebased.");
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteU32(byte[] buffer, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteU64(byte[] buffer, int offset, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), value);

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
