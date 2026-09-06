// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection.PortableExecutable;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class PeHelperTests
{
    private static readonly byte[] DotNetBundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    // COFF machine constants.
    private const ushort I386 = 0x014C;
    private const ushort Amd64 = 0x8664;
    private const ushort Arm64 = 0xAA64;
    private const ushort Armnt = 0x01C4;
    private const ushort Arm = 0x01C0;
    private const ushort Unknown = 0x9999;

    [TestMethod]
    [DataRow(I386, "x86")]
    [DataRow(Amd64, "x64")]
    [DataRow(Arm64, "arm64")]
    [DataRow(Armnt, "arm")]
    [DataRow(Arm, "arm")]
    public void ClassifyArchitecture_NativeImage_UsesMachineField(ushort machine, string expected)
    {
        Assert.AreEqual(expected, PeHelper.ClassifyArchitecture(machine, corFlags: null));
    }

    [TestMethod]
    public void ClassifyArchitecture_NativeUnknownMachine_ReturnsNull()
    {
        Assert.IsNull(PeHelper.ClassifyArchitecture(Unknown, corFlags: null));
    }

    [TestMethod]
    public void ClassifyArchitecture_IlOnlyI386_WithRequires32Bit_IsX86()
    {
        Assert.AreEqual("x86", PeHelper.ClassifyArchitecture(I386, CorFlags.ILOnly | CorFlags.Requires32Bit));
    }

    [TestMethod]
    public void ClassifyArchitecture_IlOnlyI386_WithoutRequires32Bit_IsNeutral()
    {
        Assert.AreEqual("neutral", PeHelper.ClassifyArchitecture(I386, CorFlags.ILOnly));
    }

    [TestMethod]
    [DataRow(Amd64, "x64")]
    [DataRow(Arm, "arm")]
    [DataRow(Armnt, "arm")]
    [DataRow(Arm64, "arm64")]
    public void ClassifyArchitecture_IlOnlyNonI386_MapsByMachine(ushort machine, string expected)
    {
        Assert.AreEqual(expected, PeHelper.ClassifyArchitecture(machine, CorFlags.ILOnly));
    }

    [TestMethod]
    public void ClassifyArchitecture_IlOnlyUnknownMachine_ReturnsNull()
    {
        Assert.IsNull(PeHelper.ClassifyArchitecture(Unknown, CorFlags.ILOnly));
    }

    [TestMethod]
    public void ClassifyArchitecture_MixedModeManaged_FallsBackToMachine()
    {
        // COR header present but NOT IL-only (mixed-mode / native-hosted): machine wins.
        Assert.AreEqual("x64", PeHelper.ClassifyArchitecture(Amd64, (CorFlags)0));
        Assert.AreEqual("x86", PeHelper.ClassifyArchitecture(I386, CorFlags.Requires32Bit));
    }

    [TestMethod]
    public void ClassifyArchitecture_MixedModeUnknownMachine_ReturnsNull()
    {
        Assert.IsNull(PeHelper.ClassifyArchitecture(Unknown, (CorFlags)0));
    }

    [TestMethod]
    public void DetectPeArchitecture_NonexistentFile_ReturnsNull()
    {
        var missing = Path.GetFullPath(
            $"pehelper_missing_{Guid.NewGuid():N}.dll",
            Path.GetTempPath());
        Assert.IsNull(PeHelper.DetectPeArchitecture(missing));
    }

    [TestMethod]
    public void DetectPeArchitecture_NotAPeFile_ReturnsNull()
    {
        var junk = Path.GetFullPath(
            $"pehelper_junk_{Guid.NewGuid():N}.bin",
            Path.GetTempPath());
        File.WriteAllText(junk, "this is not a PE image");
        try
        {
            Assert.IsNull(PeHelper.DetectPeArchitecture(junk));
        }
        finally
        {
            File.Delete(junk);
        }
    }

    private static readonly string[] KnownArchitectures = ["x86", "x64", "arm", "arm64"];

    [TestMethod]
    public void DetectPeArchitecture_RealNativeSystemDll_ReturnsKnownArchitecture()
    {
        // A real native Windows DLL is classified from its COFF machine field.
        var kernel32 = Path.GetFullPath("kernel32.dll", Environment.SystemDirectory);
        if (!File.Exists(kernel32))
        {
            Assert.Inconclusive("kernel32.dll not found; skipping native PE classification check.");
            return;
        }

        var arch = PeHelper.DetectPeArchitecture(kernel32);
        CollectionAssert.Contains(KnownArchitectures, arch,
            $"A real native system DLL should classify to a known architecture, got '{arch}'.");
    }

    [TestMethod]
    public void GetDotNetSingleFileBundleHeaderOffset_UnpatchedAppHostMarkerReturnsNull()
    {
        var path = WriteBundleMarkerFixture(headerOffset: 0);
        try
        {
            Assert.IsNull(PeHelper.GetDotNetSingleFileBundleHeaderOffset(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void GetDotNetSingleFileBundleHeaderOffset_PatchedMarkerReturnsOffset()
    {
        const long headerOffset = 160;
        var path = WriteBundleMarkerFixture(headerOffset);
        try
        {
            Assert.AreEqual(
                headerOffset,
                PeHelper.GetDotNetSingleFileBundleHeaderOffset(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteBundleMarkerFixture(long headerOffset)
    {
        var path = Path.GetFullPath(
            $"pehelper_bundle_{Guid.NewGuid():N}.exe",
            Path.GetTempPath());
        using var stream = File.Create(path);
        stream.SetLength(256);
        stream.Position = 64;
        stream.Write(BitConverter.GetBytes(headerOffset));
        stream.Write(DotNetBundleSignature);
        return path;
    }
}
