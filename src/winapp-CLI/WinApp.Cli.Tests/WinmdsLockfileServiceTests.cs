// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class WinmdsLockfileServiceTests
{
    private static readonly string[] _arr00 = ["Microsoft.WindowsAppSDK.AI"];

    public TestContext TestContext { get; set; } = null!;

    private DirectoryInfo _temp = null!;
    private WinmdsLockfileService _svc = null!;

    [TestInitialize]
    public void Init()
    {
        _temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"WinmdsLockfileTests_{Guid.NewGuid():N}"));
        _temp.Create();
        _svc = new WinmdsLockfileService(NullLogger<WinmdsLockfileService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _temp.Delete(recursive: true); } catch { /* ignore */ }
    }

    [TestMethod]
    public void GetLockfilePath_LandsUnderWinappDir()
    {
        var path = _svc.GetLockfilePath(_temp);
        Assert.AreEqual(Path.Combine(_temp.FullName, "winmds.lock.json"), path.FullName);
    }

    [TestMethod]
    public async Task WriteAsync_ProducesIndentedSchemaVersionedJson()
    {
        var winapp = _temp.CreateSubdirectory("winapp");
        // ExtractPackageIdFromPath requires the literal "packages" segment.
        var cache = _temp.CreateSubdirectory("packages");
        var winmd = new FileInfo(Path.Combine(
            cache.FullName, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd"));
        winmd.Directory!.Create();
        await File.WriteAllTextAsync(winmd.FullName, "");

        var usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
        };
        await _svc.WriteAsync(winapp, usedVersions, new[] { winmd }, cache, default);

        var path = _svc.GetLockfilePath(winapp);
        Assert.IsTrue(path.Exists, "Lockfile must be written under .winapp/.");
        var json = await File.ReadAllTextAsync(path.FullName);
        StringAssert.Contains(json, "\"schema\": 2");
        StringAssert.Contains(json, "\"generated_at\"");
        StringAssert.Contains(json, "Microsoft.WindowsAppSDK.AI");
        StringAssert.Contains(json, "\"category\": \"emit\"");
        Assert.IsTrue(json.Contains('\n'), "Output must be indented (multiple lines).");
        var bytes = await File.ReadAllBytesAsync(path.FullName);
        Assert.IsFalse(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Lockfile must be UTF-8 without BOM (so diff tools and external readers don't choke).");
    }

    [TestMethod]
    public async Task RoundTrip_PreservesPackageVersionsAndCategories()
    {
        var winapp = _temp.CreateSubdirectory("winapp");
        var cache = _temp.CreateSubdirectory("packages");

        // Build a realistic mix: emit + ref-only + skip + a package with zero winmds.
        var aiWinmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        var ieWinmd = MakeFile(cache, "microsoft.windowsappsdk.interactiveexperiences", "1.8.0", "metadata", "10.0.18362.0", "Microsoft.UI.winmd");
        var winuiWinmd = MakeFile(cache, "microsoft.windowsappsdk.winui", "1.8.0", "metadata", "Microsoft.UI.Xaml.winmd");

        var usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
            ["Microsoft.WindowsAppSDK.InteractiveExperiences"] = "1.8.0",
            ["Microsoft.WindowsAppSDK.WinUI"] = "1.8.0",
            ["Microsoft.WindowsAppSDK"] = "1.8.0",  // umbrella, no winmd
        };
        await _svc.WriteAsync(winapp, usedVersions, new[] { aiWinmd, ieWinmd, winuiWinmd }, cache, default);

        var lockfile = await _svc.TryReadAsync(winapp, default);
        Assert.IsNotNull(lockfile);
        Assert.AreEqual(2, lockfile.Schema);
        Assert.AreEqual(4, lockfile.Packages.Count);

        // Packages are sorted alphabetically (case-insensitive) by name.
        var ai = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK.AI");
        Assert.AreEqual("1.8.39", ai.Version);
        Assert.AreEqual("emit", ai.Category);
        Assert.AreEqual(1, ai.Winmds.Count);
        Assert.IsTrue(ai.Winmds[0].EndsWith("Microsoft.Windows.AI.winmd", StringComparison.OrdinalIgnoreCase));

        var ie = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK.InteractiveExperiences");
        Assert.AreEqual("refOnly", ie.Category);
        Assert.AreEqual(1, ie.Winmds.Count);

        var winui = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK.WinUI");
        Assert.AreEqual("skip", winui.Category);
        Assert.AreEqual(1, winui.Winmds.Count);

        var umbrella = lockfile.Packages.Single(p => p.Name == "Microsoft.WindowsAppSDK");
        Assert.AreEqual("emit", umbrella.Category);
        Assert.AreEqual(0, umbrella.Winmds.Count, "Umbrella package has no winmd files; lockfile records it for completeness.");
    }

    [TestMethod]
    public async Task TryReadAsync_MissingFile_ReturnsNull()
    {
        var result = await _svc.TryReadAsync(_temp, default);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryReadAsync_CorruptedJson_ReturnsNull()
    {
        var path = _svc.GetLockfilePath(_temp);
        await File.WriteAllTextAsync(path.FullName, "{this is not json");

        var result = await _svc.TryReadAsync(_temp, default);
        Assert.IsNull(result, "Corrupted lockfile must trigger fallback (return null) rather than throw.");
    }

    [TestMethod]
    public async Task TryReadAsync_UnknownSchemaVersion_ReturnsNull()
    {
        // A future schema bump must not crash older clients.
        var path = _svc.GetLockfilePath(_temp);
        await File.WriteAllTextAsync(path.FullName, "{\"schema\": 999, \"packages\": []}");

        var result = await _svc.TryReadAsync(_temp, default);
        Assert.IsNull(result, "Unknown schema version must be treated as missing.");
    }

    [TestMethod]
    public void BuildLockfile_VendorWinmdsOutsideCache_AreDropped()
    {
        // Lockfile is a record of "what restore put on disk", not user-supplied refs.
        var cache = _temp.CreateSubdirectory("packages");
        var vendorPath = Path.Combine(_temp.FullName, "vendor", "MyCompany.Custom.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(vendorPath)!);
        File.WriteAllText(vendorPath, "");

        var lockfile = WinmdsLockfileService.BuildLockfile(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Microsoft.WindowsAppSDK.AI"] = "1.8.39" },
            new[] { new FileInfo(vendorPath) },
            cache);

        Assert.AreEqual(1, lockfile.Packages.Count);
        Assert.AreEqual(0, lockfile.Packages[0].Winmds.Count,
            "Vendor winmd outside the NuGet cache must not get attached to any package.");
    }

    [TestMethod]
    public void BuildLockfile_PartitionFromLockfile_AppliesScopeAsEmitFilter_DemotesUnscopedToRefOnly()
    {
        // scope narrows EMIT, not codegen visibility — unscoped Emit
        // packages must end up RefOnly for cross-package type resolution.
        var cache = _temp.CreateSubdirectory("packages");
        var aiWinmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        var fdnWinmd = MakeFile(cache, "microsoft.windowsappsdk.foundation", "1.8.0", "metadata", "Microsoft.Windows.Foundation.winmd");

        var lockfile = WinmdsLockfileService.BuildLockfile(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
                ["Microsoft.WindowsAppSDK.Foundation"] = "1.8.0",
            },
            new[] { aiWinmd, fdnWinmd },
            cache);

        var (emit, refOnly, skipped) = JsBindingsWorkspaceService.PartitionFromLockfile(
            lockfile, _arr00);

        Assert.AreEqual(1, emit.Count, "Only the scoped AI package emits.");
        Assert.IsTrue(emit[0].FullName.EndsWith("Microsoft.Windows.AI.winmd", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, refOnly.Count,
            "Unscoped Foundation package MUST be preserved as RefOnly (it provides types AI references). "
            + "An earlier implementation dropped the package entirely → broken codegen.");
        Assert.IsTrue(refOnly[0].FullName.EndsWith("Microsoft.Windows.Foundation.winmd", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, skipped);
    }

    [TestMethod]
    public void PartitionFromLockfile_NullScope_ReturnsAllPackages()
    {
        var cache = _temp.CreateSubdirectory("packages");
        var aiWinmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        var winuiWinmd = MakeFile(cache, "microsoft.windowsappsdk.winui", "1.8.0", "metadata", "Microsoft.UI.Xaml.winmd");
        var ieWinmd = MakeFile(cache, "microsoft.windowsappsdk.interactiveexperiences", "1.8.0", "metadata", "Microsoft.UI.winmd");

        var lockfile = WinmdsLockfileService.BuildLockfile(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
                ["Microsoft.WindowsAppSDK.WinUI"] = "1.8.0",
                ["Microsoft.WindowsAppSDK.InteractiveExperiences"] = "1.8.0",
            },
            new[] { aiWinmd, winuiWinmd, ieWinmd },
            cache);

        var (emit, refOnly, skipped) = JsBindingsWorkspaceService.PartitionFromLockfile(lockfile, null);

        Assert.AreEqual(1, emit.Count);
        Assert.AreEqual(1, refOnly.Count);
        Assert.AreEqual(1, skipped, "WinUI package contributes 1 to the skipped count.");
    }

    // -------------------------------------------------------------------------
    // v2.3 — yaml hash, atomic write, schema-bump back-compat
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task WriteAsync_StoresYamlPackagesHash()
    {
        var winapp = _temp.CreateSubdirectory("winapp");
        var cache = _temp.CreateSubdirectory("packages");
        var winmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");

        await _svc.WriteAsync(
            winapp,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Microsoft.WindowsAppSDK.AI"] = "1.8.39" },
            new[] { winmd },
            cache,
            yamlPackagesHash: "abc123deadbeef",
            cancellationToken: default);

        var lockfile = await _svc.TryReadAsync(winapp, default);
        Assert.IsNotNull(lockfile);
        Assert.AreEqual("abc123deadbeef", lockfile.YamlPackagesHash);
    }

    [TestMethod]
    public async Task TryReadAsync_Schema1Lockfile_ReturnsNull()
    {
        // pre-v2.3 lockfiles use schema=1; readers treat them as missing
        // so the slow path rebuilds.
        var path = _svc.GetLockfilePath(_temp);
        await File.WriteAllTextAsync(path.FullName, "{\"schema\": 1, \"packages\": []}");

        var result = await _svc.TryReadAsync(_temp, default);
        Assert.IsNull(result, "Schema 1 lockfiles must be ignored after the v2.3 schema bump.");
    }

    [TestMethod]
    public async Task WriteAsync_UsesAtomicTempThenRename()
    {
        // Can't observe mid-write reliably; check post-conditions: final
        // file exists, no .tmp left behind.
        var winapp = _temp.CreateSubdirectory("winapp");
        var cache = _temp.CreateSubdirectory("packages");
        var winmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "AI.winmd");

        await _svc.WriteAsync(
            winapp,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Microsoft.WindowsAppSDK.AI"] = "1.8.39" },
            new[] { winmd },
            cache,
            yamlPackagesHash: "h",
            cancellationToken: default);

        var entries = winapp.EnumerateFiles().Select(f => f.Name).ToList();
        CollectionAssert.Contains(entries, "winmds.lock.json", "Final lockfile must exist.");
        Assert.IsFalse(
            entries.Any(n => n.StartsWith("winmds.lock.json.tmp", StringComparison.Ordinal)),
            $"No tmp staging file should remain after a successful write. Found: {string.Join(", ", entries)}");
    }

    [TestMethod]
    public void PartitionFromLockfile_UncWinmdPaths_DroppedForBothEmitAndRefOnly()
    {
        // A tampered lockfile smuggling UNC paths must drop them for both
        // emit and ref-only — any FileInfo() probe would SMB-handshake.
        var cache = _temp.CreateSubdirectory("packages");
        var legitEmit = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        var legitRef = MakeFile(cache, "microsoft.windowsappsdk.foundation", "1.8.0", "metadata", "Microsoft.Windows.Foundation.winmd");

        // Hand-build the lockfile (BuildLockfile would reject paths outside
        // the cache). RFC 2606 `.invalid` TLD never resolves.
        var lockfile = new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = cache.FullName,
            YamlPackagesHash = "h",
            Packages = new List<WinmdsLockfilePackage>
            {
                new WinmdsLockfilePackage
                {
                    Name = "Microsoft.WindowsAppSDK.AI",
                    Version = "1.8.39",
                    Category = "emit",
                    Winmds = new List<string>
                    {
                        legitEmit.FullName,
                        @"\\nonexistent-attacker.invalid\share\evil.emit.winmd",
                    },
                },
                new WinmdsLockfilePackage
                {
                    Name = "Microsoft.WindowsAppSDK.Foundation",
                    Version = "1.8.0",
                    // Default category is Emit but no scope → demoted to RefOnly.
                    Category = "emit",
                    Winmds = new List<string>
                    {
                        legitRef.FullName,
                        @"\\nonexistent-attacker.invalid\share\evil.ref.winmd",
                    },
                },
            },
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (emit, refOnly, _) = JsBindingsWorkspaceService.PartitionFromLockfile(
            lockfile, _arr00);
        sw.Stop();

        Assert.IsTrue(sw.ElapsedMilliseconds < 2_000,
            $"PartitionFromLockfile must drop UNC paths before any FileInfo probe "
            + $"(took {sw.ElapsedMilliseconds}ms; >1s suggests SMB negotiation).");

        Assert.AreEqual(1, emit.Count, "Only the legit emit winmd survives.");
        Assert.IsTrue(
            emit[0].FullName.EndsWith("Microsoft.Windows.AI.winmd", StringComparison.OrdinalIgnoreCase),
            $"Emit must keep the legit local path. Got: {string.Join(", ", emit.Select(f => f.FullName))}");
        Assert.IsFalse(
            emit.Any(f => f.FullName.Contains("nonexistent-attacker.invalid", StringComparison.OrdinalIgnoreCase)),
            "Emit MUST drop the UNC entry.");

        Assert.AreEqual(1, refOnly.Count, "Only the legit ref-only winmd survives.");
        Assert.IsTrue(
            refOnly[0].FullName.EndsWith("Microsoft.Windows.Foundation.winmd", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            refOnly.Any(f => f.FullName.Contains("nonexistent-attacker.invalid", StringComparison.OrdinalIgnoreCase)),
            "RefOnly MUST drop the UNC entry.");
    }

    private static FileInfo MakeFile(DirectoryInfo cache, params string[] segments)
    {
        var path = Path.Combine(new[] { cache.FullName }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return new FileInfo(path);
    }

    // ---------------------------------------------------------------------
    // M9 — IsLockfilePathUnsafe rejects reparse-point ancestors silently
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task WriteAsync_WinappDirIsJunction_LogsAndSkipsWithoutWriting()
    {
        // The lockfile is an optimization, not correctness. When `.winapp/`
        // is itself (or under) a junction/symlink, refuse to write rather
        // than throw — otherwise a malicious workspace could redirect the
        // write to a victim file. Skipping is still safe because codegen
        // falls back to the full discovery path on next run.
        var realDir = _temp.CreateSubdirectory("real-winapp");
        var winappJunction = Path.Combine(_temp.FullName, ".winapp");
        if (!TryCreateJunction(winappJunction, realDir.FullName))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var winappDir = new DirectoryInfo(winappJunction);
            var cache = _temp.CreateSubdirectory("packages");
            var winmd = MakeFile(cache, "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
            var usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
            };

            await _svc.WriteAsync(winappDir, usedVersions, new[] { winmd }, cache, default);

            // Nothing inside the (junction-targeted) real dir AND nothing
            // inside the junction view. Skip = no write.
            Assert.IsFalse(File.Exists(Path.Combine(realDir.FullName, "winmds.lock.json")),
                "Lockfile must NOT be written through a junctioned .winapp.");
        }
        finally
        {
            try { Directory.Delete(winappJunction, recursive: false); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task TryReadAsync_WinappDirIsJunction_ReturnsNullWithoutReading()
    {
        var realDir = _temp.CreateSubdirectory("real-winapp");
        // Plant a valid-looking lockfile under the REAL dir so the only
        // way a read could succeed is by traversing the junction.
        var lockfilePath = Path.Combine(realDir.FullName, "winmds.lock.json");
        await File.WriteAllTextAsync(lockfilePath, """
            { "schema": 2, "generated_at": "2025-01-01T00:00:00Z", "entries": [] }
            """);

        var winappJunction = Path.Combine(_temp.FullName, ".winapp");
        if (!TryCreateJunction(winappJunction, realDir.FullName))
        {
            Assert.Inconclusive("Could not create a junction (CI may lack the privilege).");
            return;
        }

        try
        {
            var winappDir = new DirectoryInfo(winappJunction);
            var result = await _svc.TryReadAsync(winappDir, default);

            Assert.IsNull(result,
                "TryReadAsync must refuse to traverse a junctioned .winapp and return null.");
        }
        finally
        {
            try { Directory.Delete(winappJunction, recursive: false); } catch { /* ignore */ }
        }
    }

    // Junction creation helper (mklink /J — non-elevating on Windows).
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
