// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Fuzz;

namespace WinApp.Cli.Tests;

/// <summary>
/// Guards the OneFuzz harness for <c>ZipRangeExtractor</c>. These tests exist because the harness
/// contract is only validated by OneFuzz at job-submission time — a renamed method or a typo in
/// <c>OneFuzzConfig.json</c> would otherwise surface as a failed fuzzing job days later.
/// </summary>
[TestClass]
public class FuzzHarnessTests
{
    private const int MutationIterations = 3000;

    /// <summary>Builds an outer bundle that STORES an inner archive, mirroring a real .msixbundle.</summary>
    private static byte[] BuildNestedBundle()
    {
        var inner = BuildZip([("AppxManifest.xml", Encoding.UTF8.GetBytes("<manifest/>"), CompressionLevel.Optimal),
                              ("arm64/winext/JsProvider.dll", FakePe(), CompressionLevel.Optimal)]);

        return BuildZip([("AppxMetadata/AppxBundleManifest.xml", Encoding.UTF8.GetBytes("<bundle/>"), CompressionLevel.Optimal),
                         ("windbg_win-arm64.msix", inner, CompressionLevel.NoCompression)]);
    }

    private static byte[] FakePe()
    {
        var payload = new byte[2048];
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';
        for (var i = 2; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 7);
        }

        return payload;
    }

    private static byte[] BuildZip(IEnumerable<(string Name, byte[] Data, CompressionLevel Level)> entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, level) in entries)
            {
                using var stream = archive.CreateEntry(name, level).Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    [TestMethod]
    public void FuzzTargets_MatchTheSignatureLibFuzzerBindsTo()
    {
        foreach (var name in new[] { nameof(FuzzableCode.FuzzArchive), nameof(FuzzableCode.FuzzParseCentralDirectory) })
        {
            var method = typeof(FuzzableCode).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, $"{name} must be public and static.");
            Assert.AreEqual(typeof(void), method.ReturnType, $"{name} must return void.");

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length, $"{name} must take exactly one parameter.");
            Assert.AreEqual(typeof(ReadOnlySpan<byte>), parameters[0].ParameterType,
                $"{name} must take ReadOnlySpan<byte>.");
        }
    }

    [TestMethod]
    public void OneFuzzConfig_ReferencesTargetsThatActuallyResolve()
    {
        var configPath = Path.Join(AppContext.BaseDirectory, "OneFuzzConfig.json");
        Assert.IsTrue(File.Exists(configPath),
            $"OneFuzzConfig.json must reach the build output so it lands in the OneFuzz drop directory. Looked in {AppContext.BaseDirectory}.");

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var entries = doc.RootElement.GetProperty("entries");
        Assert.IsTrue(entries.GetArrayLength() > 0, "At least one fuzz entry is required.");

        foreach (var entry in entries.EnumerateArray())
        {
            var target = entry.GetProperty("fuzzer");

            // $type is the required discriminator in OneFuzzConfig v3; without it the job is rejected.
            Assert.AreEqual("libfuzzerDotNet", target.GetProperty("$type").GetString());

            var className = target.GetProperty("class").GetString();
            var methodName = target.GetProperty("method").GetString();

            var type = typeof(FuzzableCode).Assembly.GetType(className!);
            Assert.IsNotNull(type, $"OneFuzzConfig.json names class '{className}', which does not exist.");
            Assert.IsNotNull(type.GetMethod(methodName!, BindingFlags.Public | BindingFlags.Static),
                $"OneFuzzConfig.json names method '{className}.{methodName}', which does not exist.");

            Assert.AreEqual("WinApp.Cli.Fuzz.dll", target.GetProperty("dll").GetString());

            // No compliance claim is generated when FuzzingTargetBinaries is empty, and the SDL work
            // item link is what ties the resulting claim back to the fuzzing requirement.
            Assert.IsTrue(target.GetProperty("FuzzingTargetBinaries").GetArrayLength() > 0,
                "FuzzingTargetBinaries must name the shipping binary, or OneFuzz generates no claim.");
            Assert.AreEqual(63509499, entry.GetProperty("SdlWorkItemId").GetInt32());
        }
    }

    [TestMethod]
    public void FuzzArchive_MutatedArchives_OnlyThrowExpectedRejections()
    {
        var escapes = RunMutationCampaign(FuzzableCode.FuzzArchive, BuildNestedBundle());
        Assert.AreEqual(0, escapes.Count,
            $"FuzzArchive let {escapes.Count} unexpected exception(s) escape: {Describe(escapes)}");
    }

    [TestMethod]
    public void FuzzParseCentralDirectory_MutatedCentralDirectories_OnlyThrowExpectedRejections()
    {
        // Seeded with real central-directory bytes: the parser bails at offset 0 unless the buffer
        // opens with PK\x01\x02, so seeding it with a whole archive would exercise nothing.
        var escapes = RunMutationCampaign(FuzzableCode.FuzzParseCentralDirectory, CarveCentralDirectory(BuildNestedBundle()));
        Assert.AreEqual(0, escapes.Count,
            $"FuzzParseCentralDirectory let {escapes.Count} unexpected exception(s) escape: {Describe(escapes)}");
    }

    /// <summary>Extracts just the central-directory bytes from an archive.</summary>
    private static byte[] CarveCentralDirectory(byte[] archive)
    {
        var reader = new WinApp.Cli.Helpers.MemoryRangeReader(archive);
        var (offset, size) = WinApp.Cli.Helpers.ZipRangeExtractor
            .FindCentralDirectoryAsync(reader, 0, archive.Length, CancellationToken.None).GetAwaiter().GetResult();
        return reader.ReadAsync(offset, (int)size, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Describe(List<Exception> escapes)
    {
        var summary = string.Join("; ", escapes.GroupBy(e => e.GetType().Name)
                                               .Select(g => $"{g.Key} x{g.Count()} (e.g. {g.First().Message})"));

        // The counts say how reachable a gap is; the first stack says where it is.
        return $"{summary}{Environment.NewLine}First escape:{Environment.NewLine}{escapes[0]}";
    }

    /// <summary>
    /// Writes the seed corpus for both fuzz targets and asserts every seed is structurally valid.
    /// </summary>
    /// <remarks>
    /// libFuzzer mutates existing inputs; it will not synthesise a valid ZIP64 central directory from
    /// random bytes, so without seeds a job burns hours at near-zero coverage. Seeds are uploaded to a
    /// OneFuzz <c>SeedCorpusContainer</c> rather than shipped in the drop directory.
    /// <para>
    /// Each seed is round-tripped through the parser here: a corpus that the parser rejects at byte
    /// zero is worse than no corpus, because the job still looks busy.
    /// </para>
    /// </remarks>
    [TestMethod]
    public void GenerateSeedCorpus_ProducesValidSeedsForBothTargets()
    {
        var root = Path.Join(AppContext.BaseDirectory, "fuzz-corpus");
        var archiveDir = Path.Join(root, "ziprangeextractor-archive");
        var directoryDir = Path.Join(root, "ziprangeextractor-centraldirectory");
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(directoryDir);

        var seeds = new Dictionary<string, byte[]>
        {
            ["minimal-stored"] = BuildZip([("a.bin", Encoding.UTF8.GetBytes("stored"), CompressionLevel.NoCompression)]),
            ["deflate"] = BuildZip([("a.bin", FakePe(), CompressionLevel.Optimal)]),
            ["multi-entry"] = BuildZip([("a.bin", Encoding.UTF8.GetBytes("one"), CompressionLevel.NoCompression),
                                        ("dir/b.bin", FakePe(), CompressionLevel.Optimal),
                                        ("dir/sub/c.txt", Encoding.UTF8.GetBytes("three"), CompressionLevel.Optimal)]),
            ["nested-bundle"] = BuildNestedBundle(),
            ["archive-comment"] = WithArchiveComment(BuildNestedBundle(), "seed-comment"),
        };

        foreach (var (name, bytes) in seeds)
        {
            // Proves the seed reaches real parsing rather than being rejected immediately.
            var entries = ParseArchive(bytes);
            Assert.IsTrue(entries.Count > 0, $"Seed '{name}' produced no entries, so it would cover nothing.");

            File.WriteAllBytes(Path.Join(archiveDir, $"{name}.zip"), bytes);
            File.WriteAllBytes(Path.Join(directoryDir, $"{name}.cd.bin"), CarveCentralDirectory(bytes));
        }

        Assert.AreEqual(seeds.Count, Directory.GetFiles(archiveDir).Length);
        Assert.AreEqual(seeds.Count, Directory.GetFiles(directoryDir).Length);

        Console.WriteLine($"Seed corpus written to {root}");
    }

    private static IReadOnlyList<WinApp.Cli.Helpers.ZipEntry> ParseArchive(byte[] archive)
    {
        var reader = new WinApp.Cli.Helpers.MemoryRangeReader(archive);
        var (offset, size) = WinApp.Cli.Helpers.ZipRangeExtractor
            .FindCentralDirectoryAsync(reader, 0, archive.Length, CancellationToken.None).GetAwaiter().GetResult();
        return WinApp.Cli.Helpers.ZipRangeExtractor.ParseCentralDirectory(
            reader.ReadAsync(offset, (int)size, CancellationToken.None).GetAwaiter().GetResult(), 0);
    }

    /// <summary>Appends an archive comment, which pushes the EOCD record away from the end of the file.</summary>
    private static byte[] WithArchiveComment(byte[] archive, string comment)
    {
        var commentBytes = Encoding.UTF8.GetBytes(comment);
        var eocd = LastIndexOfEocd(archive);
        Assert.IsTrue(eocd >= 0, "Expected a well-formed archive to contain an EOCD record.");

        var result = new byte[archive.Length + commentBytes.Length];
        archive.CopyTo(result, 0);
        commentBytes.CopyTo(result, archive.Length);
        WriteU16(result, eocd + 20, (ushort)commentBytes.Length);
        return result;
    }

    private static int LastIndexOfEocd(byte[] buffer)
    {
        for (var i = buffer.Length - 4; i >= 0; i--)
        {
            if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
            {
                return i;
            }
        }

        return -1;
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    /// <summary>
    /// Deterministically mutates a valid nested bundle and drives it through <paramref name="target"/>,
    /// collecting anything the harness did not absorb. This is a smoke test, not a substitute for a
    /// OneFuzz run — it has no coverage feedback — but it proves the harness executes and that its
    /// exception filter matches what the parser actually throws.
    /// </summary>
    private static List<Exception> RunMutationCampaign(FuzzTarget target, byte[] seed)
    {
        var random = new Random(20260814);
        var escapes = new List<Exception>();

        for (var i = 0; i < MutationIterations; i++)
        {
            var candidate = (byte[])seed.Clone();
            var mutations = random.Next(1, 12);
            for (var m = 0; m < mutations; m++)
            {
                candidate[random.Next(candidate.Length)] = (byte)random.Next(256);
            }

            // Truncation is the cheapest way to reach the length-handling paths.
            var length = random.Next(0, 8) == 0 ? random.Next(0, candidate.Length) : candidate.Length;

            try
            {
                target(candidate.AsSpan(0, length));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Otherwise unfiltered: anything reaching here is something the harness failed to
                // absorb, and narrowing it would hide the unexpected types this test exists to catch.
                // OOM is let through instead, since looping on to allocate more after it is unsound.
                escapes.Add(ex);
            }
        }

        return escapes;
    }

    private delegate void FuzzTarget(ReadOnlySpan<byte> input);
}
