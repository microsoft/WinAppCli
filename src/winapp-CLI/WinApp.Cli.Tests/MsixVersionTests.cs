// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class MsixVersionTests
{
    [TestMethod]
    [DataRow("1.2.3.4", (ushort)1, (ushort)2, (ushort)3, (ushort)4)]
    [DataRow("65535.65535.65535.65535", (ushort)65535, (ushort)65535, (ushort)65535, (ushort)65535)]
    [DataRow("0.0.0.1", (ushort)0, (ushort)0, (ushort)0, (ushort)1)]
    [DataRow("9.9.9.9", (ushort)9, (ushort)9, (ushort)9, (ushort)9)]
    public void TryParse_ValidInput_ReturnsVersion(
        string input, ushort major, ushort minor, ushort build, ushort revision)
    {
        Assert.IsTrue(MsixVersion.TryParse(input, out var version));
        Assert.AreEqual(new MsixVersion(major, minor, build, revision), version);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("1.2.3")]
    [DataRow(" 1.2.3")]
    [DataRow("1.2.3 ")]
    [DataRow(" 1.2.3.4 ")]
    [DataRow("1.02.3.4")]
    [DataRow("1.2.3.04")]
    [DataRow("01.02.03.04")]
    [DataRow("1.2.3.4.5")]
    [DataRow("1.0\" malicious")]
    [DataRow("1.2.-1.4")]
    [DataRow("-1.2.3.4")]
    [DataRow("not.a.version")]
    [DataRow("65536.0.0.0")]
    [DataRow("0.0.0.0")]
    [DataRow("1.0.0.0\n")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        Assert.IsFalse(MsixVersion.TryParse(input, out var version));
        Assert.AreEqual(default, version);
    }

    [TestMethod]
    public void Parse_ValidInput_ReturnsVersion()
    {
        var version = MsixVersion.Parse("1.2.3.4");
        Assert.AreEqual(new MsixVersion(1, 2, 3, 4), version);
    }

    [TestMethod]
    public void Parse_InvalidInput_ThrowsFormatException()
    {
        Assert.ThrowsExactly<FormatException>(() => MsixVersion.Parse("not.a.version"));
    }

    [TestMethod]
    public void ToString_ReturnsFourPartString()
    {
        var version = new MsixVersion(1, 2, 3, 4);
        Assert.AreEqual("1.2.3.4", version.ToString());
    }

    [TestMethod]
    public void Equals_SameValues_AreEqual()
    {
        var a = new MsixVersion(1, 2, 3, 4);
        var b = new MsixVersion(1, 2, 3, 4);
        Assert.AreEqual(a, b);
        Assert.IsTrue(a == b);
        Assert.IsFalse(a != b);
    }

    [TestMethod]
    [DataRow("0.2.3.4", "7.2.3.4")]
    [DataRow("1.2.3.4", "1.7.3.4")]
    [DataRow("1.2.3.4", "1.2.7.4")]
    [DataRow("1.2.3.4", "1.2.4.7")]
    public void Equals_DifferentValues_AreNotEqual(string aString, string bString)
    {
        var a = MsixVersion.Parse(aString);
        var b = MsixVersion.Parse(bString);
        Assert.AreNotEqual(a, b);
        Assert.IsFalse(a == b);
        Assert.IsTrue(a != b);
    }
}
